using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record DatabaseIdentity(string ApplicationId, string Environment, string DatabaseName);

public sealed record EvidenceReference
{
    public required string EvidenceId { get; init; }
    public required string EvidenceSha256 { get; init; }
    public string? StorageLocator { get; init; }

    public static EvidenceReference ContentAddressed(
        string evidenceKind,
        string evidenceSha256,
        string? storageLocator = null)
    {
        if (string.IsNullOrWhiteSpace(evidenceKind)
            || !Regex.IsMatch(evidenceKind, @"\A[a-z0-9]+(?:-[a-z0-9]+)*\z",
                RegexOptions.CultureInvariant))
            throw new InvalidOperationException(CertifiedStateReasons.EvidenceIdInvalid);
        if (!CertifiedStateRecordValidator.IsHash(evidenceSha256))
            throw new InvalidOperationException(CertifiedStateReasons.EvidenceHashInvalid);
        if (!CertifiedStateRecordValidator.IsStorageLocator(storageLocator))
            throw new InvalidOperationException(CertifiedStateReasons.StorageLocatorInvalid);

        var normalizedHash = evidenceSha256.ToLowerInvariant();
        return new EvidenceReference
        {
            EvidenceId = $"{evidenceKind}:sha256:{normalizedHash}",
            EvidenceSha256 = normalizedHash,
            StorageLocator = storageLocator
        };
    }
}

public sealed record QualifiedReleaseCertificationReference
{
    public required string ReleaseId { get; init; }
    public required string PayloadHash { get; init; }
    public required string ForwardHash { get; init; }
    public required string RollbackHash { get; init; }
    public required string SourceKind { get; init; }
    public required string ChangeOrigin { get; init; }
    public required string ChangePath { get; init; }
    public string? ChangeReference { get; init; }
    public string? ChangeReasonHash { get; init; }
    public required EvidenceReference QualificationEvidence { get; init; }
    public required EvidenceReference DeploymentAuthorizationEvidence { get; init; }
    public required EvidenceReference ExecutionEvidence { get; init; }
    public required string ObservedPostSchemaHash { get; init; }

    public static QualifiedReleaseCertificationReference FromPayload(
        ReleasePayloadMetadata payload,
        string? qualificationEvidenceStorageLocator,
        string qualificationEvidenceHash,
        string? deploymentAuthorizationEvidenceStorageLocator,
        string deploymentAuthorizationEvidenceHash,
        string? executionEvidenceStorageLocator,
        string executionEvidenceHash,
        string observedPostSchemaHash)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new QualifiedReleaseCertificationReference
        {
            ReleaseId = payload.ReleaseId,
            PayloadHash = payload.PayloadHash,
            ForwardHash = payload.ForwardHash,
            RollbackHash = payload.RollbackHash,
            SourceKind = payload.SourceKind,
            ChangeOrigin = payload.ChangeOrigin,
            ChangePath = payload.ChangePath,
            ChangeReference = payload.ChangeReference,
            ChangeReasonHash = payload.ChangeReason is null ? null : Hashing.Sha256(payload.ChangeReason),
            QualificationEvidence = EvidenceReference.ContentAddressed(
                "qualified-release", qualificationEvidenceHash, qualificationEvidenceStorageLocator),
            DeploymentAuthorizationEvidence = EvidenceReference.ContentAddressed(
                "deployment-authorization", deploymentAuthorizationEvidenceHash,
                deploymentAuthorizationEvidenceStorageLocator),
            ExecutionEvidence = EvidenceReference.ContentAddressed(
                "execution", executionEvidenceHash, executionEvidenceStorageLocator),
            ObservedPostSchemaHash = observedPostSchemaHash
        };
    }
}

public sealed record CertifiedStateRecord
{
    public int FormatVersion { get; init; } = 1;
    public required string CertificationId { get; init; }
    public string? PreviousCertificationId { get; init; }
    public string? PreviousCertificationEvidenceHash { get; init; }
    public required string ApplicationId { get; init; }
    public required string Environment { get; init; }
    public required string DatabaseName { get; init; }
    public required string CanonicalSchemaHash { get; init; }
    public required EvidenceReference CanonicalSchemaEvidenceReference { get; init; }
    public CertificationOrigin CertificationOrigin { get; init; }
    public CertificationDecision CertificationDecision { get; init; }
    public required EvidenceReference DecisionEvidence { get; init; }
    public required EvidenceReference TransitionEvidence { get; init; }
    public required RegistryProvenance RegistryProvenance { get; init; }
    public LineageOnboardingState LineageStatus { get; init; }
    public required EvidenceReference LineageEvidence { get; init; }
    public EvidenceReference? ReconciliationEvidence { get; init; }
    public QualifiedReleaseCertificationReference? QualifiedRelease { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public SortedDictionary<string, string> RunMetadata { get; init; } = new(StringComparer.Ordinal);
    public required string CertificationEvidenceHash { get; init; }

    [JsonIgnore]
    public CanonicalSchema? CanonicalSchemaEvidence { get; init; }

    [JsonIgnore]
    public DatabaseIdentity DatabaseIdentity => new(ApplicationId, Environment, DatabaseName);
}

public sealed class CertifiedStateRecordRequest
{
    public required string CertificationId { get; init; }
    public CertifiedStateRecord? PreviousCertification { get; init; }
    public required DatabaseIdentity DatabaseIdentity { get; init; }
    public required CanonicalSchema CanonicalSchema { get; init; }
    public string? CanonicalSchemaStorageLocator { get; init; }
    public required CertificationResult Certification { get; init; }
    public string? DecisionEvidenceStorageLocator { get; init; }
    public string? TransitionEvidenceStorageLocator { get; init; }
    public required RegistryProvenance RegistryProvenance { get; init; }
    public required LineageOnboardingState LineageStatus { get; init; }
    public string? LineageEvidenceStorageLocator { get; init; }
    public required string LineageEvidenceHash { get; init; }
    public ReconciliationResult? Reconciliation { get; init; }
    public string? ReconciliationEvidenceStorageLocator { get; init; }
    public QualifiedReleaseCertificationReference? QualifiedRelease { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public IReadOnlyDictionary<string, string>? RunMetadata { get; init; }
}

public static class CertifiedStateRecordBuilder
{
    public static CertifiedStateRecord Build(CertifiedStateRecordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.DatabaseIdentity);
        ArgumentNullException.ThrowIfNull(request.CanonicalSchema);
        ArgumentNullException.ThrowIfNull(request.Certification);
        ArgumentNullException.ThrowIfNull(request.RegistryProvenance);

        if (!request.Certification.ProducesCertifiedState)
            throw new InvalidOperationException(CertifiedStateReasons.CertificationDecisionNotFinal);
        if (!string.Equals(request.Certification.NextCertifiedSchemaHash,
                request.CanonicalSchema.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(CertifiedStateReasons.CanonicalSchemaCertificationMismatch);
        if (request.LineageStatus is not (LineageOnboardingState.ConsistentEf
            or LineageOnboardingState.LegacySql))
            throw new InvalidOperationException(CertifiedStateReasons.LineageNotEligible);

        ValidatePreviousIdentity(request);
        ValidateOriginEvidence(request);

        var decisionJson = JsonSerializer.Serialize(request.Certification.Evidence, JsonDefaults.Compact);
        var reconciliationHash = request.Reconciliation is null
            ? null
            : Hashing.Sha256(JsonSerializer.Serialize(request.Reconciliation.Evidence, JsonDefaults.Compact));
        var decisionHash = Hashing.Sha256(decisionJson);
        var transitionHash = request.QualifiedRelease is not null
            ? QualifiedReleaseEvidenceHasher.ComputeHash(request.QualifiedRelease)
            : reconciliationHash ?? decisionHash;
        var record = new CertifiedStateRecord
        {
            CertificationId = NormalizeCertificationId(request.CertificationId),
            PreviousCertificationId = request.PreviousCertification?.CertificationId,
            PreviousCertificationEvidenceHash = request.PreviousCertification?.CertificationEvidenceHash,
            ApplicationId = Required(request.DatabaseIdentity.ApplicationId, "APPLICATION_ID_REQUIRED"),
            Environment = Required(request.DatabaseIdentity.Environment, "ENVIRONMENT_REQUIRED"),
            DatabaseName = Required(request.DatabaseIdentity.DatabaseName, "DATABASE_NAME_REQUIRED"),
            CanonicalSchemaHash = request.CanonicalSchema.Sha256,
            CanonicalSchemaEvidenceReference = EvidenceReference.ContentAddressed(
                "canonical-schema", request.CanonicalSchema.Sha256,
                request.CanonicalSchemaStorageLocator),
            CertificationOrigin = request.Certification.Origin,
            CertificationDecision = request.Certification.Decision,
            DecisionEvidence = EvidenceReference.ContentAddressed(
                "certification-decision", decisionHash, request.DecisionEvidenceStorageLocator),
            TransitionEvidence = EvidenceReference.ContentAddressed(
                "certification-transition", transitionHash,
                request.TransitionEvidenceStorageLocator),
            RegistryProvenance = CloneRegistryProvenance(request.RegistryProvenance),
            LineageStatus = request.LineageStatus,
            LineageEvidence = EvidenceReference.ContentAddressed(
                "lineage", RequiredHash(request.LineageEvidenceHash),
                request.LineageEvidenceStorageLocator),
            ReconciliationEvidence = request.Reconciliation is null
                ? null
                : EvidenceReference.ContentAddressed(
                    "reconciliation", reconciliationHash!,
                    request.ReconciliationEvidenceStorageLocator),
            QualifiedRelease = CloneQualifiedRelease(request.QualifiedRelease),
            CreatedAtUtc = request.CreatedAtUtc,
            RunMetadata = SafeRunMetadata(request.RunMetadata),
            CertificationEvidenceHash = "",
            CanonicalSchemaEvidence = CloneCanonicalSchema(request.CanonicalSchema)
        };
        record = record with { CertificationEvidenceHash = CertifiedStateEvidenceHasher.ComputeHash(record) };

        var validation = CertifiedStateRecordValidator.Validate(record);
        if (validation.Count > 0)
            throw new InvalidOperationException(string.Join(";", validation));
        return record;
    }

    private static void ValidatePreviousIdentity(CertifiedStateRecordRequest request)
    {
        if (request.PreviousCertification is null) return;
        if (!CertifiedStateRecordValidator.SameIdentity(
                request.PreviousCertification.DatabaseIdentity, request.DatabaseIdentity))
            throw new InvalidOperationException(CertifiedStateReasons.DatabaseIdentityMismatch);
        if (request.Certification.Origin == CertificationOrigin.BootstrapApproved)
            throw new InvalidOperationException(CertifiedStateReasons.InitialCertificationAlreadyExists);
    }

    private static void ValidateOriginEvidence(CertifiedStateRecordRequest request)
    {
        if (request.Certification.Origin == CertificationOrigin.QualifiedRelease)
        {
            if (request.QualifiedRelease is null)
                throw new InvalidOperationException(CertifiedStateReasons.QualifiedReleaseEvidenceRequired);
            if (request.Reconciliation is not null || request.ReconciliationEvidenceStorageLocator is not null)
                throw new InvalidOperationException(CertifiedStateReasons.ReconciliationEvidenceUnexpected);
            ValidateQualifiedRelease(request.Certification.Evidence, request.QualifiedRelease,
                request.CanonicalSchema.Sha256);
            if (request.PreviousCertification is null
                && !request.Certification.Evidence.ControlledInitialCertification)
                throw new InvalidOperationException(CertifiedStateReasons.InitialCertificationOriginInvalid);
            return;
        }

        if (request.Certification.Origin is CertificationOrigin.DriftReconciliation
            or CertificationOrigin.BreakGlassReconciliation)
        {
            if (request.PreviousCertification is null)
                throw new InvalidOperationException(CertifiedStateReasons.PreviousCertificationRequired);
            if (request.QualifiedRelease is not null)
                throw new InvalidOperationException(CertifiedStateReasons.QualifiedReleaseEvidenceUnexpected);
            ValidateReconciliation(request);
            return;
        }

        if (request.PreviousCertification is not null)
            throw new InvalidOperationException(CertifiedStateReasons.InitialCertificationAlreadyExists);
        if (request.Certification.Decision != CertificationDecision.HumanApproved)
            throw new InvalidOperationException(CertifiedStateReasons.CertificationDecisionNotFinal);
        if (request.QualifiedRelease is not null || request.Reconciliation is not null
            || request.ReconciliationEvidenceStorageLocator is not null)
            throw new InvalidOperationException(CertifiedStateReasons.InitialCertificationEvidenceInvalid);
    }

    private static void ValidateQualifiedRelease(
        CertificationEvidence certification,
        QualifiedReleaseCertificationReference qualified,
        string canonicalSchemaHash)
    {
        if (!string.Equals(qualified.ReleaseId, certification.ReleaseId, StringComparison.Ordinal)
            || !SameHash(qualified.PayloadHash, certification.QualifiedPayloadHash)
            || !SameHash(qualified.ForwardHash, certification.QualifiedForwardHash)
            || !SameHash(qualified.RollbackHash, certification.QualifiedRollbackHash)
            || !SameHash(qualified.ObservedPostSchemaHash, canonicalSchemaHash))
            throw new InvalidOperationException(CertifiedStateReasons.QualifiedReleaseEvidenceMismatch);
        if (qualified.SourceKind is not ("EF" or "SQL")
            || qualified.ChangeOrigin is not (DatabaseChangeOrigins.Application or DatabaseChangeOrigins.Dba)
            || qualified.ChangePath != DatabaseChangePaths.PlannedRelease)
            throw new InvalidOperationException(CertifiedStateReasons.QualifiedReleaseMetadataInvalid);
        if (qualified.ChangeOrigin == DatabaseChangeOrigins.Dba
            && (string.IsNullOrWhiteSpace(qualified.ChangeReference)
                || string.IsNullOrWhiteSpace(qualified.ChangeReasonHash)))
            throw new InvalidOperationException(CertifiedStateReasons.QualifiedReleaseMetadataInvalid);
    }

    private static void ValidateReconciliation(CertifiedStateRecordRequest request)
    {
        var reconciliation = request.Reconciliation
            ?? throw new InvalidOperationException(CertifiedStateReasons.ReconciliationEvidenceRequired);
        if (reconciliation.ReconciliationStatus != ReconciliationStatus.ReadyForCertification
            || reconciliation.UnexplainedDifferenceCount != 0
            || reconciliation.ReconciledCanonicalStateCandidate is null
            || !SameHash(reconciliation.ReconciledCanonicalStateCandidate.Sha256,
                request.CanonicalSchema.Sha256)
            || reconciliation.Evidence.CertificationOrigin != request.Certification.Origin)
            throw new InvalidOperationException(CertifiedStateReasons.ReconciliationEvidenceMismatch);
        if (!CertifiedStateRecordValidator.IsStorageLocator(request.ReconciliationEvidenceStorageLocator))
            throw new InvalidOperationException(CertifiedStateReasons.StorageLocatorInvalid);
    }

    internal static QualifiedReleaseCertificationReference? CloneQualifiedRelease(
        QualifiedReleaseCertificationReference? value) => value is null ? null : value with
        {
            QualificationEvidence = value.QualificationEvidence with { },
            DeploymentAuthorizationEvidence = value.DeploymentAuthorizationEvidence with { },
            ExecutionEvidence = value.ExecutionEvidence with { }
        };

    internal static RegistryProvenance CloneRegistryProvenance(RegistryProvenance value) => new()
    {
        RegistryRepository = value.RegistryRepository,
        RegistryRef = value.RegistryRef,
        RegistryCommitSha = value.RegistryCommitSha,
        RegistryFilePath = value.RegistryFilePath,
        RegistryFileSha256 = value.RegistryFileSha256
    };

    internal static CanonicalSchema CloneCanonicalSchema(CanonicalSchema value)
    {
        var document = JsonSerializer.Deserialize<CanonicalSchemaDocument>(value.Json, JsonDefaults.Compact)
            ?? throw new InvalidOperationException(CertifiedStateReasons.CanonicalSchemaEvidenceInvalid);
        return new CanonicalSchema(document, value.Json, value.Sha256);
    }

    internal static SortedDictionary<string, string> SafeRunMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata ?? new Dictionary<string, string>())
        {
            if (IsSensitiveMetadataKey(pair.Key))
                throw new InvalidOperationException("RUN_METADATA_SENSITIVE_KEY_REJECTED");
            if (!IsSafeToken(pair.Key) || !IsSafeToken(pair.Value))
                throw new InvalidOperationException("RUN_METADATA_MUST_USE_SAFE_TOKENS");
            result.Add(pair.Key, pair.Value);
        }
        return result;
    }

    private static bool IsSafeToken(string value) => value.Length is > 0 and <= 128
        && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or ':');

    private static bool IsSensitiveMetadataKey(string key) =>
        key.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCertificationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty)
            throw new InvalidOperationException(CertifiedStateReasons.CertificationIdInvalid);
        return id.ToString("D");
    }

    private static string Required(string? value, string reason)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new InvalidOperationException(reason);
        return value;
    }

    private static string RequiredHash(string? value)
    {
        if (!CertifiedStateRecordValidator.IsHash(value))
            throw new InvalidOperationException(CertifiedStateReasons.EvidenceHashInvalid);
        return value!;
    }

    private static bool SameHash(string? first, string? second) => first is not null && second is not null
        && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}

internal static class QualifiedReleaseEvidenceHasher
{
    public static string ComputeHash(QualifiedReleaseCertificationReference qualified)
    {
        ArgumentNullException.ThrowIfNull(qualified);
        return Hashing.Sha256(JsonSerializer.Serialize(SemanticEvidence(qualified), JsonDefaults.Compact));
    }

    public static object SemanticEvidence(QualifiedReleaseCertificationReference qualified) => new
    {
        qualified.ReleaseId,
        qualified.PayloadHash,
        qualified.ForwardHash,
        qualified.RollbackHash,
        qualified.SourceKind,
        qualified.ChangeOrigin,
        qualified.ChangePath,
        qualified.ChangeReference,
        qualified.ChangeReasonHash,
        qualificationEvidence = CertifiedStateEvidenceHasher.Identity(qualified.QualificationEvidence),
        deploymentAuthorizationEvidence = CertifiedStateEvidenceHasher.Identity(
            qualified.DeploymentAuthorizationEvidence),
        executionEvidence = CertifiedStateEvidenceHasher.Identity(qualified.ExecutionEvidence),
        qualified.ObservedPostSchemaHash
    };
}

public static class CertifiedStateEvidenceHasher
{
    public static string ComputeHash(CertifiedStateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var evidence = new
        {
            record.FormatVersion,
            record.CertificationId,
            record.PreviousCertificationId,
            record.PreviousCertificationEvidenceHash,
            record.ApplicationId,
            record.Environment,
            record.DatabaseName,
            record.CanonicalSchemaHash,
            canonicalSchemaEvidence = Identity(record.CanonicalSchemaEvidenceReference),
            record.CertificationOrigin,
            record.CertificationDecision,
            decisionEvidence = Identity(record.DecisionEvidence),
            transitionEvidence = Identity(record.TransitionEvidence),
            registryProvenance = new
            {
                record.RegistryProvenance.RegistryRepository,
                record.RegistryProvenance.RegistryRef,
                record.RegistryProvenance.RegistryCommitSha,
                record.RegistryProvenance.RegistryFilePath,
                record.RegistryProvenance.RegistryFileSha256
            },
            record.LineageStatus,
            lineageEvidence = Identity(record.LineageEvidence),
            reconciliationEvidence = Identity(record.ReconciliationEvidence),
            qualifiedRelease = record.QualifiedRelease is null
                ? null
                : QualifiedReleaseEvidenceHasher.SemanticEvidence(record.QualifiedRelease)
        };
        return Hashing.Sha256(JsonSerializer.Serialize(evidence, JsonDefaults.Compact));
    }

    internal static object? Identity(EvidenceReference? evidence) => evidence is null
        ? null
        : new
        {
            evidence.EvidenceId,
            evidence.EvidenceSha256
        };
}

public static class CertifiedStateReasons
{
    public const string Appended = "CERTIFIED_STATE_APPENDED";
    public const string CertificationIdInvalid = "CERTIFICATION_ID_INVALID";
    public const string DuplicateCertificationId = "DUPLICATE_CERTIFICATION_ID";
    public const string CertificationRecordAlreadyExists = "CERTIFICATION_RECORD_ALREADY_EXISTS";
    public const string HistoricalRecordImmutable = "HISTORICAL_RECORD_IMMUTABLE";
    public const string PreviousCertificationIncorrect = "PREVIOUS_CERTIFICATION_INCORRECT";
    public const string PreviousEvidenceHashIncorrect = "PREVIOUS_EVIDENCE_HASH_INCORRECT";
    public const string PreviousCertificationRequired = "PREVIOUS_CERTIFICATION_REQUIRED";
    public const string InitialCertificationAlreadyExists = "INITIAL_CERTIFICATION_ALREADY_EXISTS";
    public const string InitialCertificationOriginInvalid = "INITIAL_CERTIFICATION_ORIGIN_INVALID";
    public const string InitialCertificationEvidenceInvalid = "INITIAL_CERTIFICATION_EVIDENCE_INVALID";
    public const string DatabaseIdentityMismatch = "DATABASE_IDENTITY_MISMATCH";
    public const string CanonicalSchemaEvidenceRequired = "CANONICAL_SCHEMA_EVIDENCE_REQUIRED";
    public const string CanonicalSchemaEvidenceInvalid = "CANONICAL_SCHEMA_EVIDENCE_INVALID";
    public const string CanonicalSchemaHashMismatch = "CANONICAL_SCHEMA_HASH_MISMATCH";
    public const string CanonicalSchemaCertificationMismatch = "CANONICAL_SCHEMA_CERTIFICATION_MISMATCH";
    public const string CertificationEvidenceHashMismatch = "CERTIFICATION_EVIDENCE_HASH_MISMATCH";
    public const string TransitionEvidenceHashMismatch = "TRANSITION_EVIDENCE_HASH_MISMATCH";
    public const string CertificationDecisionNotFinal = "CERTIFICATION_DECISION_NOT_FINAL";
    public const string QualifiedReleaseEvidenceRequired = "QUALIFIED_RELEASE_EVIDENCE_REQUIRED";
    public const string QualifiedReleaseEvidenceUnexpected = "QUALIFIED_RELEASE_EVIDENCE_UNEXPECTED";
    public const string QualifiedReleaseEvidenceMismatch = "QUALIFIED_RELEASE_EVIDENCE_MISMATCH";
    public const string QualifiedReleaseMetadataInvalid = "QUALIFIED_RELEASE_METADATA_INVALID";
    public const string ReconciliationEvidenceRequired = "RECONCILIATION_EVIDENCE_REQUIRED";
    public const string ReconciliationEvidenceUnexpected = "RECONCILIATION_EVIDENCE_UNEXPECTED";
    public const string ReconciliationEvidenceMismatch = "RECONCILIATION_EVIDENCE_MISMATCH";
    public const string LineageNotEligible = "LINEAGE_NOT_ELIGIBLE";
    public const string EvidenceReferenceInvalid = "EVIDENCE_REFERENCE_INVALID";
    public const string EvidenceIdInvalid = "EVIDENCE_ID_INVALID";
    public const string EvidenceHashInvalid = "EVIDENCE_HASH_INVALID";
    public const string StorageLocatorInvalid = "STORAGE_LOCATOR_INVALID";
    public const string RecordInvalid = "CERTIFIED_STATE_RECORD_INVALID";
}

public static class CertifiedStateRecordValidator
{
    private static readonly Regex Sha256 = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Validate(CertifiedStateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        if (record.FormatVersion != 1) reasons.Add(CertifiedStateReasons.RecordInvalid);
        if (!Guid.TryParseExact(record.CertificationId, "D", out var id) || id == Guid.Empty)
            reasons.Add(CertifiedStateReasons.CertificationIdInvalid);
        if (new[] { record.ApplicationId, record.Environment, record.DatabaseName }
            .Any(string.IsNullOrWhiteSpace)) reasons.Add(CertifiedStateReasons.RecordInvalid);
        if (record.CreatedAtUtc == default) reasons.Add(CertifiedStateReasons.RecordInvalid);
        if ((record.PreviousCertificationId is null) != (record.PreviousCertificationEvidenceHash is null))
            reasons.Add(CertifiedStateReasons.PreviousCertificationIncorrect);
        if (record.PreviousCertificationId is not null
            && (!Guid.TryParseExact(record.PreviousCertificationId, "D", out var previousId)
                || previousId == Guid.Empty))
            reasons.Add(CertifiedStateReasons.PreviousCertificationIncorrect);
        if (record.PreviousCertificationEvidenceHash is not null
            && !IsHash(record.PreviousCertificationEvidenceHash))
            reasons.Add(CertifiedStateReasons.PreviousEvidenceHashIncorrect);
        if (!IsHash(record.CanonicalSchemaHash)) reasons.Add(CertifiedStateReasons.CanonicalSchemaHashMismatch);
        if (record.CertificationDecision is not (CertificationDecision.Automatic
            or CertificationDecision.HumanApproved))
            reasons.Add(CertifiedStateReasons.CertificationDecisionNotFinal);
        if (record.LineageStatus is not (LineageOnboardingState.ConsistentEf
            or LineageOnboardingState.LegacySql))
            reasons.Add(CertifiedStateReasons.LineageNotEligible);
        ValidateEvidenceReference(record.CanonicalSchemaEvidenceReference,
            "canonical-schema", reasons);
        ValidateEvidenceReference(record.DecisionEvidence, "certification-decision", reasons);
        ValidateEvidenceReference(record.TransitionEvidence, "certification-transition", reasons);
        ValidateEvidenceReference(record.LineageEvidence, "lineage", reasons);
        ValidateRegistryProvenance(record.RegistryProvenance, reasons);
        ValidateCanonical(record, reasons);
        ValidateOriginShape(record, reasons);
        ValidateTransitionHash(record, reasons);

        try
        {
            _ = CertifiedStateRecordBuilder.SafeRunMetadata(record.RunMetadata);
        }
        catch (InvalidOperationException)
        {
            reasons.Add(CertifiedStateReasons.RecordInvalid);
        }

        if (!IsHash(record.CertificationEvidenceHash)
            || !string.Equals(record.CertificationEvidenceHash,
                CertifiedStateEvidenceHasher.ComputeHash(record), StringComparison.OrdinalIgnoreCase))
            reasons.Add(CertifiedStateReasons.CertificationEvidenceHashMismatch);
        return reasons.ToArray();
    }

    private static void ValidateCanonical(CertifiedStateRecord record, ISet<string> reasons)
    {
        if (record.CanonicalSchemaEvidence is null)
        {
            reasons.Add(CertifiedStateReasons.CanonicalSchemaEvidenceRequired);
            return;
        }
        var canonical = record.CanonicalSchemaEvidence;
        var documentJson = JsonSerializer.Serialize(canonical.Document, JsonDefaults.Compact);
        if (!string.Equals(documentJson, canonical.Json, StringComparison.Ordinal)
            || !string.Equals(Hashing.Sha256(canonical.Json), canonical.Sha256, StringComparison.OrdinalIgnoreCase))
            reasons.Add(CertifiedStateReasons.CanonicalSchemaEvidenceInvalid);
        if (!string.Equals(canonical.Sha256, record.CanonicalSchemaHash, StringComparison.OrdinalIgnoreCase))
            reasons.Add(CertifiedStateReasons.CanonicalSchemaHashMismatch);
        if (!string.Equals(record.CanonicalSchemaEvidenceReference?.EvidenceSha256,
                record.CanonicalSchemaHash, StringComparison.OrdinalIgnoreCase))
            reasons.Add(CertifiedStateReasons.CanonicalSchemaHashMismatch);
    }

    private static void ValidateOriginShape(CertifiedStateRecord record, ISet<string> reasons)
    {
        if (record.CertificationOrigin == CertificationOrigin.QualifiedRelease)
        {
            if (record.QualifiedRelease is null)
                reasons.Add(CertifiedStateReasons.QualifiedReleaseEvidenceRequired);
            else
                ValidateQualifiedRelease(record.QualifiedRelease, record.CanonicalSchemaHash, reasons);
            if (record.ReconciliationEvidence is not null)
                reasons.Add(CertifiedStateReasons.ReconciliationEvidenceUnexpected);
            if (record.CertificationDecision != CertificationDecision.Automatic)
                reasons.Add(CertifiedStateReasons.CertificationDecisionNotFinal);
        }
        else if (record.CertificationOrigin is CertificationOrigin.DriftReconciliation
            or CertificationOrigin.BreakGlassReconciliation)
        {
            if (record.QualifiedRelease is not null)
                reasons.Add(CertifiedStateReasons.QualifiedReleaseEvidenceUnexpected);
            if (record.ReconciliationEvidence is null)
                reasons.Add(CertifiedStateReasons.ReconciliationEvidenceRequired);
            else
                ValidateEvidenceReference(record.ReconciliationEvidence, "reconciliation", reasons);
            if (record.CertificationDecision != CertificationDecision.HumanApproved)
                reasons.Add(CertifiedStateReasons.CertificationDecisionNotFinal);
        }
        else if (record.QualifiedRelease is not null || record.ReconciliationEvidence is not null)
        {
            reasons.Add(CertifiedStateReasons.InitialCertificationEvidenceInvalid);
        }
    }

    private static void ValidateQualifiedRelease(
        QualifiedReleaseCertificationReference qualified,
        string canonicalSchemaHash,
        ISet<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(qualified.ReleaseId)
            || qualified.SourceKind is not ("EF" or "SQL")
            || qualified.ChangeOrigin is not (DatabaseChangeOrigins.Application or DatabaseChangeOrigins.Dba)
            || qualified.ChangePath != DatabaseChangePaths.PlannedRelease
            || qualified.ChangeOrigin == DatabaseChangeOrigins.Dba
                && (string.IsNullOrWhiteSpace(qualified.ChangeReference)
                    || !IsHash(qualified.ChangeReasonHash)))
            reasons.Add(CertifiedStateReasons.QualifiedReleaseMetadataInvalid);
        if (!IsHash(qualified.PayloadHash) || !IsHash(qualified.ForwardHash)
            || !IsHash(qualified.RollbackHash) || !IsHash(qualified.ObservedPostSchemaHash)
            || !string.Equals(qualified.ObservedPostSchemaHash, canonicalSchemaHash,
                StringComparison.OrdinalIgnoreCase))
            reasons.Add(CertifiedStateReasons.QualifiedReleaseEvidenceMismatch);
        ValidateEvidenceReference(qualified.QualificationEvidence, "qualified-release", reasons);
        ValidateEvidenceReference(qualified.DeploymentAuthorizationEvidence,
            "deployment-authorization", reasons);
        ValidateEvidenceReference(qualified.ExecutionEvidence, "execution", reasons);
    }

    private static void ValidateRegistryProvenance(RegistryProvenance provenance, ISet<string> reasons)
    {
        if (provenance is null
            || string.IsNullOrWhiteSpace(provenance.RegistryRepository)
            || string.IsNullOrWhiteSpace(provenance.RegistryRef)
            || string.IsNullOrWhiteSpace(provenance.RegistryFilePath)
            || !Regex.IsMatch(provenance.RegistryCommitSha ?? "", @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z")
            || !IsHash(provenance.RegistryFileSha256))
            reasons.Add(CertifiedStateReasons.RecordInvalid);
    }

    private static void ValidateTransitionHash(CertifiedStateRecord record, ISet<string> reasons)
    {
        var expected = record.CertificationOrigin switch
        {
            CertificationOrigin.QualifiedRelease when record.QualifiedRelease is not null =>
                QualifiedReleaseEvidenceHasher.ComputeHash(record.QualifiedRelease),
            CertificationOrigin.DriftReconciliation or CertificationOrigin.BreakGlassReconciliation =>
                record.ReconciliationEvidence?.EvidenceSha256,
            _ => record.DecisionEvidence?.EvidenceSha256
        };
        if (!string.Equals(record.TransitionEvidence?.EvidenceSha256,
                expected, StringComparison.OrdinalIgnoreCase))
            reasons.Add(CertifiedStateReasons.TransitionEvidenceHashMismatch);
    }

    public static bool IsHash(string? value) => value is not null && Sha256.IsMatch(value);

    public static bool IsStorageLocator(string? value) => value is null
        || !string.IsNullOrWhiteSpace(value)
        && value.Length <= 512
        && !value.Any(char.IsControl)
        && !value.Contains('?', StringComparison.Ordinal)
        && !value.Contains('#', StringComparison.Ordinal)
        && !value.Contains('@', StringComparison.Ordinal);

    private static void ValidateEvidenceReference(
        EvidenceReference? evidence,
        string evidenceKind,
        ISet<string> reasons)
    {
        if (evidence is null)
        {
            reasons.Add(CertifiedStateReasons.EvidenceReferenceInvalid);
            return;
        }
        if (!IsHash(evidence.EvidenceSha256))
            reasons.Add(CertifiedStateReasons.EvidenceHashInvalid);
        else if (!string.Equals(evidence.EvidenceId,
                $"{evidenceKind}:sha256:{evidence.EvidenceSha256.ToLowerInvariant()}",
                StringComparison.Ordinal))
            reasons.Add(CertifiedStateReasons.EvidenceIdInvalid);
        if (!IsStorageLocator(evidence.StorageLocator))
            reasons.Add(CertifiedStateReasons.StorageLocatorInvalid);
    }

    public static bool SameIdentity(DatabaseIdentity first, DatabaseIdentity second) =>
        string.Equals(first.ApplicationId, second.ApplicationId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.Environment, second.Environment, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.DatabaseName, second.DatabaseName, StringComparison.OrdinalIgnoreCase);
}

public enum CertifiedStateAppendStatus
{
    Appended,
    Blocked
}

public sealed class CertifiedStateAppendResult
{
    public CertifiedStateAppendStatus Status { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public CertifiedStateRecord? Record { get; init; }
}

public interface ICertifiedStateStore
{
    CertifiedStateRecord? GetCurrent(DatabaseIdentity databaseIdentity);
    CertifiedStateRecord? GetById(string certificationId);
    CertifiedStateAppendResult Append(CertifiedStateRecord record);
    IReadOnlyList<CertifiedStateRecord> ListHistory(DatabaseIdentity databaseIdentity);
}

public sealed class InMemoryCertifiedStateStore : ICertifiedStateStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CertifiedStateRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _history = new(StringComparer.OrdinalIgnoreCase);

    public CertifiedStateRecord? GetCurrent(DatabaseIdentity databaseIdentity)
    {
        ArgumentNullException.ThrowIfNull(databaseIdentity);
        lock (_sync)
        {
            return _history.TryGetValue(IdentityKey(databaseIdentity), out var history) && history.Count > 0
                ? CloneRecord(_records[history[^1]])
                : null;
        }
    }

    public CertifiedStateRecord? GetById(string certificationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificationId);
        lock (_sync)
        {
            return _records.TryGetValue(certificationId, out var record) ? CloneRecord(record) : null;
        }
    }

    public IReadOnlyList<CertifiedStateRecord> ListHistory(DatabaseIdentity databaseIdentity)
    {
        ArgumentNullException.ThrowIfNull(databaseIdentity);
        lock (_sync)
        {
            return _history.TryGetValue(IdentityKey(databaseIdentity), out var history)
                ? history.Select(id => CloneRecord(_records[id])).ToArray()
                : [];
        }
    }

    public CertifiedStateAppendResult Append(CertifiedStateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            var reasons = new SortedSet<string>(CertifiedStateRecordValidator.Validate(record), StringComparer.Ordinal);
            if (_records.TryGetValue(record.CertificationId, out var existing))
            {
                reasons.Add(CertifiedStateReasons.DuplicateCertificationId);
                if (string.Equals(existing.CertificationEvidenceHash,
                    record.CertificationEvidenceHash, StringComparison.OrdinalIgnoreCase))
                    reasons.Add(CertifiedStateReasons.CertificationRecordAlreadyExists);
                else
                    reasons.Add(CertifiedStateReasons.HistoricalRecordImmutable);
            }

            if (reasons.Count > 0)
                return Blocked(reasons);

            var key = IdentityKey(record.DatabaseIdentity);
            _history.TryGetValue(key, out var history);
            var current = history is { Count: > 0 } ? _records[history[^1]] : null;
            ValidateChain(record, current, reasons);

            if (record.PreviousCertificationId is not null
                && _records.TryGetValue(record.PreviousCertificationId, out var referencedPrevious)
                && !CertifiedStateRecordValidator.SameIdentity(
                    referencedPrevious.DatabaseIdentity, record.DatabaseIdentity))
                reasons.Add(CertifiedStateReasons.DatabaseIdentityMismatch);

            if (reasons.Count > 0)
                return Blocked(reasons);

            var stored = CloneRecord(record);
            _records.Add(stored.CertificationId, stored);
            if (history is null)
            {
                history = [];
                _history.Add(key, history);
            }
            history.Add(stored.CertificationId);
            return new CertifiedStateAppendResult
            {
                Status = CertifiedStateAppendStatus.Appended,
                Reasons = [CertifiedStateReasons.Appended],
                Record = CloneRecord(stored)
            };
        }
    }

    private static void ValidateChain(
        CertifiedStateRecord record,
        CertifiedStateRecord? current,
        ISet<string> reasons)
    {
        if (current is null)
        {
            if (record.PreviousCertificationId is not null
                || record.PreviousCertificationEvidenceHash is not null)
                reasons.Add(CertifiedStateReasons.PreviousCertificationIncorrect);
            if (record.CertificationOrigin is not (CertificationOrigin.BootstrapApproved
                or CertificationOrigin.QualifiedRelease))
                reasons.Add(CertifiedStateReasons.InitialCertificationOriginInvalid);
            return;
        }

        if (record.PreviousCertificationId is null)
        {
            reasons.Add(CertifiedStateReasons.InitialCertificationAlreadyExists);
            reasons.Add(CertifiedStateReasons.PreviousCertificationRequired);
            return;
        }
        if (!string.Equals(record.PreviousCertificationId,
            current.CertificationId, StringComparison.OrdinalIgnoreCase))
            reasons.Add(CertifiedStateReasons.PreviousCertificationIncorrect);
        if (!string.Equals(record.PreviousCertificationEvidenceHash,
            current.CertificationEvidenceHash, StringComparison.OrdinalIgnoreCase))
            reasons.Add(CertifiedStateReasons.PreviousEvidenceHashIncorrect);
        if (!CertifiedStateRecordValidator.SameIdentity(current.DatabaseIdentity, record.DatabaseIdentity))
            reasons.Add(CertifiedStateReasons.DatabaseIdentityMismatch);
        if (record.CertificationOrigin == CertificationOrigin.BootstrapApproved)
            reasons.Add(CertifiedStateReasons.InitialCertificationAlreadyExists);
    }

    private static CertifiedStateAppendResult Blocked(IEnumerable<string> reasons) => new()
    {
        Status = CertifiedStateAppendStatus.Blocked,
        Reasons = reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
    };

    private static string IdentityKey(DatabaseIdentity identity) => string.Join("|",
        identity.ApplicationId.Trim().ToUpperInvariant(),
        identity.Environment.Trim().ToUpperInvariant(),
        identity.DatabaseName.Trim().ToUpperInvariant());

    private static CertifiedStateRecord CloneRecord(CertifiedStateRecord record) => record with
    {
        RegistryProvenance = CertifiedStateRecordBuilder.CloneRegistryProvenance(record.RegistryProvenance),
        QualifiedRelease = CertifiedStateRecordBuilder.CloneQualifiedRelease(record.QualifiedRelease),
        RunMetadata = new SortedDictionary<string, string>(record.RunMetadata, StringComparer.Ordinal),
        CanonicalSchemaEvidence = record.CanonicalSchemaEvidence is null
            ? null
            : CertifiedStateRecordBuilder.CloneCanonicalSchema(record.CanonicalSchemaEvidence)
    };
}
