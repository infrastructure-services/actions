using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record LegacyResolverRequestV1(int ContractVersion, string TargetId,
    LegacyArtifactSelectionV1 ArtifactSelection);

public sealed record LegacyRuntimeContextV1(LegacyRepositoryV1 Repository,
    string WorkflowPath, string WorkflowRevision, string RunId,
    string RunAttempt, string JobId);

public sealed record LegacyRuntimeSourceEntryV1(string Key, string TargetId,
    string Authority, IReadOnlyList<LegacyGitDocumentV1> Inputs,
    LegacyProducerRefV1 Producer, string ResultSha256);

public sealed record LegacyRuntimeIssueV1(string Code, string Evidence,
    string? CauseCode);

public sealed record LegacyRuntimeEvidencePayloadV1(
    LegacyGovernanceV1 Governance, LegacyOnboardingV1 Onboarding,
    CertifiedStructuralBaselineV1 CertifiedStructuralBaseline,
    ObservedCurrentSnapshotV1 ObservedSnapshot,
    DatabaseStateEvaluation DatabaseStateEvaluation, string SchemaRelation,
    JsonElement Classification, LegacyArtifactValidV1 Artifact);

public sealed record TrustedLegacyRuntimeEvidenceV1(int ContractVersion,
    string Kind, string Status, LegacyResolverRequestV1 Request,
    LegacyRuntimeContextV1? Runtime, LegacyProducerRefV1? Resolver,
    IReadOnlyList<LegacyRuntimeSourceEntryV1> Sources,
    IReadOnlyList<LegacyRuntimeIssueV1> Issues,
    LegacyRuntimeEvidencePayloadV1? Evidence, string? EvidenceSetHash,
    bool ExecutionAuthorized);

public static class LegacyRuntimeEvidenceHash
{
    private static readonly JsonSerializerOptions RequiredNulls = new(JsonDefaults.Compact) {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    private static readonly string[] Keys = ["artifact", "governance", "observedSnapshot",
        "onboarding", "registry", "repositoryDiscovery", "sqlDiscovery"];
    private static readonly Regex Sha = new(@"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z");
    private static readonly Regex Sha256 = new(@"\A[0-9a-f]{64}\z");

    public static string Serialize(TrustedLegacyRuntimeEvidenceV1 value) =>
        JsonSerializer.Serialize(value, RequiredNulls);

    public static TrustedLegacyRuntimeEvidenceV1 Blocked(LegacyResolverRequestV1 request,
        string code, string status = "BLOCKED") => new(1,
            "TRUSTED_LEGACY_RUNTIME_EVIDENCE", status, request,
            null, null, [], [new(code, "runtime", null)], null, null, false);

    public static TrustedLegacyRuntimeEvidenceV1 Bind(TrustedLegacyRuntimeEvidenceV1 value)
    {
        if (value.ContractVersion != 1 || value.Kind != "TRUSTED_LEGACY_RUNTIME_EVIDENCE"
            || value.ExecutionAuthorized || value.Status != "RESOLVED"
            || value.Runtime is null || value.Resolver is null || value.Evidence is null
            || value.Issues.Count != 0 || value.Sources.Count != Keys.Length)
            throw new LegacyContractException("OBSERVED_EVIDENCE_UNVERIFIED");
        var ordered = value.Sources.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        if (!ordered.Select(x => x.Key).SequenceEqual(Keys)
            || ordered.Any(x => x.TargetId != value.Request.TargetId
                || x.Producer.ContractVersion < 1 || !AuthorityValid(x)
                || !Sha256.IsMatch(x.ResultSha256)
                || !DocumentValid(x.Producer.Source)
                || x.Inputs.Any(input => !DocumentValid(input)))
            || !DocumentValid(value.Resolver.Source)
            || value.Request.ContractVersion != 1
            || value.Request.ArtifactSelection.ContractVersion != 1
            || value.Evidence.Governance.TargetId != value.Request.TargetId
            || value.Evidence.CertifiedStructuralBaseline.TargetId != value.Request.TargetId
            || value.Evidence.ObservedSnapshot.TargetId != value.Request.TargetId
            || value.Evidence.Artifact.TargetId != value.Request.TargetId
            || value.Evidence.SchemaRelation != "CONSISTENT"
            || !Sha.IsMatch(value.Runtime.WorkflowRevision)
            || value.Runtime.RunId.Length == 0 || value.Runtime.RunAttempt.Length == 0
            || value.Runtime.JobId.Length == 0)
            throw new LegacyContractException("OBSERVED_EVIDENCE_UNVERIFIED");
        var requestHash = Hash(value.Request);
        var runtimeHash = Hash(value.Runtime);
        var resolverHash = Hash(value.Resolver);
        var sourcesHash = Hash(ordered);
        var evidenceHash = Hash(value.Evidence);
        var setHash = LegacyReadinessHash.LengthPrefix(
            "TrustedLegacyRuntimeEvidenceV1", requestHash, runtimeHash,
            resolverHash, sourcesHash, evidenceHash);
        return value with { Sources = ordered, EvidenceSetHash = setHash };
    }

    public static bool Verify(TrustedLegacyRuntimeEvidenceV1 value)
    {
        try { return value.EvidenceSetHash is not null
            && Bind(value).EvidenceSetHash == value.EvidenceSetHash; }
        catch { return false; }
    }

    public static string Hash<T>(T value) =>
        Hashing.Sha256(JsonSerializer.Serialize(value, RequiredNulls));

    private static bool AuthorityValid(LegacyRuntimeSourceEntryV1 entry) => entry.Key switch
    {
        "governance" or "onboarding" or "registry" => entry.Authority == "GOVERNED_GIT",
        "repositoryDiscovery" or "artifact" => entry.Authority == "APPLICATION_GIT",
        "sqlDiscovery" or "observedSnapshot" => entry.Authority == "CURRENT_SQL_OBSERVATION",
        _ => false
    };

    private static bool DocumentValid(LegacyGitDocumentV1 document)
    {
        try
        {
            document.Repository.Validate();
            LegacyPortablePath.Validate(document.Path);
            return Sha.IsMatch(document.Revision) && Sha256.IsMatch(document.RawSha256);
        }
        catch { return false; }
    }
}
