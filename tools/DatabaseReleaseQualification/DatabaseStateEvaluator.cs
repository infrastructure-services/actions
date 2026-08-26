using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace DatabaseReleaseQualification;

public static class DatabaseCertificationStatuses
{
    public const string BaselineRequired = "BASELINE_REQUIRED";
    public const string Certified = "CERTIFIED";
}

public static class DatabaseDriftStatuses
{
    public const string BaselineRequired = "BASELINE_REQUIRED";
    public const string Match = "MATCH";
    public const string DriftDetected = "DRIFT_DETECTED";
    public const string TargetNotRegistered = "TARGET_NOT_REGISTERED";
    public const string InvalidRegistry = "INVALID_REGISTRY";
}

public static class DatabaseGateStatuses
{
    public const string Eligible = "ELIGIBLE";
    public const string Blocked = "BLOCKED";
}

public static class DatabaseStateReasons
{
    public const string OnboardingBaselineRequired = "ONBOARDING_BASELINE_REQUIRED";
    public const string SchemaHashMatch = "SCHEMA_HASH_MATCH";
    public const string CertifiedSchemaHashMismatch = "CERTIFIED_SCHEMA_HASH_MISMATCH";
    public const string TargetNotRegistered = "DATABASE_TARGET_NOT_REGISTERED";
    public const string InvalidRegistry = "REGISTRY_VALIDATION_FAILED";
}

public sealed class DatabaseRegistryDocument
{
    public int RegistryFormatVersion { get; init; }
    public List<DatabaseTarget> Targets { get; init; } = [];
}

public sealed class RegistryProvenance
{
    public required string RegistryRepository { get; init; }
    public required string RegistryRef { get; init; }
    public required string RegistryCommitSha { get; init; }
    public required string RegistryFilePath { get; init; }
    public required string RegistryFileSha256 { get; init; }
}

public sealed class DatabaseTarget
{
    public string ApplicationId { get; init; } = "";
    public string Environment { get; init; } = "";
    public string DatabaseName { get; init; } = "";
    public string Lifecycle { get; init; } = "";
    public string CertificationStatus { get; init; } = "";
    public string? CertifiedSchemaHash { get; init; }
}

public sealed class DatabaseRegistryValidation
{
    public DatabaseRegistryDocument? Registry { get; init; }
    public RegistryProvenance? RegistryProvenance { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool IsValid => Registry is not null && RegistryProvenance is not null && Errors.Count == 0;
}

public sealed class DatabaseStateObservation
{
    public required string ApplicationId { get; init; }
    public required string Environment { get; init; }
    public required string DatabaseName { get; init; }
    public required string ObservedSchemaHash { get; init; }
    public required string SchemaCoverage { get; init; }
    public IReadOnlyList<string> UnsupportedSchemaFeatures { get; init; } = [];
    public required DateTimeOffset CaptureTimestampUtc { get; init; }
    public required string RunId { get; init; }
    public required string RunAttempt { get; init; }
}

public sealed class DatabaseStateEvaluation
{
    public required string ApplicationId { get; init; }
    public required string Environment { get; init; }
    public required string DatabaseName { get; init; }
    public required string ObservedSchemaHash { get; init; }
    public string? CertifiedSchemaHash { get; init; }
    public required string RegistryStatus { get; init; }
    public required string DriftStatus { get; init; }
    public required string GateStatus { get; init; }
    public required string Reason { get; init; }
    public int RegistryFormatVersion { get; init; }
    public RegistryProvenance? RegistryProvenance { get; init; }
    public bool BaselineCandidate { get; init; }
    public bool DriftDetected { get; init; }
    public string DriftEvidenceKind { get; init; } = "NONE";
    public bool StructuralDiffAvailable { get; init; }
    public DatabaseTarget? Target { get; init; }
    public IReadOnlyList<string> RegistryValidationErrors { get; init; } = [];
}

public sealed record DatabaseStateArtifact(
    string TargetPath,
    string RegistryEvaluationPath,
    string? BaselineCandidatePath,
    string? DriftAnalysisPath);

public static class DatabaseRegistryLoader
{
    private static readonly Regex Sha256 = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Environments = new(StringComparer.Ordinal) { "TEST", "QA", "PROD" };
    private static readonly HashSet<string> Lifecycles = new(StringComparer.Ordinal) { "NEW", "EXISTING" };
    private static readonly HashSet<string> CertificationStatuses = new(StringComparer.Ordinal)
    {
        DatabaseCertificationStatuses.BaselineRequired,
        DatabaseCertificationStatuses.Certified
    };
    private static readonly Regex Repository = new(@"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex RegistryRef = new(@"\A[A-Za-z0-9][A-Za-z0-9._/-]{0,255}\z", RegexOptions.CultureInvariant);
    private static readonly Regex CommitSha = new(@"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant);
    private static readonly Regex RegistryPath = new(@"\A[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\z", RegexOptions.CultureInvariant);

    public static DatabaseRegistryValidation Load(string path, RegistryProvenance provenance)
    {
        var errors = ValidateProvenance(provenance).ToList();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            errors.Add("REGISTRY_FILE_NOT_FOUND");
            return Invalid(provenance, errors);
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var actualFileSha256 = ComputeSha256(bytes);
            if (!string.Equals(actualFileSha256, provenance.RegistryFileSha256, StringComparison.OrdinalIgnoreCase))
                errors.Add("REGISTRY_FILE_SHA256_MISMATCH");
            var registry = JsonSerializer.Deserialize<DatabaseRegistryDocument>(
                bytes, DatabaseStateJson.Compact);
            return Validate(registry, provenance, errors);
        }
        catch (JsonException)
        {
            errors.Add("REGISTRY_JSON_INVALID");
            return Invalid(provenance, errors);
        }
        catch (IOException)
        {
            errors.Add("REGISTRY_READ_FAILED");
            return Invalid(provenance, errors);
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add("REGISTRY_READ_FAILED");
            return Invalid(provenance, errors);
        }
    }

    public static DatabaseRegistryValidation Validate(
        DatabaseRegistryDocument? registry,
        RegistryProvenance provenance) => Validate(registry, provenance, ValidateProvenance(provenance));

    public static string ComputeFileSha256(string path) => ComputeSha256(File.ReadAllBytes(path));

    private static DatabaseRegistryValidation Validate(
        DatabaseRegistryDocument? registry,
        RegistryProvenance provenance,
        IEnumerable<string> initialErrors)
    {
        var errors = initialErrors.Distinct(StringComparer.Ordinal).ToList();
        if (registry is null)
        {
            errors.Add("REGISTRY_DOCUMENT_REQUIRED");
            return Invalid(provenance, errors);
        }
        if (registry.RegistryFormatVersion != 1) errors.Add("REGISTRY_FORMAT_VERSION_INVALID");
        if (registry.Targets is null)
        {
            errors.Add("REGISTRY_TARGETS_REQUIRED");
            return new DatabaseRegistryValidation
            {
                Registry = registry,
                RegistryProvenance = provenance,
                Errors = errors.Order(StringComparer.Ordinal).ToArray()
            };
        }

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < registry.Targets.Count; index++)
        {
            var target = registry.Targets[index];
            var prefix = $"TARGET_{index + 1}";
            if (target is null)
            {
                errors.Add($"{prefix}_REQUIRED");
                continue;
            }

            ValidateRequired(target.ApplicationId, $"{prefix}_APPLICATION_ID_REQUIRED", errors);
            ValidateRequired(target.DatabaseName, $"{prefix}_DATABASE_NAME_REQUIRED", errors);
            if (!Environments.Contains(target.Environment)) errors.Add($"{prefix}_ENVIRONMENT_INVALID");
            if (!Lifecycles.Contains(target.Lifecycle)) errors.Add($"{prefix}_LIFECYCLE_INVALID");
            if (!CertificationStatuses.Contains(target.CertificationStatus))
            {
                errors.Add($"{prefix}_CERTIFICATION_STATUS_INVALID");
            }

            if (string.Equals(target.CertificationStatus, DatabaseCertificationStatuses.Certified, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(target.CertifiedSchemaHash))
                    errors.Add($"{prefix}_CERTIFIED_SCHEMA_HASH_REQUIRED");
                else if (!Sha256.IsMatch(target.CertifiedSchemaHash))
                    errors.Add($"{prefix}_CERTIFIED_SCHEMA_HASH_INVALID");
            }
            else if (string.Equals(target.CertificationStatus, DatabaseCertificationStatuses.BaselineRequired, StringComparison.Ordinal)
                && target.CertifiedSchemaHash is not null)
            {
                errors.Add($"{prefix}_BASELINE_CERTIFIED_HASH_CONTRADICTORY");
            }

            if (!string.IsNullOrWhiteSpace(target.CertifiedSchemaHash)
                && !Sha256.IsMatch(target.CertifiedSchemaHash))
            {
                var error = $"{prefix}_CERTIFIED_SCHEMA_HASH_INVALID";
                if (!errors.Contains(error, StringComparer.Ordinal)) errors.Add(error);
            }

            if (!string.IsNullOrWhiteSpace(target.ApplicationId)
                && !string.IsNullOrWhiteSpace(target.Environment)
                && !string.IsNullOrWhiteSpace(target.DatabaseName))
            {
                var identity = string.Join("|", target.ApplicationId.Trim(), target.Environment, target.DatabaseName.Trim());
                if (!identities.Add(identity)) errors.Add($"{prefix}_DUPLICATE_TARGET");
            }
        }

        return new DatabaseRegistryValidation
        {
            Registry = registry,
            RegistryProvenance = provenance,
            Errors = errors.Order(StringComparer.Ordinal).ToArray()
        };
    }

    private static IEnumerable<string> ValidateProvenance(RegistryProvenance? provenance)
    {
        if (provenance is null) return ["REGISTRY_PROVENANCE_REQUIRED"];
        var errors = new List<string>();
        if (!Repository.IsMatch(provenance.RegistryRepository)) errors.Add("REGISTRY_REPOSITORY_INVALID");
        if (!RegistryRef.IsMatch(provenance.RegistryRef)
            || provenance.RegistryRef.Contains("..", StringComparison.Ordinal)) errors.Add("REGISTRY_REF_INVALID");
        if (!CommitSha.IsMatch(provenance.RegistryCommitSha)) errors.Add("REGISTRY_COMMIT_SHA_INVALID");
        if (!RegistryPath.IsMatch(provenance.RegistryFilePath)
            || provenance.RegistryFilePath.Split('/').Any(segment => segment is "." or ".."))
            errors.Add("REGISTRY_FILE_PATH_INVALID");
        if (!Sha256.IsMatch(provenance.RegistryFileSha256)) errors.Add("REGISTRY_FILE_SHA256_INVALID");
        return errors;
    }

    private static DatabaseRegistryValidation Invalid(RegistryProvenance? provenance, IEnumerable<string> errors) => new()
    {
        RegistryProvenance = provenance,
        Errors = errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
    };

    private static string ComputeSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateRequired(string? value, string error, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)) errors.Add(error);
    }
}

public sealed class DatabaseStateEvaluator
{
    private static readonly Regex Sha256 = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);

    public DatabaseStateEvaluation Evaluate(DatabaseRegistryValidation registry, DatabaseStateObservation observation)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ValidateObservation(observation);

        if (!registry.IsValid)
        {
            return Result(registry, observation, null, DatabaseDriftStatuses.InvalidRegistry,
                DatabaseGateStatuses.Blocked, DatabaseStateReasons.InvalidRegistry,
                validationErrors: registry.Errors);
        }

        var target = registry.Registry!.Targets.SingleOrDefault(item =>
            string.Equals(item.ApplicationId, observation.ApplicationId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Environment, observation.Environment, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.DatabaseName, observation.DatabaseName, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            return Result(registry, observation, null, DatabaseDriftStatuses.TargetNotRegistered,
                DatabaseGateStatuses.Blocked, DatabaseStateReasons.TargetNotRegistered);
        }

        if (string.Equals(target.CertificationStatus, DatabaseCertificationStatuses.BaselineRequired, StringComparison.Ordinal))
        {
            return Result(registry, observation, target, DatabaseDriftStatuses.BaselineRequired,
                DatabaseGateStatuses.Blocked, DatabaseStateReasons.OnboardingBaselineRequired,
                baselineCandidate: true);
        }

        if (string.Equals(target.CertifiedSchemaHash, observation.ObservedSchemaHash, StringComparison.OrdinalIgnoreCase))
        {
            return Result(registry, observation, target, DatabaseDriftStatuses.Match,
                DatabaseGateStatuses.Eligible, DatabaseStateReasons.SchemaHashMatch);
        }

        return Result(registry, observation, target, DatabaseDriftStatuses.DriftDetected,
            DatabaseGateStatuses.Blocked, DatabaseStateReasons.CertifiedSchemaHashMismatch,
            driftDetected: true, driftEvidenceKind: "HASH_MISMATCH");
    }

    private static void ValidateObservation(DatabaseStateObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.ApplicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.Environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.DatabaseName);
        if (!Sha256.IsMatch(observation.ObservedSchemaHash))
            throw new ArgumentException("OBSERVED_SCHEMA_HASH_INVALID");
    }

    private static DatabaseStateEvaluation Result(
        DatabaseRegistryValidation registry,
        DatabaseStateObservation observation,
        DatabaseTarget? target,
        string driftStatus,
        string gateStatus,
        string reason,
        bool baselineCandidate = false,
        bool driftDetected = false,
        string driftEvidenceKind = "NONE",
        IReadOnlyList<string>? validationErrors = null) => new()
    {
        ApplicationId = observation.ApplicationId,
        Environment = observation.Environment,
        DatabaseName = observation.DatabaseName,
        ObservedSchemaHash = observation.ObservedSchemaHash,
        CertifiedSchemaHash = target?.CertifiedSchemaHash,
        RegistryStatus = target?.CertificationStatus ?? driftStatus,
        DriftStatus = driftStatus,
        GateStatus = gateStatus,
        Reason = reason,
        RegistryFormatVersion = registry.Registry?.RegistryFormatVersion ?? 0,
        RegistryProvenance = registry.RegistryProvenance,
        BaselineCandidate = baselineCandidate,
        DriftDetected = driftDetected,
        DriftEvidenceKind = driftEvidenceKind,
        StructuralDiffAvailable = false,
        Target = target,
        RegistryValidationErrors = validationErrors ?? []
    };
}

public sealed class DatabaseStateArtifactWriter
{
    public DatabaseStateArtifact Write(
        string outputDirectory,
        DatabaseStateObservation observation,
        DatabaseStateEvaluation evaluation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(evaluation);

        var registryDirectory = Path.Combine(outputDirectory, "registry");
        Directory.CreateDirectory(registryDirectory);
        var targetPath = Path.Combine(registryDirectory, "target.json");
        object targetEvidence = evaluation.Target is null
            ? new
            {
                observation.ApplicationId,
                observation.Environment,
                observation.DatabaseName,
                resolutionStatus = evaluation.RegistryStatus
            }
            : evaluation.Target;
        WriteJson(targetPath, targetEvidence);

        var evaluationPath = Path.Combine(registryDirectory, "registry-evaluation.json");
        WriteJson(evaluationPath, evaluation);

        string? baselinePath = null;
        if (evaluation.BaselineCandidate)
        {
            var baselineDirectory = Path.Combine(outputDirectory, "baseline");
            Directory.CreateDirectory(baselineDirectory);
            baselinePath = Path.Combine(baselineDirectory, "baseline-candidate.json");
            WriteJson(baselinePath, new
            {
                observation.ApplicationId,
                observation.Environment,
                observation.DatabaseName,
                observation.ObservedSchemaHash,
                observation.SchemaCoverage,
                unsupportedSchemaFeatures = observation.UnsupportedSchemaFeatures
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                observation.CaptureTimestampUtc,
                runMetadata = new { observation.RunId, observation.RunAttempt },
                evaluation.RegistryFormatVersion,
                registryProvenance = evaluation.RegistryProvenance,
                candidateStatus = "NOT_CERTIFIED"
            });
        }

        string? driftPath = null;
        if (evaluation.DriftDetected)
        {
            var driftDirectory = Path.Combine(outputDirectory, "drift");
            Directory.CreateDirectory(driftDirectory);
            driftPath = Path.Combine(driftDirectory, "drift-analysis.json");
            WriteJson(driftPath, new
            {
                observation.ApplicationId,
                observation.Environment,
                observation.DatabaseName,
                evaluation.CertifiedSchemaHash,
                evaluation.ObservedSchemaHash,
                driftDetected = true,
                evidenceKind = "HASH_MISMATCH",
                structuralDiffAvailable = false,
                evaluation.RegistryFormatVersion,
                registryProvenance = evaluation.RegistryProvenance
            });
        }

        return new DatabaseStateArtifact(targetPath, evaluationPath, baselinePath, driftPath);
    }

    private static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, DatabaseStateJson.Indented) + "\n", new UTF8Encoding(false));
}

public static class DatabaseStateJson
{
    public static JsonSerializerOptions Compact { get; } = Create(false);
    public static JsonSerializerOptions Indented { get; } = Create(true);

    private static JsonSerializerOptions Create(bool indented) => new(JsonDefaults.Compact)
    {
        WriteIndented = indented,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };
}
