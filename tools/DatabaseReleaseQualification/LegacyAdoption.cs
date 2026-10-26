using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

// Selectors only: adoption neither discovers a release manifest nor executes
// historical SQL. This is a separate read-only consumer, not Macro 1 bypass.
public sealed record LegacyAdoptionRequestV1(int ContractVersion, string TargetId,
    LegacyRepositoryV1 Repository, string Commit);

public sealed record LegacyAdoptionCandidateV1(int ContractVersion, string TargetId,
    DatabaseIdentity Identity, string EndpointReference, string ServerInstance,
    string ApplicationCommit, string EngineCommit, string SchemaHash,
    string HashProfile, string LineageEvidenceHash, string EvidenceSetHash,
    RegistryProvenance RegistryProvenance, string Structure, string Data,
    string Security, string Incarnation, string CandidateHash);

public sealed record LegacyAdoptionPlanV1(int ContractVersion, string Status,
    IReadOnlyList<string> Reasons, LegacyAdoptionCandidateV1? Candidate,
    CertificationResult? CertificationDecision, DatabaseOnboardingResult? Onboarding,
    bool ExecutionAuthorized = false, bool Certified = false);

public static class LegacyAdoptionPlanner
{
    // A candidate is review material. Hash consistency is not authentication;
    // no plan produced here can be used as a write capability or a certificate.
    public static LegacyAdoptionPlanV1 Evaluate(LegacyAdoptionRequestV1 request,
        LegacyAcquiredRuntimeV1 acquired, DateTimeOffset now)
    {
        try
        {
            ValidateRequest(request);
            var g = acquired.Governance;
            var p = acquired.RegistryProvenance;
            if (request.Repository != acquired.ApplicationRepository || request.Commit != acquired.ApplicationCommit
                || request.TargetId != g.TargetId || g.Environment != "TEST" || g.DatabaseLifecycle != "EXISTING"
                || g.ChangeManagementMode != "LEGACY_UNMANAGED" || g.AuthorityReference.Kind != "REGISTRY_GIT_PR"
                || acquired.Onboarding.AuthorityReference.Kind != "ONBOARDING_GIT_PR"
                || acquired.Onboarding.Status is not ("REQUIRED" or "PENDING" or "MANAGED")
                || g.SourceProvenance.SourceSha256 != Hashing.Sha256(acquired.GovernanceBytes)
                || acquired.Onboarding.SourceProvenance.SourceSha256 != Hashing.Sha256(acquired.OnboardingBytes)
                || p.RegistryFileSha256 != Hashing.Sha256(acquired.RegistryBytes)
                || p.RegistryCommitSha != p.RegistryRef || p.RegistryCommitSha != g.SourceProvenance.SourceRevision
                || p.RegistryRepository != g.SourceProvenance.SourceRepository
                || acquired.Onboarding.SourceProvenance.SourceRevision != p.RegistryCommitSha
                || acquired.Onboarding.SourceProvenance.SourceRepository != p.RegistryRepository)
                throw new LegacyContractException("ADOPTION_IDENTITY_OR_PROVENANCE_MISMATCH");
            if (acquired.CapturedAtUtc > now || now - acquired.CapturedAtUtc > TimeSpan.FromMinutes(15))
                throw new LegacyContractException("ADOPTION_OBSERVATION_STALE");
            var observed = LegacyStructuralEvidence.FromCurrentCaptures(request.TargetId,
                g.Binding.EndpointReference, acquired.FirstCapture, acquired.SecondCapture,
                acquired.HashContract, acquired.CapturedAtUtc);
            if (!observed.CaptureComparison.StructuralMatch || !observed.CaptureComparison.IdentityMatch
                || observed.Metadata.SchemaCoverage != "COMPLETE" || observed.Snapshot.UnsupportedSchemaFeatures.Count != 0
                || observed.ObservedIdentity.DatabaseName != g.Binding.DatabaseName
                || string.IsNullOrWhiteSpace(g.Binding.EndpointReference)
                || g.Binding.ServerMatchPolicy != "ALLOW_LIST"
                || !g.Binding.AllowedServerInstances.Contains(observed.ObservedIdentity.ServerInstance, StringComparer.OrdinalIgnoreCase))
                throw new LegacyContractException("ADOPTION_OBSERVATION_INCOMPLETE");
            var repository = Discovery(acquired.RepositoryDiscoveryBytes, "REPOSITORY_DISCOVERY_V2_EOF");
            var sql = Discovery(acquired.SqlDiscoveryBytes, "SQL_DISCOVERY_V2_EOF");
            if (repository.GetProperty("status").GetString() != "ABSENT"
                || Status(sql, "connectionSource") != "SUCCEEDED"
                || Status(sql, "databaseLookupSource") != "FOUND"
                || Status(sql, "targetConnectionSource") != "SUCCEEDED"
                || Status(sql, "metadataSource") != "SUFFICIENT"
                || Status(sql, "physicalSource") != "OBSERVED"
                || Status(sql, "historySource") != "ABSENT")
                throw new LegacyContractException("ADOPTION_LEGACY_LINEAGE_UNPROVEN");
            var validation = DatabaseRegistryLoader.Validate(JsonSerializer.Deserialize<DatabaseRegistryDocument>(
                acquired.RegistryBytes, DatabaseStateJson.Compact), p);
            if (!validation.IsValid) throw new LegacyContractException("REGISTRY_VALIDATION_FAILED");
            var state = new DatabaseStateEvaluator().Evaluate(validation, new DatabaseStateObservation
            {
                ApplicationId = g.ApplicationId,
                Environment = g.Environment,
                DatabaseName = g.Binding.DatabaseName,
                ObservedSchemaHash = observed.ObservedSchemaHash,
                SchemaCoverage = "COMPLETE",
                CaptureTimestampUtc = acquired.CapturedAtUtc,
                RunId = acquired.Runtime.RunId,
                RunAttempt = acquired.Runtime.RunAttempt
            });
            var onboarding = new DatabaseOnboardingEvaluator().Evaluate(new()
            {
                DatabaseLifecycle = "EXISTING",
                DatabaseState = state,
                LineageAssessment = new()
                {
                    Scenario = "EXISTING_LEGACY",
                    SourceKind = "SQL",
                    Discovery = new() { ConsistencyStatus = "CONSISTENT", ConsistencyReason = "NONE" }
                }
            });
            if (state.DriftStatus == DatabaseDriftStatuses.TargetNotRegistered)
                return new(1, "REGISTRATION_REQUIRED", ["TARGET_REGISTRATION_REQUIRED"], null, null, onboarding);
            if (state.DriftStatus == DatabaseDriftStatuses.DriftDetected)
                return new(1, "BLOCKED", ["DRIFT_RECONCILIATION_REQUIRED"], null, null, onboarding);
            if (state.RegistryStatus == DatabaseCertificationStatuses.Certified)
                return new(1, "CERTIFIED_STATE_VERIFICATION_REQUIRED", ["PERSISTED_POINTER_AND_GOVERNED_TRANSITION_REQUIRED"], null, null, onboarding);
            if (state.RegistryStatus != DatabaseCertificationStatuses.BaselineRequired || onboarding.LineageState != LineageOnboardingState.LegacySql)
                throw new LegacyContractException("ADOPTION_BASELINE_STATE_INVALID");
            var lineage = LegacyRuntimeEvidenceHash.Hash(new { repository, sql });
            var evidence = LegacyRuntimeEvidenceHash.Hash(new
            {
                request,
                acquired.Governance,
                acquired.Onboarding,
                acquired.RegistryProvenance,
                observed,
                lineage,
                acquired.EngineCommit
            });
            var candidate = new LegacyAdoptionCandidateV1(1, request.TargetId,
                new(g.ApplicationId, g.Environment, g.Binding.DatabaseName), g.Binding.EndpointReference,
                observed.ObservedIdentity.ServerInstance, acquired.ApplicationCommit, acquired.EngineCommit,
                observed.ObservedSchemaHash, observed.HashContract.Profile, lineage, evidence, p,
                "COMPLETE", "NOT_VERIFIED", "NOT_VERIFIED", "NOT_VERIFIED", "");
            candidate = candidate with { CandidateHash = CalculateHash(candidate) };
            var decision = new CertificationDecisionEngine().Evaluate(new()
            {
                Origin = CertificationOrigin.BootstrapApproved,
                ObservedPreSchemaHash = candidate.SchemaHash
            });
            return new(1, "PENDING_INDEPENDENT_REVIEW",
                ["ADOPTION_DIMENSION_POLICY_REQUIRED", "DATABASE_INCARNATION_EVIDENCE_REQUIRED"], candidate, decision, onboarding);
        }
        catch (LegacyContractException e) { return new(1, "BLOCKED", [e.Code], null, null, null); }
        catch (Exception e) when (e is not OutOfMemoryException) { return new(1, "BLOCKED", ["ADOPTION_EVIDENCE_INVALID"], null, null, null); }
    }

    public static string CalculateHash(LegacyAdoptionCandidateV1 candidate) =>
        LegacyRuntimeEvidenceHash.Hash(candidate with { CandidateHash = "" });
    internal static void ValidateRequest(LegacyAdoptionRequestV1 request)
    {
        request.Repository.Validate();
        if (request.ContractVersion != 1 || !Regex.IsMatch(request.Commit, @"\A[0-9a-f]{40}\z")
            || !Regex.IsMatch(request.TargetId, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z"))
            throw new LegacyContractException("ADOPTION_REQUEST_INVALID");
    }
    private static JsonElement Discovery(byte[] bytes, string marker) => JsonDocument.Parse(
        LegacyProductionRuntimeAcquisition.ReadDiscoveryOutput(new UTF8Encoding(false, true).GetString(bytes),
            "evidence-json", marker)).RootElement.Clone();
    private static string? Status(JsonElement source, string key) => source.GetProperty(key).GetProperty("status").GetString();
}

internal static class LegacyAdoptionCli
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        try
        {
            if (args.Length != 4 || args[2] != "--output") return 65;
            if (new FileInfo(args[1]).Length > 64 * 1024) return 65;
            var bytes = await File.ReadAllBytesAsync(args[1], token);
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            Exact(doc.RootElement, ["contractVersion", "targetId", "repository", "commit"]);
            Exact(doc.RootElement.GetProperty("repository"), ["host", "repositoryId", "fullName"]);
            var request = JsonSerializer.Deserialize<LegacyAdoptionRequestV1>(bytes, JsonDefaults.Compact)
                ?? throw new LegacyContractException("ADOPTION_REQUEST_INVALID");
            LegacyAdoptionPlanner.ValidateRequest(request);
            var acquisition = new LegacyProductionRuntimeAcquisition();
            var acquired = await acquisition.AcquireForAdoptionAsync(request, token);
            var plan = LegacyAdoptionPlanner.Evaluate(request, acquired, DateTimeOffset.UtcNow);
            await acquisition.VerifySourceFreshnessAsync(request.Commit, token);
            Directory.CreateDirectory(args[3]);
            await using var file = new FileStream(Path.Combine(args[3], "legacy-adoption-plan.json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(file, plan, JsonDefaults.Compact, token);
            return plan.Status == "BLOCKED" ? 66 : 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (LegacyContractException e) { Console.Error.WriteLine("LEGACY_ADOPTION_BLOCKED:" + e.Code); return 66; }
        catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine("LEGACY_ADOPTION_BLOCKED:INVALID_REQUEST_OR_SOURCE"); return 65; }
    }
    private static void Exact(JsonElement element, string[] names)
    {
        var actual = element.EnumerateObject().Select(x => x.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct().Count() != names.Length || names.Except(actual).Any())
            throw new LegacyContractException("ADOPTION_REQUEST_INVALID");
    }
}
