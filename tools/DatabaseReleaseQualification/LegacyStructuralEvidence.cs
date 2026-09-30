using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabaseReleaseQualification;

public sealed record LegacyGitDocumentV1(LegacyRepositoryV1 Repository,
    string Revision, string Path, string RawSha256);

public sealed record LegacyProducerRefV1(LegacyGitDocumentV1 Source,
    string Contract, int ContractVersion);

public sealed record StructuralHashContractV1(int ContractVersion, string Profile,
    int RegistryFormatVersion, int CanonicalSchemaFormatVersion, string Algorithm,
    LegacyProducerRefV1 Canonicalizer, LegacyProducerRefV1 CompatibilityVerifier)
{
    public const string SupportedProfile = "REGISTRY_V1_CANONICAL_SCHEMA_V1";

    public bool IsSupported => ContractVersion == 1 && Profile == SupportedProfile
        && RegistryFormatVersion == 1 && CanonicalSchemaFormatVersion == 1
        && Algorithm == "SHA-256" && Canonicalizer.ContractVersion > 0
        && CompatibilityVerifier.ContractVersion > 0;

    public string Hash(SchemaSnapshot snapshot)
    {
        if (!IsSupported || snapshot.FormatVersion != 1)
            throw new LegacyContractException("STRUCTURAL_HASH_CONTRACT_MISMATCH");
        // CanonicalSchema.Json is the compact UTF-8 document hashed by Registry V1.
        // It is deliberately distinct from the newline-terminated file artifact.
        return SchemaCanonicalizer.Canonicalize(snapshot).Sha256;
    }
}

public sealed record LegacyRegistryTargetIdentityV1(string ApplicationId,
    string Environment, string DatabaseName, string Lifecycle);

public sealed record CertifiedStructuralBaselineV1(int ContractVersion, string Kind,
    string TargetId, LegacyRegistryTargetIdentityV1 RegistryTarget, int RegistryFormatVersion,
    string CertificationStatus, string CertifiedSchemaHash,
    RegistryProvenance RegistryProvenance,
    LegacySourceProvenanceV1 GovernanceProvenance);

public sealed record ObservedSnapshotMetadataV1(string Visibility,
    string SchemaCoverage, string MetricsAvailability);

public sealed record ObservedSnapshotIdentityV1(string ServerInstance,
    string DatabaseName);

public sealed record ObservedCaptureComparisonV1(string FirstSchemaHash,
    string SecondSchemaHash, ObservedSnapshotIdentityV1 FirstIdentity,
    ObservedSnapshotIdentityV1 SecondIdentity, bool StructuralMatch,
    bool IdentityMatch);

public sealed record ObservedCurrentSnapshotV1(int ContractVersion, string Kind,
    string TargetId,
    string Environment, string EndpointReference,
    ObservedSnapshotIdentityV1 ObservedIdentity, SchemaSnapshot Snapshot,
    string ObservedSchemaHash, StructuralHashContractV1 HashContract,
    ObservedSnapshotMetadataV1 Metadata,
    ObservedCaptureComparisonV1 CaptureComparison, DateTimeOffset CapturedAtUtc);

public static class LegacyStructuralEvidence
{
    private static readonly JsonSerializerOptions RequiredNulls = new(JsonDefaults.Compact) {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Serialize<T>(T evidence) => JsonSerializer.Serialize(evidence, RequiredNulls);

    public static ObservedCurrentSnapshotV1 FromCurrentCaptures(string targetId,
        string endpointReference, SchemaCaptureSourceResult first,
        SchemaCaptureSourceResult second, StructuralHashContractV1 contract,
        DateTimeOffset capturedAtUtc)
    {
        if (!contract.IsSupported)
            throw new LegacyContractException("STRUCTURAL_HASH_CONTRACT_MISMATCH");
        if (first.Snapshot.Objects is null || first.Snapshot.ImpactMetrics is null
            || first.Snapshot.UnsupportedSchemaFeatures is null
            || second.Snapshot.Objects is null || second.Snapshot.ImpactMetrics is null
            || second.Snapshot.UnsupportedSchemaFeatures is null)
            throw new LegacyContractException("OBSERVED_EVIDENCE_UNVERIFIED");
        var firstIdentity = new ObservedSnapshotIdentityV1(first.ServerInstance, first.DatabaseName);
        var secondIdentity = new ObservedSnapshotIdentityV1(second.ServerInstance, second.DatabaseName);
        if (!ObservedDatabaseIdentityContract.IsValid(first.ServerInstance, first.DatabaseName)
            || !ObservedDatabaseIdentityContract.IsValid(second.ServerInstance, second.DatabaseName))
            throw new LegacyContractException("TARGET_IDENTITY_MISMATCH");
        var firstCanonical = SchemaCanonicalizer.Canonicalize(first.Snapshot);
        var secondCanonical = SchemaCanonicalizer.Canonicalize(second.Snapshot);
        var structuralMatch = firstCanonical.Sha256 == secondCanonical.Sha256
            && SchemaComparer.Compare(firstCanonical, secondCanonical).IsEquivalent;
        var identityMatch = firstIdentity == secondIdentity;
        return new(1, "OBSERVED_CURRENT_SNAPSHOT", targetId, "TEST",
            endpointReference, firstIdentity, first.Snapshot, firstCanonical.Sha256,
            contract,
            new("SUFFICIENT", first.Snapshot.SchemaCoverage.ToString().ToUpperInvariant(),
                first.MetricsAvailability.ToString().ToUpperInvariant()),
            new(firstCanonical.Sha256, secondCanonical.Sha256,
                firstIdentity, secondIdentity, structuralMatch, identityMatch),
            capturedAtUtc);
    }

    public static string Verify(CertifiedStructuralBaselineV1 baseline,
        ObservedCurrentSnapshotV1 observed, LegacyGovernanceV1 governance,
        DatabaseStateEvaluation state)
    {
        if (baseline.ContractVersion != 1 || baseline.Kind != "CERTIFIED_STRUCTURAL_BASELINE"
            || baseline.TargetId != governance.TargetId
            || baseline.RegistryFormatVersion != 1
            || baseline.CertificationStatus != DatabaseCertificationStatuses.Certified
            || baseline.RegistryTarget.Environment != "TEST"
            || baseline.RegistryTarget.Lifecycle != "EXISTING"
            || baseline.RegistryTarget.ApplicationId != governance.ApplicationId
            || baseline.RegistryTarget.DatabaseName != governance.Binding.DatabaseName
            || baseline.RegistryProvenance.RegistryRepository != governance.SourceProvenance.SourceRepository
            || baseline.RegistryProvenance.RegistryCommitSha != governance.SourceProvenance.SourceRevision
            || baseline.RegistryProvenance.RegistryRef != baseline.RegistryProvenance.RegistryCommitSha
            || state.RegistryProvenance is null
            || baseline.RegistryProvenance.RegistryFilePath != state.RegistryProvenance.RegistryFilePath
            || baseline.RegistryProvenance.RegistryFileSha256 != state.RegistryProvenance.RegistryFileSha256
            || baseline.RegistryProvenance.RegistryRef != state.RegistryProvenance.RegistryRef
            || baseline.RegistryProvenance.RegistryCommitSha != state.RegistryProvenance.RegistryCommitSha
            || baseline.RegistryProvenance.RegistryRepository != state.RegistryProvenance.RegistryRepository
            || baseline.GovernanceProvenance != governance.SourceProvenance)
            throw new LegacyContractException("BASELINE_NOT_CERTIFIED");
        if (observed.ContractVersion != 1 || observed.Kind != "OBSERVED_CURRENT_SNAPSHOT"
            || observed.TargetId != governance.TargetId
            || observed.Environment != "TEST"
            || observed.EndpointReference != governance.Binding.EndpointReference
            || observed.ObservedIdentity.DatabaseName != governance.Binding.DatabaseName
            || governance.Binding.ServerMatchPolicy == "ALLOW_LIST"
                && !governance.Binding.AllowedServerInstances.Contains(
                    observed.ObservedIdentity.ServerInstance, StringComparer.OrdinalIgnoreCase))
            throw new LegacyContractException("EVIDENCE_CORRELATION_MISMATCH");
        if (!observed.HashContract.IsSupported)
            throw new LegacyContractException("STRUCTURAL_HASH_CONTRACT_MISMATCH");
        if (observed.Snapshot.Objects is null || observed.Snapshot.ImpactMetrics is null
            || observed.Snapshot.UnsupportedSchemaFeatures is null
            || observed.Metadata.Visibility != "SUFFICIENT"
            || observed.Metadata.SchemaCoverage != "COMPLETE"
            || observed.Snapshot.SchemaCoverage != SchemaCoverage.Complete)
            throw new LegacyContractException("OBSERVED_METADATA_INCOMPLETE");
        if (observed.Metadata.MetricsAvailability != "COMPLETE")
            throw new LegacyContractException("OBSERVED_IMPACT_METRICS_INCOMPLETE");
        var actual = observed.HashContract.Hash(observed.Snapshot);
        if (actual != observed.ObservedSchemaHash
            || !observed.CaptureComparison.StructuralMatch
            || !observed.CaptureComparison.IdentityMatch
            || observed.CaptureComparison.FirstSchemaHash != actual
            || observed.CaptureComparison.SecondSchemaHash != actual
            || observed.CaptureComparison.FirstIdentity != observed.ObservedIdentity
            || observed.CaptureComparison.SecondIdentity != observed.ObservedIdentity)
            throw new LegacyContractException("OBSERVED_SCHEMA_HASH_INVALID");
        if (actual != baseline.CertifiedSchemaHash
            || state.CertifiedSchemaHash != baseline.CertifiedSchemaHash
            || state.ObservedSchemaHash != actual)
            throw new LegacyContractException("CERTIFIED_SCHEMA_HASH_MISMATCH");
        return actual;
    }
}
