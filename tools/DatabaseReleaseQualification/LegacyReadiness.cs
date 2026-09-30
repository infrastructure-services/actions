using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabaseReleaseQualification;

public sealed record LegacyEvidenceReferenceV1(string Kind, string Sha256);
public sealed record LegacyReadinessChecksV1(string Governance, string Onboarding,
    string Baseline, string Safety, string QualificationPreconditions);
public sealed record LegacySourceProvenanceV1(string SourceRepository, string SourcePath,
    string SourceRevision, string SourceSha256);
public sealed record LegacyAuthorityReferenceV1(string Kind, string PolicyPath);
public sealed record LegacyBindingV1(string EndpointReference, string DatabaseName,
    string ServerMatchPolicy, IReadOnlyList<string> AllowedServerInstances);
public sealed record LegacyGovernanceV1(LegacySourceProvenanceV1 SourceProvenance,
    LegacyAuthorityReferenceV1 AuthorityReference, LegacyBindingV1 Binding,
    string ApplicationId, string Environment, string TargetId, string DatabaseLifecycle,
    string ChangeManagementMode);
public sealed record LegacyGovernanceHandoffV1(LegacySourceProvenanceV1 SourceProvenance,
    LegacyAuthorityReferenceV1 AuthorityReference, LegacyBindingV1 Binding,
    string ApplicationId, string Environment);
public sealed record LegacyOnboardingV1(LegacySourceProvenanceV1 SourceProvenance,
    LegacyAuthorityReferenceV1 AuthorityReference, string Status);
public sealed record LegacyBaselineV1(string BaselineIdentity, RegistryProvenance RegistryProvenance,
    string CertifiedSchemaHash);
public sealed record LegacyStaticSafetyLinkV1(string PolicyVersion, string ParserVersion,
    string EvidenceHash);
public sealed record LegacyDataValidatorLinkV1(string ContractId, int Version, string ScopeHash);
public sealed record LegacyRecoveryHandoffV1(string Structure, string Data, string Security,
    string ImpactHash, RecoverySecurityScope SecurityScope, string SecurityScopeHash,
    string? SecurityReadinessEvidenceHash, string SecurityProfile,
    LegacyDataValidatorLinkV1? DataValidator);
public sealed record LegacyApprovalRequirementV1(bool Required, string PolicyVersion,
    IReadOnlyList<string> ReasonCodes);
public sealed record LegacyHandoffV1(
    int ContractVersion, string PackageIdentity, string PayloadHash, string TargetId,
    string ReleaseId, LegacyArtifactValidV1 Artifacts, LegacyGovernanceHandoffV1 Governance,
    LegacyOnboardingV1 Onboarding, LegacyBaselineV1 Baseline,
    string ClassificationEvidenceHash, string ObservedSchemaHash, string ScopeSafetyHash,
    LegacyStaticSafetyLinkV1 StaticSafety, LegacyRecoveryHandoffV1 Recovery,
    RiskAnalysisReport Risk, LegacyApprovalRequirementV1 ApprovalRequirement,
    string TransactionPolicy, string EngineCommit, string EvidenceSetHash,
    bool ExecutionAuthorized);
public sealed record LegacyReadinessV1(
    int ContractVersion, string Scenario, string Status, string TargetId,
    string? PackageIdentity, IReadOnlyList<string> ReasonCodes,
    LegacyReadinessChecksV1 Checks, IReadOnlyList<LegacyEvidenceReferenceV1> EvidenceReferences,
    string EvidenceHash, string QualificationStatus, bool CanProceedToPromotion,
    LegacyHandoffV1? Handoff);
public sealed record LegacyRequestRejectedV1(int ContractVersion, string Status,
    IReadOnlyList<string> ReasonCodes);

public sealed record LegacyDataScopeTargetV1(string Schema, string Object, IReadOnlyList<string>? Columns);
public sealed record LegacyDataScopeV1(IReadOnlyList<LegacyDataScopeTargetV1> Targets);
public sealed record LegacyDataValidationDescriptorV1(string ContractId, int Version,
    string ScopeHash, IDataRollbackValidationContract Provider);

public static class LegacyReadinessHash
{
    private static readonly JsonSerializerOptions RequiredNulls = new(JsonDefaults.Compact) {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Serialize(LegacyReadinessV1 readiness) =>
        JsonSerializer.Serialize(readiness, RequiredNulls);

    public static LegacyReadinessV1 Bind(LegacyReadinessV1 readiness)
    {
        var body = new {
            readiness.ContractVersion, readiness.Scenario, readiness.Status,
            readiness.TargetId, readiness.PackageIdentity, readiness.ReasonCodes,
            readiness.Checks, readiness.EvidenceReferences, readiness.QualificationStatus,
            readiness.CanProceedToPromotion, readiness.Handoff
        };
        return readiness with { EvidenceHash = Hashing.Sha256(JsonSerializer.Serialize(body, RequiredNulls)) };
    }

    public static string LengthPrefix(params string[] fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            var value = Encoding.UTF8.GetBytes(field);
            stream.Write(Encoding.ASCII.GetBytes(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
            stream.Write(value);
        }
        return Hashing.Sha256(stream.ToArray());
    }

    public static (LegacyDataScopeV1 Scope, string Hash) DataScope(ScriptAnalysis forward,
        ScriptAnalysis rollback)
    {
        var groups = forward.Operations.Concat(rollback.Operations)
            .Where(x => x.IsDataMutation || x.HasPotentialDataLoss)
            .GroupBy(x => (x.Schema, x.Object))
            .OrderBy(x => x.Key.Schema, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Object, StringComparer.Ordinal);
        var targets = groups.Select(group => new LegacyDataScopeTargetV1(
            group.Key.Schema, group.Key.Object,
            group.Any(x => x.Column is null)
                ? null : group.Select(x => x.Column!).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray())).ToArray();
        var scope = new LegacyDataScopeV1(targets);
        return (scope, Hashing.Sha256(JsonSerializer.Serialize(scope, RequiredNulls)));
    }
}
