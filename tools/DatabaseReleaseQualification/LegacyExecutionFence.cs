namespace DatabaseReleaseQualification;

internal sealed record LegacyExecutionIdentityV1(string TargetId, string EndpointReference,
    string DatabaseName, string Incarnation, string ReleaseId, string PackageIdentity,
    string RunId, string Phase, string PredecessorHash,
    string? CanonicalResourceId = null, string? Repository = null);

internal sealed record LegacyExecutionReservationV1(LegacyExecutionIdentityV1 Identity,
    string Lease, string State, long Revision, string? ResultHash = null,
    string? ReconciliationEvidenceHash = null);

// Acquired through a trusted provider, never deserialized as CLI authority.
// ABORT_BEFORE_START requires proof that the original runner cannot resume.
// CONFIRM requires independently authenticated execution AND the complete
// checkpoint/receipt (including required DATA/SECURITY), not a schema hash.
internal sealed record LegacyExecutionReconciliationV1(string Lease, long ExpectedRevision,
    LegacyExecutionIdentityV1 Identity, string Resolution, string EvidenceHash,
    string Actor, string Reviewer, DateTimeOffset ExpiresAtUtc, bool RunnerQuiesced,
    bool ExecutionAndCoverageVerified, string? ResultHash);

internal interface ILegacyExecutionReconciliationSourceV1
{
    Task<LegacyExecutionReconciliationV1> AcquireAsync(string reference, CancellationToken token);
}

internal interface ILegacyExecutionFenceV1
{
    string Claim(LegacyExecutionIdentityV1 identity);
    void MarkStarted(string lease);
    void Confirm(string lease, string resultHash);
    void MarkUnknown(string lease);
    LegacyExecutionReservationV1 Read(string lease);
    void Reconcile(LegacyExecutionReconciliationV1 evidence);
}

internal sealed class LegacyExecutionReconcilerV1(ILegacyExecutionFenceV1 fence,
    ILegacyExecutionReconciliationSourceV1 source)
{
    internal async Task<LegacyExecutionReservationV1> ReconcileAsync(string reference, CancellationToken token = default)
    {
        var evidence = await source.AcquireAsync(reference, token);
        token.ThrowIfCancellationRequested();
        fence.Reconcile(evidence);
        return fence.Read(evidence.Lease);
    }
}

// A production binding must attest an atomic, durable claim spanning callers,
// repositories and runner restarts, and independent incarnation/freshness proof.
// Neither environment variables nor a caller's token can register that provider.
internal static class LegacyExecutionFence
{
    internal static LegacyExecutionFenceBindingV1 RequireProductionBinding() =>
        throw new LegacyContractException("DURABLE_EXECUTION_FENCE_REQUIRED");

    internal static LegacyExecutionLeaseV1 AcquireProtected(LegacyHandoffV1 h, LegacyRuntimeContextV1 context,
        string phase, string predecessor)
    {
        var binding = RequireProductionBinding();
        if (binding.Fence is InMemoryLegacyExecutionFence
            || string.IsNullOrWhiteSpace(binding.CanonicalResourceId)
            || !CertifiedStateRecordValidator.IsHash(binding.IncarnationEvidenceHash))
            throw new LegacyContractException("DURABLE_EXECUTION_FENCE_REQUIRED");
        return new(binding.Fence, new(h.TargetId, h.Governance.Binding.EndpointReference, h.Governance.Binding.DatabaseName,
            binding.Incarnation, h.ReleaseId, h.PackageIdentity, context.RunId, phase, predecessor,
            binding.CanonicalResourceId, context.Repository.FullName));
    }
}
internal sealed record LegacyExecutionFenceBindingV1(ILegacyExecutionFenceV1 Fence, string Incarnation,
    string CanonicalResourceId, string IncarnationEvidenceHash);

internal sealed class LegacyExecutionLeaseV1
{
    private readonly ILegacyExecutionFenceV1 fence;
    private readonly LegacyExecutionIdentityV1 identity;
    private readonly string lease;
    private int state;
    internal LegacyExecutionLeaseV1(ILegacyExecutionFenceV1 fence, LegacyExecutionIdentityV1 identity)
    { this.fence = fence; this.identity = identity; lease = fence.Claim(identity); }
    internal void Begin(LegacyHandoffV1 h, LegacyRuntimeContextV1 context, string phase)
    {
        if (h.TargetId != identity.TargetId || h.PackageIdentity != identity.PackageIdentity || h.ReleaseId != identity.ReleaseId
            || context.RunId != identity.RunId || phase != identity.Phase
            || h.Governance.Binding.EndpointReference != identity.EndpointReference
            || h.Governance.Binding.DatabaseName != identity.DatabaseName || Interlocked.CompareExchange(ref state, 1, 0) != 0)
            throw new LegacyContractException("EXECUTION_FENCE_LEASE_INVALID");
        fence.MarkStarted(lease);
    }
    internal void Confirm(string hash)
    {
        if (Volatile.Read(ref state) != 1) throw new LegacyContractException("EXECUTION_FENCE_LEASE_INVALID");
        fence.Confirm(lease, hash); Volatile.Write(ref state, 2);
    }
    internal void PreserveFailure()
    {
        if (Interlocked.CompareExchange(ref state, 3, 1) == 1) fence.MarkUnknown(lease);
        // A claimed but not started lease also remains reserved. Never unlock
        // automatically after a runner interruption or failed precondition.
    }
}

// Executable reference semantics for an approved future durable adapter. This
// volatile journal is ONLY for offline composition; it never binds the SQL host.
internal sealed class InMemoryLegacyExecutionFence : ILegacyExecutionFenceV1
{
    // Shared backing models clients disappearing/reconnecting to the same
    // journal. It is not durable storage and cannot be bound to production.
    internal sealed class Journal
    {
        internal readonly object Sync = new();
        internal readonly Dictionary<string, LegacyExecutionReservationV1> Current = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, LegacyExecutionReservationV1> Releases = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, LegacyExecutionReservationV1> Leases = new(StringComparer.Ordinal);
        internal readonly List<LegacyExecutionReservationV1> History = [];
    }
    private readonly Journal journal;
    internal InMemoryLegacyExecutionFence(Journal? journal = null) => this.journal = journal ?? new();
    private object sync => journal.Sync;
    private Dictionary<string, LegacyExecutionReservationV1> current => journal.Current;
    private Dictionary<string, LegacyExecutionReservationV1> releases => journal.Releases;
    private Dictionary<string, LegacyExecutionReservationV1> leases => journal.Leases;
    private static string Resource(LegacyExecutionIdentityV1 i) => i.CanonicalResourceId is { Length: > 0 } canonical
        ? LegacyReadinessHash.LengthPrefix("CANONICAL", canonical)
        : LegacyReadinessHash.LengthPrefix("OFFLINE_ENDPOINT", i.EndpointReference, i.DatabaseName);
    private static string Release(LegacyExecutionIdentityV1 i) => LegacyReadinessHash.LengthPrefix(Resource(i), i.ReleaseId);
    public string Claim(LegacyExecutionIdentityV1 identity)
    {
        lock (sync)
        {
            if (new[] {identity.TargetId,identity.EndpointReference,identity.DatabaseName,identity.Incarnation,
                identity.ReleaseId,identity.PackageIdentity,identity.RunId,identity.PredecessorHash}.Any(string.IsNullOrWhiteSpace)
                || !CertifiedStateRecordValidator.IsHash(identity.PredecessorHash)
                || identity.Phase is not ("FORWARD1" or "ROLLBACK" or "FORWARD2"))
                throw new LegacyContractException("EXECUTION_FENCE_IDENTITY_INVALID");
            var resource = Resource(identity); var key = Release(identity);
            releases.TryGetValue(key, out var previous);
            if (current.TryGetValue(resource, out var last) && last.Identity.Incarnation != identity.Incarnation)
                throw new LegacyContractException("EXECUTION_FENCE_INCARNATION_CHANGED");
            if (current.TryGetValue(resource, out var active)
                && active.State != "ABORTED_BEFORE_START"
                && (active.State != "CONFIRMED" || active.Identity.Phase != "FORWARD2")
                && (previous is null || active.Lease != previous.Lease))
                throw new LegacyContractException("TARGET_EXECUTION_ALREADY_CLAIMED");
            if (previous is null)
            {
                if (identity.Phase != "FORWARD1") throw new LegacyContractException("EXECUTION_FENCE_PHASE_ORDER");
            }
            else
            {
                if (identity.TargetId != previous.Identity.TargetId || identity.Incarnation != previous.Identity.Incarnation
                    || identity.PackageIdentity != previous.Identity.PackageIdentity || identity.RunId != previous.Identity.RunId
                    || identity.Repository != previous.Identity.Repository)
                    throw new LegacyContractException("EXECUTION_FENCE_REPLAY_OR_SUBSTITUTION");
                var expected = previous.Identity.Phase == "FORWARD1" ? "ROLLBACK" : previous.Identity.Phase == "ROLLBACK" ? "FORWARD2" : "COMPLETE";
                if (previous.State != "CONFIRMED" || identity.Phase != expected || identity.PredecessorHash != previous.ResultHash)
                    throw new LegacyContractException("EXECUTION_FENCE_PHASE_OR_OUTCOME_UNCERTAIN");
            }
            var lease = Guid.NewGuid().ToString("N");
            var entry = new LegacyExecutionReservationV1(identity, lease, "CLAIMED", 1);
            Save(entry); return lease;
        }
    }
    public void MarkStarted(string lease) => Change(lease, "CLAIMED", "STARTED", null);
    public void Confirm(string lease, string resultHash)
    {
        if (!CertifiedStateRecordValidator.IsHash(resultHash)) throw new LegacyContractException("EXECUTION_FENCE_RESULT_INVALID");
        Change(lease, "STARTED", "CONFIRMED", resultHash);
    }
    public void MarkUnknown(string lease) => Change(lease, "STARTED", "UNKNOWN", null);
    public LegacyExecutionReservationV1 Read(string lease)
    {
        lock (sync) return leases.TryGetValue(lease, out var entry) ? entry
            : throw new LegacyContractException("EXECUTION_FENCE_LEASE_INVALID");
    }
    internal IReadOnlyList<LegacyExecutionReservationV1> History()
    { lock (sync) return journal.History.ToArray(); }

    public void Reconcile(LegacyExecutionReconciliationV1 evidence)
    {
        lock (sync)
        {
            var entry = Read(evidence.Lease);
            var now = DateTimeOffset.UtcNow;
            if (entry.Identity != evidence.Identity || !CertifiedStateRecordValidator.IsHash(evidence.EvidenceHash)
                || string.IsNullOrWhiteSpace(evidence.Actor) || string.IsNullOrWhiteSpace(evidence.Reviewer)
                || evidence.Actor == evidence.Reviewer || evidence.ExpiresAtUtc <= now
                || evidence.ExpiresAtUtc > now.AddHours(1) || !evidence.RunnerQuiesced)
                throw new LegacyContractException("EXECUTION_RECONCILIATION_EVIDENCE_INVALID");
            // An acknowledgement lost after atomic reconciliation may be read
            // again. It never starts SQL and cannot change the prior decision.
            if (entry.ReconciliationEvidenceHash == evidence.EvidenceHash
                && entry.Revision == evidence.ExpectedRevision + 1
                && entry.ResultHash == evidence.ResultHash
                && (entry.State == "CONFIRMED" && evidence.Resolution == "CONFIRM"
                    || entry.State == "ABORTED_BEFORE_START" && evidence.Resolution == "ABORT_BEFORE_START")) return;
            if (entry.Revision != evidence.ExpectedRevision || current[Resource(entry.Identity)].Lease != entry.Lease)
                throw new LegacyContractException("EXECUTION_RECONCILIATION_CONFLICT");
            string state;
            if (evidence.Resolution == "ABORT_BEFORE_START" && entry.State == "CLAIMED" && evidence.ResultHash is null)
                state = "ABORTED_BEFORE_START";
            else if (evidence.Resolution == "CONFIRM" && entry.State is "STARTED" or "UNKNOWN"
                && evidence.ExecutionAndCoverageVerified && CertifiedStateRecordValidator.IsHash(evidence.ResultHash))
                state = "CONFIRMED";
            else throw new LegacyContractException("EXECUTION_OUTCOME_REMAINS_UNCERTAIN");
            Save(entry with
            {
                State = state,
                Revision = entry.Revision + 1,
                ResultHash = evidence.ResultHash,
                ReconciliationEvidenceHash = evidence.EvidenceHash
            });
        }
    }

    private void Save(LegacyExecutionReservationV1 entry)
    {
        leases[entry.Lease] = entry; current[Resource(entry.Identity)] = entry;
        releases[Release(entry.Identity)] = entry; journal.History.Add(entry);
    }
    private void Change(string lease, string expected, string state, string? hash)
    {
        lock (sync)
        {
            if (!leases.TryGetValue(lease, out var entry) || entry.State != expected
                || current[Resource(entry.Identity)].Lease != lease)
                throw new LegacyContractException("EXECUTION_FENCE_LEASE_INVALID");
            Save(entry with { State = state, ResultHash = hash, Revision = entry.Revision + 1 });
        }
    }
}
