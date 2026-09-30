using System.Text.Json;

namespace DatabaseReleaseQualification;

public enum LegacyRehearsalStateV1
{
    Qualified, PreCaptured, Forward1Applied, Post1Captured, RollbackApplied,
    Pre2Captured, Forward2Applied, Post2Captured, RehearsalEvaluated, Blocked,
    Failed, Cancelled, TimedOut
}

public sealed record LegacyPhaseReceiptV1(string Phase, string Status,
    DateTimeOffset StartedAtUtc, DateTimeOffset FinishedAtUtc, string? ScriptHash,
    string? EvidenceHash, string? ReasonCode);

// Authority is provided by a trusted composition root, never by deserializing a
// boolean supplied by the application. Implementations must bind all three writes.
public interface ILegacyRehearsalAuthorityV1
{
    Task VerifyAsync(LegacyHandoffV1 handoff, string phase, CancellationToken token);
}

// The runtime implementation rechecks governance and source freshness on every
// mutation. Its observation is compared to the last captured state by the harness.
public interface ILegacyRehearsalRuntimeV1 : IRecoverySecurityEvidenceProvider
{
    string EvidenceKind { get; }
    LegacyRuntimeContextV1 Context { get; }
    Task RevalidateAsync(LegacyHandoffV1 handoff, CancellationToken token);
    Task<ObservedCurrentSnapshotV1> CaptureAsync(CancellationToken token);
    Task VerifyRollbackAgainstPost1Async(ReleaseScript rollback,
        ObservedCurrentSnapshotV1 post1, CancellationToken token);
    Task ApplyExactAsync(ReleaseScript script, string expectedHash,
        string transactionPolicy, ObservedCurrentSnapshotV1 expectedPre, CancellationToken token);
}

public sealed record LegacyRehearsalReceiptV1(int ContractVersion, string Kind,
    string EvidenceKind, LegacyRuntimeContextV1 Runtime, string TargetId,
    string PackageIdentity, string QualificationIdentity, string EvidenceSetHash,
    LegacyArtifactValidV1 Artifacts, LegacyRecoveryHandoffV1 RecoveryRequirements,
    string BaselineIdentity, LegacyRehearsalStateV1 State, string Status,
    string RecoveryClass, bool CanProceedToPromotion,
    LegacyRehearsalAuthorizationEvidenceV1? Authorization,
    LegacyDataContractDefinitionV1? DataContract,
    IReadOnlyList<LegacyPhaseReceiptV1> Phases,
    IReadOnlyDictionary<RecoveryPhase, ObservedCurrentSnapshotV1> Observations,
    IReadOnlyDictionary<RecoveryPhase, LegacyDataPhaseEvidenceV1> DataEvidence,
    RehearsalResult? Evaluation, IReadOnlyList<string> ReasonCodes,
    DateTimeOffset StartedAtUtc, DateTimeOffset FinishedAtUtc, string ReceiptHash);

public static class LegacyRehearsalReceiptHash
{
    public static LegacyRehearsalReceiptV1 Bind(LegacyRehearsalReceiptV1 receipt) =>
        receipt with { ReceiptHash = LegacyRuntimeEvidenceHash.Hash(receipt with { ReceiptHash = "" }) };

    // Integrity is necessary but is not authenticity. Promotion consumers must
    // obtain receipts from their governed producer, never from application JSON.
    public static bool VerifyIntegrity(LegacyRehearsalReceiptV1 receipt) =>
        receipt.ContractVersion == 1 && receipt.Kind == "LEGACY_REHEARSAL_RECEIPT"
        && Bind(receipt).ReceiptHash == receipt.ReceiptHash;
}

public sealed class LegacyRehearsalStateMachineV1
{
    public LegacyRehearsalStateV1 State { get; private set; } = LegacyRehearsalStateV1.Qualified;
    private static readonly LegacyRehearsalStateV1[] Order = [
        LegacyRehearsalStateV1.Qualified, LegacyRehearsalStateV1.PreCaptured,
        LegacyRehearsalStateV1.Forward1Applied, LegacyRehearsalStateV1.Post1Captured,
        LegacyRehearsalStateV1.RollbackApplied, LegacyRehearsalStateV1.Pre2Captured,
        LegacyRehearsalStateV1.Forward2Applied, LegacyRehearsalStateV1.Post2Captured,
        LegacyRehearsalStateV1.RehearsalEvaluated];

    public void Advance(LegacyRehearsalStateV1 next)
    {
        var index = Array.IndexOf(Order, State);
        if (index < 0 || index + 1 >= Order.Length || Order[index + 1] != next)
            throw new LegacyContractException("REHEARSAL_PHASE_ORDER_INVALID");
        State = next;
    }

    public void Stop(LegacyRehearsalStateV1 terminal)
    {
        if (terminal is not (LegacyRehearsalStateV1.Blocked or LegacyRehearsalStateV1.Failed
            or LegacyRehearsalStateV1.Cancelled or LegacyRehearsalStateV1.TimedOut)
            || Array.IndexOf(Order, State) < 0 || State == LegacyRehearsalStateV1.RehearsalEvaluated)
            throw new LegacyContractException("REHEARSAL_PHASE_ORDER_INVALID");
        State = terminal;
    }
}

// Single-use. No resume, retry, recovery SQL or cleanup SQL is performed here.
// Synthetic and future real adapters use this same orchestration and engine.
public sealed class LegacyRehearsalHarnessV1(
    ILegacyRehearsalRuntimeV1 runtime, ILegacyRehearsalAuthorityV1 authority,
    Action<LegacyPhaseReceiptV1>? journal = null)
{
    private int started;

    public async Task<LegacyRehearsalReceiptV1> RunAsync(
        LegacyQualificationOutcomeV1 qualification, TimeSpan timeout,
        LegacyDataValidationDescriptorV1? data = null, CancellationToken token = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new LegacyContractException("REHEARSAL_RESUME_NOT_SUPPORTED");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(20))
            throw new LegacyContractException("REHEARSAL_TIMEOUT_INVALID");
        var readiness = qualification.Readiness
            ?? throw new LegacyContractException("QUALIFICATION_REQUIRED");
        var handoff = readiness.Handoff
            ?? throw new LegacyContractException("QUALIFICATION_REQUIRED");
        var package = qualification.Package
            ?? throw new LegacyContractException("QUALIFICATION_REQUIRED");
        var start = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var state = new LegacyRehearsalStateMachineV1();
        var bridge = new Bridge(runtime, authority, qualification, state, data?.Provider, journal);
        RehearsalResult? result = null;
        var reasons = new List<string>();
        try
        {
            VerifyQualification(qualification);
            if (runtime.EvidenceKind is not ("SYNTHETIC" or "REAL_TEST"))
                throw new LegacyContractException("EVIDENCE_KIND_INVALID");
            if (handoff.Recovery.Data == "REQUIRED"
                && (data is null || data.Provider is not IDataReapplyValidationContract
                    || data.Provider is not LegacyDataValidationProviderV1
                    || handoff.Recovery.DataValidator != new LegacyDataValidatorLinkV1(
                        data.ContractId, data.Version, data.ScopeHash)))
                throw new LegacyContractException("DATA_VALIDATOR_REQUIRED");
            await authority.VerifyAsync(handoff, "PRE", deadline.Token);
            await runtime.RevalidateAsync(handoff, deadline.Token);
            result = await new RehearsalEngine().QualifyAsync(new ReleaseDescriptor {
                ReleaseId = handoff.ReleaseId, Environment = "TEST", SourceKind = "SQL",
                Scenario = "EXISTING_LEGACY", DatabaseLifecycle = "EXISTING"
            }, new DiscoveryGate { ConsistencyStatus = "CONSISTENT", ConsistencyReason = "NONE" },
                new ReleaseScript("forward", package.ForwardBytes),
                new ReleaseScript("rollback", package.RollbackBytes), bridge,
                data?.Provider, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            bridge.CheckFrozenEvidence();
            if (bridge.Failure is not null) throw new LegacyContractException(bridge.Failure);
            if (result.QualificationStatus != "QUALIFIED" || result.RecoveryCoverage?.Complete != true
                || !result.ForwardCertified || !result.RollbackCertified || !result.ReapplyCertified)
                throw new LegacyContractException(result.QualificationStatus == "QUALIFIED"
                    ? "RECOVERY_COVERAGE_INCOMPLETE" : result.QualificationStatus);
            state.Advance(LegacyRehearsalStateV1.RehearsalEvaluated);
        }
        catch (OperationCanceledException)
        {
            state.Stop(token.IsCancellationRequested ? LegacyRehearsalStateV1.Cancelled
                : LegacyRehearsalStateV1.TimedOut);
            reasons.Add(token.IsCancellationRequested ? "REHEARSAL_CANCELLED" : "REHEARSAL_TIMEOUT");
        }
        catch (Exception exception)
        {
            state.Stop(exception is LegacyContractException { Code: "REHEARSAL_SQL_TIMEOUT" }
                ? LegacyRehearsalStateV1.TimedOut : bridge.MutationStarted ? LegacyRehearsalStateV1.Failed : LegacyRehearsalStateV1.Blocked);
            reasons.Add(exception is LegacyContractException contract ? contract.Code : "REHEARSAL_TECHNICAL_ERROR");
        }
        var complete = state.State == LegacyRehearsalStateV1.RehearsalEvaluated;
        // A hash cannot authenticate a receipt. This is evidence for a later
        // governed freeze consumer; it does not itself authorize promotion.
        var receipt = LegacyRehearsalReceiptHash.Bind(new(1, "LEGACY_REHEARSAL_RECEIPT",
            runtime.EvidenceKind, runtime.Context, handoff.TargetId, handoff.PackageIdentity,
            readiness.EvidenceHash, handoff.EvidenceSetHash, package.Evidence, handoff.Recovery,
            handoff.Baseline.BaselineIdentity, state.State,
            complete ? "REHEARSAL_COMPLETE" : "REHEARSAL_BLOCKED",
            complete ? "FULL_REVERSIBLE" : FailureClass(result), false,
            (authority as GovernedLegacyRehearsalAuthorityV1)?.Evidence,
            (data?.Provider as LegacyDataValidationProviderV1)?.Definition,
            bridge.Phases.ToArray(), new Dictionary<RecoveryPhase, ObservedCurrentSnapshotV1>(bridge.Observations),
            data?.Provider is LegacyDataValidationProviderV1 provider ? provider.Evidence
                : new Dictionary<RecoveryPhase, LegacyDataPhaseEvidenceV1>(),
            result, reasons.ToArray(), start, DateTimeOffset.UtcNow, ""));
        if (complete && runtime is SqlLegacyRehearsalRuntimeV1 && authority is GovernedLegacyRehearsalAuthorityV1)
            LegacyPromotionFreeze.RegisterProducedReceipt(receipt);
        return receipt;
    }

    private static string FailureClass(RehearsalResult? result) => result?.RollbackCapability switch {
        RollbackCapability.RestoreRequired => "RESTORE_REQUIRED",
        RollbackCapability.SchemaOnly when result.RecoveryCoverage?.Security.Required == false => "SCHEMA_ONLY",
        RollbackCapability.ForwardFixOnly => "FORWARD_FIX_ONLY",
        _ => "UNKNOWN"
    };

    internal static void VerifyQualification(LegacyQualificationOutcomeV1 q)
    {
        if (q.Readiness is not { Status: "READY_FOR_TEST_REHEARSAL",
                QualificationStatus: "ANALYZED_NOT_REHEARSED", CanProceedToPromotion: false,
                Handoff: { ExecutionAuthorized: false } h } r
            || q.Package is null || q.StaticSafety is not { State: "PASS" } s
            || q.TrustedRuntime is null || !LegacyRuntimeEvidenceHash.Verify(q.TrustedRuntime)
            || q.CertifiedBaseline is null || q.ObservedSnapshot is null
            || r.ContractVersion != 1 || r.Scenario != "EXISTING_LEGACY"
            || r.TargetId != h.TargetId || r.PackageIdentity != h.PackageIdentity
            || r.ReasonCodes.Count != 0 || h.ContractVersion != 1 || s.ContractVersion != 1
            || !s.Impact.Complete
            || r.Checks != new LegacyReadinessChecksV1("PASS","PASS","PASS","PASS","PASS")
            || LegacyReadinessHash.Bind(r).EvidenceHash != r.EvidenceHash
            || h.EvidenceSetHash != q.TrustedRuntime.EvidenceSetHash
            || h.Governance.Environment != "TEST" || h.Onboarding.Status != "MANAGED"
            || h.TransactionPolicy != "HARNESS_OWNS_PHASE_TRANSACTIONS_V1"
            || h.PackageIdentity != q.Package.Evidence.PackageIdentity
            || LegacyRuntimeEvidenceHash.Hash(h.Artifacts) != LegacyRuntimeEvidenceHash.Hash(q.Package.Evidence)
            || s.EvidenceHash != LegacyStaticSafety.CalculateHash(s)
            || h.StaticSafety.EvidenceHash != s.EvidenceHash
            || h.TargetId != q.Package.Evidence.TargetId
            || h.ReleaseId != q.Package.Evidence.ReleaseId
            || h.Recovery.Structure != "REQUIRED"
            || h.Recovery.Data != (s.Impact.DataRequired ? "REQUIRED" : "NOT_REQUIRED")
            || h.Recovery.Security != (s.Impact.SecurityRequired ? "REQUIRED" : "NOT_REQUIRED")
            || h.Recovery.SecurityScopeHash != s.Impact.SecurityScope.Sha256)
            throw new LegacyContractException("REQUALIFICATION_REQUIRED");
        q.Package.Verify();
        var trusted = q.TrustedRuntime.Evidence!;
        LegacyStructuralEvidence.Verify(q.CertifiedBaseline,q.ObservedSnapshot,trusted.Governance,trusted.DatabaseStateEvaluation);
        if (LegacyRuntimeEvidenceHash.Hash(trusted.Artifact) != LegacyRuntimeEvidenceHash.Hash(q.Package.Evidence)
            || LegacyRuntimeEvidenceHash.Hash(trusted.ObservedSnapshot) != LegacyRuntimeEvidenceHash.Hash(q.ObservedSnapshot)
            || LegacyRuntimeEvidenceHash.Hash(trusted.CertifiedStructuralBaseline) != LegacyRuntimeEvidenceHash.Hash(q.CertifiedBaseline)
            || q.BaselineEvidenceHash != Hashing.Sha256(LegacyStructuralEvidence.Serialize(q.CertifiedBaseline))
            || q.ObservedEvidenceHash != Hashing.Sha256(LegacyStructuralEvidence.Serialize(q.ObservedSnapshot))
            || h.Baseline.CertifiedSchemaHash != q.CertifiedBaseline.CertifiedSchemaHash
            || h.ObservedSchemaHash != q.ObservedSnapshot.ObservedSchemaHash
            || h.Governance.SourceProvenance != trusted.Governance.SourceProvenance
            || LegacyRuntimeEvidenceHash.Hash(h.Governance.Binding) != LegacyRuntimeEvidenceHash.Hash(trusted.Governance.Binding)
            || h.Onboarding != trusted.Onboarding
            || s.PackageIdentity != q.Package.Evidence.PackageIdentity
            || s.ForwardHash != q.Package.Evidence.Forward.Sha256 || s.RollbackHash != q.Package.Evidence.Rollback.Sha256)
            throw new LegacyContractException("REQUALIFICATION_REQUIRED");
    }

    private sealed class Bridge(ILegacyRehearsalRuntimeV1 runtime,
        ILegacyRehearsalAuthorityV1 authority, LegacyQualificationOutcomeV1 qualification,
        LegacyRehearsalStateMachineV1 state, IDataRollbackValidationContract? data,
        Action<LegacyPhaseReceiptV1>? journal) : IRehearsalDatabase, IRecoverySecurityEvidenceProvider
    {
        private readonly LegacyHandoffV1 handoff = qualification.Readiness!.Handoff!;
        private readonly string qualificationHash = LegacyRuntimeEvidenceHash.Hash(qualification);
        private readonly Dictionary<RecoveryPhase, string> frozenHashes = [];
        private int captureIndex, executionIndex;
        public bool MutationStarted { get; private set; }
        public string? Failure { get; private set; }
        public List<LegacyPhaseReceiptV1> Phases { get; } = [];
        public Dictionary<RecoveryPhase, ObservedCurrentSnapshotV1> Observations { get; } = [];

        public void CheckFrozenEvidence()
        {
            VerifyQualification(qualification);
            if (LegacyRuntimeEvidenceHash.Hash(qualification) != qualificationHash
                || frozenHashes.Any(x => LegacyRuntimeEvidenceHash.Hash(Observations[x.Key]) != x.Value))
                throw new LegacyContractException("REHEARSAL_EVIDENCE_MUTATED");
        }

        public async Task<SchemaSnapshot> CaptureSchemaAsync(CancellationToken cancellationToken = default)
        {
            var start = DateTimeOffset.UtcNow;
            try { return await CaptureSchemaCoreAsync(cancellationToken); }
            catch (Exception exception)
            {
                var phase = captureIndex < 4 ? Enum.GetValues<RecoveryPhase>()[captureIndex].ToString().ToUpperInvariant() : "INVALID";
                var reason = exception is LegacyContractException contract ? contract.Code : "PHASE_CAPTURE_FAILED";
                var receipt = new LegacyPhaseReceiptV1(phase, "FAILED", start, DateTimeOffset.UtcNow, null, null, reason);
                Phases.Add(receipt);
                journal?.Invoke(receipt);
                throw;
            }
        }

        private async Task<SchemaSnapshot> CaptureSchemaCoreAsync(CancellationToken cancellationToken)
        {
            CheckFrozenEvidence();
            var phase = captureIndex < 4 ? Enum.GetValues<RecoveryPhase>()[captureIndex]
                : throw new LegacyContractException("REHEARSAL_PHASE_ORDER_INVALID");
            var start = DateTimeOffset.UtcNow;
            var observed = await runtime.CaptureAsync(cancellationToken);
            ValidateObservation(observed, start);
            if (phase == RecoveryPhase.Pre && observed.ObservedSchemaHash != handoff.Baseline.CertifiedSchemaHash)
                throw new LegacyContractException("DRIFT_BEFORE_PRE");
            // Own a deep copy: adapter-side mutation cannot rewrite prior evidence.
            var owned = JsonSerializer.Deserialize<ObservedCurrentSnapshotV1>(
                JsonSerializer.Serialize(observed, JsonDefaults.Compact), JsonDefaults.Compact)!;
            Observations.Add(phase, owned);
            frozenHashes.Add(phase, LegacyRuntimeEvidenceHash.Hash(owned));
            state.Advance(phase switch {
                RecoveryPhase.Pre => LegacyRehearsalStateV1.PreCaptured,
                RecoveryPhase.Post1 => LegacyRehearsalStateV1.Post1Captured,
                RecoveryPhase.Pre2 => LegacyRehearsalStateV1.Pre2Captured,
                _ => LegacyRehearsalStateV1.Post2Captured });
            var receipt = new LegacyPhaseReceiptV1(phase.ToString().ToUpperInvariant(), "CAPTURED", start,
                DateTimeOffset.UtcNow, null, frozenHashes[phase], null);
            Phases.Add(receipt);
            journal?.Invoke(receipt);
            captureIndex++;
            return owned.Snapshot;
        }

        public async Task ExecuteSqlAsync(ReleaseScript script, string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            var start = DateTimeOffset.UtcNow;
            var phase = executionIndex switch { 0 => "FORWARD1", 1 => "ROLLBACK", 2 => "FORWARD2", _ => "INVALID" };
            try
            {
                CheckFrozenEvidence();
                var expectedState = executionIndex switch {
                    0 => LegacyRehearsalStateV1.PreCaptured,
                    1 => LegacyRehearsalStateV1.Post1Captured,
                    2 => LegacyRehearsalStateV1.Pre2Captured,
                    _ => throw new LegacyContractException("REHEARSAL_PHASE_ORDER_INVALID") };
                if (state.State != expectedState)
                    throw new LegacyContractException("REHEARSAL_PHASE_ORDER_INVALID");
                var expected = executionIndex == 1 ? handoff.Artifacts.Rollback.Sha256 : handoff.Artifacts.Forward.Sha256;
                if (expectedSha256 != expected || script.Sha256 != expected)
                    throw new LegacyContractException("REQUALIFICATION_REQUIRED");
                cancellationToken.ThrowIfCancellationRequested();
                await authority.VerifyAsync(handoff, phase, cancellationToken);
                await runtime.RevalidateAsync(handoff, cancellationToken);
                var observation = await runtime.CaptureAsync(cancellationToken);
                ValidateObservation(observation, start);
                var last = Observations[Enum.GetValues<RecoveryPhase>()[executionIndex]];
                if (observation.ObservedSchemaHash != last.ObservedSchemaHash
                    || observation.ObservedIdentity != last.ObservedIdentity)
                    throw new LegacyContractException("DRIFT_BEFORE_MUTATION");
                var lastPhase = Enum.GetValues<RecoveryPhase>()[executionIndex];
                if (handoff.Recovery.Security == "REQUIRED")
                {
                    var security = await runtime.CaptureSecurityAsync(handoff.Recovery.SecurityScope, lastPhase, cancellationToken);
                    var canonical = RecoverySecurityCanonicalizer.Canonicalize(handoff.Recovery.SecurityScope, lastPhase, security);
                    if (canonical is null || !securityHashes.TryGetValue(lastPhase, out var previous) || canonical.Sha256 != previous)
                        throw new LegacyContractException("SECURITY_DRIFT_BEFORE_MUTATION");
                }
                if (data is LegacyDataValidationProviderV1 selected)
                    await selected.VerifyUnchangedAsync(lastPhase, cancellationToken);
                CheckFrozenEvidence();
                cancellationToken.ThrowIfCancellationRequested();
                MutationStarted = true;
                journal?.Invoke(new(phase, "STARTED", start, start, expected, null, null));
                await runtime.ApplyExactAsync(script, expected, handoff.TransactionPolicy, observation, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                state.Advance(executionIndex switch {
                    0 => LegacyRehearsalStateV1.Forward1Applied,
                    1 => LegacyRehearsalStateV1.RollbackApplied,
                    _ => LegacyRehearsalStateV1.Forward2Applied });
                executionIndex++;
                var receipt = new LegacyPhaseReceiptV1(phase, "APPLIED", start, DateTimeOffset.UtcNow, expected, null, null);
                Phases.Add(receipt);
                journal?.Invoke(receipt);
            }
            catch (Exception exception)
            {
                Failure = exception is LegacyContractException contract ? contract.Code
                    : exception is OperationCanceledException ? "REHEARSAL_CANCELLED" : "REHEARSAL_EXECUTION_FAILED";
                var receipt = new LegacyPhaseReceiptV1(phase, "FAILED", start, DateTimeOffset.UtcNow, expectedSha256, null, Failure);
                Phases.Add(receipt);
                journal?.Invoke(receipt);
                throw;
            }
        }

        private void ValidateObservation(ObservedCurrentSnapshotV1 observation, DateTimeOffset start)
        {
            var h = handoff;
            if (observation.ContractVersion != 1 || observation.Kind != "OBSERVED_CURRENT_SNAPSHOT"
                || observation.TargetId != h.TargetId || observation.Environment != "TEST"
                || observation.EndpointReference != h.Governance.Binding.EndpointReference
                || observation.ObservedIdentity.DatabaseName != h.Governance.Binding.DatabaseName
                || h.Governance.Binding.ServerMatchPolicy == "ALLOW_LIST"
                    && !h.Governance.Binding.AllowedServerInstances.Contains(observation.ObservedIdentity.ServerInstance, StringComparer.OrdinalIgnoreCase))
                throw new LegacyContractException("TARGET_IDENTITY_MISMATCH");
            if (observation.CapturedAtUtc < start || observation.CapturedAtUtc > DateTimeOffset.UtcNow
                || !observation.HashContract.IsSupported
                || observation.HashContract != qualification.ObservedSnapshot!.HashContract)
                throw new LegacyContractException("STALE_TRUSTED_EVIDENCE");
            var hash = observation.HashContract.Hash(observation.Snapshot);
            if (observation.Metadata != new ObservedSnapshotMetadataV1("SUFFICIENT", "COMPLETE", "COMPLETE")
                || observation.Snapshot.SchemaCoverage != SchemaCoverage.Complete
                || hash != observation.ObservedSchemaHash || !observation.CaptureComparison.StructuralMatch
                || !observation.CaptureComparison.IdentityMatch
                || observation.CaptureComparison.FirstSchemaHash != hash
                || observation.CaptureComparison.SecondSchemaHash != hash
                || observation.CaptureComparison.FirstIdentity != observation.ObservedIdentity
                || observation.CaptureComparison.SecondIdentity != observation.ObservedIdentity)
                throw new LegacyContractException("PHASE_EVIDENCE_INCOMPLETE");
        }

        private readonly Dictionary<RecoveryPhase, string> securityHashes = [];
        public async Task<RecoverySecuritySnapshot> CaptureSecurityAsync(RecoverySecurityScope scope,
            RecoveryPhase phase, CancellationToken cancellationToken = default)
        {
            CheckFrozenEvidence();
            if (scope.Sha256 != handoff.Recovery.SecurityScopeHash || !Observations.ContainsKey(phase))
                throw new LegacyContractException("SECURITY_SCOPE_MISMATCH");
            var snapshot = await runtime.CaptureSecurityAsync(scope, phase, cancellationToken);
            if (snapshot.DatabaseName != Observations[phase].ObservedIdentity.DatabaseName
                || snapshot.ServerInstance != Observations[phase].ObservedIdentity.ServerInstance)
                throw new LegacyContractException("SECURITY_IDENTITY_MISMATCH");
            var canonical = RecoverySecurityCanonicalizer.Canonicalize(scope, phase, snapshot);
            if (canonical is null) throw new LegacyContractException("SECURITY_EVIDENCE_INCOMPLETE");
            securityHashes.Add(phase, canonical.Sha256);
            return snapshot;
        }
    }
}
