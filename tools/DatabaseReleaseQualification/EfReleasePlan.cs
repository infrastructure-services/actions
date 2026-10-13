using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

// Evidence consistency is not authenticity, execution authority or certification.
// A future acquisition adapter must obtain these facts independently of the caller.
public sealed record EfPlanProvenanceV1(string Repository, string Revision, EvidenceReference Document);
public sealed record EfPlanTargetV1(string TargetId, string ApplicationId, string Environment,
    string EndpointReference, string DatabaseName, string ServerMatchPolicy,
    string[] AllowedServerInstances, string SourceRepository);
public sealed record EfPlanBaselineV1(string SchemaHash, EfPlanProvenanceV1 Provenance);
public sealed record EfPlanGovernanceV1(EfPlanTargetV1 Target, string Lifecycle,
    string ChangeManagementMode, string OnboardingState, EfPlanProvenanceV1 Governance,
    EfPlanProvenanceV1 Onboarding, string Scenario, EvidenceReference Classification,
    EvidenceReference? BootstrapEvidence, EfPlanBaselineV1? CertifiedBaseline);
public sealed record EfPlanSourceV1(string Repository, string Revision, string ProjectPath,
    string StartupProjectPath, string[] ManagedMigrationIds, EvidenceReference RepositoryEvidence,
    EfPlanProvenanceV1? AdoptionProvenance);
public sealed record EfPlanPolicyV1(string PolicyId, EfPlanProvenanceV1 Provenance,
    int MaximumValiditySeconds);
public sealed record EfPlanObservationV1(string TargetId, string Environment,
    string EndpointReference, string ServerInstance, string DatabaseName,
    string DatabaseIncarnation, string HistoryStatus, string[] AppliedMigrationIds,
    SchemaSnapshot Schema, string MetadataCoverage, string ImpactCoverage,
    string DataHash, string SecurityHash, EvidenceReference Evidence, DateTimeOffset CapturedAtUtc,
    SqlDiscovery.V2.PhysicalResult? Physical = null);
public sealed record EfPlanRecoveryV1(RollbackCapability Strategy,
    EvidenceReference? StrategyApproval, EvidenceReference? ExerciseEvidence,
    string TargetId, string PayloadHash, RehearsalResult? Exercise,
    EfPlanObservationV1? RecoveredPre, EfPlanObservationV1? ReappliedPost);
public sealed record EfPlanRequestV1(int ContractVersion, string ReleaseId, string OperationId,
    DateTimeOffset PreparedAtUtc, DateTimeOffset ExpiresAtUtc, EfPlanPolicyV1 Policy,
    EfPlanGovernanceV1 Authority, EfPlanSourceV1 Source, EfPlanObservationV1 Pre,
    EfPlanObservationV1 ExpectedPost, EfPlanRecoveryV1 Recovery);

public sealed record EfPlanStateV1(string TargetId, string Environment, string EndpointReference,
    string ServerInstance, string DatabaseName, string DatabaseIncarnation, string HistoryStatus,
    string[] AppliedMigrationIds, string SchemaHash, string DataHash, string SecurityHash,
    EvidenceReference Evidence, DateTimeOffset CapturedAtUtc);
public sealed record EfPlanRecoverySummaryV1(RollbackCapability Strategy, bool ScriptAvailable,
    bool PotentialDataLoss, bool DataValidationRequired,
    SchemaRollbackValidity SchemaValidity, DataRollbackValidity DataValidity,
    bool ExerciseVerified, string? ExerciseHash, EvidenceReference? StrategyApproval,
    EvidenceReference? ExerciseEvidence);
public sealed record EfPlanManifestV1(int ContractVersion, string Kind, string Status,
    string OperationId, DateTimeOffset PreparedAtUtc, DateTimeOffset ExpiresAtUtc,
    EfPlanPolicyV1 Policy, EfPlanGovernanceV1 Authority, EfPlanSourceV1 Source,
    ReleasePayloadMetadata Payload, EfPlanStateV1 Pre, EfPlanStateV1 ExpectedPost,
    string[] PendingMigrationIds, RiskAnalysisReport? Risk, bool ImpactComplete,
    EfPlanRecoverySummaryV1 Recovery, string[] BlockingReasons, EfPlanAcquisitionV1? Acquisition = null)
{
    public bool ExecutionAuthorized => false;
}

// Frozen JSON avoids retaining mutable snapshots, arrays or reports supplied by a caller.
// No deserializer or public constructor can manufacture an executable capability.
public sealed class EfReleasePlanV1
{
    internal EfReleasePlanV1(EfPlanManifestV1 manifest)
    {
        Json = JsonSerializer.Serialize(manifest, JsonDefaults.Compact);
        Sha256 = Hashing.Sha256(Json);
    }
    public string Json { get; }
    public string Sha256 { get; }
    public EfPlanManifestV1 Read() => JsonSerializer.Deserialize<EfPlanManifestV1>(Json, JsonDefaults.Compact)!;
}

public sealed record EfPlanResultV1(string Status, EfReleasePlanV1? Plan, string[] Reasons)
{
    public bool ExecutionAuthorized => false;
}
public sealed record EfPlanExecutionEvidenceV1(string OperationId, string TargetId,
    string PlanHash, string PayloadHash, string ForwardHash, string Outcome,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, EvidenceReference Evidence);

public static class EfReleasePlannerV1
{
    private sealed class ContractError(string code) : Exception(code);
    private static void Require(bool valid, string code)
    {
        if (!valid) throw new ContractError(code);
    }
    private static bool Hash(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z");
    private static bool Sha(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{40}\z");
    private static bool Token(string? value) => value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}\z");
    private static bool Repository(string? value) => value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z");
    private static bool Project(string? value) => value is not null
        && Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\.csproj\z")
        && !value.Split('/').Any(x => x is "." or "..");

    private static void Reference(EvidenceReference? reference)
    {
        Require(reference is not null && Token(reference.EvidenceId) && Hash(reference.EvidenceSha256), "EVIDENCE_REFERENCE_REQUIRED");
        // Storage is deliberately not selected by this contract. Locators and their
        // possible credentials must not enter a plan's semantic identity or artifacts.
        Require(reference!.StorageLocator is null, "EVIDENCE_LOCATOR_NOT_SUPPORTED");
    }
    private static void Provenance(EfPlanProvenanceV1 value)
    {
        Require(Repository(value.Repository) && Sha(value.Revision), "PROVENANCE_INVALID");
        Reference(value.Document);
    }
    private static void Migrations(string[] ids)
    {
        Require(ids.Length <= 10000 && ids.All(x => x is not null
            && Regex.IsMatch(x, @"\A[0-9]{14}_[A-Za-z0-9_]+\z"))
            && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length
            && ids.SequenceEqual(ids.Order(StringComparer.Ordinal)), "MIGRATION_SEQUENCE_INVALID");
    }
    private static void Authority(EfPlanRequestV1 r)
    {
        var a = r.Authority; var t = a.Target;
        Require(Token(t.TargetId) && Token(t.ApplicationId) && Token(t.Environment)
            && Token(t.EndpointReference) && Repository(t.SourceRepository), "TARGET_INVALID");
        Require(t.DatabaseName.Length is > 0 and <= 128 && !t.DatabaseName.Any(char.IsControl), "TARGET_INVALID");
        Require(t.ServerMatchPolicy is "ALLOW_LIST" or "MANAGED_ENDPOINT", "TARGET_POLICY_INVALID");
        Require(t.AllowedServerInstances.Distinct(StringComparer.OrdinalIgnoreCase).Count() == t.AllowedServerInstances.Length
            && t.AllowedServerInstances.All(x => ObservedDatabaseIdentityContract.IsValid(x, t.DatabaseName))
            && (t.ServerMatchPolicy == "ALLOW_LIST" ? t.AllowedServerInstances.Length > 0 : t.AllowedServerInstances.Length == 0), "TARGET_POLICY_INVALID");
        Require(a.ChangeManagementMode == "EF_MIGRATIONS"
            && (a.Scenario == "EXISTING_EF" && a.Lifecycle == "EXISTING" && a.OnboardingState == "MANAGED"
                || a.Scenario == "NEW_EF" && a.Lifecycle == "NEW" && a.OnboardingState == "NOT_REQUIRED"), "GOVERNANCE_BLOCKED");
        Provenance(a.Governance); Provenance(a.Onboarding); Reference(a.Classification);
        Require(a.Governance.Repository == a.Onboarding.Repository
            && a.Governance.Revision == a.Onboarding.Revision, "GOVERNANCE_REVISION_MISMATCH");
        Require(r.Source.Repository == t.SourceRepository && Repository(r.Source.Repository)
            && Sha(r.Source.Revision) && Project(r.Source.ProjectPath) && Project(r.Source.StartupProjectPath), "SOURCE_BINDING_INVALID");
        Reference(r.Source.RepositoryEvidence);
        if (r.Source.AdoptionProvenance is { } adoption)
        {
            Provenance(adoption);
            Require(a.Scenario == "EXISTING_EF" && adoption.Repository == r.Source.Repository, "ADOPTION_SOURCE_INVALID");
            // Historical adoption revision is not the current application revision.
        }
        if (a.BootstrapEvidence is not null) Reference(a.BootstrapEvidence);
    }
    private static EfPlanStateV1 State(EfPlanObservationV1 o, EfPlanTargetV1 t, DateTimeOffset now)
    {
        Require(o.TargetId == t.TargetId && o.Environment == t.Environment
            && o.EndpointReference == t.EndpointReference && o.DatabaseName == t.DatabaseName
            && ObservedDatabaseIdentityContract.IsValid(o.ServerInstance, o.DatabaseName)
            && (t.ServerMatchPolicy == "MANAGED_ENDPOINT" || t.AllowedServerInstances.Contains(o.ServerInstance, StringComparer.OrdinalIgnoreCase)), "OBSERVED_TARGET_MISMATCH");
        Require(Token(o.DatabaseIncarnation) && o.CapturedAtUtc <= now, "OBSERVATION_INVALID");
        Reference(o.Evidence); Migrations(o.AppliedMigrationIds);
        Require(o.HistoryStatus is "ABSENT" or "EMPTY" or "PRESENT"
            && (o.HistoryStatus == "PRESENT" ? o.AppliedMigrationIds.Length > 0 : o.AppliedMigrationIds.Length == 0), "HISTORY_UNVERIFIED");
        Require(o.MetadataCoverage == "COMPLETE" && o.Schema.FormatVersion == 1
            && o.Schema.SchemaCoverage == SchemaCoverage.Complete, "SCHEMA_COVERAGE_REQUIRED");
        Require(Hash(o.DataHash) && Hash(o.SecurityHash), "DATA_SECURITY_EVIDENCE_REQUIRED");
        var schema = SchemaCanonicalizer.Canonicalize(o.Schema);
        Require(schema.Document.Objects.All(x => x.Kind.Length > 0 && x.Name.Length > 0)
            && schema.Document.Objects.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count() == o.Schema.Objects.Count, "SCHEMA_OBJECTS_INVALID");
        return new(o.TargetId, o.Environment, o.EndpointReference, o.ServerInstance, o.DatabaseName,
            o.DatabaseIncarnation, o.HistoryStatus, o.AppliedMigrationIds.ToArray(),
            schema.Sha256, o.DataHash, o.SecurityHash,
            o.Evidence, o.CapturedAtUtc);
    }
    private static ScriptAnalysis Analyze(ReleaseScript script, SchemaSnapshot snapshot)
    {
        Require(script.Length is > 0 and <= 8 * 1024 * 1024, "SQL_PAYLOAD_REQUIRED");
        var text = new UTF8Encoding(false, true).GetString(script.Bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        Require(!text.Contains('\uFEFF'), "SQL_ENCODING_INVALID");
        // Analysis only: does not expand any executor's grammar or permissions.
        return new SqlScriptAnalyzer().Analyze(script.Role, text, snapshot);
    }

    public static EfPlanResultV1 Build(EfPlanRequestV1 request, ReleaseScript forward,
        ReleaseScript rollback, DateTimeOffset now)
        => BuildCore(request, forward, rollback, now, null);

    internal static EfPlanResultV1 BuildAcquired(EfPlanRequestV1 request, ReleaseScript forward,
        ReleaseScript rollback, DateTimeOffset now, EfPlanAcquisitionV1 acquisition)
        => BuildCore(request, forward, rollback, now, acquisition);

    private static EfPlanResultV1 BuildCore(EfPlanRequestV1 request, ReleaseScript forward,
        ReleaseScript rollback, DateTimeOffset now, EfPlanAcquisitionV1? acquisition)
    {
        try
        {
            // Snapshot every mutable input before validating or hashing it.
            var r = JsonSerializer.Deserialize<EfPlanRequestV1>(JsonSerializer.Serialize(request, JsonDefaults.Compact), JsonDefaults.Compact)!;
            if (acquisition is not null)
                acquisition = JsonSerializer.Deserialize<EfPlanAcquisitionV1>(JsonSerializer.Serialize(acquisition, JsonDefaults.Compact), JsonDefaults.Compact)!;
            Require(r.ContractVersion == 1 && Token(r.ReleaseId) && Guid.TryParseExact(r.OperationId, "D", out _), "REQUEST_INVALID");
            Provenance(r.Policy.Provenance);
            Require(Token(r.Policy.PolicyId) && r.Policy.MaximumValiditySeconds > 0
                && r.PreparedAtUtc <= now && now < r.ExpiresAtUtc && r.ExpiresAtUtc > r.PreparedAtUtc
                && (r.ExpiresAtUtc - r.PreparedAtUtc).TotalSeconds <= r.Policy.MaximumValiditySeconds, "PLAN_VALIDITY_INVALID");
            Authority(r); Migrations(r.Source.ManagedMigrationIds);
            Require(forward.Role == "forward" && rollback.Role == "rollback", "SQL_ROLE_INVALID");
            var pre = State(r.Pre, r.Authority.Target, now);
            var qualificationTarget = acquisition?.RehearsalTarget ?? r.Authority.Target;
            var qualificationPre = acquisition?.RehearsalPre ?? pre;
            var post = State(r.ExpectedPost, qualificationTarget, now);
            if (acquisition is not null)
            {
                Require(qualificationTarget.TargetId != pre.TargetId
                    && qualificationPre.DatabaseIncarnation != pre.DatabaseIncarnation
                    && (!qualificationPre.DatabaseName.Equals(pre.DatabaseName, StringComparison.OrdinalIgnoreCase)
                        || !qualificationPre.ServerInstance.Equals(pre.ServerInstance, StringComparison.OrdinalIgnoreCase)), "REHEARSAL_ISOLATION_REQUIRED");
                Require(qualificationTarget.SourceRepository == r.Source.Repository
                    && qualificationTarget.ServerMatchPolicy is "ALLOW_LIST" or "MANAGED_ENDPOINT", "REHEARSAL_BINDING_INVALID");
                SameContent(pre, qualificationPre, "REHEARSAL_PRE_NOT_EQUIVALENT");
                Reference(acquisition.QualificationEvidence); Reference(acquisition.SqlResolution.Evidence);
                Reference(acquisition.ContentHashContract);
                foreach (var evidence in acquisition.Evidence) Reference(evidence);
            }
            if (r.Authority.Scenario == "EXISTING_EF")
            {
                Require(r.Authority.CertifiedBaseline is { } baseline && Hash(baseline.SchemaHash)
                    && baseline.SchemaHash == pre.SchemaHash, "CERTIFIED_PRE_MISMATCH");
                Provenance(r.Authority.CertifiedBaseline!.Provenance);
            }
            Require((now - r.Pre.CapturedAtUtc).TotalSeconds <= r.Policy.MaximumValiditySeconds, "PRE_EVIDENCE_STALE");
            Require(qualificationPre.DatabaseIncarnation == post.DatabaseIncarnation, "QUALIFICATION_INCARNATION_MISMATCH");
            Require(qualificationPre.CapturedAtUtc <= post.CapturedAtUtc, "QUALIFICATION_TIME_INVALID");
            Require(pre.Evidence.EvidenceId != post.Evidence.EvidenceId, "INDEPENDENT_POST_EVIDENCE_REQUIRED");
            var repositoryIds = r.Source.ManagedMigrationIds;
            Require(pre.AppliedMigrationIds.Length < repositoryIds.Length
                && repositoryIds.Take(pre.AppliedMigrationIds.Length).SequenceEqual(pre.AppliedMigrationIds), "HISTORY_NOT_VALID_PREFIX");
            Require(post.HistoryStatus == "PRESENT" && post.AppliedMigrationIds.SequenceEqual(repositoryIds), "EXPECTED_HISTORY_MISMATCH");
            Require(r.Authority.Scenario != "EXISTING_EF" || pre.HistoryStatus == "PRESENT", "EXISTING_HISTORY_REQUIRED");
            Require(r.Authority.Scenario != "NEW_EF" || pre.AppliedMigrationIds.Length == 0, "NEW_HISTORY_NOT_EMPTY");
            Require(r.Authority.Scenario != "NEW_EF" || r.Pre.Physical is { } physical
                && physical.Status == SqlDiscovery.V2.PhysicalStatus.Complete
                && SqlDiscovery.V2.EmptyForNewEfV1.Valid(physical)
                && physical.BusinessObjectCount == 0 && physical.TechnicalObjectCount == 0
                && EmptySchema(r.Pre.Schema),
                "NEW_EMPTY_EVIDENCE_REQUIRED");
            var payload = ReleasePayloadBuilder.Build(new ReleaseDescriptor {
                ReleaseId = r.ReleaseId, Environment = r.Authority.Target.Environment, SourceKind = "EF",
                Scenario = r.Authority.Scenario, DatabaseLifecycle = r.Authority.Lifecycle
            }, forward, rollback);
            var f = acquisition is null ? Analyze(forward, r.Pre.Schema)
                : EfSqlSemanticAnalysisV1.Analyze(forward, r.Pre.Schema, acquisition.SqlResolution).Analysis;
            var b = acquisition is null ? Analyze(rollback, r.ExpectedPost.Schema)
                : EfSqlSemanticAnalysisV1.Analyze(rollback, r.ExpectedPost.Schema, acquisition.SqlResolution).Analysis;
            var impact = RecoveryImpact.Derive(f, b);
            var metricsComplete = r.Pre.ImpactCoverage == "COMPLETE" && r.ExpectedPost.ImpactCoverage == "COMPLETE"
                && MetricsComplete(f, r.Pre.Schema) && MetricsComplete(b, r.ExpectedPost.Schema);
            if (!metricsComplete)
            {
                f.Confidence = (AnalysisConfidence)Math.Max((int)f.Confidence, (int)AnalysisConfidence.Partial);
                f.ConfidenceReasons.Add("EF_IMPACT_METRICS_INCOMPLETE");
            }
            var risk = new RiskEngine().Evaluate(f, r.Pre.Schema, b, r.ExpectedPost.Schema);
            var reasons = new List<string>();
            if (r.Authority.Scenario == "NEW_EF" && r.Authority.BootstrapEvidence is null)
                reasons.Add("NEW_BOOTSTRAP_EVIDENCE_REQUIRED");
            var analysisComplete = f.Confidence == AnalysisConfidence.Complete && b.Confidence == AnalysisConfidence.Complete
                && f.ParseErrors.Count == 0 && b.ParseErrors.Count == 0
                && f.UnknownStatementTypes.Count == 0 && b.UnknownStatementTypes.Count == 0
                && f.Operations.Count > 0 && b.Operations.Count > 0;
            if (!analysisComplete)
                reasons.Add("SQL_ANALYSIS_INCOMPLETE");
            if (!impact.Complete || !metricsComplete)
                reasons.Add("IMPACT_COVERAGE_REQUIRED");
            if (f.Findings.Concat(b.Findings).Any(x => x.Severity == FindingSeverity.Blocking))
                reasons.Add("BLOCKING_DEPENDENCY");
            var recovery = Recovery(r.Recovery, payload, qualificationPre, post, impact,
                f.HasPotentialDataLoss || b.HasPotentialDataLoss, qualificationTarget, now, reasons);
            var status = reasons.Count == 0 ? "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED" : "BLOCKED";
            var codes = reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var manifest = new EfPlanManifestV1(1, "EF_RELEASE_PLAN", status, r.OperationId,
                r.PreparedAtUtc, r.ExpiresAtUtc, r.Policy, r.Authority, r.Source, payload, pre, post,
                repositoryIds.Skip(pre.AppliedMigrationIds.Length).ToArray(),
                analysisComplete && impact.Complete && metricsComplete ? risk : null,
                analysisComplete && impact.Complete && metricsComplete, recovery, codes, acquisition);
            return new(status, new EfReleasePlanV1(manifest), codes);
        }
        catch (ContractError e) { return new("BLOCKED", null, [e.Message]); }
        catch { return new("BLOCKED", null, ["EF_PLAN_INPUT_INVALID"]); }
    }

    private static bool MetricsComplete(ScriptAnalysis analysis, SchemaSnapshot snapshot)
    {
        if (snapshot.ImpactMetrics.Any(m => m.RowCount < 0 || m.ReservedMb < 0 || m.IndexMb < 0
            || m.LobMb < 0 || m.PartitionCount < 0 || m.IndexCount < 0 || m.ForeignKeyCount < 0
            || m.TriggerCount < 0 || m.DependencyCount < 0)) return false;
        if (snapshot.ImpactMetrics.Select(m => (m.Schema, m.Table)).Distinct().Count() != snapshot.ImpactMetrics.Count) return false;
        // Missing metrics are not zero. Newly created objects are absent in PRE;
        // their authoritative rollback metrics must come from the qualified POST.
        return snapshot.Objects.Where(s => s.Kind == "table")
            .All(s => snapshot.ImpactMetrics.Count(m => m.Schema == s.Schema && m.Table == s.Name) == 1)
            && analysis.Operations.Where(o => o.IsDataMutation).All(o =>
                snapshot.Objects.Any(s => s.Kind == "table" && s.Schema == o.Schema && s.Name == o.Object)
                || analysis.Operations.Any(c => c.Operation == "CREATE_TABLE" && c.Schema == o.Schema && c.Object == o.Object));
    }

    private static bool EmptySchema(SchemaSnapshot snapshot) => snapshot.Objects.All(o =>
        o.Kind == "schema" && SqlDiscovery.V2.EmptyForNewEfV1.StandardSchemas.Contains(o.Name, StringComparer.Ordinal)
        || o.Schema == "dbo" && (o.Kind == "table" && o.Name == "__EFMigrationsHistory"
            || o.Parent == "__EFMigrationsHistory"));

    private static EfPlanRecoverySummaryV1 Recovery(EfPlanRecoveryV1 r, ReleasePayloadMetadata payload,
        EfPlanStateV1 pre, EfPlanStateV1 post, RecoveryImpact impact, bool potentialDataLoss, EfPlanTargetV1 target,
        DateTimeOffset now, List<string> reasons)
    {
        if (!Enum.IsDefined(r.Strategy) || r.Strategy == RollbackCapability.Unknown)
            reasons.Add("RECOVERY_STRATEGY_REQUIRED");
        if (r.StrategyApproval is null) reasons.Add("RECOVERY_APPROVAL_REQUIRED"); else Reference(r.StrategyApproval);
        if (r.ExerciseEvidence is null) reasons.Add("RECOVERY_EXERCISE_REQUIRED"); else Reference(r.ExerciseEvidence);
        Require(r.TargetId == pre.TargetId && r.PayloadHash == payload.PayloadHash, "RECOVERY_PAYLOAD_BINDING_MISMATCH");
        var e = r.Exercise;
        var exerciseHash = e is null ? null : Hashing.Sha256(JsonSerializer.Serialize(e, JsonDefaults.Compact));
        var valid = e is not null && e.CanProceed && e.ForwardCertified
            && r.ExerciseEvidence?.EvidenceSha256 == exerciseHash
            && e.Pre?.Sha256 == pre.SchemaHash && e.Post1?.Sha256 == post.SchemaHash
            && e.Pre2?.Sha256 == pre.SchemaHash && e.Post2?.Sha256 == post.SchemaHash
            && new[] { e.Pre, e.Post1, e.Pre2, e.Post2 }.All(x => x is not null
                && Hashing.Sha256(x.Json) == x.Sha256 && JsonSerializer.Serialize(x.Document, JsonDefaults.Compact) == x.Json)
            && e.RecoveryCoverage is { } coverage && coverage.ForwardHash == payload.ForwardHash
            && coverage.RollbackHash == payload.RollbackHash
            && coverage.Data.Required == impact.DataRequired && coverage.Security.Required == impact.SecurityRequired
            && (!impact.SecurityRequired || coverage.SecurityScopeHash == impact.SecurityScope.Sha256);
        if (r.RecoveredPre is null || r.ReappliedPost is null) valid = false;
        else
        {
            var recovered = State(r.RecoveredPre, target, now);
            var reapplied = State(r.ReappliedPost, target, now);
            Require(post.CapturedAtUtc <= recovered.CapturedAtUtc && recovered.CapturedAtUtc <= reapplied.CapturedAtUtc,
                "QUALIFICATION_TIME_INVALID");
            SameState(pre, recovered, "RECOVERY_PRE_MISMATCH");
            SameState(post, reapplied, "RECOVERY_REAPPLY_MISMATCH");
            Require(new[] { pre.Evidence.EvidenceId, post.Evidence.EvidenceId,
                recovered.Evidence.EvidenceId, reapplied.Evidence.EvidenceId }.Distinct(StringComparer.Ordinal).Count() == 4,
                "RECOVERY_INDEPENDENT_EVIDENCE_REQUIRED");
        }
        if (e?.SchemaRollbackValidity == SchemaRollbackValidity.Invalid || e?.DataRollbackValidity == DataRollbackValidity.Invalid)
            reasons.Add("RECOVERY_INVALID");
        // These strategies need their own verified evidence contract; a human
        // reference alone must not pretend that a restore/forward fix was tested.
        if (r.Strategy is not (RollbackCapability.FullReversible or RollbackCapability.Unknown))
            reasons.Add("RECOVERY_STRATEGY_EVIDENCE_ADAPTER_REQUIRED");
        if (!valid || r.Strategy != RollbackCapability.FullReversible) reasons.Add("RECOVERY_NOT_VERIFIED");
        return new(r.Strategy, true, potentialDataLoss, impact.DataRequired,
            e?.SchemaRollbackValidity ?? SchemaRollbackValidity.NotTested,
            e?.DataRollbackValidity ?? DataRollbackValidity.NotTested, valid && r.Strategy == RollbackCapability.FullReversible,
            exerciseHash,
            r.StrategyApproval, r.ExerciseEvidence);
    }

    // Pure preflight/postcheck comparisons. They never obtain credentials, authorize
    // SQL, produce a certification or imply that input evidence was acquired live.
    public static EfPlanResultV1 Revalidate(EfReleasePlanV1 plan, string trustedHash,
        EfPlanObservationV1 current, ReleaseScript forward, ReleaseScript rollback, DateTimeOffset now)
        => Check(plan, trustedHash, () => {
            var p = plan.Read();
            Require(now >= p.PreparedAtUtc && now < p.ExpiresAtUtc, "PLAN_EXPIRED");
            Require(forward.Sha256 == p.Payload.ForwardHash && rollback.Sha256 == p.Payload.RollbackHash, "PAYLOAD_CHANGED");
            var state = State(current, p.Authority.Target, now);
            Require(current.CapturedAtUtc >= p.PreparedAtUtc, "FRESH_OBSERVATION_REQUIRED");
            SameState(p.Pre, state, "PRE_STATE_CHANGED");
        }, "PRE_MATCH_NOT_AUTHORIZED");

    public static EfPlanResultV1 VerifyPost(EfReleasePlanV1 plan, string trustedHash,
        EfPlanExecutionEvidenceV1 execution, EfPlanObservationV1 observed, DateTimeOffset now)
        => Check(plan, trustedHash, () => {
            var p = plan.Read(); Reference(execution.Evidence);
            Require(execution.Outcome == "SUCCEEDED", "EXECUTION_UNCERTAIN_OR_FAILED");
            Require(execution.OperationId == p.OperationId && execution.TargetId == p.Authority.Target.TargetId
                && execution.PlanHash == plan.Sha256 && execution.PayloadHash == p.Payload.PayloadHash
                && execution.ForwardHash == p.Payload.ForwardHash, "EXECUTION_BINDING_MISMATCH");
            Require(execution.StartedAtUtc >= p.PreparedAtUtc && execution.CompletedAtUtc < p.ExpiresAtUtc
                && execution.CompletedAtUtc >= execution.StartedAtUtc && execution.CompletedAtUtc <= now
                && observed.CapturedAtUtc >= execution.CompletedAtUtc, "EXECUTION_TIME_INVALID");
            Require(observed.Evidence.EvidenceId != p.Pre.Evidence.EvidenceId
                && observed.Evidence.EvidenceId != p.ExpectedPost.Evidence.EvidenceId
                && observed.Evidence.EvidenceId != execution.Evidence.EvidenceId, "INDEPENDENT_POST_EVIDENCE_REQUIRED");
            var actual = State(observed, p.Authority.Target, now);
            Require(actual.DatabaseIncarnation == p.Pre.DatabaseIncarnation, "POST_INCARNATION_MISMATCH");
            SameContent(p.ExpectedPost, actual, "POST_STATE_MISMATCH");
        }, "POST_MATCH_NOT_CERTIFIED");

    private static EfPlanResultV1 Check(EfReleasePlanV1 plan, string trustedHash, Action validate, string status)
    {
        try
        {
            Require(Hash(trustedHash) && plan.Sha256 == trustedHash && Hashing.Sha256(plan.Json) == trustedHash, "PLAN_HASH_MISMATCH");
            Require(plan.Read().Status == "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED", "PLAN_BLOCKED");
            validate(); return new(status, plan, []);
        }
        catch (ContractError e) { return new("BLOCKED", null, [e.Message]); }
        catch { return new("BLOCKED", null, ["EF_PLAN_INPUT_INVALID"]); }
    }
    private static void SameState(EfPlanStateV1 expected, EfPlanStateV1 actual, string code)
    {
        Require(expected.DatabaseIncarnation == actual.DatabaseIncarnation, code);
        SameContent(expected, actual, code);
    }
    private static void SameContent(EfPlanStateV1 expected, EfPlanStateV1 actual, string code)
        => Require(expected.HistoryStatus == actual.HistoryStatus && expected.AppliedMigrationIds.SequenceEqual(actual.AppliedMigrationIds)
            && expected.SchemaHash == actual.SchemaHash && expected.DataHash == actual.DataHash
            && expected.SecurityHash == actual.SecurityHash, code);

    internal static EfPlanStateV1 Observe(EfPlanObservationV1 observation, EfPlanTargetV1 target, DateTimeOffset now)
        => State(observation, target, now);

    internal static EfPlanStateV1 ValidateNoChanges(EfPlanRequestV1 request, DateTimeOffset now)
    {
        Authority(request); Migrations(request.Source.ManagedMigrationIds);
        Provenance(request.Policy.Provenance);
        Require(request.Policy.MaximumValiditySeconds > 0 && request.PreparedAtUtc <= now
            && now < request.ExpiresAtUtc && (request.ExpiresAtUtc - request.PreparedAtUtc).TotalSeconds <= request.Policy.MaximumValiditySeconds,
            "PLAN_VALIDITY_INVALID");
        var pre = State(request.Pre, request.Authority.Target, now);
        Require(request.Authority.Scenario == "EXISTING_EF" && pre.HistoryStatus == "PRESENT"
            && pre.AppliedMigrationIds.SequenceEqual(request.Source.ManagedMigrationIds), "NO_CHANGES_NOT_PROVEN");
        Require((now - pre.CapturedAtUtc).TotalSeconds <= request.Policy.MaximumValiditySeconds, "PRE_EVIDENCE_STALE");
        Require(request.Authority.CertifiedBaseline is { } baseline && baseline.SchemaHash == pre.SchemaHash, "CERTIFIED_PRE_MISMATCH");
        Provenance(request.Authority.CertifiedBaseline!.Provenance);
        return pre;
    }
}
