using DatabaseReleaseQualification;

internal static class LegacyExecutionFenceTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases = [
        ("legacy execution fence orders phases and rejects cross-run replay", Order),
        ("legacy execution fence serializes targets and preserves unknown outcomes", Concurrent),
        ("legacy execution fence survives logical client restart and canonical aliases", Restart),
        ("legacy execution fence reconciles independent proof without a second SQL write", Reconciliation),
        ("legacy execution fence retains uncertain DATA outcome and rejects false reconciliation", UncertainData),
        ("legacy execution fence preserves lost acknowledgements and unavailable providers", LostConfirmation),
        ("legacy real SQL remains blocked without durable corporate fencing", Production)
    ];
    private static readonly LegacyExecutionIdentityV1 Identity = new("target", "endpoint", "database", "incarnation", "release", "package", "run", "FORWARD1", new string('a', 64));
    private static void Blocks(Action action)
    { try { action(); throw new Exception("Expected execution fence block"); } catch (LegacyContractException) { } }
    private static Task Order()
    {
        var fence = new InMemoryLegacyExecutionFence(); var identity = Identity;
        foreach (var phase in new[] { "FORWARD1", "ROLLBACK", "FORWARD2" })
        {
            identity = identity with { Phase = phase }; var lease = fence.Claim(identity);
            Blocks(() => fence.Claim(identity)); fence.MarkStarted(lease);
            Blocks(() => fence.MarkStarted(lease)); fence.Confirm(lease, new string('b', 64));
            identity = identity with { PredecessorHash = new string('b', 64) };
        }
        Blocks(() => fence.Claim(Identity with { RunId = "another-run" }));
        Blocks(() => fence.Claim(Identity with { PackageIdentity = "other-package" }));
        Blocks(() => fence.Claim(Identity with { Incarnation = "other-incarnation" }));
        Blocks(() => fence.Claim(Identity with { Incarnation = "other-incarnation", ReleaseId = "new-release" }));
        Blocks(() => fence.Claim(Identity));
        return Task.CompletedTask;
    }
    private static async Task Concurrent()
    {
        var fence = new InMemoryLegacyExecutionFence();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try { return fence.Claim(Identity); } catch (LegacyContractException) { return null; }
        })));
        var lease = results.Single(x => x is not null)!; fence.MarkStarted(lease); fence.MarkUnknown(lease);
        Blocks(() => fence.Claim(Identity with { ReleaseId = "another-release", RunId = "other" }));
        Blocks(() => fence.Claim(Identity with { TargetId = "another-label", ReleaseId = "another-release" }));
        Blocks(() => fence.Confirm(lease, new string('b', 64)));
        _ = fence.Claim(Identity with { EndpointReference = "independent-endpoint" });
    }
    private static Task Production()
    { Blocks(() => LegacyExecutionFence.RequireProductionBinding()); return Task.CompletedTask; }

    private static Task Restart()
    {
        var journal = new InMemoryLegacyExecutionFence.Journal();
        var first = new InMemoryLegacyExecutionFence(journal);
        var identity = Identity with { CanonicalResourceId = "corporate-resource-1", Repository = "org/app-a" };
        var lease = first.Claim(identity); first.MarkStarted(lease);
        var restarted = new InMemoryLegacyExecutionFence(journal);
        Blocks(() => restarted.Claim(identity with
        {
            EndpointReference = "alias",
            DatabaseName = "alias-db",
            TargetId = "another-target",
            Repository = "org/app-b",
            ReleaseId = "next"
        }));
        if (restarted.Read(lease).State != "STARTED" || restarted.History().Count != 2)
            throw new Exception("Restart discarded reservation or history");
        // An incarnation change does not release an uncertain physical resource.
        Blocks(() => restarted.Claim(identity with { Incarnation = "recreated", ReleaseId = "next" }));
        return Task.CompletedTask;
    }
    private sealed class ReconciliationSource(LegacyExecutionReconciliationV1 evidence) : ILegacyExecutionReconciliationSourceV1
    {
        public Task<LegacyExecutionReconciliationV1> AcquireAsync(string reference, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(evidence); }
    }
    private static LegacyExecutionReconciliationV1 Proof(LegacyExecutionReservationV1 entry, string resolution = "CONFIRM") =>
        new(entry.Lease, entry.Revision, entry.Identity, resolution, Hashing.Sha256("independent-evidence-" + entry.Lease),
            "operator", "independent-reviewer", DateTimeOffset.UtcNow.AddMinutes(10), true, true,
            resolution == "CONFIRM" ? new string('c', 64) : null);
    private static async Task Reconciliation()
    {
        var fence = new InMemoryLegacyExecutionFence(); var lease = fence.Claim(Identity);
        fence.MarkStarted(lease); fence.MarkUnknown(lease);
        var evidence = Proof(fence.Read(lease));
        var consumer = new LegacyExecutionReconcilerV1(fence, new ReconciliationSource(evidence));
        var confirmed = await consumer.ReconcileAsync("protected-evidence-ref");
        var repeat = await consumer.ReconcileAsync("protected-evidence-ref");
        if (confirmed != repeat || fence.History().Count != 4) throw new Exception("Reconciliation replay mutated journal");
        Blocks(() => fence.Claim(Identity));
        _ = fence.Claim(Identity with { Phase = "ROLLBACK", PredecessorHash = confirmed.ResultHash! });
        Blocks(() => fence.Reconcile(evidence with { EvidenceHash = Hashing.Sha256("changed-proof") }));

        var unused = new InMemoryLegacyExecutionFence(); var unusedLease = unused.Claim(Identity);
        unused.Reconcile(Proof(unused.Read(unusedLease), "ABORT_BEFORE_START"));
        Blocks(() => unused.MarkStarted(unusedLease));
        Blocks(() => unused.Claim(Identity)); // consumed release remains a tombstone
        _ = unused.Claim(Identity with { ReleaseId = "new-qualified-release" });
    }
    private static Task UncertainData()
    {
        var fence = new InMemoryLegacyExecutionFence(); var lease = fence.Claim(Identity);
        fence.MarkStarted(lease); fence.MarkUnknown(lease);
        var proof = Proof(fence.Read(lease));
        foreach (var bad in new[] {
            proof with { ExecutionAndCoverageVerified = false, ResultHash = Identity.PredecessorHash },
            proof with { Resolution = "ABORT_BEFORE_START", ResultHash = null },
            proof with { RunnerQuiesced = false }, proof with { Reviewer = proof.Actor },
            proof with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) },
            proof with { Identity = Identity with { Incarnation = "substituted" } },
            proof with { ExpectedRevision = 1 }, proof with { EvidenceHash = "caller-says-success" },
            proof with { ResultHash = "schema-only" }
        }) Blocks(() => fence.Reconcile(bad));
        Blocks(() => fence.Claim(Identity with { ReleaseId = "next", PredecessorHash = Identity.PredecessorHash }));
        if (fence.Read(lease).State != "UNKNOWN" || fence.History().Count != 3)
            throw new Exception("Invalid proof changed uncertain reservation");
        return Task.CompletedTask;
    }
    private sealed class FailingFence(ILegacyExecutionFenceV1 inner, string point) : ILegacyExecutionFenceV1
    {
        private void Fail(string at) { if (point == at) throw new IOException("Simulated lost acknowledgement"); }
        public string Claim(LegacyExecutionIdentityV1 identity) { Fail("unavailable"); var lease = inner.Claim(identity); Fail("claim"); return lease; }
        public void MarkStarted(string lease) { inner.MarkStarted(lease); Fail("start"); }
        public void Confirm(string lease, string resultHash) { inner.Confirm(lease, resultHash); Fail("confirm"); }
        public void MarkUnknown(string lease) => inner.MarkUnknown(lease);
        public LegacyExecutionReservationV1 Read(string lease) => inner.Read(lease);
        public void Reconcile(LegacyExecutionReconciliationV1 evidence) { inner.Reconcile(evidence); Fail("reconcile"); }
    }
    private static Task LostConfirmation()
    {
        foreach (var point in new[] { "unavailable", "claim", "start", "confirm", "reconcile" })
        {
            var inner = new InMemoryLegacyExecutionFence(); var faulty = new FailingFence(inner, point);
            try
            {
                var lease = faulty.Claim(Identity); faulty.MarkStarted(lease);
                if (point == "reconcile")
                { faulty.MarkUnknown(lease); faulty.Reconcile(Proof(faulty.Read(lease))); }
                else faulty.Confirm(lease, new string('b', 64));
                throw new Exception("Fault did not fire");
            }
            catch (IOException) { }
            if (point == "unavailable")
            { if (inner.History().Count != 0) throw new Exception("Unavailable provider created authority"); }
            else
            {
                Blocks(() => inner.Claim(Identity));
                var entry = inner.History()[^1];
                if (point is "confirm" or "reconcile")
                {
                    // Failure cleanup cannot turn a durable confirmation into UNKNOWN.
                    Blocks(() => inner.MarkUnknown(entry.Lease));
                    if (inner.Read(entry.Lease).State != "CONFIRMED") throw new Exception("Lost durable result");
                }
                else Blocks(() => inner.Claim(Identity with { ReleaseId = "another" }));
            }
        }
        return Task.CompletedTask;
    }
}
