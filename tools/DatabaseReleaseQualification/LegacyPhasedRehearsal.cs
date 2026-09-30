using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record LegacyPhasedQualificationV1(
    LegacyReadinessV1 Readiness, LegacyStaticSafetyEvidenceV1 StaticSafety,
    CertifiedStructuralBaselineV1 CertifiedBaseline, ObservedCurrentSnapshotV1 InitialObservation,
    string BaselineEvidenceHash, string ObservedEvidenceHash,
    TrustedLegacyRuntimeEvidenceV1 TrustedRuntime);

public sealed record LegacyRehearsalCheckpointV1(
    int ContractVersion, string Kind, LegacyRuntimeContextV1 Runtime,
    string RehearsalIdentity, string TargetId, string PackageIdentity,
    string QualificationIdentity, string EvidenceSetHash,
    string ManifestHash, string ForwardHash, string RollbackHash,
    string BaselineIdentity, string PreconditionIdentity,
    string CompletedPhase, string ExpectedNextPhase, string PhaseResult,
    LegacyPhasedQualificationV1 Qualification,
    LegacyDataContractDefinitionV1? DataContract,
    IReadOnlyList<LegacyPhaseReceiptV1> Phases,
    IReadOnlyDictionary<RecoveryPhase, ObservedCurrentSnapshotV1> Observations,
    IReadOnlyDictionary<RecoveryPhase, LegacyDataPhaseEvidenceV1> DataEvidence,
    IReadOnlyDictionary<RecoveryPhase, CanonicalRecoverySecurity> SecurityEvidence,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    string PreviousCheckpointHash, string CheckpointHash,
    IReadOnlyList<string> ReasonCodes, bool Terminal);

public static class LegacyPhaseCheckpoint
{
    private static readonly Regex Hash = new(@"\A[0-9a-f]{64}\z");
    private static readonly IReadOnlyDictionary<string, (string Next, RecoveryPhase Observation, string Job)> Steps =
        new Dictionary<string, (string, RecoveryPhase, string)>(StringComparer.Ordinal) {
            ["PRE"] = ("FORWARD1", RecoveryPhase.Pre, "legacy_pre"),
            ["POST1"] = ("ROLLBACK", RecoveryPhase.Post1, "legacy_forward1"),
            ["PRE2"] = ("FORWARD2", RecoveryPhase.Pre2, "legacy_rollback")
        };

    public static LegacyRehearsalCheckpointV1 Bind(LegacyRehearsalCheckpointV1 value) =>
        value with { CheckpointHash = LegacyRuntimeEvidenceHash.Hash(value with { CheckpointHash = "" }) };

    public static void Verify(LegacyRehearsalCheckpointV1 value, string expectedHash,
        string phase, LegacyRuntimeContextV1 current, LegacyFrozenPackage package)
    {
        if (!Steps.TryGetValue(value.CompletedPhase, out var predecessor)
            || predecessor.Next != phase || value.ExpectedNextPhase != phase
            || !Hash.IsMatch(expectedHash) || value.CheckpointHash != expectedHash
            || Bind(value).CheckpointHash != expectedHash
            || value.ContractVersion != 1 || value.Kind != "LEGACY_PHASE_CHECKPOINT"
            || value.PhaseResult != "COMPLETE" || value.Terminal || value.ReasonCodes.Count != 0
            || value.Runtime.JobId != predecessor.Job
            || value.Runtime.RunId != current.RunId || current.RunAttempt != "1"
            || value.Runtime.RunAttempt != current.RunAttempt
            || value.Runtime.Repository != current.Repository
            || value.Runtime.WorkflowPath != current.WorkflowPath
            || value.Runtime.WorkflowRevision != current.WorkflowRevision
            || value.TargetId != package.Evidence.TargetId
            || value.PackageIdentity != package.Evidence.PackageIdentity
            || value.ManifestHash != package.Evidence.Manifest.Sha256
            || value.ForwardHash != package.Evidence.Forward.Sha256
            || value.RollbackHash != package.Evidence.Rollback.Sha256
            || value.QualificationIdentity != value.Qualification.Readiness.EvidenceHash
            || value.EvidenceSetHash != value.Qualification.TrustedRuntime.EvidenceSetHash
            || value.BaselineIdentity != value.Qualification.Readiness.Handoff?.Baseline.BaselineIdentity
            || value.PreconditionIdentity != GovernedLegacyRehearsalAuthorityV1.PreconditionIdentity(
                value.Qualification.Readiness.Handoff!)
            || value.RehearsalIdentity != Identity(value.Runtime, value.TargetId, value.PackageIdentity)
            || value.StartedAtUtc > value.CompletedAtUtc
            || value.CompletedAtUtc > DateTimeOffset.UtcNow
            || DateTimeOffset.UtcNow - value.CompletedAtUtc > TimeSpan.FromHours(24)
            || value.Phases.Count != (value.CompletedPhase == "PRE" ? 1 :
                value.CompletedPhase == "POST1" ? 3 : 5)
            || value.Observations.Count != (value.CompletedPhase == "PRE" ? 1 :
                value.CompletedPhase == "POST1" ? 2 : 3)
            || !value.Observations.ContainsKey(predecessor.Observation)
            || value.Phases[^1].Phase != value.CompletedPhase
            || !value.Phases.Select(x => x.Phase).SequenceEqual(
                new[] { "PRE", "FORWARD1", "POST1", "ROLLBACK", "PRE2" }
                    .Take(value.Phases.Count))
            || value.Phases.Any(x => x.Status is not ("CAPTURED" or "APPLIED"))
            || value.CompletedPhase == "PRE" && value.PreviousCheckpointHash != ""
            || value.CompletedPhase != "PRE" && !Hash.IsMatch(value.PreviousCheckpointHash))
            throw new LegacyContractException("PHASE_CHECKPOINT_INVALID");
        package.Verify();
        if (value.Phases.Where(x => x.Phase is "FORWARD1" or "ROLLBACK")
            .Any(x => x.ScriptHash != (x.Phase == "ROLLBACK" ? value.RollbackHash : value.ForwardHash)))
            throw new LegacyContractException("PHASE_CHECKPOINT_INVALID");
        var q = Rehydrate(value, package);
        LegacyRehearsalHarnessV1.VerifyQualification(q);
        var count = value.Observations.Count;
        for (var i = 0; i < count; i++)
        {
            var key = Enum.GetValues<RecoveryPhase>()[i];
            if (!value.Observations.TryGetValue(key, out var observation)
                || observation.TargetId != value.TargetId
                || observation.Environment != "TEST"
                || observation.EndpointReference != q.Readiness!.Handoff!.Governance.Binding.EndpointReference
                || observation.ObservedIdentity.DatabaseName
                    != q.Readiness.Handoff.Governance.Binding.DatabaseName
                || q.Readiness.Handoff.Governance.Binding.ServerMatchPolicy == "ALLOW_LIST"
                    && !q.Readiness.Handoff.Governance.Binding.AllowedServerInstances.Contains(
                        observation.ObservedIdentity.ServerInstance, StringComparer.OrdinalIgnoreCase)
                || observation.Metadata != new ObservedSnapshotMetadataV1("SUFFICIENT", "COMPLETE", "COMPLETE")
                || observation.ObservedSchemaHash != observation.HashContract.Hash(observation.Snapshot)
                || !observation.CaptureComparison.IdentityMatch
                || !observation.CaptureComparison.StructuralMatch
                || value.Phases[i * 2].EvidenceHash != LegacyRuntimeEvidenceHash.Hash(observation))
                throw new LegacyContractException("PHASE_CHECKPOINT_EVIDENCE_INVALID");
        }
        if (q.Readiness!.Handoff!.Recovery.Data == "REQUIRED")
        {
            if (value.DataContract is null || value.DataEvidence.Count != count)
                throw new LegacyContractException("DATA_EVIDENCE_INCOMPLETE");
            for (var i = 0; i < count; i++)
                if (!value.DataEvidence.TryGetValue(Enum.GetValues<RecoveryPhase>()[i], out var item)
                    || !item.Complete || !Hash.IsMatch(item.ContentHash)
                    || item.ContractVersion != 1 || item.Phase != Enum.GetValues<RecoveryPhase>()[i]
                    || item.TargetId != value.TargetId || item.Selector != value.DataContract.Selector
                    || item.Version != value.DataContract.Version
                    || item.ScopeHash != value.DataContract.ScopeHash)
                    throw new LegacyContractException("DATA_EVIDENCE_INCOMPLETE");
        }
        else if (value.DataEvidence.Count != 0 || value.DataContract is not null)
            throw new LegacyContractException("DATA_EVIDENCE_INCOMPLETE");
        if (q.Readiness.Handoff.Recovery.Security == "REQUIRED")
        {
            if (value.SecurityEvidence.Count != count
                || Enumerable.Range(0, count).Any(i =>
                    !value.SecurityEvidence.ContainsKey(Enum.GetValues<RecoveryPhase>()[i]))
                || value.SecurityEvidence.Values.Any(x => !Hash.IsMatch(x.Sha256)
                    || Hashing.Sha256(x.Json) != x.Sha256))
                throw new LegacyContractException("SECURITY_EVIDENCE_INCOMPLETE");
        }
        else if (value.SecurityEvidence.Count != 0)
            throw new LegacyContractException("SECURITY_EVIDENCE_INCOMPLETE");
    }

    public static LegacyQualificationOutcomeV1 Rehydrate(LegacyRehearsalCheckpointV1 checkpoint,
        LegacyFrozenPackage package)
    {
        var state = checkpoint.Qualification;
        return new(state.Readiness, null, null, package, state.StaticSafety, null,
            state.CertifiedBaseline, state.InitialObservation, state.BaselineEvidenceHash,
            state.ObservedEvidenceHash, state.TrustedRuntime);
    }

    public static string Identity(LegacyRuntimeContextV1 context, string targetId, string packageIdentity) =>
        LegacyReadinessHash.LengthPrefix(context.Repository.FullName, context.RunId,
            context.RunAttempt, context.WorkflowRevision, targetId, packageIdentity);
}

public sealed class ProtectedEnvironmentLegacyAuthorityV1(string phase,
    LegacyRuntimeContextV1 runtime) : ILegacyRehearsalAuthorityV1
{
    public Task VerifyAsync(LegacyHandoffV1 handoff, string requested, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var job = phase switch {
            "FORWARD1" => "legacy_forward1", "ROLLBACK" => "legacy_rollback",
            "FORWARD2" => "legacy_forward2", "PRE" => "legacy_pre", _ => ""
        };
        if (requested != phase || runtime.JobId != job
            || Environment.GetEnvironmentVariable("GITHUB_JOB") != job
            || Environment.GetEnvironmentVariable("GITHUB_RUN_ID") != runtime.RunId
            || Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") != "1"
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || handoff.Governance.Environment != "TEST"
            || phase != "PRE" && Environment.GetEnvironmentVariable("LEGACY_PROTECTED_ENVIRONMENT")
                != "legacy-rehearsal-test")
            throw new LegacyContractException("PHASE_JOB_AUTHORITY_INVALID");
        return Task.CompletedTask;
    }
}

// One instance performs at most one mutation. Its checkpoint can only be consumed
// by the exact successor job in the same governed run.
public sealed class LegacyPhasedRehearsalV1(
    ILegacyRehearsalRuntimeV1 runtime, ILegacyRehearsalAuthorityV1 authority,
    Action<string>? mutationJournal = null)
{
    private int started;

    public async Task<LegacyRehearsalCheckpointV1> CapturePreAsync(
        LegacyQualificationOutcomeV1 qualification, LegacyDataValidationProviderV1? data,
        CancellationToken token)
    {
        Enter();
        LegacyRehearsalHarnessV1.VerifyQualification(qualification);
        var h = qualification.Readiness!.Handoff!;
        var package = qualification.Package!;
        RequireData(h, data);
        await authority.VerifyAsync(h, "PRE", token);
        await runtime.RevalidateAsync(h, token);
        var observation = await CaptureAsync(h, qualification.ObservedSnapshot!.HashContract, token);
        if (observation.ObservedSchemaHash != h.Baseline.CertifiedSchemaHash)
            throw new LegacyContractException("DRIFT_BEFORE_PRE");
        if (data is not null) await data.CapturePreDataAsync(token);
        var security = new Dictionary<RecoveryPhase, CanonicalRecoverySecurity>();
        await CaptureSecurity(h, observation, RecoveryPhase.Pre, security, token);
        var now = DateTimeOffset.UtcNow;
        var observations = new Dictionary<RecoveryPhase, ObservedCurrentSnapshotV1> {
            [RecoveryPhase.Pre] = observation
        };
        var phases = new[] { Captured("PRE", observation, now) };
        var state = new LegacyPhasedQualificationV1(qualification.Readiness,
            qualification.StaticSafety!, qualification.CertifiedBaseline!,
            qualification.ObservedSnapshot, qualification.BaselineEvidenceHash!,
            qualification.ObservedEvidenceHash!, qualification.TrustedRuntime!);
        return LegacyPhaseCheckpoint.Bind(new(1, "LEGACY_PHASE_CHECKPOINT",
            runtime.Context, LegacyPhaseCheckpoint.Identity(runtime.Context, h.TargetId, h.PackageIdentity),
            h.TargetId, h.PackageIdentity, qualification.Readiness.EvidenceHash,
            h.EvidenceSetHash, package.Evidence.Manifest.Sha256, package.Evidence.Forward.Sha256,
            package.Evidence.Rollback.Sha256, h.Baseline.BaselineIdentity,
            GovernedLegacyRehearsalAuthorityV1.PreconditionIdentity(h),
            "PRE", "FORWARD1", "COMPLETE", state, data?.Definition, phases,
            observations, data?.Evidence ?? new Dictionary<RecoveryPhase, LegacyDataPhaseEvidenceV1>(),
            security, now, DateTimeOffset.UtcNow, "", "", [], false));
    }

    public async Task<(LegacyRehearsalCheckpointV1? Checkpoint, LegacyRehearsalReceiptV1? Receipt)>
        RunNextAsync(LegacyRehearsalCheckpointV1 previous, string trustedPreviousHash,
            string phase, LegacyFrozenPackage package, LegacyDataValidationProviderV1? data,
            CancellationToken token)
    {
        Enter();
        LegacyPhaseCheckpoint.Verify(previous, trustedPreviousHash, phase, runtime.Context, package);
        var qualification = LegacyPhaseCheckpoint.Rehydrate(previous, package);
        var h = qualification.Readiness!.Handoff!;
        RequireData(h, data);
        if (data is not null)
        {
            if (LegacyRuntimeEvidenceHash.Hash(previous.DataContract)
                != LegacyRuntimeEvidenceHash.Hash(data.Definition))
                throw new LegacyContractException("DATA_CONTRACT_MUTATED");
            data.RestoreVerifiedEvidence(previous.DataEvidence);
        }
        await authority.VerifyAsync(h, phase, token);
        await runtime.RevalidateAsync(h, token);
        var preceding = phase switch {
            "FORWARD1" => RecoveryPhase.Pre, "ROLLBACK" => RecoveryPhase.Post1,
            "FORWARD2" => RecoveryPhase.Pre2,
            _ => throw new LegacyContractException("REHEARSAL_PHASE_ORDER_INVALID")
        };
        var last = previous.Observations[preceding];
        var current = await CaptureAsync(h, last.HashContract, token);
        if (current.ObservedIdentity != last.ObservedIdentity
            || current.ObservedSchemaHash != last.ObservedSchemaHash
            || current.Metadata != last.Metadata
            || LegacyRuntimeEvidenceHash.Hash(current.Snapshot.ImpactMetrics)
                != LegacyRuntimeEvidenceHash.Hash(last.Snapshot.ImpactMetrics))
            throw new LegacyContractException("DRIFT_BEFORE_MUTATION");
        if (data is not null) await data.VerifyUnchangedAsync(preceding, token);
        var security = new Dictionary<RecoveryPhase, CanonicalRecoverySecurity>(previous.SecurityEvidence);
        if (h.Recovery.Security == "REQUIRED")
        {
            var before = new Dictionary<RecoveryPhase, CanonicalRecoverySecurity>();
            await CaptureSecurity(h, current, preceding, before, token);
            if (before[preceding].Sha256 != security[preceding].Sha256)
                throw new LegacyContractException("SECURITY_DRIFT_BEFORE_MUTATION");
        }
        var script = phase == "ROLLBACK" ? new ReleaseScript("rollback", package.RollbackBytes)
            : new ReleaseScript("forward", package.ForwardBytes);
        var expected = phase == "ROLLBACK" ? previous.RollbackHash : previous.ForwardHash;
        var start = DateTimeOffset.UtcNow;
        token.ThrowIfCancellationRequested();
        // A failure after this point is uncertain. No checkpoint is emitted and
        // no later job can start. The workflow uploads the append-only journal.
        mutationJournal?.Invoke("MUTATION_STARTED");
        await runtime.ApplyExactAsync(script, expected, h.TransactionPolicy, current, token);
        mutationJournal?.Invoke("MUTATION_APPLIED");
        var observedPhase = phase switch {
            "FORWARD1" => RecoveryPhase.Post1, "ROLLBACK" => RecoveryPhase.Pre2,
            _ => RecoveryPhase.Post2
        };
        var observed = await CaptureAsync(h, last.HashContract, token);
        if (phase == "ROLLBACK" && !SchemaComparer.Compare(
                SchemaCanonicalizer.Canonicalize(previous.Observations[RecoveryPhase.Pre].Snapshot),
                SchemaCanonicalizer.Canonicalize(observed.Snapshot)).IsEquivalent)
            throw new LegacyContractException("BLOCKED_SCHEMA_ROLLBACK_MISMATCH");
        if (phase == "FORWARD2" && !SchemaComparer.Compare(
                SchemaCanonicalizer.Canonicalize(previous.Observations[RecoveryPhase.Post1].Snapshot),
                SchemaCanonicalizer.Canonicalize(observed.Snapshot)).IsEquivalent)
            throw new LegacyContractException("BLOCKED_REAPPLY_MISMATCH");
        if (data is not null)
        {
            if (phase == "FORWARD1") await data.CapturePostDataAsync(token);
            if (phase == "ROLLBACK" && await data.ValidateRollbackDataAsync(token) != DataRollbackValidity.Valid)
                throw new LegacyContractException("BLOCKED_DATA_ROLLBACK_MISMATCH");
            if (phase == "FORWARD2" && await data.ValidateReapplyDataAsync(token) != DataRollbackValidity.Valid)
                throw new LegacyContractException("BLOCKED_DATA_REAPPLY_UNVERIFIED");
        }
        await CaptureSecurity(h, observed, observedPhase, security, token);
        if (phase == "FORWARD1")
            await runtime.VerifyRollbackAgainstPost1Async(
                new ReleaseScript("rollback", package.RollbackBytes), observed, token);
        if (phase == "ROLLBACK" && h.Recovery.Security == "REQUIRED"
            && security[RecoveryPhase.Pre].Sha256 != security[RecoveryPhase.Pre2].Sha256)
            throw new LegacyContractException("BLOCKED_SECURITY_MISMATCH");
        if (phase == "FORWARD2" && h.Recovery.Security == "REQUIRED"
            && security[RecoveryPhase.Post1].Sha256 != security[RecoveryPhase.Post2].Sha256)
            throw new LegacyContractException("BLOCKED_SECURITY_MISMATCH");
        var observations = new Dictionary<RecoveryPhase, ObservedCurrentSnapshotV1>(previous.Observations) {
            [observedPhase] = observed
        };
        var phases = previous.Phases.Concat(new[] {
            new LegacyPhaseReceiptV1(phase, "APPLIED", start, DateTimeOffset.UtcNow, expected, null, null),
            Captured(observedPhase.ToString().ToUpperInvariant(), observed, DateTimeOffset.UtcNow)
        }).ToArray();
        if (phase == "FORWARD2")
            return (null, FinalReceipt(previous, h, package, data, observations, security, phases));
        return (LegacyPhaseCheckpoint.Bind(previous with {
            Runtime = runtime.Context, CompletedPhase = observedPhase.ToString().ToUpperInvariant(),
            ExpectedNextPhase = phase == "FORWARD1" ? "ROLLBACK" : "FORWARD2",
            Phases = phases, Observations = observations,
            DataEvidence = data?.Evidence ?? new Dictionary<RecoveryPhase, LegacyDataPhaseEvidenceV1>(),
            SecurityEvidence = security, CompletedAtUtc = DateTimeOffset.UtcNow,
            PreviousCheckpointHash = trustedPreviousHash, CheckpointHash = ""
        }), null);
    }

    private LegacyRehearsalReceiptV1 FinalReceipt(LegacyRehearsalCheckpointV1 previous,
        LegacyHandoffV1 h, LegacyFrozenPackage package, LegacyDataValidationProviderV1? data,
        IReadOnlyDictionary<RecoveryPhase, ObservedCurrentSnapshotV1> observations,
        IReadOnlyDictionary<RecoveryPhase, CanonicalRecoverySecurity> security,
        IReadOnlyList<LegacyPhaseReceiptV1> phases)
    {
        var requiredData = h.Recovery.Data == "REQUIRED";
        var requiredSecurity = h.Recovery.Security == "REQUIRED";
        var hashes = security.ToDictionary(x => x.Key, x => x.Value.Sha256);
        var coverage = new RecoveryCoverageEvidence(h.Artifacts.Forward.Sha256,
            h.Artifacts.Rollback.Sha256, previous.Qualification.StaticSafety.Impact.Complete,
            new(true, RecoveryEvidenceCoverage.Complete, RecoveryComparison.Match, RecoveryComparison.Match),
            new(requiredData, requiredData ? RecoveryEvidenceCoverage.Complete : RecoveryEvidenceCoverage.NotRequired,
                requiredData ? RecoveryComparison.Match : RecoveryComparison.NotEvaluated,
                requiredData ? RecoveryComparison.Match : RecoveryComparison.NotEvaluated),
            new(requiredSecurity, requiredSecurity ? RecoveryEvidenceCoverage.Complete : RecoveryEvidenceCoverage.NotRequired,
                requiredSecurity ? RecoveryComparison.Match : RecoveryComparison.NotEvaluated,
                requiredSecurity ? RecoveryComparison.Match : RecoveryComparison.NotEvaluated),
            hashes, requiredSecurity ? h.Recovery.SecurityScopeHash : null, security);
        if (!coverage.Complete)
            throw new LegacyContractException("RECOVERY_COVERAGE_INCOMPLETE");
        var pre = SchemaCanonicalizer.Canonicalize(observations[RecoveryPhase.Pre].Snapshot);
        var post1 = SchemaCanonicalizer.Canonicalize(observations[RecoveryPhase.Post1].Snapshot);
        var pre2 = SchemaCanonicalizer.Canonicalize(observations[RecoveryPhase.Pre2].Snapshot);
        var post2 = SchemaCanonicalizer.Canonicalize(observations[RecoveryPhase.Post2].Snapshot);
        var evaluation = new RehearsalResult {
            QualificationStatus = "QUALIFIED", SchemaRollbackValidity = SchemaRollbackValidity.Valid,
            DataRollbackValidity = requiredData ? DataRollbackValidity.Valid : DataRollbackValidity.NotApplicable,
            RollbackCapability = RollbackCapability.FullReversible, ForwardCertified = true,
            RollbackCertified = true, ReapplyCertified = true, Pre = pre, Post1 = post1,
            Pre2 = pre2, Post2 = post2, RollbackDiff = SchemaComparer.Compare(pre, pre2),
            ReapplyDiff = SchemaComparer.Compare(post1, post2), RecoveryCoverage = coverage
        };
        var receipt = LegacyRehearsalReceiptHash.Bind(new(1, "LEGACY_REHEARSAL_RECEIPT",
            runtime.EvidenceKind, runtime.Context, h.TargetId, h.PackageIdentity,
            previous.QualificationIdentity, h.EvidenceSetHash, package.Evidence, h.Recovery,
            h.Baseline.BaselineIdentity, LegacyRehearsalStateV1.RehearsalEvaluated,
            "REHEARSAL_COMPLETE", "FULL_REVERSIBLE", false, null, data?.Definition,
            phases, observations, data?.Evidence ?? new Dictionary<RecoveryPhase, LegacyDataPhaseEvidenceV1>(),
            evaluation, [], previous.StartedAtUtc, DateTimeOffset.UtcNow, ""));
        if (runtime.EvidenceKind == "REAL_TEST" && authority is ProtectedEnvironmentLegacyAuthorityV1)
            LegacyPromotionFreeze.RegisterProducedReceipt(receipt);
        return receipt;
    }

    private static LegacyPhaseReceiptV1 Captured(string phase,
        ObservedCurrentSnapshotV1 observation, DateTimeOffset start) =>
        new(phase, "CAPTURED", start, DateTimeOffset.UtcNow, null,
            LegacyRuntimeEvidenceHash.Hash(observation), null);

    private async Task<ObservedCurrentSnapshotV1> CaptureAsync(LegacyHandoffV1 h,
        StructuralHashContractV1 hashContract, CancellationToken token)
    {
        var start = DateTimeOffset.UtcNow;
        var observation = await runtime.CaptureAsync(token);
        if (observation.ContractVersion != 1 || observation.Kind != "OBSERVED_CURRENT_SNAPSHOT"
            || observation.TargetId != h.TargetId || observation.Environment != "TEST"
            || observation.EndpointReference != h.Governance.Binding.EndpointReference
            || observation.ObservedIdentity.DatabaseName != h.Governance.Binding.DatabaseName
            || h.Governance.Binding.ServerMatchPolicy == "ALLOW_LIST"
                && !h.Governance.Binding.AllowedServerInstances.Contains(
                    observation.ObservedIdentity.ServerInstance, StringComparer.OrdinalIgnoreCase)
            || observation.CapturedAtUtc < start || observation.CapturedAtUtc > DateTimeOffset.UtcNow
            || observation.HashContract != hashContract || !hashContract.IsSupported
            || observation.Metadata != new ObservedSnapshotMetadataV1("SUFFICIENT", "COMPLETE", "COMPLETE")
            || observation.Snapshot.SchemaCoverage != SchemaCoverage.Complete
            || observation.ObservedSchemaHash != hashContract.Hash(observation.Snapshot)
            || !observation.CaptureComparison.IdentityMatch
            || !observation.CaptureComparison.StructuralMatch)
            throw new LegacyContractException("PHASE_EVIDENCE_INCOMPLETE");
        return JsonSerializer.Deserialize<ObservedCurrentSnapshotV1>(
            JsonSerializer.Serialize(observation, JsonDefaults.Compact), JsonDefaults.Compact)!;
    }

    private async Task CaptureSecurity(LegacyHandoffV1 h, ObservedCurrentSnapshotV1 observed,
        RecoveryPhase phase, Dictionary<RecoveryPhase, CanonicalRecoverySecurity> evidence,
        CancellationToken token)
    {
        if (h.Recovery.Security != "REQUIRED") return;
        var raw = await runtime.CaptureSecurityAsync(h.Recovery.SecurityScope, phase, token);
        if (raw.DatabaseName != observed.ObservedIdentity.DatabaseName
            || raw.ServerInstance != observed.ObservedIdentity.ServerInstance)
            throw new LegacyContractException("SECURITY_IDENTITY_MISMATCH");
        var canonical = RecoverySecurityCanonicalizer.Canonicalize(h.Recovery.SecurityScope, phase, raw)
            ?? throw new LegacyContractException("SECURITY_EVIDENCE_INCOMPLETE");
        evidence.Add(phase, canonical);
    }

    private static void RequireData(LegacyHandoffV1 h, LegacyDataValidationProviderV1? data)
    {
        if (h.Recovery.Data == "REQUIRED"
            && (data is null || h.Recovery.DataValidator != new LegacyDataValidatorLinkV1(
                data.Definition.Selector, data.Definition.Version, data.Definition.ScopeHash))
            || h.Recovery.Data != "REQUIRED" && data is not null)
            throw new LegacyContractException("DATA_VALIDATOR_REQUIRED");
    }

    private void Enter()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new LegacyContractException("REHEARSAL_RESUME_NOT_SUPPORTED");
    }
}
