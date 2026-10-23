using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SqlDiscovery.V2;

namespace DatabaseReleaseQualification;

public sealed record EfEvidenceScopeV1(string TargetId, string SourceRepository, string SourceRevision, string SessionId);
public sealed record EfEvidenceReadV1(EfEvidenceScopeV1 Scope, string Purpose, EvidenceReference Reference);
public sealed record EfAuthorizedContentV1(string ProducerRepository, string ProducerRevision, byte[] Content);

// This interface is a HOST trust boundary. No implementation is selected by JSON,
// an artifact, environment variable or caller flag. A production host must register
// an independently authenticated, role-aware producer verifier. None ships in A2.1.
public interface IEfAuthorizedEvidenceProviderV1
{
    Task<EfAuthorizedContentV1> ReadAuthorizedAsync(EfEvidenceReadV1 request, CancellationToken token);
    Task VerifyCurrentAsync(EfEvidenceScopeV1 scope, IReadOnlyList<EvidenceReference> evidence, CancellationToken token);
}

public sealed record EfPreparationSelectionV1(EfEvidenceScopeV1 Scope, string ReleaseId, string OperationId,
    EvidenceReference Governance, EvidenceReference Source, EvidenceReference Pre, EvidenceReference? Qualification,
    EfObservationSourcesV1? PreSources = null);
public sealed record EfGovernedPreparationV1(EfPlanGovernanceV1 Authority, EfPlanPolicyV1 Policy,
    EfSqlResolutionV1 SqlResolution, EvidenceReference ContentHashContract, int ClassificationRulesVersion = 1);
public sealed record EfSourcePreparationV1(EfPlanSourceV1 Source, byte[] Forward, byte[] Rollback);
public sealed record EfObservationEvidenceV1(string Phase, EfPlanObservationV1 Observation,
    SqlDiscoveryResult Discovery, EvidenceReference ContentHashContract);
public sealed record EfQualificationEvidenceV1(string TargetId, string SourceRevision, string PayloadHash,
    EfPlanTargetV1 RehearsalTarget, EfObservationEvidenceV1 Pre, EfObservationEvidenceV1 Post,
    EfObservationEvidenceV1 RecoveredPre, EfObservationEvidenceV1 ReappliedPost, EfPlanRecoveryV1 Recovery);
public sealed record EfRecoveryApprovalV1(string TargetId, string PayloadHash, RollbackCapability Strategy, DateTimeOffset ExpiresAtUtc);
// Pinned projection code must preserve data, principals, grants, ownership and
// object scope while separating physical clone identity. No algorithm is inferred.
public sealed record EfContentHashContractV1(int Version, string ScopeHash,
    EfPlanProvenanceV1 DataProjection, EfPlanProvenanceV1 SecurityProjection);
public sealed record EfProducerReceiptV1(string Purpose, EvidenceReference Evidence, string Repository, string Revision);
public sealed record EfPlanAcquisitionV1(EfSqlResolutionV1 SqlResolution, EfPlanTargetV1 RehearsalTarget,
    EfPlanStateV1 RehearsalPre, EvidenceReference QualificationEvidence,
    EvidenceReference ContentHashContract, EvidenceReference[] Evidence, EfProducerReceiptV1[] Producers,
    EfInitialRecoveryEvidenceV1? InitialRecovery = null);
public sealed record EfPreparationResultV1(string Status, EfPlanResultV1? PlanResult,
    EfSemanticResultV1? Forward, EfSemanticResultV1? Rollback, string[] Reasons, string? EvidenceSetHash)
{
    public bool ExecutionAuthorized => false;
}
public sealed record EfBaselineAuthorizationV1(string TargetId, string OperationId, string PlanHash,
    EvidenceReference? PreviousBaseline, DateTimeOffset NotBefore, DateTimeOffset ExpiresAtUtc,
    DeploymentAuthorizationEvidence Authorization);
public sealed record EfBaselineTransitionResultV1(string Status, EvidenceReference? PreviousBaseline,
    EfPlanStateV1? Post, string? ProposedSchemaHash, CertificationEvidence? Decision, string[] Reasons)
{
    public bool ExecutionAuthorized => false;
    public bool Committed => false;
}

public sealed partial class EfEvidencePreparationV1(IEfAuthorizedEvidenceProviderV1? provider = null,
    IEfProtectedExecutionHostV1? executionHost = null, ICertificationCommitStore? certificationStore = null)
{
    private sealed class Block(string code) : Exception(code);
    private sealed class EvidenceLog : List<EvidenceReference>
    {
        public List<EfProducerReceiptV1> Producers { get; } = [];
    }
    private static void Require(bool valid, string code) { if (!valid) throw new Block(code); }
    private static readonly JsonSerializerOptions Strict = Options();
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Compact) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Insert(0, new HistoryConverter());
        return options;
    }
    // Adapt serialization only; discovery states and classification are unchanged.
    private sealed record HistoryDocument(HistoryStatus Status, string[] MigrationIds, StageDiagnostic? Diagnostic);
    private sealed class HistoryConverter : JsonConverter<HistoryResult>
    {
        public override HistoryResult Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var d = JsonSerializer.Deserialize<HistoryDocument>(ref reader, options)!;
            return new(d.Status, d.MigrationIds, d.Diagnostic);
        }
        public override void Write(Utf8JsonWriter writer, HistoryResult value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new HistoryDocument(value.Status, value.MigrationIds.ToArray(), value.Diagnostic), options);
    }
    private static bool Hash(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z");
    private static bool Revision(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{40}\z");
    private static bool Repository(string? value) => value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z");
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonDefaults.Compact), JsonDefaults.Compact)!;

    private async Task<byte[]> Read(EfEvidenceScopeV1 scope, EvidenceReference reference, string purpose,
        EvidenceLog used, CancellationToken token)
    {
        Require(provider is not null, "AUTHORIZED_PROVIDER_REQUIRED");
        Require(reference is not null && Hash(reference.EvidenceSha256) && !string.IsNullOrWhiteSpace(reference.EvidenceId)
            && reference.StorageLocator is null, "EVIDENCE_REFERENCE_INVALID");
        var copy = Clone(reference!);
        var result = await provider!.ReadAuthorizedAsync(new(Clone(scope), purpose, Clone(copy)), token);
        Require(result is not null && Repository(result.ProducerRepository) && Revision(result.ProducerRevision)
            && result.Content is { Length: > 0 and <= 16 * 1024 * 1024 }, "AUTHORIZED_CONTENT_REQUIRED");
        var bytes = result!.Content.ToArray();
        Require(Hashing.Sha256(bytes) == copy.EvidenceSha256, "EVIDENCE_HASH_MISMATCH");
        Require(!used.Any(x => x.EvidenceId == copy.EvidenceId && x.EvidenceSha256 != copy.EvidenceSha256), "EVIDENCE_ID_REBOUND");
        used.Add(copy);
        used.Producers.Add(new(purpose, copy, result.ProducerRepository, result.ProducerRevision));
        return bytes;
    }
    private async Task<T> Document<T>(EfEvidenceScopeV1 scope, EvidenceReference reference, string purpose,
        EvidenceLog used, CancellationToken token)
    {
        var bytes = await Read(scope, reference, purpose, used, token);
        using var document = JsonDocument.Parse(bytes);
        Unique(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Strict) ?? throw new Block("EVIDENCE_DOCUMENT_INVALID");
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in element.EnumerateObject()) { Require(names.Add(item.Name), "EVIDENCE_DUPLICATE_FIELD"); Unique(item.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
    private async Task Provenance(EfEvidenceScopeV1 scope, EfPlanProvenanceV1 provenance, string purpose,
        EvidenceLog used, CancellationToken token)
    {
        Require(Repository(provenance.Repository) && Revision(provenance.Revision), "PROVENANCE_INVALID");
        _ = await Read(scope, provenance.Document, purpose, used, token);
    }
    private async Task Observation(EfEvidenceScopeV1 scope, EfObservationEvidenceV1 evidence, string phase,
        EvidenceReference hashContract, EvidenceLog used, CancellationToken token, bool normalized = false)
    {
        var o = evidence.Observation; var d = evidence.Discovery;
        Require(evidence.Phase == phase && evidence.ContentHashContract == hashContract, "OBSERVATION_PHASE_OR_HASH_CONTRACT_MISMATCH");
        Require(d.ServerConnection.Status == ConnectionStatus.Succeeded && d.TargetConnection.Status == ConnectionStatus.Succeeded
            && d.DatabaseLookup.Status == DatabaseLookupStatus.Found && d.Metadata.Status == MetadataStatus.Sufficient
            && d.ObservedIdentity.Status == ObservedIdentityStatus.Available
            && d.ObservedIdentity.Identity?.ServerInstance == o.ServerInstance && d.ObservedIdentity.Identity.DatabaseName == o.DatabaseName
            && d.Physical.Status == PhysicalStatus.Complete && EmptyForNewEfV1.Valid(d.Physical)
            && d.History.Status is HistoryStatus.Absent or HistoryStatus.Empty or HistoryStatus.Present
            && d.History.Status.ToString().ToUpperInvariant() == o.HistoryStatus
            && d.History.MigrationIds.SequenceEqual(o.AppliedMigrationIds), "DISCOVERY_EVIDENCE_MISMATCH");
        Require(o.MetadataCoverage == "COMPLETE" && o.ImpactCoverage == "COMPLETE", "OBSERVATION_COVERAGE_REQUIRED");
        if (o.Physical is not null)
            Require(JsonSerializer.Serialize(o.Physical, JsonDefaults.Compact) == JsonSerializer.Serialize(d.Physical, JsonDefaults.Compact), "PHYSICAL_EVIDENCE_MISMATCH");
        if (!normalized) _ = await Read(scope, o.Evidence, phase, used, token);
    }

    public async Task<EfPreparationResultV1> PrepareAsync(EfPreparationSelectionV1 selection,
        DateTimeOffset now, CancellationToken token = default)
    {
        EfSemanticResultV1? forwardAnalysis = null, rollbackAnalysis = null;
        try
        {
            Require(provider is not null, "AUTHORIZED_PROVIDER_REQUIRED");
            var s = Clone(selection); var scope = s.Scope; var used = new EvidenceLog();
            Require(Repository(scope.SourceRepository) && Revision(scope.SourceRevision)
                && !string.IsNullOrWhiteSpace(scope.TargetId) && !string.IsNullOrWhiteSpace(scope.SessionId)
                && Guid.TryParseExact(s.OperationId, "D", out _), "PREPARATION_SELECTION_INVALID");
            var g = await Document<EfGovernedPreparationV1>(scope, s.Governance, "GOVERNANCE", used, token);
            Require(g.ClassificationRulesVersion is 1 or 2, "CLASSIFICATION_RULES_UNSUPPORTED");
            var source = await Document<EfSourcePreparationV1>(scope, s.Source, "SOURCE", used, token);
            var pre = s.PreSources is null
                ? await Document<EfObservationEvidenceV1>(scope, s.Pre, "TARGET_PRE", used, token)
                : await NormalizeObservation(s, g, now, used, token);
            Require(g.Authority.Target.TargetId == scope.TargetId && source.Source.Repository == scope.SourceRepository
                && source.Source.Revision == scope.SourceRevision, "SOURCE_OR_TARGET_CHANGED");
            await Provenance(scope, g.Authority.Governance, "GOVERNANCE_BYTES", used, token);
            await Provenance(scope, g.Authority.Onboarding, "ONBOARDING_BYTES", used, token);
            await Provenance(scope, g.Policy.Provenance, "POLICY", used, token);
            _ = await Read(scope, g.Authority.Classification, "CLASSIFICATION_V2", used, token);
            _ = await Read(scope, source.Source.RepositoryEvidence, "REPOSITORY_DISCOVERY_V2", used, token);
            if (source.Source.AdoptionProvenance is { } adoption) await Provenance(scope, adoption, "ADOPTION", used, token);
            if (g.Authority.CertifiedBaseline is { } baseline) await Provenance(scope, baseline.Provenance, "CERTIFIED_BASELINE", used, token);
            _ = await Read(scope, g.SqlResolution.Evidence, "SQL_NAME_RESOLUTION", used, token);
            var hashContract = await Document<EfContentHashContractV1>(scope, g.ContentHashContract, "CONTENT_HASH_CONTRACT", used, token);
            Require(hashContract.Version == 1 && Hash(hashContract.ScopeHash), "CONTENT_HASH_CONTRACT_INVALID");
            await Provenance(scope, hashContract.DataProjection, "DATA_PROJECTION", used, token);
            await Provenance(scope, hashContract.SecurityProjection, "SECURITY_PROJECTION", used, token);
            await Observation(scope, pre, "TARGET_PRE", g.ContentHashContract, used, token, s.PreSources is not null);
            EfPreprovisionedBootstrapV1? bootstrap = null;
            if (g.Authority.Scenario == "NEW_EF")
            {
                Require(g.Authority.BootstrapEvidence is not null, "NEW_BOOTSTRAP_EVIDENCE_REQUIRED");
                bootstrap = await Document<EfPreprovisionedBootstrapV1>(scope, g.Authority.BootstrapEvidence!, "NEW_BOOTSTRAP", used, token);
                EfPreprovisionedBootstrap.Validate(bootstrap, s, g, pre, now);
            }
            else Require(g.Authority.BootstrapEvidence is null, "UNEXPECTED_BOOTSTRAP_EVIDENCE");
            if (g.ClassificationRulesVersion == 2)
            {
                var classification = await Document<EfUnifiedClassificationEvidenceV1>(scope, g.Authority.Classification,
                    "CLASSIFICATION_V2", used, token);
                ValidateUnifiedClassification(classification, s, g, pre, source.Source, bootstrap);
            }
            Require(g.Policy.MaximumValiditySeconds > 0, "PLAN_VALIDITY_INVALID");
            var request = new EfPlanRequestV1(1, s.ReleaseId, s.OperationId, now, now.AddSeconds(g.Policy.MaximumValiditySeconds),
                g.Policy, g.Authority, source.Source, pre.Observation, null!, null!);
            if (pre.Observation.AppliedMigrationIds.SequenceEqual(source.Source.ManagedMigrationIds))
            {
                Require(source.Forward.Length == 0 && source.Rollback.Length == 0, "NO_CHANGES_WITH_SQL");
                _ = EfReleasePlannerV1.ValidateNoChanges(request, now);
                await provider!.VerifyCurrentAsync(Clone(scope), used.Select(Clone).ToArray(), token);
                return new("NO_CHANGES", null, null, null, [], EvidenceHash(used));
            }
            var forward = new ReleaseScript("forward", source.Forward); var rollback = new ReleaseScript("rollback", source.Rollback);
            forwardAnalysis = EfSqlSemanticAnalysisV1.Analyze(forward, pre.Observation.Schema, g.SqlResolution);
            Require(s.Qualification is not null, "QUALIFIED_POST_REQUIRED");
            var q = await Document<EfQualificationEvidenceV1>(scope, s.Qualification!, "ISOLATED_QUALIFICATION", used, token);
            var payload = ReleasePayloadBuilder.Build(new() { ReleaseId = s.ReleaseId, Environment = g.Authority.Target.Environment,
                SourceKind = "EF", Scenario = g.Authority.Scenario, DatabaseLifecycle = g.Authority.Lifecycle }, forward, rollback);
            Require(q.TargetId == scope.TargetId && q.SourceRevision == scope.SourceRevision && q.PayloadHash == payload.PayloadHash,
                "QUALIFICATION_BINDING_MISMATCH");
            await Observation(scope, q.Pre, "REHEARSAL_PRE", g.ContentHashContract, used, token);
            await Observation(scope, q.Post, "QUALIFIED_POST", g.ContentHashContract, used, token);
            await Observation(scope, q.RecoveredPre, "RECOVERED_PRE", g.ContentHashContract, used, token);
            await Observation(scope, q.ReappliedPost, "REAPPLIED_POST", g.ContentHashContract, used, token);
            rollbackAnalysis = EfSqlSemanticAnalysisV1.Analyze(rollback, q.Post.Observation.Schema, g.SqlResolution);
            if (q.Recovery.ExerciseEvidence is { } exercise)
            {
                var bytes = await Read(scope, exercise, "RECOVERY_EXERCISE", used, token);
                Require(Hashing.Sha256(JsonSerializer.Serialize(q.Recovery.Exercise, JsonDefaults.Compact)) == Hashing.Sha256(bytes), "RECOVERY_CONTENT_MISMATCH");
                if (q.Recovery.Exercise?.RecoveryCoverage is { Security.Required: true } coverage)
                {
                    var identity = JsonSerializer.Serialize(new { q.Pre.Observation.ServerInstance, q.Pre.Observation.DatabaseName }, JsonDefaults.Compact);
                    foreach (var security in coverage.SecurityEvidence.Values)
                    {
                        using var document = JsonDocument.Parse(security.Json);
                        Require(security.DatabaseIdentity == identity
                            && document.RootElement.GetProperty("databaseIdentity").GetString() == identity,
                            "RECOVERY_LAB_IDENTITY_MISMATCH");
                    }
                }
            }
            if (q.Recovery.StrategyApproval is { } strategy)
            {
                var approval = await Document<EfRecoveryApprovalV1>(scope, strategy, "RECOVERY_APPROVAL", used, token);
                Require(approval.TargetId == scope.TargetId && approval.PayloadHash == payload.PayloadHash
                    && approval.Strategy == q.Recovery.Strategy && approval.ExpiresAtUtc > now, "RECOVERY_APPROVAL_MISMATCH");
            }
            Require(Equal(q.Recovery.RecoveredPre, q.RecoveredPre.Observation)
                && Equal(q.Recovery.ReappliedPost, q.ReappliedPost.Observation), "RECOVERY_OBSERVATION_MISMATCH");
            var initial = q.Recovery.Exercise?.RecoveryCoverage?.InitialRecovery;
            if (initial is not null)
            {
                var policy = await Document<EfInitialRecoveryPolicyV1>(scope, initial.Selection.Reference,
                    "EF_INITIAL_RECOVERY_POLICY", used, token);
                Require(Equal(policy, initial.Selection.Policy) && g.Authority.Scenario == "NEW_EF"
                    && policy.Scope == scope && policy.ReleaseId == s.ReleaseId && policy.OperationId == s.OperationId
                    && policy.Bootstrap == g.Authority.BootstrapEvidence && policy.ContentHashContract == g.ContentHashContract
                    && Equal(policy.RehearsalTarget, q.RehearsalTarget) && policy.ExpiresAtUtc > now,
                    "EF_INITIAL_POLICY_BINDING_MISMATCH");
                EfInitialRecoveryV1.ValidatePolicy(initial.Selection, new() { ReleaseId = s.ReleaseId,
                    Environment = "TEST", SourceKind = "EF", Scenario = g.Authority.Scenario,
                    DatabaseLifecycle = g.Authority.Lifecycle }, forward, rollback);
                Require(EfInitialRecoveryV1.Complete(initial), "EF_INITIAL_RECOVERY_UNVERIFIED");
                foreach (var (phase, observation) in new[] { (RecoveryPhase.Pre, q.Pre), (RecoveryPhase.Post1, q.Post),
                    (RecoveryPhase.Pre2, q.RecoveredPre), (RecoveryPhase.Post2, q.ReappliedPost) })
                {
                    var capture = initial.Phases[phase];
                    Require(Equal(capture.Observation, observation.Observation), "EF_INITIAL_PHASE_BINDING_MISMATCH");
                    // Obtain independently authorized phase content, not just the
                    // embedded claim inside the qualification document.
                    var observed = await Document<EfFullSecurityV1>(scope, capture.SecurityEvidence,
                        "EF_INITIAL_" + phase.ToString().ToUpperInvariant(), used, token);
                    Require(Equal(observed, capture.Security), "EF_INITIAL_PHASE_CONTENT_MISMATCH");
                }
            }
            var origin = new EfPlanAcquisitionV1(g.SqlResolution, q.RehearsalTarget,
                EfReleasePlannerV1.Observe(q.Pre.Observation, q.RehearsalTarget, now), s.Qualification!, g.ContentHashContract,
                used.Distinct().OrderBy(x => x.EvidenceId, StringComparer.Ordinal).ToArray(),
                used.Producers.Distinct().OrderBy(x => x.Purpose, StringComparer.Ordinal).ThenBy(x => x.Evidence.EvidenceId, StringComparer.Ordinal).ToArray(), initial);
            request = request with { ExpectedPost = q.Post.Observation, Recovery = q.Recovery };
            var result = EfReleasePlannerV1.BuildAcquired(request, forward, rollback, now, origin);
            await provider!.VerifyCurrentAsync(Clone(scope), used.Select(Clone).ToArray(), token);
            return new(result.Status, result, forwardAnalysis, rollbackAnalysis, result.Reasons, EvidenceHash(used));
        }
        catch (EfBootstrapException e) { return new("BLOCKED", null, forwardAnalysis, rollbackAnalysis, [e.Message], null); }
        catch (Block e) { return new("BLOCKED", null, forwardAnalysis, rollbackAnalysis, [e.Message], null); }
        catch (OperationCanceledException) { return new("BLOCKED", null, forwardAnalysis, rollbackAnalysis, ["EVIDENCE_ACQUISITION_CANCELLED"], null); }
        catch { return new("BLOCKED", null, forwardAnalysis, rollbackAnalysis, ["EVIDENCE_NOT_VERIFIABLE"], null); }
    }
    private static bool Equal<T>(T a, T b) => JsonSerializer.Serialize(a, JsonDefaults.Compact) == JsonSerializer.Serialize(b, JsonDefaults.Compact);
    private static string EvidenceHash(EvidenceLog used) => Hashing.Sha256(JsonSerializer.Serialize(new {
        evidence = used.Distinct().OrderBy(x => x.EvidenceId, StringComparer.Ordinal),
        producers = used.Producers.Distinct().OrderBy(x => x.Purpose, StringComparer.Ordinal).ThenBy(x => x.Evidence.EvidenceId, StringComparer.Ordinal)
    }, JsonDefaults.Compact));

    private static EfPlanManifestV1 BoundPlan(EfReleasePlanV1 plan, string trustedHash, EfEvidenceScopeV1 scope)
    {
        Require(Hash(trustedHash) && plan.Sha256 == trustedHash && Hashing.Sha256(plan.Json) == trustedHash, "PLAN_HASH_MISMATCH");
        var p = plan.Read();
        Require(p.Status == "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED" && p.Acquisition is not null
            && p.Authority.Target.TargetId == scope.TargetId && p.Source.Repository == scope.SourceRepository
            && p.Source.Revision == scope.SourceRevision, "PLAN_SCOPE_MISMATCH");
        return p;
    }

    public async Task<EfPlanResultV1> RevalidateAsync(EfReleasePlanV1 plan, string trustedHash, EfEvidenceScopeV1 scope,
        EvidenceReference freshPre, EvidenceReference currentSource, DateTimeOffset now, CancellationToken token = default)
    {
        try
        {
            Require(provider is not null, "AUTHORIZED_PROVIDER_REQUIRED");
            scope = Clone(scope); var p = BoundPlan(plan, trustedHash, scope); var used = new EvidenceLog();
            used.AddRange(p.Acquisition!.Evidence); used.Producers.AddRange(p.Acquisition.Producers);
            var source = await Document<EfSourcePreparationV1>(scope, currentSource, "SOURCE", used, token);
            Require(Equal(source.Source, p.Source), "SOURCE_OR_TARGET_CHANGED");
            var pre = await Document<EfObservationEvidenceV1>(scope, freshPre, "FRESH_PRE", used, token);
            await Observation(scope, pre, "FRESH_PRE", p.Acquisition.ContentHashContract, used, token);
            Require(pre.Observation.Evidence.EvidenceId != p.Pre.Evidence.EvidenceId, "FRESH_OBSERVATION_REQUIRED");
            var result = EfReleasePlannerV1.Revalidate(plan, trustedHash, pre.Observation,
                new("forward", source.Forward), new("rollback", source.Rollback), now);
            await provider!.VerifyCurrentAsync(scope, used.Select(Clone).ToArray(), token);
            return result;
        }
        catch (Block e) { return new("BLOCKED", null, [e.Message]); }
        catch { return new("BLOCKED", null, ["EVIDENCE_NOT_VERIFIABLE"]); }
    }

    private async Task<(EfPlanManifestV1 Plan, EfPlanExecutionEvidenceV1 Execution, EfPlanObservationV1 Post)> Post(
        EfReleasePlanV1 plan, string trustedHash, EfEvidenceScopeV1 scope, EvidenceReference execution,
        EvidenceReference actualPost, DateTimeOffset now, EvidenceLog used, CancellationToken token)
    {
        var p = BoundPlan(plan, trustedHash, scope); used.AddRange(p.Acquisition!.Evidence); used.Producers.AddRange(p.Acquisition.Producers);
        var receipt = await Document<EfPlanExecutionEvidenceV1>(scope, execution, "EXECUTION_RECEIPT", used, token);
        _ = await Read(scope, receipt.Evidence, "EXECUTION_RECEIPT_BYTES", used, token);
        var post = await Document<EfObservationEvidenceV1>(scope, actualPost, "ACTUAL_POST", used, token);
        await Observation(scope, post, "ACTUAL_POST", p.Acquisition.ContentHashContract, used, token);
        var result = EfReleasePlannerV1.VerifyPost(plan, trustedHash, receipt, post.Observation, now);
        Require(result.Status == "POST_MATCH_NOT_CERTIFIED", result.Reasons.FirstOrDefault() ?? "POST_NOT_VERIFIED");
        return (p, receipt, post.Observation);
    }
    public async Task<EfPlanResultV1> VerifyPostAsync(EfReleasePlanV1 plan, string trustedHash, EfEvidenceScopeV1 scope,
        EvidenceReference execution, EvidenceReference actualPost, DateTimeOffset now, CancellationToken token = default)
    {
        try
        {
            Require(provider is not null, "AUTHORIZED_PROVIDER_REQUIRED"); scope = Clone(scope);
            var used = new EvidenceLog();
            _ = await Post(plan, trustedHash, scope, execution, actualPost, now, used, token);
            await provider!.VerifyCurrentAsync(scope, used.Select(Clone).ToArray(), token);
            return new("POST_MATCH_NOT_CERTIFIED", plan, []);
        }
        catch (Block e) { return new("BLOCKED", null, [e.Message]); }
        catch { return new("BLOCKED", null, ["EVIDENCE_NOT_VERIFIABLE"]); }
    }

    // A candidate for the existing certification commit protocol, never a Registry
    // update or a committed certificate. The store still needs predecessor/CAS checks.
    public async Task<EfBaselineTransitionResultV1> ProposeBaselineTransitionAsync(EfReleasePlanV1 plan, string trustedHash,
        EfEvidenceScopeV1 scope, EvidenceReference execution, EvidenceReference actualPost,
        EvidenceReference independentAuthorization, DateTimeOffset now, CancellationToken token = default)
    {
        try
        {
            Require(provider is not null, "AUTHORIZED_PROVIDER_REQUIRED"); scope = Clone(scope);
            var used = new EvidenceLog();
            var (p, receipt, post) = await Post(plan, trustedHash, scope, execution, actualPost, now, used, token);
            var previous = p.Authority.CertifiedBaseline?.Provenance.Document;
            if (previous is not null) _ = await Read(scope, previous, "CURRENT_CERTIFIED_BASELINE", used, token);
            var consent = await Document<EfBaselineAuthorizationV1>(scope, independentAuthorization, "BASELINE_TRANSITION_AUTHORIZATION", used, token);
            Require(consent.TargetId == scope.TargetId && consent.OperationId == p.OperationId && consent.PlanHash == trustedHash
                && consent.PreviousBaseline == previous && consent.NotBefore <= receipt.StartedAtUtc && consent.ExpiresAtUtc > receipt.CompletedAtUtc,
                "BASELINE_TRANSITION_AUTHORIZATION_MISMATCH");
            var a = consent.Authorization;
            Require(a.Risk == p.Risk!.FinalRisk && a.AnalysisConfidence == p.Risk.AnalysisConfidence
                && a.SchemaRollbackValidity == p.Recovery.SchemaValidity && a.DataRollbackValidity == p.Recovery.DataValidity
                && a.RollbackCapability == p.Recovery.Strategy
                && (!p.Risk.RequiresDbaApproval || a.Requirement == DeploymentAuthorizationRequirement.DbaApproval), "AUTHORIZATION_DOWNGRADE");
            var state = EfReleasePlannerV1.Observe(post, p.Authority.Target, now);
            var decision = new CertificationDecisionEngine().Evaluate(new() {
                Origin = CertificationOrigin.QualifiedRelease, DatabaseLifecycle = p.Authority.Lifecycle,
                InitialPreStateValidated = p.Authority.Scenario == "NEW_EF" && p.Authority.BootstrapEvidence is not null,
                CertifiedPreSchemaHash = p.Authority.CertifiedBaseline?.SchemaHash, ObservedPreSchemaHash = p.Pre.SchemaHash,
                QualifiedPreSchemaHash = p.Pre.SchemaHash, QualifiedPostSchemaHash = p.ExpectedPost.SchemaHash,
                ObservedPostSchemaHash = state.SchemaHash, ReleaseId = p.Payload.ReleaseId,
                QualifiedPayloadHash = p.Payload.PayloadHash, ExecutedPayloadHash = receipt.PayloadHash,
                QualifiedForwardHash = p.Payload.ForwardHash, ExecutedForwardHash = receipt.ForwardHash,
                QualifiedRollbackHash = p.Payload.RollbackHash, VerifiedRollbackHash = p.Payload.RollbackHash,
                QualifiedRelease = p.Recovery.ExerciseVerified, ExecutionSucceeded = receipt.Outcome == "SUCCEEDED",
                DeploymentAuthorization = a });
            Require(decision.ProducesCertifiedState, "BASELINE_TRANSITION_BLOCKED");
            await provider!.VerifyCurrentAsync(scope, used.Select(Clone).ToArray(), token);
            return new("BASELINE_TRANSITION_CANDIDATE_NOT_COMMITTED", previous, state, decision.NextCertifiedSchemaHash, decision.Evidence, []);
        }
        catch (Block e) { return new("BLOCKED", null, null, null, null, [e.Message]); }
        catch { return new("BLOCKED", null, null, null, null, ["EVIDENCE_NOT_VERIFIABLE"]); }
    }
}
