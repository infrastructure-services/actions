using DatabaseReleaseQualification;

internal static class LegacyContinuityTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases = [
        ("legacy adoption and three module releases use corporate CAS offline", Chain),
        ("legacy certification requires independent DATA SECURITY and fresh no-change evidence", MixedDimensions),
        ("legacy certification replay and concurrent initial adoption are idempotent", Replay),
        ("legacy interrupted certification retains uncertainty without SQL retry", Uncertainty),
        ("legacy initial certification recovers prepared records and rejects conflicting CAS", PreparedConflict),
        ("legacy certification rejects self approval stale review drift and substitution", Negative),
        ("legacy synthetic lifecycle cannot acquire corporate certification adapter", Boundary)
    ];
    private static void Assert(bool value, string message) => LegacyAdoptionTests.Assert(value, message);
    private sealed class Provider : ILegacyCertificationEvidenceProviderV1
    {
        internal readonly Dictionary<string, LegacyCertificationEvidenceV1> Evidence = [];
        public Task<LegacyCertificationEvidenceV1> AcquireAsync(string reference, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Evidence[reference]); }
    }
    private static async Task<LegacyAdoptionCandidateV1> Candidate(SchemaSnapshot snapshot)
    {
        var raw = await LegacyAdoptionTests.Acquired();
        var capture = new SchemaCaptureSourceResult
        {
            Snapshot = snapshot,
            ServerInstance = "SQL1",
            DatabaseName = "TestDb",
            ServerVersion = "16",
            ServerMajorVersion = 16,
            MetricsAvailability = MetricsAvailability.Complete
        };
        var plan = LegacyAdoptionPlanner.Evaluate(LegacyAdoptionTests.Request,
            raw with { FirstCapture = capture, SecondCapture = capture }, DateTimeOffset.UtcNow);
        return plan.Candidate ?? throw new Exception(string.Join(",", plan.Reasons));
    }
    private static LegacyCertificationReviewV1 Review(LegacyAdoptionCandidateV1 c, bool release = false) => new(
        c.CandidateHash, "author", "reviewer", release ? "LEGACY_QUALIFIED_RELEASE_V1" : "LEGACY_STRUCTURE_ONLY_ADOPTION_V1",
        "review:independent", DateTimeOffset.UtcNow.AddMinutes(10), "incarnation-1", c.EndpointReference);
    private static LegacyCertificationObservationV1 Observe(LegacyAdoptionCandidateV1 c, SchemaSnapshot current,
        LegacyRehearsalReceiptV1? receipt = null) => new(c.TargetId, c.Identity, c.EndpointReference, "incarnation-1",
            Hashing.Sha256("independent-incarnation"), DateTimeOffset.UtcNow, SchemaCanonicalizer.Canonicalize(current).Sha256,
            Hashing.Sha256("independent-current-observation"),
            receipt?.DataContract?.ScopeHash, receipt?.DataEvidence.GetValueOrDefault(RecoveryPhase.Post2)?.ContentHash,
            receipt?.Evaluation?.RecoveryCoverage?.SecurityScopeHash,
            receipt?.Evaluation?.RecoveryCoverage?.SecurityHashes.GetValueOrDefault(RecoveryPhase.Post2));
    private static async Task<(Provider Provider, InMemoryCertificationCommitStore Store, LegacyCertificationEvidenceV1 Evidence)> Initial(SchemaSnapshot? snapshot = null)
    {
        snapshot ??= new(); var c = await Candidate(snapshot);
        var p = new Provider(); var e = new LegacyCertificationEvidenceV1("adopt-1", Guid.NewGuid().ToString("D"), c, snapshot, Review(c), null,
            Observation: Observe(c, snapshot));
        p.Evidence.Add("initial", e); return (p, new(), e);
    }
    private static async Task Chain()
    {
        var first = await LegacyModuleTests.ModuleCycle(1, 2);
        var f = await Initial(first.Qualification.ObservedSnapshot!.Snapshot);
        var consumer = new LegacyCertificationContinuationV1(f.Provider, f.Store);
        var initial = await consumer.ContinueAsync("initial");
        Assert(initial.Status == "GOVERNED_TRANSITION_REQUIRED", string.Join(",", initial.Reasons));
        Assert(!initial.CorporateCertified && !initial.ExecutionAuthorized, "Simulation acquired authority");
        for (var i = 1; i <= 3; i++)
        {
            var cycle = i == 1 ? first : await LegacyModuleTests.ModuleCycle(i, i + 1);
            var pointer = f.Store.GetCurrentPointer(f.Evidence.Candidate.Identity)!;
            var c = await Candidate(cycle.Qualification.ObservedSnapshot!.Snapshot);
            var payload = cycle.Qualification.Payload!;
            var proof = new LegacyExecutedReleaseV1("deployment-" + i, c.TargetId, "incarnation-1", payload.PayloadHash,
                payload.ForwardHash, payload.RollbackHash, c.SchemaHash, cycle.Receipt.Observations[RecoveryPhase.Post2].ObservedSchemaHash,
                Hashing.Sha256("independent-target-execution-" + i), Hashing.Sha256("deployment-authorization-" + i), true,
                PreSecurityHash: cycle.Receipt.Evaluation!.RecoveryCoverage!.SecurityHashes[RecoveryPhase.Pre],
                PostSecurityHash: cycle.Receipt.Evaluation.RecoveryCoverage.SecurityHashes[RecoveryPhase.Post2]);
            var e = new LegacyCertificationEvidenceV1("commit-" + i, Guid.NewGuid().ToString("D"), c,
                cycle.Receipt.Observations[RecoveryPhase.Post2].Snapshot, Review(c, true),
                new(pointer.CertificationId, pointer.CertificationEvidenceHash, pointer.Version), cycle.Receipt, payload, proof,
                GovernedState(c), (await LegacyAdoptionTests.Acquired()).Onboarding with { Status = "MANAGED" },
                Observe(c, cycle.Receipt.Observations[RecoveryPhase.Post2].Snapshot, cycle.Receipt));
            f.Provider.Evidence["ungoverned"] = e with { GovernedState = null };
            Assert((await consumer.ContinueAsync("ungoverned")).Status == "BLOCKED", "Persisted baseline bypassed governance transition");
            f.Provider.Evidence["release"] = e;
            var result = await consumer.ContinueAsync("release");
            Assert(result.Status == "GOVERNED_TRANSITION_REQUIRED", string.Join(",", result.Reasons));
            Assert(!result.CorporateCertified && f.Store.GetCurrentPointer(c.Identity)!.Version == i + 1, "Invalid chain");
            var replay = await consumer.ContinueAsync("release");
            Assert(replay.Commit?.Status == CertificationCommitStatus.IdempotentReplay, "Commit replay was not idempotent");
            var currentCandidate = c with { SchemaHash = e.Observation!.SchemaHash };
            f.Provider.Evidence["duplicate"] = e with
            {
                OperationId = "another-" + i,
                CertificationId = Guid.NewGuid().ToString("D"),
                GovernedState = GovernedState(currentCandidate)
            };
            var duplicate = await consumer.ContinueAsync("duplicate");
            Assert(duplicate.Status == "NO_CHANGES_ALREADY_CERTIFIED" && duplicate.Commit is null
                && !duplicate.ExecutionAuthorized, "Exact certified release did not stop without a write");
            foreach (var invalid in new[] {
                e with { Execution = proof with { PostSecurityHash = new string('0',64) } },
                e with { Observation = e.Observation with { Incarnation = "recreated" } },
                e with { Observation = e.Observation with { SecurityHash = new string('0',64) } }
            }) { f.Provider.Evidence["invalid"] = invalid; Assert((await consumer.ContinueAsync("invalid")).Status == "BLOCKED", "Execution dimensions or incarnation substituted"); }
            f.Provider.Evidence["invalid"] = e with { Execution = null };
            Assert((await consumer.ContinueAsync("invalid")).Status == "BLOCKED", "Rehearsal substituted for target execution");
        }
        Assert(f.Store.ListCertifiedHistory(f.Evidence.Candidate.Identity).Count == 4, "Missing predecessor history");
    }
    private static async Task Replay()
    {
        var f = await Initial(); var consumer = new LegacyCertificationContinuationV1(f.Provider, f.Store);
        var a = await consumer.ContinueAsync("initial");
        var b = await consumer.ContinueAsync("initial");
        Assert(a.Commit?.Status == CertificationCommitStatus.Committed && b.Commit?.Status == CertificationCommitStatus.IdempotentReplay, "Initial replay failed");
        f.Provider.Evidence["other"] = f.Evidence with { OperationId = "other", CertificationId = Guid.NewGuid().ToString("D") };
        Assert((await consumer.ContinueAsync("other")).Status == "BLOCKED" && f.Store.ListCertifiedHistory(f.Evidence.Candidate.Identity).Count == 1, "Second initial baseline accepted");
        var concurrent = await Initial();
        using var race = new BeforeCasBarrier();
        var next = new LegacyCertificationContinuationV1(concurrent.Provider, concurrent.Store, race);
        var results = await Task.WhenAll(Task.Run(() => next.ContinueAsync("initial")), Task.Run(() => next.ContinueAsync("initial")));
        Assert(concurrent.Store.ListCertifiedHistory(concurrent.Evidence.Candidate.Identity).Count == 1
            && results.Count(x => x.Commit?.PointerAdvance == CommitOutcomeState.ConfirmedYes) == 1
            && results.Count(x => x.Commit?.Status == CertificationCommitStatus.ConcurrencyConflict) == 1,
            "Concurrent CAS did not preserve one winner and one explicit conflict: " + string.Join(",", results.Select(x => x.Commit?.Status.ToString() ?? x.Status)));
        var retry = await new LegacyCertificationContinuationV1(concurrent.Provider, concurrent.Store).ContinueAsync("initial");
        Assert(retry.Commit?.Status == CertificationCommitStatus.IdempotentReplay
            && concurrent.Store.ListCertifiedHistory(concurrent.Evidence.Candidate.Identity).Count == 1,
            "Explicit retry duplicated a concurrent initial commit");
    }
    private sealed class BeforeCasBarrier : ICertificationCommitFaultInjector, IDisposable
    {
        private readonly Barrier barrier = new(2);
        public void ThrowIfArmed(CertificationCommitFaultPoint point)
        {
            if (point == CertificationCommitFaultPoint.BeforeCompareAndSwap && !barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new Exception("Concurrent fixture failed to reach both CAS calls");
        }
        public void Dispose() => barrier.Dispose();
    }
    private static async Task MixedDimensions()
    {
        var cycle = await LegacyModuleTests.ModuleCycle(1, 2, "MIXED");
        var f = await Initial(cycle.Qualification.ObservedSnapshot!.Snapshot);
        var consumer = new LegacyCertificationContinuationV1(f.Provider, f.Store);
        _ = await consumer.ContinueAsync("initial");
        var pointer = f.Store.GetCurrentPointer(f.Evidence.Candidate.Identity)!;
        var candidate = f.Evidence.Candidate; var receipt = cycle.Receipt; var payload = cycle.Qualification.Payload!;
        var coverage = receipt.Evaluation!.RecoveryCoverage!;
        var execution = new LegacyExecutedReleaseV1("separate-deployment", candidate.TargetId, "incarnation-1",
            payload.PayloadHash, payload.ForwardHash, payload.RollbackHash, candidate.SchemaHash,
            receipt.Observations[RecoveryPhase.Post2].ObservedSchemaHash, Hashing.Sha256("actual-mixed-execution"),
            Hashing.Sha256("separate-execution-authorization"), true,
            receipt.DataEvidence[RecoveryPhase.Pre].ContentHash, receipt.DataEvidence[RecoveryPhase.Post2].ContentHash,
            coverage.SecurityHashes[RecoveryPhase.Pre], coverage.SecurityHashes[RecoveryPhase.Post2]);
        var current = receipt.Observations[RecoveryPhase.Post2].Snapshot;
        var e = new LegacyCertificationEvidenceV1("mixed-commit", Guid.NewGuid().ToString("D"), candidate, current,
            Review(candidate, true), new(pointer.CertificationId, pointer.CertificationEvidenceHash, pointer.Version),
            receipt, payload, execution, GovernedState(candidate),
            (await LegacyAdoptionTests.Acquired()).Onboarding with { Status = "MANAGED" }, Observe(candidate, current, receipt));
        foreach (var bad in new[] {
            e with { Execution = execution with { PostDataHash = null } },
            e with { Execution = execution with { PreSecurityHash = new string('0',64) } },
            e with { Observation = e.Observation! with { DataHash = new string('0',64) } },
            e with { Observation = e.Observation! with { DataScopeHash = new string('0',64) } }
        }) { f.Provider.Evidence["bad"] = bad; Assert((await consumer.ContinueAsync("bad")).Status == "BLOCKED", "Schema-only proof certified mixed effects"); }
        f.Provider.Evidence["mixed"] = e;
        Assert((await consumer.ContinueAsync("mixed")).Status == "GOVERNED_TRANSITION_REQUIRED", "Valid mixed execution failed");
        var replay = e with
        {
            OperationId = "no-write-check",
            CertificationId = Guid.NewGuid().ToString("D"),
            GovernedState = GovernedState(candidate with { SchemaHash = e.Observation!.SchemaHash })
        };
        f.Provider.Evidence["check"] = replay;
        Assert((await consumer.ContinueAsync("check")).Status == "NO_CHANGES_ALREADY_CERTIFIED", "Verified exact replay did not stop");
        // Same schema and package, different rows: never infer NO_CHANGES.
        f.Provider.Evidence["check"] = replay with { Observation = replay.Observation! with { DataHash = new string('0', 64) } };
        Assert((await consumer.ContinueAsync("check")).Status == "BLOCKED", "DATA drift concealed by equal schema");
        Assert(f.Store.ListCertifiedHistory(candidate.Identity).Count == 2, "No-change check wrote another baseline");
    }
    private static async Task Uncertainty()
    {
        var f = await Initial();
        var consumer = new LegacyCertificationContinuationV1(f.Provider, f.Store,
            new InMemoryCertificationCommitFaultInjector(CertificationCommitFaultPoint.AfterCasBeforeConfirmation));
        var failed = await consumer.ContinueAsync("initial");
        Assert(failed.Status == "RECONCILIATION_REQUIRED" && failed.Commit!.RecoveryRequired, "Lost acknowledgement concealed");
        var recovered = await consumer.ContinueAsync("initial");
        Assert(recovered.Status == "GOVERNED_TRANSITION_REQUIRED" && f.Store.ListCertifiedHistory(f.Evidence.Candidate.Identity).Count == 1, "Reconciliation duplicated baseline");
    }
    private static async Task PreparedConflict()
    {
        var f = await Initial();
        var interrupted = new LegacyCertificationContinuationV1(f.Provider, f.Store,
            new InMemoryCertificationCommitFaultInjector(CertificationCommitFaultPoint.AfterRecordPersistenceBeforeCas));
        var pending = await interrupted.ContinueAsync("initial");
        Assert(pending.Commit?.RecoveryRequired == true && f.Store.GetCurrentPointer(f.Evidence.Candidate.Identity) is null,
            "Prepared record was certified before CAS");
        var resumed = await new LegacyCertificationContinuationV1(f.Provider, f.Store).ContinueAsync("initial");
        Assert(resumed.Commit?.PointerAdvance == CommitOutcomeState.ConfirmedYes
            && f.Store.ListCertifiedHistory(f.Evidence.Candidate.Identity).Count == 1, "Prepared recovery duplicated certification");

        var conflict = await Initial();
        var delayed = new LegacyCertificationContinuationV1(conflict.Provider, conflict.Store,
            new InMemoryCertificationCommitFaultInjector(CertificationCommitFaultPoint.AfterRecordPersistenceBeforeCas));
        _ = await delayed.ContinueAsync("initial");
        conflict.Provider.Evidence["competitor"] = conflict.Evidence with
        {
            OperationId = "competing-adoption",
            CertificationId = Guid.NewGuid().ToString("D")
        };
        var live = new LegacyCertificationContinuationV1(conflict.Provider, conflict.Store);
        Assert((await live.ContinueAsync("competitor")).Commit?.PointerAdvance == CommitOutcomeState.ConfirmedYes, "Competing fixture did not advance");
        var lost = await live.ContinueAsync("initial");
        Assert(lost.Commit?.PointerAdvance != CommitOutcomeState.ConfirmedYes
            && conflict.Store.ListCertifiedHistory(conflict.Evidence.Candidate.Identity).Count == 1, "CAS loser overwrote baseline");
    }
    private static async Task Negative()
    {
        var f = await Initial(); var consumer = new LegacyCertificationContinuationV1(f.Provider, f.Store);
        foreach (var e in new[] { f.Evidence with { Review=f.Evidence.Review with {Reviewer="author"} },
            f.Evidence with { Review=f.Evidence.Review with {ExpiresAtUtc=DateTimeOffset.UtcNow.AddMinutes(-1)} },
            f.Evidence with { Review=f.Evidence.Review with {CandidateHash=new string('0',64)} },
            f.Evidence with { Review=f.Evidence.Review with {Incarnation=""} },
            f.Evidence with { Observation=null },
            f.Evidence with { Observation=f.Evidence.Observation! with {CapturedAtUtc=DateTimeOffset.UtcNow.AddHours(-1)} },
            f.Evidence with { Candidate=f.Evidence.Candidate with {Data="COMPLETE"} }
        }) { f.Provider.Evidence["bad"] = e; Assert((await consumer.ContinueAsync("bad")).Status == "BLOCKED", "Unverified review admitted"); }
        Assert(f.Store.PreparedRecordCount == 0, "Invalid evidence persisted");
    }
    private static Task Boundary()
    {
        try { LegacyCertificationContinuationV1.CreateProduction(); throw new Exception("Unbound corporate certification available"); }
        catch (LegacyContractException e) when (e.Code == "LEGACY_CERTIFICATION_AUTHORITY_AND_STORE_UNBOUND") { }
        return Task.CompletedTask;
    }
    private static DatabaseStateEvaluation GovernedState(LegacyAdoptionCandidateV1 c) =>
        new DatabaseStateEvaluator().Evaluate(DatabaseRegistryLoader.Validate(new DatabaseRegistryDocument
        {
            RegistryFormatVersion = 1,
            Targets = [new DatabaseTarget { ApplicationId=c.Identity.ApplicationId,
                Environment=c.Identity.Environment,DatabaseName=c.Identity.DatabaseName,Lifecycle="EXISTING",
                CertificationStatus="CERTIFIED",CertifiedSchemaHash=c.SchemaHash }]
        }, c.RegistryProvenance),
            new DatabaseStateObservation
            {
                ApplicationId = c.Identity.ApplicationId,
                Environment = c.Identity.Environment,
                DatabaseName = c.Identity.DatabaseName,
                ObservedSchemaHash = c.SchemaHash,
                SchemaCoverage = "COMPLETE",
                CaptureTimestampUtc = DateTimeOffset.UtcNow,
                RunId = "offline",
                RunAttempt = "1"
            });
}
