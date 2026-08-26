using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace DatabaseReleaseQualification;

public static class SchemaCaptureStatuses
{
    public const string Success = "SUCCESS";
    public const string DatabaseUnreachable = "FAIL_DATABASE_UNREACHABLE";
    public const string MetadataVisibility = "FAIL_METADATA_VISIBILITY";
    public const string CaptureFailed = "FAIL_SCHEMA_CAPTURE";
    public const string Nondeterministic = "FAIL_SCHEMA_CAPTURE_NONDETERMINISTIC";
}

public enum MetricsAvailability { Complete, Partial, Unavailable }
public enum SchemaCapturePhase { OpenConnection, MetadataVisibility, SchemaMetadata, ImpactMetrics }

public sealed record SchemaCaptureFailure(string Status, string DiagnosticCode, int ExitCode);

public sealed class SchemaCaptureException : Exception
{
    public SchemaCaptureException(SchemaCaptureFailure failure, Exception? innerException = null)
        : base(failure.Status, innerException) => Failure = failure;

    public SchemaCaptureFailure Failure { get; }
}

public static class SchemaCaptureErrorClassifier
{
    public static SchemaCaptureFailure MissingConnection() =>
        new(SchemaCaptureStatuses.DatabaseUnreachable, "DB_CONNECTION_REQUIRED", 4);

    public static SchemaCaptureFailure Classify(SchemaCapturePhase phase, Exception exception)
    {
        var diagnostic = SafeDiagnostic(exception);
        return phase switch
        {
            SchemaCapturePhase.OpenConnection =>
                new SchemaCaptureFailure(SchemaCaptureStatuses.DatabaseUnreachable, diagnostic, 4),
            SchemaCapturePhase.MetadataVisibility =>
                new SchemaCaptureFailure(SchemaCaptureStatuses.MetadataVisibility, diagnostic, 5),
            SchemaCapturePhase.SchemaMetadata when exception is SqlException { Number: 229 } =>
                new SchemaCaptureFailure(SchemaCaptureStatuses.MetadataVisibility, diagnostic, 5),
            _ => new SchemaCaptureFailure(SchemaCaptureStatuses.CaptureFailed, diagnostic, 6)
        };
    }

    public static string SafeDiagnostic(Exception exception) => exception is SqlException sqlException
        ? $"SQL_{sqlException.Number}"
        : exception.GetType().Name;
}

public sealed class SchemaCaptureSourceResult
{
    public required SchemaSnapshot Snapshot { get; init; }
    public required string DatabaseName { get; init; }
    public required string ServerVersion { get; init; }
    public int ServerMajorVersion { get; init; }
    public MetricsAvailability MetricsAvailability { get; init; }
    public string? MetricsDiagnosticCode { get; init; }
}

public sealed class SchemaCaptureMetadata
{
    public int FormatVersion { get; init; } = 1;
    public required string CaptureId { get; init; }
    public string Status { get; init; } = SchemaCaptureStatuses.Success;
    public string IdentityPurpose { get; init; } = "INSPECTION";
    public string Environment { get; init; } = "TEST";
    public required string DatabaseName { get; init; }
    public required string ServerVersion { get; init; }
    public int ServerMajorVersion { get; init; }
    public SchemaCoverage SchemaCoverage { get; init; }
    public List<string> UnsupportedSchemaFeatures { get; init; } = [];
    public MetricsAvailability MetricsAvailability { get; init; }
    public string? MetricsDiagnosticCode { get; init; }
    public SortedDictionary<string, int> ObjectCounts { get; init; } = new(StringComparer.Ordinal);
}

public sealed record SchemaCaptureArtifact(
    string CaptureDirectory,
    string CanonicalSchemaPath,
    string SchemaHashPath,
    string MetadataPath,
    string ImpactMetricsPath,
    string SchemaHash,
    SchemaCaptureMetadata Metadata);

public sealed class SchemaCaptureComparison
{
    public string Status { get; init; } = SchemaCaptureStatuses.Success;
    public required string Capture1SchemaHash { get; init; }
    public required string Capture2SchemaHash { get; init; }
    public bool Deterministic { get; init; }
    public required string DiagnosticCode { get; init; }
    public required SchemaDiff SchemaDiff { get; init; }
}

public static class SchemaCapturePolicy
{
    public static bool AllowsReadOnlyCapture(string? discoveryStatus) => true;
}

public static class SchemaCaptureSqlGuard
{
    public static void EnsureSelectOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)
            || !Regex.IsMatch(sql, @"\A\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(sql, @"\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC|EXECUTE)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("SCHEMA_CAPTURE_SQL_NOT_READ_ONLY");
        }
    }
}

public sealed class SchemaCaptureArtifactWriter
{
    public SchemaCaptureArtifact WriteCapture(
        string captureDirectory,
        string captureId,
        SchemaCaptureSourceResult source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        ArgumentNullException.ThrowIfNull(source);
        Directory.CreateDirectory(captureDirectory);

        var canonical = SchemaCanonicalizer.Canonicalize(source.Snapshot);
        var metadata = new SchemaCaptureMetadata
        {
            CaptureId = captureId,
            DatabaseName = source.DatabaseName,
            ServerVersion = source.ServerVersion,
            ServerMajorVersion = source.ServerMajorVersion,
            SchemaCoverage = source.Snapshot.SchemaCoverage,
            UnsupportedSchemaFeatures = source.Snapshot.UnsupportedSchemaFeatures
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            MetricsAvailability = source.MetricsAvailability,
            MetricsDiagnosticCode = source.MetricsDiagnosticCode,
            ObjectCounts = new SortedDictionary<string, int>(source.Snapshot.Objects
                .GroupBy(item => item.Kind, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal), StringComparer.Ordinal)
        };

        var canonicalPath = Path.Combine(captureDirectory, "canonical-schema.json");
        var hashPath = Path.Combine(captureDirectory, "schema.sha256");
        var metadataPath = Path.Combine(captureDirectory, "metadata.json");
        var impactMetricsPath = Path.Combine(captureDirectory, "impact-metrics.json");
        WriteText(canonicalPath, canonical.Json + "\n");
        WriteText(hashPath, canonical.Sha256 + "\n");
        WriteJson(metadataPath, metadata);
        WriteJson(impactMetricsPath, source.Snapshot.ImpactMetrics
            .OrderBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Table, StringComparer.Ordinal)
            .ToArray());
        return new SchemaCaptureArtifact(
            captureDirectory, canonicalPath, hashPath, metadataPath, impactMetricsPath, canonical.Sha256, metadata);
    }

    public SchemaCaptureComparison CompareAndWrite(
        SchemaCaptureArtifact first,
        SchemaCaptureArtifact second,
        string comparisonDirectory)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        Directory.CreateDirectory(comparisonDirectory);
        var firstSchema = ReadCanonical(first.CanonicalSchemaPath, first.SchemaHash);
        var secondSchema = ReadCanonical(second.CanonicalSchemaPath, second.SchemaHash);
        var diff = SchemaComparer.Compare(firstSchema, secondSchema);
        var deterministic = string.Equals(first.SchemaHash, second.SchemaHash, StringComparison.Ordinal)
            && diff.IsEquivalent;
        var comparison = new SchemaCaptureComparison
        {
            Status = deterministic ? SchemaCaptureStatuses.Success : SchemaCaptureStatuses.Nondeterministic,
            Capture1SchemaHash = first.SchemaHash,
            Capture2SchemaHash = second.SchemaHash,
            Deterministic = deterministic,
            DiagnosticCode = deterministic ? "SCHEMA_HASHES_MATCH" : "CONCURRENT_DDL_OR_NONDETERMINISTIC_CAPTURE",
            SchemaDiff = diff
        };
        WriteJson(Path.Combine(comparisonDirectory, "determinism.json"), comparison);
        WriteJson(Path.Combine(comparisonDirectory, "schema-diff.json"), diff);
        return comparison;
    }

    public static SchemaCaptureArtifact ReadArtifact(string captureDirectory)
    {
        var metadataPath = Path.Combine(captureDirectory, "metadata.json");
        var metadata = JsonSerializer.Deserialize<SchemaCaptureMetadata>(
            File.ReadAllText(metadataPath), JsonDefaults.Compact)
            ?? throw new InvalidOperationException("SCHEMA_CAPTURE_METADATA_INVALID");
        var hashPath = Path.Combine(captureDirectory, "schema.sha256");
        var hash = File.ReadAllText(hashPath).Trim();
        return new SchemaCaptureArtifact(
            captureDirectory,
            Path.Combine(captureDirectory, "canonical-schema.json"),
            hashPath,
            metadataPath,
            Path.Combine(captureDirectory, "impact-metrics.json"),
            hash,
            metadata);
    }

    private static CanonicalSchema ReadCanonical(string path, string expectedHash)
    {
        var json = File.ReadAllText(path).TrimEnd('\r', '\n');
        var actualHash = Hashing.Sha256(json);
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("SCHEMA_CAPTURE_HASH_MISMATCH");
        var document = JsonSerializer.Deserialize<CanonicalSchemaDocument>(json, JsonDefaults.Compact)
            ?? throw new InvalidOperationException("CANONICAL_SCHEMA_INVALID");
        return new CanonicalSchema(document, json, actualHash);
    }

    private static void WriteJson<T>(string path, T value) =>
        WriteText(path, JsonSerializer.Serialize(value, JsonDefaults.Indented) + "\n");

    private static void WriteText(string path, string value) =>
        File.WriteAllText(path, value, new UTF8Encoding(false));
}
