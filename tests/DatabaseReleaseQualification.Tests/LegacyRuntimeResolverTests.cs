using System.Text;
using System.Text.Json;
using DatabaseReleaseQualification;

public static class LegacyRuntimeResolverTests
{
    public static (string Name, Func<Task> Run)[] Cases = [
        ("legacy resolver real produce READY sintético sin snapshot histórico", Positive),
        ("legacy resolver conserva clasificación ante artifact inválido", InvalidArtifact),
        ("legacy resolver conserva clasificación ante baseline no certificada", UncertifiedBaseline),
        ("legacy resolver bloquea freshness cambiante", Stale),
        ("legacy resolver rechaza result hash alterado", AlteredSource),
        ("legacy resolver rechaza productor incorrecto", WrongProducer),
        ("legacy resolver rechaza revisión de productor incorrecta", WrongProducerRevision),
        ("legacy resolver rechaza repo de productor incorrecto", WrongProducerRepository),
        ("legacy resolver rechaza fuente ausente o duplicada", MissingOrDuplicate),
        ("legacy resolver rechaza runtime de otra sesión", WrongRuntime),
        ("legacy productor exige output exacto sin duplicados", DiscoveryOutputContract),
        ("legacy rechaza alias de remote git", RemoteIdentity)
    ];

    private static readonly byte[] Forward = Encoding.UTF8.GetBytes("SELECT 1;\n");
    private static readonly byte[] Rollback = Encoding.UTF8.GetBytes("SELECT 2;\n");
    private static readonly LegacyArtifactSelectionV1 Selection = new(1,
        "db/releases/m.json", new string('a', 40),
        new("github.com", "1234", "team/repo"));

    private static async Task Positive()
    {
        var (adapter, acquisition) = await Setup();
        var outcome = await adapter.EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Readiness is not { Status: "READY_FOR_TEST_REHEARSAL",
                Handoff: not null, CanProceedToPromotion: false }
            || outcome.Readiness.Handoff.ExecutionAuthorized
            || outcome.TrustedRuntime?.EvidenceSetHash
                != outcome.Readiness.Handoff.EvidenceSetHash
            || acquisition.FreshnessChecks != 2)
            throw new Exception("Synthetic resolved runtime did not yield bound analyze-only readiness: "
                + string.Join(",", outcome.Readiness?.ReasonCodes
                    ?? outcome.Rejected?.ReasonCodes ?? []));
    }

    private static async Task Stale()
    {
        var (adapter, acquisition) = await Setup();
        acquisition.FailSecondFreshness = true;
        var outcome = await adapter.EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Readiness is not { Status: "BLOCKED" }
            || !outcome.Readiness.ReasonCodes.Contains("SOURCE_FRESHNESS_UNVERIFIED"))
            throw new Exception("Changed governed revision became READY");
    }

    private static async Task InvalidArtifact()
    {
        var (adapter, _) = await Setup();
        var missing = Selection with { ManifestPath = "db/releases/missing.json" };
        var outcome = await adapter.EvaluateAsync(new(1, missing, "target1", null));
        if (outcome.Readiness is not { Scenario: "EXISTING_LEGACY", Status: "BLOCKED" }
            || outcome.Readiness.ReasonCodes.Count == 0
            || outcome.TrustedRuntime is not { Status: "BLOCKED", Evidence: null,
                EvidenceSetHash: null })
            throw new Exception("Invalid package lost governed Legacy classification");
    }

    private static async Task UncertifiedBaseline()
    {
        var (adapter, acquisition) = await Setup();
        acquisition.Mutate = "uncertified";
        var outcome = await adapter.EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Readiness is not { Scenario: "EXISTING_LEGACY", Status: "BLOCKED" }
            || !outcome.Readiness.ReasonCodes.Contains("BASELINE_NOT_CERTIFIED"))
            throw new Exception("Uncertified baseline erased governed classification");
    }

    private static async Task AlteredSource()
    {
        var (adapter, acquisition) = await Setup();
        acquisition.AlterRegistryHash = true;
        var outcome = await adapter.EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Readiness is not { Scenario: "EXISTING_LEGACY", Status: "BLOCKED" }
            || outcome.Readiness.ReasonCodes.SingleOrDefault() != "OBSERVED_EVIDENCE_UNVERIFIED")
            throw new Exception("Forged source hash accepted");
    }

    private static async Task WrongProducer()
    {
        var (adapter, acquisition) = await Setup();
        acquisition.Mutate = "producerPath";
        await Rejected(adapter);
    }

    private static async Task WrongProducerRevision()
    {
        var (adapter, acquisition) = await Setup();
        acquisition.Mutate = "producerRevision";
        await Rejected(adapter);
    }

    private static async Task WrongProducerRepository()
    {
        var (adapter, acquisition) = await Setup();
        acquisition.Mutate = "producerRepository";
        await Rejected(adapter);
    }

    private static async Task MissingOrDuplicate()
    {
        foreach (var mutation in new[] { "missing", "duplicate" })
        {
            var (adapter, acquisition) = await Setup();
            acquisition.Mutate = mutation;
            var outcome = await adapter.EvaluateAsync(new(1, Selection, "target1", null));
            if (outcome.Readiness is not { Scenario: "EXISTING_LEGACY", Status: "BLOCKED" })
                throw new Exception("Invalid source set accepted: " + mutation);
        }
    }

    private static async Task WrongRuntime()
    {
        var (adapter, acquisition) = await Setup();
        acquisition.Mutate = "runtimeRepository";
        await Rejected(adapter);
    }

    private static async Task Rejected(LegacyPackageQualificationAdapter adapter)
    {
        var outcome = await adapter.EvaluateAsync(new(1, Selection, "target1", null));
        if (outcome.Readiness is not { Scenario: "EXISTING_LEGACY", Status: "BLOCKED" }
            || outcome.Readiness.ReasonCodes.SingleOrDefault() != "OBSERVED_EVIDENCE_UNVERIFIED")
            throw new Exception("Wrong source lineage accepted");
    }

    private static Task DiscoveryOutputContract()
    {
        const string heading = "evidence-json<<SQL_DISCOVERY_V2_EOF\n{}\nSQL_DISCOVERY_V2_EOF\n";
        if (LegacyProductionRuntimeAcquisition.ReadDiscoveryOutput(heading,
            "evidence-json", "SQL_DISCOVERY_V2_EOF") != "{}")
            throw new Exception("Producer output not consumed exactly");
        foreach (var invalid in new[] { "", heading + heading,
            "evidence-json<<SQL_DISCOVERY_V2_EOF\n{}\nOTHER\n" })
        {
            try
            {
                _ = LegacyProductionRuntimeAcquisition.ReadDiscoveryOutput(invalid,
                    "evidence-json", "SQL_DISCOVERY_V2_EOF");
            }
            catch (LegacyContractException) { continue; }
            throw new Exception("Missing or duplicate producer output was accepted");
        }
        return Task.CompletedTask;
    }

    private static Task RemoteIdentity()
    {
        const string name = "infrastructure-services/actions";
        foreach (var valid in new[] { "https://github.com/" + name,
            "https://github.com/" + name + ".git", "git@github.com:" + name,
            "ssh://git@github.com/" + name + ".git" })
            if (!LegacyProductionRuntimeAcquisition.IsExpectedRemote(valid, name))
                throw new Exception("Valid GitHub remote was rejected");
        foreach (var invalid in new[] { "https://evilgithub.com/" + name,
            "https://github.com.evil/" + name, "http://github.com/" + name,
            "https://github.com/other/" + name, "https://github.com/" + name + ".git/extra" })
            if (LegacyProductionRuntimeAcquisition.IsExpectedRemote(invalid, name))
                throw new Exception("Aliased Git remote was accepted: " + invalid);
        return Task.CompletedTask;
    }

    private static Task<(LegacyPackageQualificationAdapter Adapter,
        FakeAcquisition Acquisition)> Setup()
    {
        var git = LegacyArtifactTests.Transport(Forward, Rollback);
        var acquisition = new FakeAcquisition();
        var resolver = new TrustedLegacyRuntimeEvidenceResolver(acquisition, git);
        return Task.FromResult((new LegacyPackageQualificationAdapter(resolver, git,
            new EmptySafety(), new NoSecurity()), acquisition));
    }

    private sealed class FakeAcquisition : ILegacyRuntimeAcquisition
    {
        public int FreshnessChecks { get; private set; }
        public bool FailSecondFreshness { get; set; }
        public bool AlterRegistryHash { get; set; }
        public string? Mutate { get; set; }

        public Task VerifyFreshnessAsync(LegacyResolverRequestV1 request,
            CancellationToken token)
        {
            FreshnessChecks++;
            if (FailSecondFreshness && FreshnessChecks == 2)
                throw new LegacyContractException("SOURCE_FRESHNESS_UNVERIFIED");
            return Task.CompletedTask;
        }

        public Task<LegacyAcquiredRuntimeV1> AcquireAsync(
            LegacyResolverRequestV1 request, CancellationToken token)
        {
            var snapshot = new SchemaSnapshot();
            var hash = SchemaCanonicalizer.Canonicalize(snapshot).Sha256;
            var target = new DatabaseTarget {
                ApplicationId = "app1", Environment = "TEST", DatabaseName = "TestDb",
                Lifecycle = "EXISTING", CertificationStatus = Mutate == "uncertified"
                    ? "BASELINE_REQUIRED" : "CERTIFIED",
                CertifiedSchemaHash = Mutate == "uncertified" ? null : hash
            };
            var registryBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new DatabaseRegistryDocument { RegistryFormatVersion = 1,
                    Targets = [target] }, DatabaseStateJson.Compact));
            var governanceBytes = Encoding.UTF8.GetBytes("governance");
            var onboardingBytes = Encoding.UTF8.GetBytes("onboarding");
            var source = new LegacySourceProvenanceV1("infrastructure-services/workflow", "governance.json",
                new string('b', 40), Hashing.Sha256(governanceBytes));
            var governance = new LegacyGovernanceV1(source,
                new("REGISTRY_GIT_PR", "policy.json"),
                new("endpoint1", "TestDb", "ALLOW_LIST", ["SQL1"]),
                "app1", "TEST", "target1", "EXISTING", "LEGACY_UNMANAGED");
            var onboarding = new LegacyOnboardingV1(source with {
                SourcePath = "onboarding.json", SourceSha256 = Hashing.Sha256(onboardingBytes)
            }, new("ONBOARDING_GIT_PR", "policy.json"), "MANAGED");
            var provenance = new RegistryProvenance {
                RegistryRepository = "infrastructure-services/workflow", RegistryRef = new string('b', 40),
                RegistryCommitSha = new string('b', 40), RegistryFilePath = "targets.json",
                RegistryFileSha256 = Hashing.Sha256(registryBytes)
            };
            var capture = new SchemaCaptureSourceResult {
                Snapshot = snapshot, ServerInstance = "SQL1", DatabaseName = "TestDb",
                ServerVersion = "16", ServerMajorVersion = 16,
                MetricsAvailability = MetricsAvailability.Complete
            };
            var now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
            var governanceRepo = new LegacyRepositoryV1("github.com", "22",
                "infrastructure-services/workflow");
            var actionsRepo = new LegacyRepositoryV1("github.com", "23",
                "infrastructure-services/actions");
            var governanceRevision = new string('b', 40);
            var actionsRevision = new string('f', 40);
            LegacyGitDocumentV1 GovernanceDoc(string path) => new(governanceRepo,
                governanceRevision, path, new string('1', 64));
            LegacyGitDocumentV1 ActionsDoc(string path) => new(actionsRepo,
                actionsRevision, path, new string('2', 64));
            var resolverDoc = ActionsDoc(
                "tools/DatabaseReleaseQualification/TrustedLegacyRuntimeEvidenceResolver.cs");
            var producer = new LegacyProducerRefV1(resolverDoc, "SYNTHETIC_TEST", 1);
            var contract = new StructuralHashContractV1(1,
                StructuralHashContractV1.SupportedProfile, 1, 1, "SHA-256",
                new(ActionsDoc("tools/DatabaseReleaseQualification/CanonicalSchema.cs"),
                    "SCHEMA_CANONICALIZER_V1", 1),
                new(ActionsDoc("tools/DatabaseReleaseQualification/LegacyStructuralEvidence.cs"),
                    "REGISTRY_V1_COMPATIBILITY_V1", 1));
            var observed = LegacyStructuralEvidence.FromCurrentCaptures(
                "target1", "endpoint1", capture, capture, contract, now);
            var classification = JsonDocument.Parse("{\"status\":\"CLASSIFIED\",\"classificationInvoked\":true,\"targetId\":\"target1\",\"classification\":{\"classificationResult\":{\"inferences\":{\"scenario\":\"EXISTING_LEGACY\"}}}}").RootElement.Clone();
            var repoBytes = Encoding.UTF8.GetBytes("repository discovery");
            var sqlBytes = Encoding.UTF8.GetBytes("sql discovery");
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal) {
                ["governance"] = Hashing.Sha256(governanceBytes),
                ["onboarding"] = Hashing.Sha256(onboardingBytes),
                ["registry"] = AlterRegistryHash ? new string('0', 64) : Hashing.Sha256(registryBytes),
                ["repositoryDiscovery"] = Hashing.Sha256(repoBytes),
                ["sqlDiscovery"] = Hashing.Sha256(sqlBytes),
                ["observedSnapshot"] = LegacyRuntimeEvidenceHash.Hash(observed)
            };
            var producerPaths = new Dictionary<string, LegacyGitDocumentV1> {
                ["governance"] = GovernanceDoc("database-registry/resolve-governance-runtime.mjs"),
                ["onboarding"] = GovernanceDoc("database-registry/resolve-governance-runtime.mjs"),
                ["registry"] = resolverDoc,
                ["repositoryDiscovery"] = ActionsDoc("scripts/run-repository-discovery-v2-public.mjs"),
                ["sqlDiscovery"] = ActionsDoc("scripts/run-sql-discovery-v2-public.mjs"),
                ["observedSnapshot"] = ActionsDoc("tools/DatabaseReleaseQualification/SqlServerSchemaReader.cs")
            };
            var sources = hashes.Select(pair => new LegacyRuntimeSourceEntryV1(
                pair.Key, "target1", pair.Key is "governance" or "onboarding" or "registry"
                    ? "GOVERNED_GIT" : pair.Key is "artifact" or "repositoryDiscovery"
                    ? "APPLICATION_GIT" : "CURRENT_SQL_OBSERVATION",
                [], new(producerPaths[pair.Key], "SYNTHETIC_TEST", 1), pair.Value)).ToArray();
            if (Mutate is "producerPath" or "producerRevision" or "producerRepository")
            {
                var sourceEntry = sources.Single(x => x.Key == "sqlDiscovery");
                var sourceDoc = sourceEntry.Producer.Source;
                sourceDoc = Mutate switch {
                    "producerPath" => sourceDoc with { Path = "scripts/other.mjs" },
                    "producerRevision" => sourceDoc with { Revision = new string('e', 40) },
                    _ => sourceDoc with { Repository = governanceRepo }
                };
                sources = sources.Select(x => x.Key == "sqlDiscovery"
                    ? x with { Producer = x.Producer with { Source = sourceDoc } } : x).ToArray();
            }
            if (Mutate == "missing") sources = sources.Skip(1).ToArray();
            if (Mutate == "duplicate") sources = [.. sources, sources[0]];
            var runtime = new LegacyRuntimeContextV1(Selection.ExpectedRepository,
                ".github/workflows/legacy-package-qualification-v1-test.yml",
                governanceRevision, "1", "1", "job");
            if (Mutate == "runtimeRepository") runtime = runtime with {
                Repository = governanceRepo
            };
            return Task.FromResult(new LegacyAcquiredRuntimeV1(runtime, producer, sources,
                Selection.ExpectedRepository, Selection.ExpectedCommit, actionsRevision,
                governance, governanceBytes, onboarding, onboardingBytes,
                provenance, registryBytes, capture, capture, now, classification,
                repoBytes, sqlBytes, contract,
                ActionsDoc("tools/DatabaseReleaseQualification/LegacyArtifactDiscovery.cs")));
        }
    }

    private sealed class EmptySafety : ILegacyScopeSafetySource
    {
        public Task<LegacyScopeSafetySnapshotV1> CaptureAsync(
            IReadOnlyList<RecoverySecuritySecurable> scope, CancellationToken token)
        {
            if (scope.Count != 0) throw new Exception("Unexpected scope");
            var result = new LegacyScopeSafetySnapshotV1(1, true, "SQL1", "TestDb",
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

    private sealed class NoSecurity : IRecoverySecurityCatalogReader
    {
        public Task<RecoverySecuritySnapshot> ReadAsync(RecoverySecurityScope scope,
            RecoveryPhase phase, CancellationToken cancellationToken = default) =>
            throw new Exception("Unexpected SQL security access");
    }
}
