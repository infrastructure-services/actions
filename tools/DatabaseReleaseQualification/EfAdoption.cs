using System.Text.Json;

namespace DatabaseReleaseQualification;

// Complementary to V1–V6: none of these states authorizes a migration. All
// references are selectors acquired through the existing purpose-aware host.
public sealed record EfAdoptionSelectionV1(EvidenceReference Lineage,
    EvidenceReference? Review = null, EvidenceReference? Approval = null,
    EvidenceReference? Persisted = null, EvidenceReference? Transition = null);
public sealed record EfAdoptionPolicyV1(int Version, string RegistrationState,
    string StructuralState, RegistryProvenance Registry);
public sealed record EfAdoptionLineageV1(int Version, EfEvidenceScopeV1 Scope,
    EvidenceReference Source, EvidenceReference Pre, EfPlanProvenanceV1 HistoricalSource,
    string[] HistoricalMigrationIds, EvidenceReference HistoricalRepositoryEvidence);
public sealed record EfAdoptionRepositoryV1(int Version, EfEvidenceScopeV1 Scope,
    string Repository, string Revision, JsonElement RepositorySource,
    Dictionary<string, EvidenceReference> ArtifactEvidence);
public sealed record EfAdoptionReviewV1(int Version, EfEvidenceScopeV1 Scope,
    EvidenceReference Source, EvidenceReference Pre, EvidenceReference Lineage,
    string SchemaHash, string DataHash, string SecurityHash, string Disposition,
    string[] Findings, EvidenceReference SupportingEvidence);
public sealed record EfAdoptionApprovalV1(int Version, EfEvidenceScopeV1 Scope,
    string OperationId, string CandidateHash, string DatabaseIncarnation,
    DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresAtUtc,
    CertificationApprovalRequirement Granted, CommitAuthorizationContext Actor);
public sealed record EfAdoptionPersistedV1(EfEvidenceScopeV1 Scope, string CandidateHash,
    CertifiedStateRecord Record, CertificationPointer Pointer, CertificationCommitReceipt Receipt,
    DateTimeOffset CheckedAtUtc);
public sealed record EfAdoptionTransitionV1(int Version, EfEvidenceScopeV1 Scope,
    string CandidateHash, string CertificationId, string CertificationEvidenceHash,
    string DatabaseIncarnation, EfPlanProvenanceV1 Governance, EfPlanProvenanceV1 Onboarding,
    string Lifecycle, string ChangeManagementMode, string OnboardingState,
    EvidenceReference CertifiedBaseline, DateTimeOffset CheckedAtUtc, EvidenceReference GovernedState);
public sealed record EfAdoptionManifestV1(int Version, EfEvidenceScopeV1 Scope, string OperationId,
    EfGovernedPreparationV1 Governed, EfPlanSourceV1 Source, EfObservationEvidenceV1 Pre,
    EfAdoptionLineageV1 Lineage, EfAdoptionReviewV1? Review,
    EvidenceReference GovernanceReference, EvidenceReference SourceReference,
    EvidenceReference PreReference, EvidenceReference LineageReference,
    EvidenceReference? ReviewReference, EvidenceReference[] Evidence,
    DateTimeOffset PreparedAtUtc, DateTimeOffset ExpiresAtUtc);
public sealed class EfAdoptionCandidateV1
{
    internal EfAdoptionCandidateV1(EfAdoptionManifestV1 value)
    { Json = JsonSerializer.Serialize(value, JsonDefaults.Compact); Sha256 = Hashing.Sha256(Json); }
    public string Json { get; }
    public string Sha256 { get; }
    public EfAdoptionManifestV1 Read() => EfEvidencePreparationV1.ReadAdoptionManifest(Json);
}
public sealed record EfAdoptionResultV1(string Status, EfAdoptionCandidateV1? Candidate,
    string[] Reasons, string? ManagedVariant = null, string? EvidenceSetHash = null)
{
    public bool ExecutionAuthorized => false;
    public bool Certified => false;
}
public sealed record EfAdoptionCommitResultV1(string Status, CertificationCommitResult? Commit, string[] Reasons);

public sealed partial class EfEvidencePreparationV1
{
    internal static EfAdoptionManifestV1 ReadAdoptionManifest(string json)
        => JsonSerializer.Deserialize<EfAdoptionManifestV1>(json, Strict)!;
    private static string AdoptionJson<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Compact);
    private static void AdoptionIds(string[] ids)
        => Require(ids.Length > 0 && ids.All(x => System.Text.RegularExpressions.Regex.IsMatch(x,
            @"\A[0-9]{14}_[A-Za-z0-9_]+\z")) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length
            && ids.SequenceEqual(ids.Order(StringComparer.Ordinal)), "ADOPTION_LINEAGE_INVALID");
    private async Task<Dictionary<string, string>> RepositoryLineage(EfEvidenceScopeV1 scope, EvidenceReference reference,
        string[] ids, string revision, string projectPath, string purpose, EvidenceLog used, CancellationToken token)
    {
        var document = await Document<EfAdoptionRepositoryV1>(scope, reference, purpose, used, token);
        Require(document.Version == 1 && document.Scope == scope && document.Repository == scope.SourceRepository
            && document.Revision == revision, "ADOPTION_REPOSITORY_PROVENANCE_MISMATCH");
        var raw = document.RepositorySource;
        Require(raw.GetProperty("adoption").GetProperty("status").GetString() == "VALID"
            && raw.GetProperty("adoption").GetProperty("projectPath").GetString() == projectPath
            && raw.GetProperty("adoption").GetProperty("sourceRevision").GetString() == revision,
            "ADOPTION_REPOSITORY_PROVENANCE_MISMATCH");
        Require(raw.GetProperty("status").GetString() == "PRESENT_VALID"
            && raw.GetProperty("migrations").GetProperty("count").GetInt32() == ids.Length
            && raw.GetProperty("migrations").GetProperty("ids").EnumerateArray()
                .Select(x => x.GetString()).SequenceEqual(ids), "ADOPTION_REPOSITORY_LINEAGE_MISMATCH");
        var detail = raw.GetProperty("rawEvidence");
        Require(detail.GetProperty("rawEvidenceContractVersion").GetInt32() == 1
            && detail.GetProperty("anomalies").GetArrayLength() == 0, "ADOPTION_REPOSITORY_PROOF_REQUIRED");
        var valid = detail.GetProperty("validMigrations").EnumerateArray().ToArray();
        Require(valid.Length == ids.Length && ids.All(id => valid.Count(m => m.GetProperty("id").GetString() == id) == 1),
            "ADOPTION_REPOSITORY_PROOF_REQUIRED");
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var migration = valid.Single(m => m.GetProperty("id").GetString() == id);
            Require(migration.GetProperty("projectPath").GetString() == projectPath, "ADOPTION_REPOSITORY_PROVENANCE_MISMATCH");
            var parts = new List<string>();
            foreach (var key in new[] { "bodyPath", "designerPath" })
            {
                var path = migration.GetProperty(key).GetString()!;
                var artifacts = detail.GetProperty("migrationArtifacts").EnumerateArray()
                    .Where(x => x.GetProperty("path").GetString() == path).ToArray();
                Require(artifacts.Length == 1 && document.ArtifactEvidence.TryGetValue(path, out var file)
                    && file.EvidenceSha256 == artifacts[0].GetProperty("sha256").GetString(), "ADOPTION_ARTIFACT_HASH_MISMATCH");
                _ = await Read(scope, document.ArtifactEvidence[path], purpose + "_FILE", used, token);
                parts.Add(document.ArtifactEvidence[path].EvidenceSha256);
            }
            fingerprints.Add(id, Hashing.Sha256(string.Join("\n", parts)));
        }
        return fingerprints;
    }

    public async Task<EfAdoptionResultV1> PrepareAdoptionAsync(EfPreparationSelectionV1 selection,
        DateTimeOffset now, CancellationToken token = default)
    {
        try
        {
            Require(provider is not null, "AUTHORIZED_PROVIDER_REQUIRED");
            var s = Clone(selection); var scope = s.Scope; var a = s.Adoption;
            Require(a is not null && s.Qualification is null && Guid.TryParseExact(s.OperationId, "D", out var operationId) && operationId != Guid.Empty,
                "ADOPTION_SELECTION_REQUIRED");
            Require(s.PreSources is not null, "ADOPTION_PRE_SOURCES_REQUIRED");
            var used = new EvidenceLog();
            var g = await Document<EfGovernedPreparationV1>(scope, s.Governance, "GOVERNANCE", used, token);
            var policy = g.AdoptionPolicy; var target = g.Authority.Target;
            Require(g.ClassificationRulesVersion == 2 && policy is { Version: 1 }
                && policy.RegistrationState is "REGISTERED" or "REGISTRATION_REQUIRED"
                && policy.StructuralState is "BASELINE_REQUIRED" or "CERTIFIED"
                && g.Authority.Scenario == "EXISTING_EF" && g.Authority.Lifecycle == "EXISTING"
                && g.Authority.ChangeManagementMode == "EF_MIGRATIONS" && target.Environment == "TEST"
                && target.TargetId == scope.TargetId && target.SourceRepository == scope.SourceRepository
                && g.Authority.BootstrapEvidence is null && g.Policy.MaximumValiditySeconds is > 0 and <= 86400,
                "ADOPTION_GOVERNANCE_INVALID");
            await Provenance(scope, g.Authority.Governance, "GOVERNANCE_BYTES", used, token);
            await Provenance(scope, g.Authority.Onboarding, "ONBOARDING_BYTES", used, token);
            await Provenance(scope, g.Policy.Provenance, "POLICY", used, token);
            var source = await Document<EfSourcePreparationV1>(scope, s.Source, "SOURCE", used, token);
            Require(source.Forward.Length == 0 && source.Rollback.Length == 0
                && source.Source.Repository == scope.SourceRepository && source.Source.Revision == scope.SourceRevision
                && Repository(scope.SourceRepository) && Revision(scope.SourceRevision), "ADOPTION_SOURCE_INVALID");
            AdoptionIds(source.Source.ManagedMigrationIds);
            var currentArtifacts = await RepositoryLineage(scope, source.Source.RepositoryEvidence, source.Source.ManagedMigrationIds, scope.SourceRevision, source.Source.ProjectPath,
                "REPOSITORY_DISCOVERY_V2", used, token);
            var pre = s.PreSources is null
                ? await Document<EfObservationEvidenceV1>(scope, s.Pre, "TARGET_PRE", used, token)
                : await NormalizeObservation(s, g, now, used, token);
            await Observation(scope, pre, "TARGET_PRE", g.ContentHashContract, used, token, s.PreSources is not null);
            var o = pre.Observation; var state = EfReleasePlannerV1.Observe(o, target, now);
            Require(o.Schema.ImpactMetrics.All(m => m.RowCount >= 0 && m.ReservedMb >= 0 && m.IndexMb >= 0)
                && o.Schema.ImpactMetrics.Select(m => (m.Schema, m.Table)).Distinct().Count() == o.Schema.ImpactMetrics.Count
                && o.Schema.Objects.Where(x => x.Kind == "table").All(t =>
                o.Schema.ImpactMetrics.Count(m => m.Schema == t.Schema && m.Table == t.Name && m.RowCount >= 0) == 1),
                "ADOPTION_IMPACT_COVERAGE_REQUIRED");
            Require(o.HistoryStatus == "PRESENT", "ADOPTION_POPULATED_HISTORY_REQUIRED");
            Require((now - o.CapturedAtUtc).TotalSeconds <= g.Policy.MaximumValiditySeconds,
                "ADOPTION_OBSERVATION_STALE");
            AdoptionIds(o.AppliedMigrationIds);
            Require(o.AppliedMigrationIds.Length <= source.Source.ManagedMigrationIds.Length
                && o.AppliedMigrationIds.SequenceEqual(source.Source.ManagedMigrationIds.Take(o.AppliedMigrationIds.Length)),
                "ADOPTION_HISTORY_DIVERGED");
            Require(EfPreprovisionedBootstrap.ValidHistorySchema(o.Schema.Objects.Where(x => x.Schema == "dbo"
                && (x.Name == "__EFMigrationsHistory" || x.Parent == "__EFMigrationsHistory"))), "ADOPTION_HISTORY_STRUCTURE_INVALID");
            var hashes = await Document<EfContentHashContractV1>(scope, g.ContentHashContract, "CONTENT_HASH_CONTRACT", used, token);
            Require(hashes.Version == 1 && Hash(hashes.ScopeHash), "CONTENT_HASH_CONTRACT_INVALID");
            await Provenance(scope, hashes.DataProjection, "DATA_PROJECTION", used, token);
            await Provenance(scope, hashes.SecurityProjection, "SECURITY_PROJECTION", used, token);
            var lineage = await Document<EfAdoptionLineageV1>(scope, a!.Lineage, "EF_ADOPTION_LINEAGE", used, token);
            Require(lineage.Version == 1 && lineage.Scope == scope && lineage.Source == s.Source && lineage.Pre == s.Pre
                && lineage.HistoricalSource.Repository == scope.SourceRepository
                && source.Source.AdoptionProvenance == lineage.HistoricalSource
                && Revision(lineage.HistoricalSource.Revision), "ADOPTION_LINEAGE_BINDING_MISMATCH");
            AdoptionIds(lineage.HistoricalMigrationIds);
            Require(o.AppliedMigrationIds.SequenceEqual(lineage.HistoricalMigrationIds.Take(o.AppliedMigrationIds.Length))
                && lineage.HistoricalMigrationIds.Length <= source.Source.ManagedMigrationIds.Length
                && lineage.HistoricalMigrationIds.SequenceEqual(source.Source.ManagedMigrationIds.Take(lineage.HistoricalMigrationIds.Length)),
                "ADOPTION_HISTORICAL_LINEAGE_DIVERGED");
            await Provenance(scope, lineage.HistoricalSource, "EF_ADOPTION_HISTORICAL_SOURCE", used, token);
            var historicalArtifacts = await RepositoryLineage(scope, lineage.HistoricalRepositoryEvidence, lineage.HistoricalMigrationIds, lineage.HistoricalSource.Revision, source.Source.ProjectPath,
                "EF_ADOPTION_HISTORICAL_REPOSITORY", used, token);
            Require(lineage.HistoricalMigrationIds.All(id => currentArtifacts[id] == historicalArtifacts[id]),
                "ADOPTION_HISTORICAL_ARTIFACT_DIVERGED");
            EfAdoptionReviewV1? review = null;
            if (a.Review is not null)
            {
                review = await Document<EfAdoptionReviewV1>(scope, a.Review, "EF_ADOPTION_STATE_REVIEW", used, token);
                Require(review.Version == 1 && review.Scope == scope && review.Source == s.Source && review.Pre == s.Pre
                    && review.Lineage == a.Lineage && review.SchemaHash == state.SchemaHash
                    && review.DataHash == state.DataHash && review.SecurityHash == state.SecurityHash
                    && review.Disposition is "VERIFIED" or "REVIEW_REQUIRED" && review.Findings.Length == 0,
                    "ADOPTION_STATE_UNRESOLVED");
                _ = await Read(scope, review.SupportingEvidence, "EF_ADOPTION_STATE_REVIEW_BYTES", used, token);
            }
            var candidate = new EfAdoptionCandidateV1(new(1, scope, s.OperationId, g, source.Source, pre, lineage, review,
                s.Governance, s.Source, s.Pre, a.Lineage, a.Review,
                used.Distinct().OrderBy(x => x.EvidenceId, StringComparer.Ordinal).ToArray(), o.CapturedAtUtc,
                o.CapturedAtUtc.AddSeconds(g.Policy.MaximumValiditySeconds)));
            await provider!.VerifyCurrentAsync(scope, used.Select(Clone).ToArray(), token);
            if (policy!.RegistrationState == "REGISTRATION_REQUIRED")
                return new("ADOPTION_REQUIRED", candidate, ["TARGET_REGISTRATION_REQUIRED"], EvidenceSetHash: EvidenceHash(used));
            if (a.Persisted is not null)
                return await VerifyAdoptionManaged(s, candidate, now, used, token);
            Require(policy.StructuralState != "CERTIFIED" && g.Authority.CertifiedBaseline is null,
                "ADOPTION_EXISTING_CERTIFICATION_REQUIRED");
            if (review is null) return new("ADOPTION_REQUIRED", candidate, ["ADOPTION_STATE_REVIEW_REQUIRED"], EvidenceSetHash: EvidenceHash(used));
            if (a.Approval is null) return new("PENDING_APPROVAL", candidate, [], EvidenceSetHash: EvidenceHash(used));
            _ = await AdoptionApproval(candidate, a.Approval, now, used, token);
            await provider.VerifyCurrentAsync(scope, used.Select(Clone).ToArray(), token);
            return new("CERTIFICATION_COMMIT_REQUIRED", candidate, [], EvidenceSetHash: EvidenceHash(used));
        }
        catch (Block e) { return new("BLOCKED", null, [e.Message]); }
        catch (OperationCanceledException) { return new("BLOCKED", null, ["ADOPTION_ACQUISITION_CANCELLED"]); }
        catch (UnauthorizedAccessException) { return new("BLOCKED", null, ["ADOPTION_EVIDENCE_NOT_AUTHORIZED"]); }
        catch (JsonException) { return new("BLOCKED", null, ["ADOPTION_EVIDENCE_DOCUMENT_INVALID"]); }
        catch { return new("BLOCKED", null, ["ADOPTION_EVIDENCE_NOT_VERIFIABLE"]); }
    }

    private async Task<EfAdoptionApprovalV1> AdoptionApproval(EfAdoptionCandidateV1 candidate,
        EvidenceReference approval, DateTimeOffset now, EvidenceLog used, CancellationToken token)
    {
        var p = candidate.Read();
        var a = await Document<EfAdoptionApprovalV1>(p.Scope, approval, "EF_ADOPTION_APPROVAL", used, token);
        Require(a.Version == 1 && a.Scope == p.Scope && a.OperationId == p.OperationId && a.CandidateHash == candidate.Sha256
            && a.DatabaseIncarnation == p.Pre.Observation.DatabaseIncarnation && a.NotBeforeUtc <= now
            && now < a.ExpiresAtUtc && now < p.ExpiresAtUtc && a.ExpiresAtUtc <= p.ExpiresAtUtc
            && a.Granted is CertificationApprovalRequirement.Human or CertificationApprovalRequirement.Dba
            && !string.IsNullOrWhiteSpace(a.Actor.ActorId) && !string.IsNullOrWhiteSpace(a.Actor.PolicyId)
            && !string.IsNullOrWhiteSpace(a.Actor.Mechanism) && !string.IsNullOrWhiteSpace(a.Actor.AuthorityReference),
            "ADOPTION_APPROVAL_INVALID");
        Require(p.Review is not null, "ADOPTION_STATE_REVIEW_REQUIRED");
        return a;
    }

    private async Task<EfAdoptionResultV1> VerifyAdoptionManaged(EfPreparationSelectionV1 s,
        EfAdoptionCandidateV1 candidate, DateTimeOffset now, EvidenceLog used, CancellationToken token)
    {
        var p = candidate.Read(); var a = s.Adoption!;
        var persisted = await Document<EfAdoptionPersistedV1>(s.Scope, a.Persisted!, "EF_ADOPTION_PERSISTED_STATE", used, token);
        var canonicalBytes = await Read(s.Scope, persisted.Record.CanonicalSchemaEvidenceReference,
            "CERTIFIED_CANONICAL_SCHEMA", used, token);
        var canonicalText = System.Text.Encoding.UTF8.GetString(canonicalBytes);
        var canonicalDocument = JsonSerializer.Deserialize<CanonicalSchemaDocument>(canonicalBytes, Strict)!;
        var record = persisted.Record with { CanonicalSchemaEvidence = new(canonicalDocument, canonicalText, Hashing.Sha256(canonicalBytes)) };
        var pointer = persisted.Pointer; var receipt = persisted.Receipt;
        Require(CertifiedStateRecordValidator.Validate(record).Count == 0, "ADOPTION_CERTIFIED_RECORD_INVALID");
        Require(record.DatabaseIdentity == new DatabaseIdentity(p.Governed.Authority.Target.ApplicationId, "TEST", p.Pre.Observation.DatabaseName),
            "ADOPTION_CERTIFIED_IDENTITY_MISMATCH");
        Require(record.CanonicalSchemaHash == EfReleasePlannerV1.Observe(p.Pre.Observation, p.Governed.Authority.Target, now).SchemaHash,
            "ADOPTION_CERTIFIED_SCHEMA_MISMATCH");
        Require(Hash(persisted.CandidateHash) && (record.CertificationOrigin != CertificationOrigin.BootstrapApproved
            || record.LineageEvidence.EvidenceSha256 == persisted.CandidateHash), "ADOPTION_PERSISTENCE_BINDING_MISMATCH");
        Require(persisted.Scope == s.Scope && persisted.CheckedAtUtc <= now
            && (now - persisted.CheckedAtUtc).TotalSeconds <= p.Governed.Policy.MaximumValiditySeconds
            && pointer.DatabaseIdentity == record.DatabaseIdentity && pointer.CertificationId == record.CertificationId
            && pointer.CertificationEvidenceHash == record.CertificationEvidenceHash && pointer.Version > 0
            && receipt.Status == CertificationCommitReceiptStatus.Committed && receipt.DatabaseIdentity == record.DatabaseIdentity
            && receipt.CertificationId == record.CertificationId && receipt.CertificationEvidenceHash == record.CertificationEvidenceHash,
            "ADOPTION_PERSISTENCE_UNVERIFIED");
        if (p.Governed.AdoptionPolicy!.StructuralState != "CERTIFIED")
            Require(persisted.CandidateHash == candidate.Sha256 && record.LineageEvidence.EvidenceSha256 == candidate.Sha256
                && record.CertificationOrigin == CertificationOrigin.BootstrapApproved && receipt.OperationId == p.OperationId,
                "ADOPTION_PERSISTENCE_BINDING_MISMATCH");
        if (a.Transition is null)
        {
            await provider!.VerifyCurrentAsync(s.Scope, used.Select(Clone).ToArray(), token);
            return new("GOVERNANCE_TRANSITION_REQUIRED", candidate, [], EvidenceSetHash: EvidenceHash(used));
        }
        var transition = await Document<EfAdoptionTransitionV1>(s.Scope, a.Transition, "EF_ADOPTION_GOVERNANCE_TRANSITION", used, token);
        Require(transition.Version == 1 && transition.Scope == s.Scope && transition.CandidateHash == persisted.CandidateHash
            && transition.CertificationId == record.CertificationId && transition.CertificationEvidenceHash == record.CertificationEvidenceHash
            && transition.DatabaseIncarnation == p.Pre.Observation.DatabaseIncarnation && transition.Lifecycle == "EXISTING"
            && transition.ChangeManagementMode == "EF_MIGRATIONS" && transition.OnboardingState == "MANAGED"
            && transition.CheckedAtUtc <= now && (now - transition.CheckedAtUtc).TotalSeconds <= p.Governed.Policy.MaximumValiditySeconds,
            "ADOPTION_GOVERNANCE_TRANSITION_INVALID");
        await Provenance(s.Scope, transition.Governance, "GOVERNANCE_BYTES", used, token);
        await Provenance(s.Scope, transition.Onboarding, "ONBOARDING_BYTES", used, token);
        var governed = await Document<EfGovernedPreparationV1>(s.Scope, transition.GovernedState, "GOVERNANCE", used, token);
        Require(Equal(governed.Authority.Target, p.Governed.Authority.Target)
            && governed.Authority.Governance == transition.Governance && governed.Authority.Onboarding == transition.Onboarding
            && governed.Authority.OnboardingState == "MANAGED" && governed.Authority.Lifecycle == "EXISTING"
            && governed.Authority.ChangeManagementMode == "EF_MIGRATIONS" && governed.Authority.Scenario == "EXISTING_EF"
            && governed.Authority.CertifiedBaseline?.SchemaHash == record.CanonicalSchemaHash
            && governed.Authority.CertifiedBaseline.Provenance.Document == transition.CertifiedBaseline,
            "ADOPTION_GOVERNED_BASELINE_MISMATCH");
        var baselineBytes = await Read(s.Scope, transition.CertifiedBaseline, "CERTIFIED_BASELINE", used, token);
        Require(Hashing.Sha256(AdoptionJson(record)) == Hashing.Sha256(baselineBytes), "ADOPTION_BASELINE_RECORD_MISMATCH");
        var state = new DatabaseStateEvaluation { ApplicationId = record.ApplicationId, Environment = "TEST", DatabaseName = record.DatabaseName,
            ObservedSchemaHash = record.CanonicalSchemaHash, CertifiedSchemaHash = record.CanonicalSchemaHash,
            RegistryStatus = "CERTIFIED", DriftStatus = DatabaseDriftStatuses.Match, GateStatus = "MATCH", Reason = "ADOPTION_CERTIFIED",
            Target = new() { Lifecycle = "EXISTING", CertificationStatus = "CERTIFIED", CertifiedSchemaHash = record.CanonicalSchemaHash } };
        var onboarded = new DatabaseOnboardingEvaluator().Evaluate(new() { DatabaseLifecycle = "EXISTING", DatabaseState = state,
            LineageAssessment = new() { Scenario = "EXISTING_EF", SourceKind = "EF", Discovery = new() {
                ConsistencyStatus = "CONSISTENT", ConsistencyReason = "ADOPTION_VERIFIED_LINEAGE" } } });
        Require(onboarded.IsManaged, "ADOPTION_ONBOARDING_NOT_MANAGED");
        await provider!.VerifyCurrentAsync(s.Scope, used.Select(Clone).ToArray(), token);
        return new("EXISTING_EF_MANAGED", candidate, [],
            p.Pre.Observation.AppliedMigrationIds.Length < p.Source.ManagedMigrationIds.Length ? "V4" : "V5", EvidenceHash(used));
    }
}
