using System.Text;
using System.Text.Json;

namespace DatabaseReleaseQualification;

public static class ReleasePayloadBuilder
{
    public static ReleasePayloadMetadata Build(
        ReleaseDescriptor release,
        ReleaseScript forward,
        ReleaseScript rollback)
    {
        var stableIdentity = string.Concat(
            StableField("formatVersion", "1"),
            StableField("releaseId", release.ReleaseId),
            StableField("sourceKind", release.SourceKind),
            StableField("scenario", release.Scenario),
            StableField("databaseLifecycle", release.DatabaseLifecycle),
            StableField("forwardHash", forward.Sha256),
            StableField("rollbackHash", rollback.Sha256));
        return new ReleasePayloadMetadata
        {
            ReleaseId = release.ReleaseId,
            SourceKind = release.SourceKind,
            Scenario = release.Scenario,
            DatabaseLifecycle = release.DatabaseLifecycle,
            ForwardHash = forward.Sha256,
            RollbackHash = rollback.Sha256,
            PayloadHash = Hashing.Sha256(stableIdentity)
        };
    }

    private static string StableField(string name, string value) =>
        $"{name.Length}:{name}{value.Length}:{value}";
}

public sealed class ReleasePackageWriter
{
    public ReleasePackageResult Write(
        string outputRoot,
        string attestationId,
        ReleaseDescriptor release,
        ReleaseScript forward,
        ReleaseScript rollback,
        SchemaSnapshot certifiedSnapshot,
        DependencyAnalysisReport dependencyAnalysis,
        RiskAnalysisReport riskAnalysis,
        RehearsalResult rehearsal,
        IReadOnlyDictionary<string, string>? runMetadata = null)
    {
        ValidateSegment(release.ReleaseId, "RELEASE_ID");
        ValidateSegment(attestationId, "ATTESTATION_ID");
        ValidateSegment(release.Environment, "ENVIRONMENT");

        var normalizedRoot = Path.GetFullPath(outputRoot) + Path.DirectorySeparatorChar;
        var releaseDirectory = Path.GetFullPath(Path.Combine(outputRoot, release.ReleaseId));
        EnsureInside(normalizedRoot, releaseDirectory);
        Directory.CreateDirectory(releaseDirectory);

        var payload = ReleasePayloadBuilder.Build(release, forward, rollback);
        var payloadDirectory = Path.Combine(releaseDirectory, "payload");
        WriteOrVerifyPayload(payloadDirectory, payload, forward, rollback);

        var attestationDirectory = Path.GetFullPath(Path.Combine(
            releaseDirectory,
            "attestations",
            release.Environment.ToUpperInvariant(),
            attestationId));
        EnsureInside(releaseDirectory + Path.DirectorySeparatorChar, attestationDirectory);
        if (Directory.Exists(attestationDirectory) && Directory.EnumerateFileSystemEntries(attestationDirectory).Any())
            throw new InvalidOperationException("QUALIFICATION_ATTESTATION_ALREADY_EXISTS");
        Directory.CreateDirectory(attestationDirectory);

        var analysisEvidence = rehearsal.AnalysisEvidence;
        var effectiveDependencyAnalysis = analysisEvidence?.EffectiveDependencyAnalysis ?? dependencyAnalysis;
        var effectiveRiskAnalysis = analysisEvidence?.EffectiveRisk ?? riskAnalysis;
        WriteJson(Path.Combine(attestationDirectory, "dependency-analysis.json"), effectiveDependencyAnalysis);
        WriteJson(Path.Combine(attestationDirectory, "risk-analysis.json"), effectiveRiskAnalysis);
        if (analysisEvidence is not null)
        {
            WriteJson(Path.Combine(attestationDirectory, "preliminary-dependency-analysis.json"),
                analysisEvidence.PreliminaryDependencyAnalysis);
            WriteJson(Path.Combine(attestationDirectory, "preliminary-risk-analysis.json"),
                analysisEvidence.PreliminaryRisk);
            if (analysisEvidence.RollbackAgainstPost1 is not null)
            {
                WriteJson(Path.Combine(attestationDirectory, "post1-rollback-analysis.json"),
                    analysisEvidence.RollbackAgainstPost1);
            }
        }
        WriteSchemaEvidence(attestationDirectory, rehearsal);

        if (rehearsal.RollbackDiff is not null || rehearsal.ReapplyDiff is not null)
        {
            WriteJson(Path.Combine(attestationDirectory, "schema-diff.json"), new
            {
                rollback = rehearsal.RollbackDiff,
                reapply = rehearsal.ReapplyDiff
            });
        }

        var attestation = new QualificationAttestation
        {
            AttestationId = attestationId,
            ReleaseId = release.ReleaseId,
            Environment = release.Environment.ToUpperInvariant(),
            PayloadHash = payload.PayloadHash,
            ForwardHash = payload.ForwardHash,
            RollbackHash = payload.RollbackHash,
            PreSchemaHash = rehearsal.Pre?.Sha256,
            PostSchemaHash = rehearsal.Post1?.Sha256,
            SchemaRollbackValidity = rehearsal.SchemaRollbackValidity,
            DataRollbackValidity = rehearsal.DataRollbackValidity,
            RollbackCapability = rehearsal.RollbackCapability,
            ForwardRisk = effectiveRiskAnalysis.ForwardRisk,
            RollbackRisk = effectiveRiskAnalysis.RollbackRisk,
            RollbackAnalysisBasis = analysisEvidence?.RollbackAnalysisBasis ?? "PRELIMINARY_PRE",
            RollbackDependencyRisk = effectiveRiskAnalysis.RollbackDependencyRisk,
            RollbackOperationalRisk = effectiveRiskAnalysis.RollbackOperationalRisk,
            PreliminaryFinalRisk = analysisEvidence?.PreliminaryRisk.FinalRisk ?? riskAnalysis.FinalRisk,
            DependencyRisk = effectiveRiskAnalysis.DependencyRisk,
            DataRisk = effectiveRiskAnalysis.DataRisk,
            OperationalRisk = effectiveRiskAnalysis.OperationalRisk,
            FinalRisk = effectiveRiskAnalysis.FinalRisk,
            AnalysisConfidence = effectiveRiskAnalysis.AnalysisConfidence,
            SchemaCoverage = effectiveRiskAnalysis.SchemaCoverage,
            UnsupportedSchemaFeatures = certifiedSnapshot.UnsupportedSchemaFeatures
                .Concat(analysisEvidence?.Post1UnsupportedSchemaFeatures ?? [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            RequiresDbaApproval = rehearsal.SchemaRollbackValidity != SchemaRollbackValidity.Invalid
                && rehearsal.DataRollbackValidity != DataRollbackValidity.Invalid
                && effectiveRiskAnalysis.RequiresDbaApproval,
            QualificationStatus = rehearsal.QualificationStatus,
            ForwardCertified = rehearsal.ForwardCertified,
            RollbackCertified = rehearsal.RollbackCertified,
            ReapplyCertified = rehearsal.ReapplyCertified,
            RunMetadata = SafeRunMetadata(runMetadata)
        };
        WriteJson(Path.Combine(attestationDirectory, "qualification-attestation.json"), attestation);

        return new ReleasePackageResult(releaseDirectory, payloadDirectory, attestationDirectory, payload.PayloadHash);
    }

    private static void WriteOrVerifyPayload(
        string payloadDirectory,
        ReleasePayloadMetadata payload,
        ReleaseScript forward,
        ReleaseScript rollback)
    {
        if (Directory.Exists(payloadDirectory) && Directory.EnumerateFileSystemEntries(payloadDirectory).Any())
        {
            VerifyExact(Path.Combine(payloadDirectory, "forward.sql"), forward.Bytes, "FORWARD_PAYLOAD_MISMATCH");
            VerifyExact(Path.Combine(payloadDirectory, "rollback.sql"), rollback.Bytes, "ROLLBACK_PAYLOAD_MISMATCH");
            VerifyText(Path.Combine(payloadDirectory, "forward.sha256"), forward.Sha256, "FORWARD_HASH_MISMATCH");
            VerifyText(Path.Combine(payloadDirectory, "rollback.sha256"), rollback.Sha256, "ROLLBACK_HASH_MISMATCH");
            var existing = JsonSerializer.Deserialize<ReleasePayloadMetadata>(
                File.ReadAllText(Path.Combine(payloadDirectory, "payload.json")), JsonDefaults.Compact)
                ?? throw new InvalidOperationException("PAYLOAD_METADATA_INVALID");
            if (!string.Equals(existing.PayloadHash, payload.PayloadHash, StringComparison.Ordinal)
                || !string.Equals(existing.ReleaseId, payload.ReleaseId, StringComparison.Ordinal)
                || !string.Equals(existing.SourceKind, payload.SourceKind, StringComparison.Ordinal)
                || !string.Equals(existing.Scenario, payload.Scenario, StringComparison.Ordinal)
                || !string.Equals(existing.DatabaseLifecycle, payload.DatabaseLifecycle, StringComparison.Ordinal)
                || !string.Equals(existing.ForwardHash, payload.ForwardHash, StringComparison.Ordinal)
                || !string.Equals(existing.RollbackHash, payload.RollbackHash, StringComparison.Ordinal))
                throw new InvalidOperationException("PAYLOAD_IDENTITY_MISMATCH");
            return;
        }

        Directory.CreateDirectory(payloadDirectory);
        WriteExact(Path.Combine(payloadDirectory, "forward.sql"), forward.Bytes);
        WriteExact(Path.Combine(payloadDirectory, "rollback.sql"), rollback.Bytes);
        WriteText(Path.Combine(payloadDirectory, "forward.sha256"), forward.Sha256 + "\n");
        WriteText(Path.Combine(payloadDirectory, "rollback.sha256"), rollback.Sha256 + "\n");
        WriteJson(Path.Combine(payloadDirectory, "payload.json"), payload);
    }

    private static void WriteSchemaEvidence(string directory, RehearsalResult rehearsal)
    {
        if (rehearsal.Pre is not null)
        {
            WriteText(Path.Combine(directory, "pre-schema.json"), rehearsal.Pre.Json);
            WriteText(Path.Combine(directory, "pre-schema.sha256"), rehearsal.Pre.Sha256 + "\n");
        }
        if (rehearsal.Post1 is not null)
        {
            WriteText(Path.Combine(directory, "post-schema.json"), rehearsal.Post1.Json);
            WriteText(Path.Combine(directory, "post-schema.sha256"), rehearsal.Post1.Sha256 + "\n");
        }
    }

    private static void ValidateSegment(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) || value is "." or "..")
            throw new InvalidOperationException($"{name}_MUST_BE_A_SINGLE_PATH_SEGMENT");
    }

    private static SortedDictionary<string, string> SafeRunMetadata(IReadOnlyDictionary<string, string>? metadata)
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

    private static void EnsureInside(string parent, string child)
    {
        if (!child.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PACKAGE_PATH_ESCAPES_OUTPUT_ROOT");
    }

    private static void VerifyExact(string path, byte[] expected, string error)
    {
        if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(expected))
            throw new InvalidOperationException(error);
    }

    private static void VerifyText(string path, string expected, string error)
    {
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path).Trim(), expected, StringComparison.Ordinal))
            throw new InvalidOperationException(error);
    }

    private static void WriteJson<T>(string path, T value) =>
        WriteText(path, JsonSerializer.Serialize(value, JsonDefaults.Indented) + "\n");

    private static void WriteText(string path, string value) =>
        WriteExact(path, new UTF8Encoding(false).GetBytes(value));

    private static void WriteExact(string path, byte[] value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(value);
    }
}
