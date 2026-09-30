using System.Text;
using System.Text.Json;
using DatabaseReleaseQualification;

public static class LegacyReadinessTests
{
    public static (string Name, Func<Task> Run)[] Cases = [
        ("legacy package positivo queda READY sin permiso de promoción", Ready),
        ("legacy package onboarding no MANAGED bloquea", OnboardingBlock),
        ("legacy package baseline no certificado bloquea", BaselineBlock),
        ("legacy observed metadata incompleta bloquea aun con hash igual", MetadataBlock),
        ("legacy observed metrics incompletas bloquean aun con hash igual", MetricsBlock),
        ("legacy hash contract incompatible bloquea", HashContractBlock),
        ("legacy hash estructural no incluye newline del artifact", StructuralHashVector),
        ("legacy DatabaseState se recalcula desde Registry", DatabaseStateRecomputed),
        ("legacy observed de otro target bloquea aun con hash igual", ObservedTargetBlock),
        ("legacy safety metadata de otro servidor bloquea", SafetyIdentityBlock),
        ("legacy package governance hash y Registry commit stale bloquean", ProvenanceBlocks),
        ("legacy package inválido conserva clasificación EXISTING_LEGACY", ArtifactBlock),
        ("legacy package clasificación no LEGACY rechaza request", ClassificationBlock),
        ("legacy writer conserva manifest y bytes exactos", Writer),
        ("legacy writer rechaza readiness stale", WriterRejectsStale),
        ("legacy writer rechaza metrics mutadas tras verificación", WriterRejectsMutation),
        ("legacy evidenceSetHash ordena fuentes y cubre metrics", EvidenceSetHashVector),
        ("legacy evidenceSetHash cubre runtime fuentes baseline artifact y clasificación", EvidenceSetHashCoverage),
        ("legacy CLI con resolver fake escribe readiness y bloquea sin resolver", Cli)
    ];

    private static readonly byte[] Forward = Encoding.UTF8.GetBytes("SELECT 1;\r\n");
    private static readonly byte[] Rollback = Encoding.UTF8.GetBytes("SELECT 2;\n");
    private static readonly LegacyArtifactSelectionV1 Selection = new(1,
        "db/releases/m.json", new string('a', 40),
        new("github.com", "1234", "team/repo"));

    private static async Task Ready()
    {
        var outcome = await Adapter().EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Readiness is not { Status: "READY_FOR_TEST_REHEARSAL",
                QualificationStatus: "ANALYZED_NOT_REHEARSED",
                CanProceedToPromotion: false, Handoff: not null })
            throw new Exception("Expected ready analyze-only handoff: " +
                string.Join(",", outcome.Readiness?.ReasonCodes ?? outcome.Rejected?.ReasonCodes ?? []));
        if (outcome.Readiness.Handoff.ExecutionAuthorized
            || outcome.Readiness.Handoff.Recovery.Structure != "REQUIRED"
            || outcome.Readiness.Handoff.Recovery.Data != "NOT_REQUIRED")
            throw new Exception("Handoff promoted or lost recovery requirements");
    }

    private static async Task OnboardingBlock()
    {
        var outcome = await Adapter(onboarding: "PENDING").EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Readiness?.Status != "BLOCKED"
            || !outcome.Readiness.ReasonCodes.Contains("ONBOARDING_NOT_MANAGED")
            || outcome.Readiness.Scenario != "EXISTING_LEGACY")
            throw new Exception("Onboarding failure changed classification/readiness");
    }

    private static async Task BaselineBlock()
    {
        var outcome = await Adapter(certified: false).EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Readiness?.Status != "BLOCKED"
            || !outcome.Readiness.ReasonCodes.Contains("BASELINE_NOT_CERTIFIED"))
            throw new Exception("Baseline failure not blocked");
    }

    private static async Task MetadataBlock() =>
        await ExpectReason("OBSERVED_METADATA_INCOMPLETE", Adapter(observedFailure: "metadata"));

    private static async Task MetricsBlock() =>
        await ExpectReason("OBSERVED_IMPACT_METRICS_INCOMPLETE", Adapter(observedFailure: "metrics"));

    private static async Task HashContractBlock() =>
        await ExpectReason("STRUCTURAL_HASH_CONTRACT_MISMATCH", Adapter(observedFailure: "hashContract"));

    private static async Task ObservedTargetBlock()
    {
        var outcome = await Adapter(observedFailure: "target")
            .EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Rejected?.ReasonCodes.SingleOrDefault() != "OBSERVED_EVIDENCE_UNVERIFIED")
            throw new Exception("Wrong target escaped trusted runtime boundary");
    }

    private static async Task SafetyIdentityBlock() =>
        await ExpectReason("TARGET_IDENTITY_MISMATCH", Adapter(observedFailure: "safetyServer"));

    private static Task StructuralHashVector()
    {
        var snapshot = new SchemaSnapshot();
        var canonical = SchemaCanonicalizer.Canonicalize(snapshot);
        var contract = HashContract();
        if (contract.Hash(snapshot) != Hashing.Sha256(Encoding.UTF8.GetBytes(canonical.Json))
            || contract.Hash(snapshot) == Hashing.Sha256(Encoding.UTF8.GetBytes(canonical.Json + "\n")))
            throw new Exception("Structural hash includes serialized file newline");
        return Task.CompletedTask;
    }

    private static async Task DatabaseStateRecomputed() =>
        await ExpectReason("EVIDENCE_CORRELATION_MISMATCH", Adapter(stateMismatch: true));

    private static async Task ExpectReason(string reason, LegacyPackageQualificationAdapter adapter)
    {
        var outcome = await adapter.EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Readiness?.Status != "BLOCKED"
            || !outcome.Readiness.ReasonCodes.Contains(reason))
            throw new Exception("Missing blocked reason: " + reason);
    }

    private static StructuralHashContractV1 HashContract(bool incompatible = false)
    {
        var source = new LegacyGitDocumentV1(Selection.ExpectedRepository,
            Selection.ExpectedCommit, "tools/canonicalizer.cs", new string('1', 64));
        var producer = new LegacyProducerRefV1(source, "SCHEMA_CANONICALIZER_V1", 1);
        return new(1, incompatible ? "UNKNOWN" : StructuralHashContractV1.SupportedProfile,
            1, 1, "SHA-256", producer, producer);
    }

    private static async Task ArtifactBlock()
    {
        var selection = Selection with { ManifestPath = "db/releases/missing.json" };
        var outcome = await Adapter().EvaluateAsync(new(1, selection, "target1", null));
        if (outcome.Readiness is not { Scenario: "EXISTING_LEGACY", Status: "BLOCKED" }
            || outcome.ArtifactBlocked?.Status != "BLOCKED"
            || !outcome.Readiness.ReasonCodes.Contains("MANIFEST_MISSING"))
            throw new Exception("Artifact failure corrupted classification");
    }

    private static async Task ProvenanceBlocks()
    {
        var governance = await Adapter(governanceMismatch: true)
            .EvaluateAsync(new(1, Selection, "target1", null));
        if (!governance.Readiness!.ReasonCodes.Contains("GOVERNANCE_INVALID"))
            throw new Exception("Bad governance bytes accepted");
        var registry = await Adapter(registryMismatch: true)
            .EvaluateAsync(new(1, Selection, "target1", null));
        if (!registry.Readiness!.ReasonCodes.Contains("BASELINE_NOT_CERTIFIED"))
            throw new Exception("Stale Registry commit accepted");
    }

    private static async Task ClassificationBlock()
    {
        var outcome = await Adapter(scenario: "UNCLASSIFIED").EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Rejected?.Status != "BLOCKED" || outcome.Readiness is not null)
            throw new Exception("Invalid classification fabricated scenario");
    }

    private static async Task Writer()
    {
        var outcome = await Adapter().EvaluateAsync(new(1, Selection, "target1", null));
        var root = Path.Combine(Path.GetTempPath(), "lpqv1-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new ReleasePackageWriter().WriteLegacy(root, "a1", outcome);
            if (!File.ReadAllBytes(Path.Combine(result.PayloadDirectory, "forward.sql")).SequenceEqual(Forward)
                || !File.ReadAllBytes(Path.Combine(result.PayloadDirectory, "rollback.sql")).SequenceEqual(Rollback)
                || !File.ReadAllBytes(Path.Combine(result.AttestationDirectory, "legacy-manifest.json"))
                    .SequenceEqual(outcome.Package!.ManifestBytes))
                throw new Exception("Writer changed Git bytes");
            var sidecar = JsonDocument.Parse(File.ReadAllBytes(
                Path.Combine(result.AttestationDirectory, "legacy-provenance.json")));
            if (sidecar.RootElement.GetProperty("packageIdentity").GetString()
                != outcome.Readiness!.PackageIdentity)
                throw new Exception("Sidecar identity mismatch");
            if (!File.Exists(Path.Combine(result.AttestationDirectory,
                    "certified-structural-baseline.json"))
                || !File.Exists(Path.Combine(result.AttestationDirectory,
                    "observed-current-snapshot.json"))
                || File.Exists(Path.Combine(result.AttestationDirectory,
                    "certified-snapshot.json")))
                throw new Exception("Writer confused certified baseline with observed snapshot");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("lpqv1-test-", StringComparison.Ordinal))
                Directory.Delete(full, true);
        }
    }

    private static async Task Cli()
    {
        var root = Path.Combine(Path.GetTempPath(), "lpqv1-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var requestPath = Path.Combine(root, "request.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new {
                contractVersion = 1,
                artifactSelection = new {
                    contractVersion = 1,
                    manifestPath = Selection.ManifestPath,
                    expectedCommit = Selection.ExpectedCommit,
                    expectedRepository = new {
                        host = Selection.ExpectedRepository.Host,
                        repositoryId = Selection.ExpectedRepository.RepositoryId,
                        fullName = Selection.ExpectedRepository.FullName
                    }
                },
                targetId = "target1"
            }));
            var blocked = await LegacyPackageCli.RunAsync(
                ["--request", requestPath, "--output", Path.Combine(root, "blocked")]);
            if (blocked != 66) throw new Exception("Untrusted CLI request was accepted");
            var output = Path.Combine(root, "ready");
            var ready = await LegacyPackageCli.RunAsync(
                ["--request", requestPath, "--output", output], Adapter());
            if (ready != 0 || !Directory.EnumerateFiles(output, "legacy-readiness.json",
                    SearchOption.AllDirectories).Any())
                throw new Exception("Fake CLI did not write readiness");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("lpqv1-cli-", StringComparison.Ordinal))
                Directory.Delete(full, true);
        }
    }

    private static async Task WriterRejectsStale()
    {
        var outcome = await Adapter().EvaluateAsync(new(1, Selection, "target1", null));
        var stale = outcome with {
            Readiness = outcome.Readiness! with { EvidenceHash = new string('0', 64) }
        };
        var root = Path.Combine(Path.GetTempPath(), "lpqv1-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            new ReleasePackageWriter().WriteLegacy(root, "stale", stale);
        }
        catch (InvalidOperationException exception) when (exception.Message == "LEGACY_READINESS_EVIDENCE_STALE")
        { return; }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("lpqv1-stale-", StringComparison.Ordinal)
                && Directory.Exists(full))
                Directory.Delete(full, true);
        }
        throw new Exception("Stale readiness accepted");
    }

    private static async Task WriterRejectsMutation()
    {
        var outcome = await Adapter().EvaluateAsync(new(1, Selection, "target1", null));
        outcome.ObservedSnapshot!.Snapshot.ImpactMetrics.Add(new TableImpactMetric {
            Schema = "dbo", Table = "Widget", RowCount = 1
        });
        var root = Path.Combine(Path.GetTempPath(), "lpqv1-mutated-" + Guid.NewGuid().ToString("N"));
        try
        {
            new ReleasePackageWriter().WriteLegacy(root, "mutated", outcome);
        }
        catch (InvalidOperationException exception)
            when (exception.Message == "LEGACY_STRUCTURAL_EVIDENCE_STALE")
        { return; }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        throw new Exception("Mutated observed metrics accepted by writer");
    }

    private static async Task EvidenceSetHashVector()
    {
        var outcome = await Adapter().EvaluateAsync(new(1, Selection, "target1", null));
        var trusted = outcome.TrustedRuntime!;
        var reordered = trusted with { Sources = trusted.Sources.Reverse().ToArray() };
        if (LegacyRuntimeEvidenceHash.Bind(reordered).EvidenceSetHash != trusted.EvidenceSetHash)
            throw new Exception("Source order affected evidenceSetHash");
        var oldHash = trusted.EvidenceSetHash;
        trusted.Evidence!.ObservedSnapshot.Snapshot.ImpactMetrics.Add(new TableImpactMetric {
            Schema = "dbo", Table = "Widget", RowCount = 2
        });
        if (LegacyRuntimeEvidenceHash.Verify(trusted)
            || LegacyRuntimeEvidenceHash.Bind(trusted).EvidenceSetHash == oldHash)
            throw new Exception("Metrics mutation omitted from evidenceSetHash");
    }

    private static async Task EvidenceSetHashCoverage()
    {
        var trusted = (await Adapter().EvaluateAsync(new(1, Selection, "target1", null)))
            .TrustedRuntime!;
        var payload = trusted.Evidence!;
        var changedClassification = JsonDocument.Parse("{\"status\":\"CLASSIFIED\",\"classificationInvoked\":true,\"targetId\":\"target1\",\"classification\":{\"classificationResult\":{\"inferences\":{\"scenario\":\"EXISTING_EF\"}}}}").RootElement.Clone();
        var snapshot = payload.ObservedSnapshot.Snapshot;
        var changedSnapshot = new SchemaSnapshot {
            Objects = [.. snapshot.Objects], ImpactMetrics = [.. snapshot.ImpactMetrics],
            UnsupportedSchemaFeatures = [.. snapshot.UnsupportedSchemaFeatures, "feature"]
        };
        var changed = new[] {
            trusted with { Runtime = trusted.Runtime! with { RunAttempt = "2" } },
            trusted with { Sources = trusted.Sources.Select((source, index) => index == 0
                ? source with { Producer = source.Producer with {
                    Source = source.Producer.Source with { Revision = new string('e', 40) }
                } } : source).ToArray() },
            trusted with { Evidence = payload with { CertifiedStructuralBaseline =
                payload.CertifiedStructuralBaseline with { CertifiedSchemaHash = new string('0', 64) } } },
            trusted with { Evidence = payload with { ObservedSnapshot =
                payload.ObservedSnapshot with { Snapshot = changedSnapshot } } },
            trusted with { Evidence = payload with { Artifact = payload.Artifact with {
                Manifest = payload.Artifact.Manifest with { Sha256 = new string('0', 64) }
            } } },
            trusted with { Evidence = payload with { Classification = changedClassification } },
            trusted with { Request = trusted.Request with { TargetId = "other" } }
        };
        foreach (var mutation in changed)
            if (LegacyRuntimeEvidenceHash.Verify(mutation))
                throw new Exception("Contractual evidence mutation retained evidenceSetHash");
    }

    private static LegacyPackageQualificationAdapter Adapter(
        string onboarding = "MANAGED", bool certified = true,
        string scenario = "EXISTING_LEGACY",
        bool governanceMismatch = false, bool registryMismatch = false,
        string? observedFailure = null, bool stateMismatch = false)
    {
        return new(new FakeEvidence(onboarding, certified, scenario,
                governanceMismatch, registryMismatch, observedFailure, stateMismatch),
            LegacyArtifactTests.Transport(Forward, Rollback),
            new FakeSafety(observedFailure == "safetyServer" ? "OTHER" : "SQL1"),
            new UnusedSecurity());
    }

    private sealed class FakeEvidence(string onboardingStatus, bool certified, string scenario,
        bool governanceMismatch, bool registryMismatch, string? observedFailure,
        bool stateMismatch)
        : ILegacyVerifiedEvidenceSource
    {
        public Task VerifyFreshnessAsync(LegacyQualificationRequestV1 request,
            CancellationToken token) => Task.CompletedTask;

        public async Task<LegacyVerifiedEvidenceV1> ResolveAsync(
            LegacyQualificationRequestV1 request, CancellationToken token)
        {
            var targetId = request.TargetId;
            var governanceBytes = Encoding.UTF8.GetBytes("governance");
            var onboardingBytes = Encoding.UTF8.GetBytes("onboarding");
            var snapshot = new SchemaSnapshot();
            var canonical = SchemaCanonicalizer.Canonicalize(snapshot);
            var registryTarget = new DatabaseTarget {
                ApplicationId = "app1", Environment = "TEST", DatabaseName = "TestDb",
                Lifecycle = "EXISTING",
                CertificationStatus = certified ? "CERTIFIED" : "BASELINE_REQUIRED",
                CertifiedSchemaHash = certified ? canonical.Sha256 : null
            };
            var registryBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new DatabaseRegistryDocument { RegistryFormatVersion = 1,
                    Targets = [registryTarget] }, DatabaseStateJson.Compact));
            var provenance = new LegacySourceProvenanceV1("team/registry", "governance.json",
                new string('b', 40), governanceMismatch
                    ? new string('0', 64) : Hashing.Sha256(governanceBytes));
            var onboardingProvenance = provenance with {
                SourcePath = "onboarding.json",
                SourceSha256 = Hashing.Sha256(onboardingBytes)
            };
            var authority = new LegacyAuthorityReferenceV1("REGISTRY_GIT_PR", "policy.json");
            var governance = new LegacyGovernanceV1(provenance, authority,
                new("endpoint1", "TestDb", "ALLOW_LIST", ["SQL1"]),
                "app1", "TEST", targetId, "EXISTING", "LEGACY_UNMANAGED");
            var onboarding = new LegacyOnboardingV1(onboardingProvenance,
                new("ONBOARDING_GIT_PR", "onboarding-policy.json"), onboardingStatus);
            var registry = new RegistryProvenance {
                RegistryRepository = "team/registry", RegistryRef = new string('b', 40),
                RegistryCommitSha = new string(registryMismatch ? 'c' : 'b', 40),
                RegistryFilePath = "targets.json",
                RegistryFileSha256 = Hashing.Sha256(registryBytes)
            };
            var state = new DatabaseStateEvaluation {
                ApplicationId = "app1", Environment = "TEST", DatabaseName = "TestDb",
                ObservedSchemaHash = canonical.Sha256,
                CertifiedSchemaHash = canonical.Sha256,
                RegistryStatus = certified ? "CERTIFIED" : "BASELINE_REQUIRED",
                DriftStatus = DatabaseDriftStatuses.Match,
                GateStatus = DatabaseGateStatuses.Eligible,
                Reason = stateMismatch ? "FORGED_MATCH" : "SCHEMA_HASH_MATCH",
                RegistryFormatVersion = 1,
                RegistryProvenance = registry,
                Target = registryTarget
            };
            var baseline = new CertifiedStructuralBaselineV1(1,
                "CERTIFIED_STRUCTURAL_BASELINE", targetId,
                new("app1", "TEST", "TestDb", "EXISTING"), 1,
                state.Target!.CertificationStatus, canonical.Sha256, registry,
                provenance);
            var identity = new ObservedSnapshotIdentityV1("SQL1", "TestDb");
            var observed = new ObservedCurrentSnapshotV1(1, "OBSERVED_CURRENT_SNAPSHOT",
                observedFailure == "target" ? "other-target" : targetId, "TEST",
                "endpoint1", identity, snapshot, canonical.Sha256,
                HashContract(observedFailure == "hashContract"),
                new(observedFailure == "metadata" ? "PARTIAL" : "SUFFICIENT", "COMPLETE",
                    observedFailure == "metrics" ? "PARTIAL" : "COMPLETE"),
                new(canonical.Sha256, canonical.Sha256, identity, identity, true, true),
                DateTimeOffset.UtcNow);
            var classification = JsonDocument.Parse(JsonSerializer.Serialize(new {
                status = "CLASSIFIED", classificationInvoked = true, targetId,
                classification = new { classificationResult = new {
                    inferences = new { scenario }
                } }
            })).RootElement.Clone();
            var discovered = await new LegacyArtifactDiscovery(
                LegacyArtifactTests.Transport(Forward, Rollback)).DiscoverAsync(
                    Selection, targetId, token);
            var sourceDocument = new LegacyGitDocumentV1(Selection.ExpectedRepository,
                Selection.ExpectedCommit, "scripts/producer.mjs", new string('1', 64));
            var producer = new LegacyProducerRefV1(sourceDocument, "SYNTHETIC_TEST", 1);
            var keys = new[] { "artifact", "governance", "onboarding", "observedSnapshot",
                "registry", "repositoryDiscovery", "sqlDiscovery" };
            var sources = keys.Select(key => new LegacyRuntimeSourceEntryV1(key,
                targetId, key is "governance" or "onboarding" or "registry" ? "GOVERNED_GIT"
                    : key is "artifact" or "repositoryDiscovery" ? "APPLICATION_GIT"
                    : "CURRENT_SQL_OBSERVATION", [sourceDocument], producer,
                new string('2', 64))).ToArray();
            var runtime = new LegacyRuntimeContextV1(Selection.ExpectedRepository,
                ".github/workflows/test.yml", new string('f', 40), "1", "1", "job");
            var payload = new LegacyRuntimeEvidencePayloadV1(governance, onboarding,
                baseline, observed, state, "CONSISTENT", classification,
                discovered.Evidence);
            var trusted = LegacyRuntimeEvidenceHash.Bind(new(1,
                "TRUSTED_LEGACY_RUNTIME_EVIDENCE", "RESOLVED",
                new(1, targetId, request.ArtifactSelection), runtime, producer,
                sources, [], payload, null, false));
            return new LegacyVerifiedEvidenceV1(
                Selection.ExpectedRepository, Selection.ExpectedCommit,
                new string('f', 40), governance, governanceBytes,
                onboarding, onboardingBytes, scenario,
                Encoding.UTF8.GetBytes(classification.GetRawText()), state, registryBytes,
                baseline, observed, trusted);
        }
    }

    private sealed class FakeSafety(string server) : ILegacyScopeSafetySource
    {
        public Task<LegacyScopeSafetySnapshotV1> CaptureAsync(
            IReadOnlyList<RecoverySecuritySecurable> scope, CancellationToken token)
        {
            if (scope.Count != 0) throw new Exception("Unexpected scope");
            var result = new LegacyScopeSafetySnapshotV1(1, true, server, "TestDb",
                true, true, false, [], "");
            var hash = Hashing.Sha256(JsonSerializer.Serialize(new {
                result.ContractVersion, result.Complete, result.ServerInstance,
                result.DatabaseName, result.DatabaseDdlTriggersComplete,
                result.ServerDdlTriggersComplete, result.HasEnabledDdlTrigger,
                result.Objects
            }, JsonDefaults.Compact));
            return Task.FromResult(result with { Sha256 = hash });
        }
    }

    private sealed class UnusedSecurity : IRecoverySecurityCatalogReader
    {
        public Task<RecoverySecuritySnapshot> ReadAsync(RecoverySecurityScope scope,
            RecoveryPhase phase, CancellationToken cancellationToken = default) =>
            throw new Exception("Unexpected SQL security access");
    }
}
