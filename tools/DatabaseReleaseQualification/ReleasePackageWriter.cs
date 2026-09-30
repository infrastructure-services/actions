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
        ValidateChangeMetadata(release);
        var stableIdentity = string.Concat(
            StableField("formatVersion", "1"),
            StableField("releaseId", release.ReleaseId),
            StableField("sourceKind", release.SourceKind),
            StableField("scenario", release.Scenario),
            StableField("databaseLifecycle", release.DatabaseLifecycle),
            StableField("changeOrigin", release.ChangeOrigin),
            StableField("changePath", release.ChangePath),
            StableField("changeReference", release.ChangeReference ?? ""),
            StableField("changeReason", release.ChangeReason ?? ""),
            StableField("forwardHash", forward.Sha256),
            StableField("rollbackHash", rollback.Sha256));
        return new ReleasePayloadMetadata
        {
            ReleaseId = release.ReleaseId,
            SourceKind = release.SourceKind,
            Scenario = release.Scenario,
            DatabaseLifecycle = release.DatabaseLifecycle,
            ChangeOrigin = release.ChangeOrigin,
            ChangePath = release.ChangePath,
            ChangeReference = release.ChangeReference,
            ChangeReason = release.ChangeReason,
            ForwardHash = forward.Sha256,
            RollbackHash = rollback.Sha256,
            PayloadHash = Hashing.Sha256(stableIdentity)
        };
    }

    private static string StableField(string name, string value) =>
        $"{name.Length}:{name}{value.Length}:{value}";

    private static void ValidateChangeMetadata(ReleaseDescriptor release)
    {
        if (release.SourceKind is not ("EF" or "SQL"))
            throw new InvalidOperationException("SOURCE_KIND_INVALID");
        if (release.ChangeOrigin is not (DatabaseChangeOrigins.Application or DatabaseChangeOrigins.Dba))
            throw new InvalidOperationException("CHANGE_ORIGIN_INVALID");
        if (release.ChangePath != DatabaseChangePaths.PlannedRelease)
            throw new InvalidOperationException("OUT_OF_BAND_MUST_USE_RECONCILIATION");
        if (release.ChangeOrigin == DatabaseChangeOrigins.Dba
            && (string.IsNullOrWhiteSpace(release.ChangeReference)
                || string.IsNullOrWhiteSpace(release.ChangeReason)))
            throw new InvalidOperationException("DBA_CHANGE_METADATA_REQUIRED");
        if ((release.ChangeReference?.Length ?? 0) > 256 || (release.ChangeReason?.Length ?? 0) > 1024)
            throw new InvalidOperationException("CHANGE_METADATA_TOO_LONG");
    }
}

public sealed class ReleasePackageWriter
{
    public ReleasePackageResult WriteLegacy(
        string outputRoot, string attestationId, LegacyQualificationOutcomeV1 outcome)
    {
        if (outcome.Readiness is not { Status: "READY_FOR_TEST_REHEARSAL", Handoff: not null }
            || outcome.Package is null || outcome.StaticSafety is not { State: "PASS" }
            || outcome.Payload is null || outcome.CertifiedBaseline is null
            || outcome.ObservedSnapshot is null || outcome.TrustedRuntime is null)
            throw new InvalidOperationException("LEGACY_READINESS_REQUIRED");
        var package = outcome.Package;
        if (outcome.CertifiedBaseline.CertifiedSchemaHash
                != outcome.Readiness.Handoff.Baseline.CertifiedSchemaHash
            || outcome.ObservedSnapshot.ObservedSchemaHash
                != outcome.Readiness.Handoff.ObservedSchemaHash)
            throw new InvalidOperationException("LEGACY_STRUCTURAL_EVIDENCE_STALE");
        if (outcome.BaselineEvidenceHash != Hashing.Sha256(
                LegacyStructuralEvidence.Serialize(outcome.CertifiedBaseline))
            || outcome.ObservedEvidenceHash != Hashing.Sha256(
                LegacyStructuralEvidence.Serialize(outcome.ObservedSnapshot)))
            throw new InvalidOperationException("LEGACY_STRUCTURAL_EVIDENCE_STALE");
        if (!LegacyRuntimeEvidenceHash.Verify(outcome.TrustedRuntime)
            || outcome.TrustedRuntime.EvidenceSetHash != outcome.Readiness.Handoff.EvidenceSetHash)
            throw new InvalidOperationException("LEGACY_RUNTIME_EVIDENCE_STALE");
        package.Verify();
        if (LegacyReadinessHash.Bind(outcome.Readiness).EvidenceHash != outcome.Readiness.EvidenceHash)
            throw new InvalidOperationException("LEGACY_READINESS_EVIDENCE_STALE");
        if (LegacyStaticSafety.CalculateHash(outcome.StaticSafety) != outcome.StaticSafety.EvidenceHash
            || outcome.StaticSafety.EvidenceHash != outcome.Readiness.Handoff.StaticSafety.EvidenceHash
            || outcome.StaticSafety.PackageIdentity != package.Evidence.PackageIdentity)
            throw new InvalidOperationException("LEGACY_STATIC_SAFETY_EVIDENCE_STALE");
        if (package.Evidence.PackageIdentity != outcome.Readiness.Handoff.PackageIdentity
            || package.Evidence.Forward.Sha256 != outcome.Payload.ForwardHash
            || package.Evidence.Rollback.Sha256 != outcome.Payload.RollbackHash
            || outcome.Readiness.Handoff.PayloadHash != outcome.Payload.PayloadHash)
            throw new InvalidOperationException("LEGACY_PACKAGE_IDENTITY_MISMATCH");
        var manifest = package.ParsedManifest;
        var release = new ReleaseDescriptor {
            ReleaseId = manifest.ReleaseId, Environment = "TEST",
            SourceKind = "SQL", Scenario = "EXISTING_LEGACY",
            DatabaseLifecycle = "EXISTING", ChangeOrigin = manifest.ChangeOrigin,
            ChangeReference = manifest.ChangeReference, ChangeReason = manifest.ChangeReason
        };
        var forward = new ReleaseScript("forward", package.ForwardBytes);
        var rollback = new ReleaseScript("rollback", package.RollbackBytes);
        if (ReleasePayloadBuilder.Build(release, forward, rollback).PayloadHash != outcome.Payload.PayloadHash)
            throw new InvalidOperationException("LEGACY_PAYLOAD_IDENTITY_STALE");
        var analysis = new DependencyAnalysisReport {
            Forward = outcome.StaticSafety.ForwardAnalysis,
            Rollback = outcome.StaticSafety.RollbackAnalysis
        };
        var rehearsal = new RehearsalResult {
            QualificationStatus = "ANALYZED_NOT_REHEARSED",
            SchemaRollbackValidity = SchemaRollbackValidity.NotTested,
            DataRollbackValidity = outcome.StaticSafety.Impact.DataRequired
                ? DataRollbackValidity.NotTested : DataRollbackValidity.NotApplicable,
            RollbackCapability = RollbackCapability.Unknown,
            ForwardCertified = false, RollbackCertified = false, ReapplyCertified = false,
            ExecutionAudit = ["REHEARSAL:NOT_EXECUTED"]
        };
        var result = Write(outputRoot, attestationId, release, forward, rollback,
            outcome.ObservedSnapshot.Snapshot, analysis, outcome.Readiness.Handoff.Risk, rehearsal,
            new Dictionary<string, string> { ["engineMode"] = "ANALYZE_ONLY" },
            package.Evidence.PackageIdentity);
        var manifestPath = Path.Combine(result.AttestationDirectory, "legacy-manifest.json");
        WriteExact(manifestPath, package.ManifestBytes);
        VerifyExact(manifestPath, package.ManifestBytes, "LEGACY_MANIFEST_MUTATED");
        WriteJson(Path.Combine(result.AttestationDirectory,
            "certified-structural-baseline.json"), outcome.CertifiedBaseline);
        WriteJson(Path.Combine(result.AttestationDirectory,
            "observed-current-snapshot.json"), outcome.ObservedSnapshot);
        WriteExact(Path.Combine(result.AttestationDirectory,
            "trusted-runtime-evidence.json"), Encoding.UTF8.GetBytes(
                LegacyRuntimeEvidenceHash.Serialize(outcome.TrustedRuntime) + "\n"));
        var sidecar = new {
            contractVersion = 1,
            packageIdentity = package.Evidence.PackageIdentity,
            payloadHash = result.PayloadHash,
            readinessEvidenceHash = outcome.Readiness.EvidenceHash,
            artifacts = package.Evidence
        };
        var sidecarPath = Path.Combine(result.AttestationDirectory, "legacy-provenance.json");
        WriteJson(sidecarPath, sidecar);
        var written = JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(sidecarPath));
        if (written.GetProperty("packageIdentity").GetString() != package.Evidence.PackageIdentity
            || written.GetProperty("payloadHash").GetString() != result.PayloadHash
            || written.GetProperty("readinessEvidenceHash").GetString() != outcome.Readiness.EvidenceHash)
            throw new InvalidOperationException("LEGACY_PROVENANCE_MISMATCH");
        return result;
    }

    public ReleasePackageResult Write(
        string outputRoot,
        string attestationId,
        ReleaseDescriptor release,
        ReleaseScript forward,
        ReleaseScript rollback,
        SchemaSnapshot analysisContext,
        DependencyAnalysisReport dependencyAnalysis,
        RiskAnalysisReport riskAnalysis,
        RehearsalResult rehearsal,
        IReadOnlyDictionary<string, string>? runMetadata = null,
        string? legacyPackageIdentity = null)
    {
        ValidateSegment(release.ReleaseId, "RELEASE_ID");
        ValidateSegment(attestationId, "ATTESTATION_ID");
        ValidateSegment(release.Environment, "ENVIRONMENT");
        if (rehearsal.RecoveryCoverage is { } coverage
            && (coverage.ForwardHash != forward.Sha256 || coverage.RollbackHash != rollback.Sha256))
            throw new InvalidOperationException("RECOVERY_COVERAGE_PAYLOAD_MISMATCH");

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
            LegacyPackageIdentity = legacyPackageIdentity,
            RecoveryCoverage = rehearsal.RecoveryCoverage,
            AttestationId = attestationId,
            ReleaseId = release.ReleaseId,
            Environment = release.Environment.ToUpperInvariant(),
            PayloadHash = payload.PayloadHash,
            ForwardHash = payload.ForwardHash,
            RollbackHash = payload.RollbackHash,
            ChangeOrigin = payload.ChangeOrigin,
            ChangePath = payload.ChangePath,
            ChangeReference = payload.ChangeReference,
            ChangeReason = payload.ChangeReason,
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
            UnsupportedSchemaFeatures = analysisContext.UnsupportedSchemaFeatures
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
                || !string.Equals(existing.ChangeOrigin, payload.ChangeOrigin, StringComparison.Ordinal)
                || !string.Equals(existing.ChangePath, payload.ChangePath, StringComparison.Ordinal)
                || !string.Equals(existing.ChangeReference, payload.ChangeReference, StringComparison.Ordinal)
                || !string.Equals(existing.ChangeReason, payload.ChangeReason, StringComparison.Ordinal)
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
