using System.Text;
using System.Text.Json;

namespace DatabaseReleaseQualification;

public sealed record LegacyAcquiredRuntimeV1(
    LegacyRuntimeContextV1 Runtime, LegacyProducerRefV1 Resolver,
    IReadOnlyList<LegacyRuntimeSourceEntryV1> Sources,
    LegacyRepositoryV1 ApplicationRepository, string ApplicationCommit,
    string EngineCommit, LegacyGovernanceV1 Governance, byte[] GovernanceBytes,
    LegacyOnboardingV1 Onboarding, byte[] OnboardingBytes,
    RegistryProvenance RegistryProvenance, byte[] RegistryBytes,
    SchemaCaptureSourceResult FirstCapture, SchemaCaptureSourceResult SecondCapture,
    DateTimeOffset CapturedAtUtc,
    JsonElement Classification, byte[] RepositoryDiscoveryBytes,
    byte[] SqlDiscoveryBytes, StructuralHashContractV1 HashContract,
    LegacyGitDocumentV1 ArtifactProducer);

public interface ILegacyRuntimeAcquisition
{
    Task<LegacyAcquiredRuntimeV1> AcquireAsync(LegacyResolverRequestV1 request,
        CancellationToken token);
    Task VerifyFreshnessAsync(LegacyResolverRequestV1 request, CancellationToken token);
}

public sealed class LegacyArtifactResolutionException(
    LegacyArtifactBlockedV1 blocked, TrustedLegacyRuntimeEvidenceV1 runtime)
    : Exception(blocked.ReasonCodes.FirstOrDefault() ?? "TECHNICAL_ERROR")
{
    public LegacyArtifactBlockedV1 Blocked { get; } = blocked;
    public TrustedLegacyRuntimeEvidenceV1 Runtime { get; } = runtime;
}

public sealed class LegacyClassifiedResolutionException(string code,
    TrustedLegacyRuntimeEvidenceV1 runtime) : Exception(code)
{
    public string Code { get; } = code;
    public TrustedLegacyRuntimeEvidenceV1 Runtime { get; } = runtime;
}

public sealed class TrustedLegacyRuntimeEvidenceResolver(
    ILegacyRuntimeAcquisition acquisition, ILegacyGitTransport applicationGit)
    : ILegacyVerifiedEvidenceSource
{
    public async Task<LegacyVerifiedEvidenceV1> ResolveAsync(
        LegacyQualificationRequestV1 request, CancellationToken token)
    {
        var selector = new LegacyResolverRequestV1(1, request.TargetId,
            request.ArtifactSelection);
        var acquired = await acquisition.AcquireAsync(selector, token);
        if (acquired.ApplicationRepository != request.ArtifactSelection.ExpectedRepository
            || acquired.ApplicationCommit != request.ArtifactSelection.ExpectedCommit
            || acquired.Governance.TargetId != request.TargetId
            || acquired.Governance.SourceProvenance.SourceRevision
                != acquired.Onboarding.SourceProvenance.SourceRevision
            || acquired.RegistryProvenance.RegistryCommitSha
                != acquired.Governance.SourceProvenance.SourceRevision
            || acquired.RegistryProvenance.RegistryRef
                != acquired.RegistryProvenance.RegistryCommitSha
            || acquired.RegistryProvenance.RegistryRepository
                != acquired.Governance.SourceProvenance.SourceRepository
            || acquired.Governance.SourceProvenance.SourceSha256
                != Hashing.Sha256(acquired.GovernanceBytes)
            || acquired.Onboarding.SourceProvenance.SourceSha256
                != Hashing.Sha256(acquired.OnboardingBytes)
            || acquired.RegistryProvenance.RegistryFileSha256
                != Hashing.Sha256(acquired.RegistryBytes))
            throw new LegacyContractException("EVIDENCE_CORRELATION_MISMATCH");
        var classification = acquired.Classification;
        string scenario;
        try
        {
            scenario = classification.GetProperty("classification")
                .GetProperty("classificationResult")
                .GetProperty("inferences").GetProperty("scenario").GetString() ?? "";
            if (classification.GetProperty("status").GetString() != "CLASSIFIED"
                || !classification.GetProperty("classificationInvoked").GetBoolean()
                || classification.GetProperty("targetId").GetString() != request.TargetId)
                throw new JsonException();
        }
        catch { throw new LegacyContractException("EVIDENCE_CORRELATION_MISMATCH"); }
        if (scenario != "EXISTING_LEGACY")
            throw new LegacyContractException("GOVERNANCE_INVALID");
        try
        {
        CertifiedStructuralBaselineV1 baseline;
        ObservedCurrentSnapshotV1 observed;
        DatabaseStateEvaluation state;
        try
        {
        var registry = JsonSerializer.Deserialize<DatabaseRegistryDocument>(
            acquired.RegistryBytes, DatabaseStateJson.Compact)
            ?? throw new LegacyContractException("REGISTRY_SOURCE_INVALID");
        var validation = DatabaseRegistryLoader.Validate(registry,
            acquired.RegistryProvenance);
        if (!validation.IsValid)
            throw new LegacyContractException("REGISTRY_VALIDATION_FAILED");
        var candidates = registry.Targets.Where(x => x.ApplicationId == acquired.Governance.ApplicationId
            && x.Environment == "TEST"
            && x.DatabaseName == acquired.Governance.Binding.DatabaseName).ToArray();
        if (candidates.Length != 1)
            throw new LegacyContractException("EVIDENCE_CORRELATION_MISMATCH");
        var target = candidates[0];
        if (target.Lifecycle != "EXISTING"
            || target.CertificationStatus != DatabaseCertificationStatuses.Certified
            || target.CertifiedSchemaHash is null)
            throw new LegacyContractException("BASELINE_NOT_CERTIFIED");
        baseline = new CertifiedStructuralBaselineV1(1,
            "CERTIFIED_STRUCTURAL_BASELINE", request.TargetId,
            new(target.ApplicationId, target.Environment, target.DatabaseName,
                target.Lifecycle), registry.RegistryFormatVersion,
            target.CertificationStatus, target.CertifiedSchemaHash.ToLowerInvariant(),
            acquired.RegistryProvenance, acquired.Governance.SourceProvenance);
        observed = LegacyStructuralEvidence.FromCurrentCaptures(request.TargetId,
            acquired.Governance.Binding.EndpointReference, acquired.FirstCapture,
            acquired.SecondCapture, acquired.HashContract, acquired.CapturedAtUtc);
        var observation = new DatabaseStateObservation {
            ApplicationId = acquired.Governance.ApplicationId,
            Environment = acquired.Governance.Environment,
            DatabaseName = acquired.Governance.Binding.DatabaseName,
            ObservedSchemaHash = observed.ObservedSchemaHash,
            SchemaCoverage = observed.Metadata.SchemaCoverage,
            UnsupportedSchemaFeatures = observed.Snapshot.UnsupportedSchemaFeatures,
            CaptureTimestampUtc = observed.CapturedAtUtc,
            RunId = acquired.Runtime.RunId, RunAttempt = acquired.Runtime.RunAttempt
        };
        state = new DatabaseStateEvaluator().Evaluate(validation, observation);
        _ = LegacyStructuralEvidence.Verify(baseline, observed,
            acquired.Governance, state);
        if (state.RegistryStatus != DatabaseCertificationStatuses.Certified
            || state.DriftStatus != DatabaseDriftStatuses.Match
            || state.GateStatus != DatabaseGateStatuses.Eligible
            || state.Reason != DatabaseStateReasons.SchemaHashMatch)
            throw new LegacyContractException("CERTIFIED_SCHEMA_HASH_MISMATCH");
        }
        catch (LegacyContractException exception)
        {
            throw new LegacyClassifiedResolutionException(exception.Code,
                LegacyRuntimeEvidenceHash.Blocked(selector, exception.Code));
        }
        catch (JsonException)
        {
            throw new LegacyClassifiedResolutionException("REGISTRY_SOURCE_INVALID",
                LegacyRuntimeEvidenceHash.Blocked(selector, "REGISTRY_SOURCE_INVALID"));
        }
        var sources = acquired.Sources.ToDictionary(x => x.Key, StringComparer.Ordinal);
        if (!LineageValid(acquired, sources)
            || sources.Count != 6
            || !SourceMatches(sources, "governance", Hashing.Sha256(acquired.GovernanceBytes))
            || !SourceMatches(sources, "onboarding", Hashing.Sha256(acquired.OnboardingBytes))
            || !SourceMatches(sources, "registry", Hashing.Sha256(acquired.RegistryBytes))
            || !SourceMatches(sources, "repositoryDiscovery",
                Hashing.Sha256(acquired.RepositoryDiscoveryBytes))
            || !SourceMatches(sources, "sqlDiscovery",
                Hashing.Sha256(acquired.SqlDiscoveryBytes))
            || !SourceMatches(sources, "observedSnapshot",
                LegacyRuntimeEvidenceHash.Hash(observed)))
            throw new LegacyContractException("OBSERVED_EVIDENCE_UNVERIFIED");
        var discovery = await new LegacyArtifactDiscovery(applicationGit)
            .DiscoverEvidenceAsync(request.ArtifactSelection, request.TargetId, token);
        if (discovery.Valid is null || discovery.Package is null)
        {
            var blocked = discovery.Blocked ?? new LegacyArtifactBlockedV1(1,
                "BLOCKED", ["TECHNICAL_ERROR"]);
            throw new LegacyArtifactResolutionException(blocked, new(1,
                "TRUSTED_LEGACY_RUNTIME_EVIDENCE", "BLOCKED", selector,
                acquired.Runtime, acquired.Resolver, acquired.Sources,
                blocked.ReasonCodes.Select(code => new LegacyRuntimeIssueV1(
                    code, "artifact", null)).ToArray(), null, null, false));
        }
        var package = discovery.Package;
        var artifactSource = new LegacyRuntimeSourceEntryV1("artifact", request.TargetId,
            "APPLICATION_GIT", [new LegacyGitDocumentV1(acquired.ApplicationRepository,
                acquired.ApplicationCommit, package.Evidence.Manifest.Path,
                package.Evidence.Manifest.Sha256)],
            new(acquired.ArtifactProducer, "LEGACY_RUNTIME_SOURCE_V1", 1),
            LegacyRuntimeEvidenceHash.Hash(package.Evidence));
        await acquisition.VerifyFreshnessAsync(selector, token);
        var payload = new LegacyRuntimeEvidencePayloadV1(acquired.Governance,
            acquired.Onboarding, baseline, observed, state, "CONSISTENT",
            classification.Clone(), package.Evidence);
        var trusted = LegacyRuntimeEvidenceHash.Bind(new(1,
            "TRUSTED_LEGACY_RUNTIME_EVIDENCE", "RESOLVED", selector,
            acquired.Runtime, acquired.Resolver, [.. acquired.Sources, artifactSource], [], payload,
            null, false));
        return new(acquired.ApplicationRepository, acquired.ApplicationCommit,
            acquired.EngineCommit, acquired.Governance,
            acquired.GovernanceBytes.ToArray(), acquired.Onboarding,
            acquired.OnboardingBytes.ToArray(), scenario,
            Encoding.UTF8.GetBytes(classification.GetRawText()), state,
            acquired.RegistryBytes.ToArray(), baseline, observed, trusted);
        }
        catch (LegacyClassifiedResolutionException) { throw; }
        catch (LegacyArtifactResolutionException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (LegacyContractException exception)
        {
            throw new LegacyClassifiedResolutionException(exception.Code,
                LegacyRuntimeEvidenceHash.Blocked(selector, exception.Code));
        }
        catch
        {
            throw new LegacyClassifiedResolutionException("TECHNICAL_ERROR",
                LegacyRuntimeEvidenceHash.Blocked(selector, "TECHNICAL_ERROR"));
        }
    }

    public Task VerifyFreshnessAsync(LegacyQualificationRequestV1 request,
        CancellationToken token) => acquisition.VerifyFreshnessAsync(
            new LegacyResolverRequestV1(1, request.TargetId,
                request.ArtifactSelection), token);

    private static bool SourceMatches(
        IReadOnlyDictionary<string, LegacyRuntimeSourceEntryV1> sources,
        string key, string hash) => sources.TryGetValue(key, out var source)
            && source.ResultSha256 == hash;

    private static bool LineageValid(LegacyAcquiredRuntimeV1 acquired,
        IReadOnlyDictionary<string, LegacyRuntimeSourceEntryV1> sources)
    {
        var governanceRevision = acquired.Governance.SourceProvenance.SourceRevision;
        var actionsRevision = acquired.EngineCommit;
        if (acquired.Runtime.Repository != acquired.ApplicationRepository
            || acquired.Runtime.WorkflowRevision != governanceRevision
            || acquired.Runtime.WorkflowPath
                != ".github/workflows/legacy-package-qualification-v1-test.yml"
            || !Producer(acquired.Resolver.Source, "infrastructure-services/actions",
                actionsRevision,
                "tools/DatabaseReleaseQualification/TrustedLegacyRuntimeEvidenceResolver.cs")
            || !Producer(acquired.ArtifactProducer, "infrastructure-services/actions",
                actionsRevision,
                "tools/DatabaseReleaseQualification/LegacyArtifactDiscovery.cs")
            || !Producer(acquired.HashContract.Canonicalizer.Source,
                "infrastructure-services/actions", actionsRevision,
                "tools/DatabaseReleaseQualification/CanonicalSchema.cs")
            || !Producer(acquired.HashContract.CompatibilityVerifier.Source,
                "infrastructure-services/actions", actionsRevision,
                "tools/DatabaseReleaseQualification/LegacyStructuralEvidence.cs"))
            return false;
        var expected = new Dictionary<string, (string Repository, string Revision,
            string Path)>(StringComparer.Ordinal) {
            ["governance"] = (acquired.Governance.SourceProvenance.SourceRepository,
                governanceRevision, "database-registry/resolve-governance-runtime.mjs"),
            ["onboarding"] = (acquired.Governance.SourceProvenance.SourceRepository,
                governanceRevision, "database-registry/resolve-governance-runtime.mjs"),
            ["registry"] = ("infrastructure-services/actions", actionsRevision,
                "tools/DatabaseReleaseQualification/TrustedLegacyRuntimeEvidenceResolver.cs"),
            ["repositoryDiscovery"] = ("infrastructure-services/actions", actionsRevision,
                "scripts/run-repository-discovery-v2-public.mjs"),
            ["sqlDiscovery"] = ("infrastructure-services/actions", actionsRevision,
                "scripts/run-sql-discovery-v2-public.mjs"),
            ["observedSnapshot"] = ("infrastructure-services/actions", actionsRevision,
                "tools/DatabaseReleaseQualification/SqlServerSchemaReader.cs")
        };
        return sources.Count == expected.Count && expected.All(pair =>
            sources.TryGetValue(pair.Key, out var source)
            && source.TargetId == acquired.Governance.TargetId
            && Producer(source.Producer.Source, pair.Value.Repository,
                pair.Value.Revision, pair.Value.Path));
    }

    private static bool Producer(LegacyGitDocumentV1 document, string repository,
        string revision, string path) => document.Repository.Host == "github.com"
            && document.Repository.FullName == repository
            && document.Revision == revision && document.Path == path;
}
