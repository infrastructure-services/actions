using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

// The request contains selectors only. This root acquires current evidence from
// checkouts and the SQL connection provisioned by the governed runner.
public static class LegacyProductionCompositionRoot
{
    public static LegacyPackageQualificationAdapter Create()
    {
        var session = new LegacyProductionRuntimeAcquisition();
        var git = new ProcessLegacyGitTransport(session.ApplicationRoot);
        var resolver = new TrustedLegacyRuntimeEvidenceResolver(session, git);
        return new(resolver, git, new BoundSafety(session), new BoundSecurity(session));
    }

    private sealed class BoundSafety(LegacyProductionRuntimeAcquisition session)
        : ILegacyScopeSafetySource
    {
        public Task<LegacyScopeSafetySnapshotV1> CaptureAsync(
            IReadOnlyList<RecoverySecuritySecurable> scope, CancellationToken token) =>
            new SqlLegacyScopeSafetySource(session.SecurityTransport(), session.Binding())
                .CaptureAsync(scope, token);
    }

    private sealed class BoundSecurity(LegacyProductionRuntimeAcquisition session)
        : IRecoverySecurityCatalogReader
    {
        public Task<RecoverySecuritySnapshot> ReadAsync(RecoverySecurityScope scope,
            RecoveryPhase phase, CancellationToken cancellationToken = default) =>
            new SqlRecoverySecurityCatalogReader(session.SecurityTransport(),
                session.Binding()).ReadAsync(scope, phase, cancellationToken);
    }
}

public sealed class LegacyProductionRuntimeAcquisition : ILegacyRuntimeAcquisition
{
    private static readonly Regex Sha = new(@"\A[0-9a-f]{40}\z");
    private readonly string workspace;
    private readonly string governanceRoot;
    private readonly string actionsRoot;
    private readonly string connection;
    private readonly string governanceRepositoryId;
    private readonly string actionsRepositoryId;
    private readonly string workflowPath;
    private readonly string executedWorkflowRevision;
    private readonly string endpointReference;
    private SecurityTargetBindingV1? binding;
    private string? governanceRevision;
    private string? actionsRevision;

    public string ApplicationRoot { get; }

    public LegacyProductionRuntimeAcquisition()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
        workspace = Path.GetFullPath(Required("GITHUB_WORKSPACE"));
        ApplicationRoot = ChildRoot(Required("LEGACY_APPLICATION_ROOT"));
        governanceRoot = ChildRoot(Required("LEGACY_GOVERNANCE_ROOT"));
        actionsRoot = ChildRoot(Required("LEGACY_ACTIONS_ROOT"));
        connection = Required("LEGACY_INSPECTION_CONNECTION_STRING");
        governanceRepositoryId = Required("LEGACY_GOVERNANCE_REPOSITORY_ID");
        actionsRepositoryId = Required("LEGACY_ACTIONS_REPOSITORY_ID");
        workflowPath = Required("LEGACY_WORKFLOW_PATH");
        LegacyPortablePath.Validate(workflowPath);
        executedWorkflowRevision = Required("LEGACY_JOB_WORKFLOW_SHA");
        if (!Sha.IsMatch(executedWorkflowRevision)
            || Required("LEGACY_JOB_WORKFLOW_REPOSITORY")
                != "infrastructure-services/workflow"
            || Required("LEGACY_JOB_WORKFLOW_FILE_PATH") != workflowPath)
            throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
        endpointReference = Required("LEGACY_CONNECTION_ENDPOINT_REFERENCE");
    }

    public SecurityTargetBindingV1 Binding() => binding
        ?? throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");

    public ISecurityCatalogTransport SecurityTransport() =>
        new SqlClientSecurityCatalogTransport(() => connection, Binding());

    public async Task<LegacyAcquiredRuntimeV1> AcquireAsync(
        LegacyResolverRequestV1 request, CancellationToken token)
    {
        if (request.ContractVersion != 1 || request.ArtifactSelection.ContractVersion != 1)
            throw new LegacyContractException("TECHNICAL_ERROR");
        var appRepository = new LegacyRepositoryV1("github.com",
            Required("GITHUB_REPOSITORY_ID"), Required("GITHUB_REPOSITORY"));
        appRepository.Validate();
        if (appRepository != request.ArtifactSelection.ExpectedRepository
            || Required("GITHUB_SHA") != request.ArtifactSelection.ExpectedCommit)
            throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
        var appGit = new ProcessLegacyGitTransport(ApplicationRoot);
        var governanceGit = new ProcessLegacyGitTransport(governanceRoot);
        var actionsGit = new ProcessLegacyGitTransport(actionsRoot);
        var appCommit = await GitText(appGit, ["rev-parse", "HEAD"], token);
        governanceRevision = await GitText(governanceGit, ["rev-parse", "HEAD"], token);
        actionsRevision = await GitText(actionsGit, ["rev-parse", "HEAD"], token);
        if (appCommit != request.ArtifactSelection.ExpectedCommit
            || governanceRevision != executedWorkflowRevision
            || !Sha.IsMatch(governanceRevision) || !Sha.IsMatch(actionsRevision))
            throw new LegacyContractException("SOURCE_REVISION_NOT_IMMUTABLE");
        await RequireRemote(appGit, appRepository.FullName, token);
        await RequireRemote(governanceGit, "infrastructure-services/workflow", token);
        await RequireRemote(actionsGit, "infrastructure-services/actions", token);
        await RequireClean(appGit, token);
        await RequireClean(governanceGit, token);
        await RequireClean(actionsGit, token);
        await VerifyFreshnessAsync(request, token);
        var governanceRepo = new LegacyRepositoryV1("github.com",
            governanceRepositoryId, "infrastructure-services/workflow");
        var actionsRepo = new LegacyRepositoryV1("github.com",
            actionsRepositoryId, "infrastructure-services/actions");
        governanceRepo.Validate();
        actionsRepo.Validate();
        var workflow = await Document(governanceGit, governanceRoot,
            governanceRepo, governanceRevision, workflowPath, true, token);
        var hg5 = await Document(governanceGit, governanceRoot, governanceRepo,
            governanceRevision, "database-registry/resolve-governance-runtime.mjs", true, token);
        var hg6 = await Document(governanceGit, governanceRoot, governanceRepo,
            governanceRevision, "database-registry/compose-hg6b-b1-runtime.mjs", true, token);
        var governance = await Document(governanceGit, governanceRoot, governanceRepo,
            governanceRevision, "database-registry/governance-targets.json", true, token);
        var onboarding = await Document(governanceGit, governanceRoot, governanceRepo,
            governanceRevision, "database-registry/onboarding-targets.json", true, token);
        var registry = await Document(governanceGit, governanceRoot, governanceRepo,
            governanceRevision, "database-registry/targets.json", true, token);
        var classify = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "scripts/compose-governed-classification-v2.mjs", true, token);
        var _ = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "scripts/adapt-classification-evidence-v2.mjs", true, token);
        _ = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "scripts/compose-classification-evidence-v2.mjs", true, token);
        _ = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "scripts/classify-scenario-v2.sh", true, token);
        foreach (var dependency in new[] {
            "scripts/empty-for-new-ef-v1.mjs",
            "scripts/run-repository-discovery-v2-public.mjs",
            "scripts/discover-repository-v2.mjs",
            "scripts/run-sql-discovery-v2-public.mjs",
            "tools/SqlDiscovery/SqlDiscovery.csproj",
            "tools/SqlDiscovery/Program.cs",
            "tools/DatabaseReleaseQualification/DatabaseReleaseQualification.csproj",
            "tools/DatabaseReleaseQualification/LegacyProductionCompositionRoot.cs",
            "tools/DatabaseReleaseQualification/LegacyPackageCli.cs",
            "tools/DatabaseReleaseQualification/LegacyPackageQualification.cs"
        })
            _ = await Document(actionsGit, actionsRoot, actionsRepo,
                actionsRevision, dependency, true, token);
        var resolverDocument = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "tools/DatabaseReleaseQualification/TrustedLegacyRuntimeEvidenceResolver.cs",
            true, token);
        var canonicalizer = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "tools/DatabaseReleaseQualification/CanonicalSchema.cs", true, token);
        var compatibility = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "tools/DatabaseReleaseQualification/LegacyStructuralEvidence.cs", true, token);
        _ = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "repository-discovery-v2/action.yml", true, token);
        _ = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "sql-discovery-v2/action.yml", true, token);
        var repoProducer = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "scripts/run-repository-discovery-v2-public.mjs", true, token);
        var sqlProducer = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "scripts/run-sql-discovery-v2-public.mjs", true, token);
        var observedProducer = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "tools/DatabaseReleaseQualification/SqlServerSchemaReader.cs", true, token);
        var artifactProducer = await Document(actionsGit, actionsRoot, actionsRepo,
            actionsRevision, "tools/DatabaseReleaseQualification/LegacyArtifactDiscovery.cs", true, token);
        var hg5Request = new {
            runtimeContractVersion = 1,
            target = new { targetId = request.TargetId },
            provenance = new {
                sourceRepository = governanceRepo.FullName,
                requestedRevision = governanceRevision,
                sourceRevision = governanceRevision,
                governance = new { sourcePath = governance.Document.Path,
                    sourceSha256 = governance.Document.RawSha256 },
                onboarding = new { sourcePath = onboarding.Document.Path,
                    sourceSha256 = onboarding.Document.RawSha256 }
            }
        };
        var authority = await RunNode(governanceRoot,
            Path.Combine(governanceRoot, hg5.Document.Path.Replace('/', Path.DirectorySeparatorChar)),
            JsonSerializer.Serialize(hg5Request), token);
        if (authority.GetProperty("status").GetString() != "PRESENT")
            throw new LegacyContractException(authority.TryGetProperty("reason", out var reason)
                ? reason.GetString() ?? "GOVERNANCE_INVALID" : "GOVERNANCE_INVALID");
        var selected = authority.GetProperty("registryGovernance");
        var selectedOnboarding = authority.GetProperty("onboarding");
        if (selected.GetProperty("status").GetString() != "PRESENT"
            || selectedOnboarding.GetProperty("status").GetString() != "PRESENT")
            throw new LegacyContractException("GOVERNANCE_INVALID");
        var governed = ParseGovernance(selected);
        var onboarded = ParseOnboarding(selectedOnboarding);
        if (governed.Binding.EndpointReference != endpointReference
            || governed.TargetId != request.TargetId || onboarded.Status != "MANAGED")
            throw new LegacyContractException("EVIDENCE_CORRELATION_MISMATCH");
        binding = new(governed.Binding.DatabaseName,
            governed.Binding.ServerMatchPolicy, governed.Binding.AllowedServerInstances);
        var repositoryOutput = await RunDiscoveryProducer("repository-discovery-v2",
            "scripts/run-repository-discovery-v2-public.mjs", new Dictionary<string, string> {
                ["INSPECTION_STATUS"] = "READY", ["WORKSPACE"] = ApplicationRoot
            }, token);
        var sqlOutput = await RunDiscoveryProducer("sql-discovery-v2",
            "scripts/run-sql-discovery-v2-public.mjs", new Dictionary<string, string> {
                ["SQL_SERVER_CONNECTION"] = connection,
                ["SQL_DATABASE_NAME"] = governed.Binding.DatabaseName
            }, token);
        var repositoryBytes = Encoding.UTF8.GetBytes(ReadDiscoveryOutput(repositoryOutput,
            "evidence-json", "REPOSITORY_DISCOVERY_V2_EOF"));
        var sqlBytes = Encoding.UTF8.GetBytes(ReadDiscoveryOutput(sqlOutput,
            "evidence-json", "SQL_DISCOVERY_V2_EOF"));
        var sqlIdentityBytes = Encoding.UTF8.GetBytes(ReadDiscoveryOutput(sqlOutput,
            "observed-database-identity-json", "SQL_DISCOVERY_V2_EOF"));
        var repositoryEnvelopeBytes = Encoding.UTF8.GetBytes(repositoryOutput);
        var sqlEnvelopeBytes = Encoding.UTF8.GetBytes(sqlOutput);
        var registryProvenance = new RegistryProvenance {
            RegistryRepository = governanceRepo.FullName,
            RegistryRef = governanceRevision,
            RegistryCommitSha = governanceRevision,
            RegistryFilePath = registry.Document.Path,
            RegistryFileSha256 = registry.Document.RawSha256
        };
        var targetConnection = SqlClientSecurityCatalogTransport.BuildConnectionOptions(
            connection, binding).ConnectionString;
        var first = await new SqlServerSchemaReader().CaptureWithMetadataAsync(
            targetConnection, token);
        var second = await new SqlServerSchemaReader().CaptureWithMetadataAsync(
            targetConnection, token);
        var capturedAt = DateTimeOffset.UtcNow;
        if (first.DatabaseName != governed.Binding.DatabaseName
            || second.DatabaseName != governed.Binding.DatabaseName
            || governed.Binding.ServerMatchPolicy == "ALLOW_LIST"
                && (!governed.Binding.AllowedServerInstances.Contains(first.ServerInstance,
                        StringComparer.OrdinalIgnoreCase)
                    || !governed.Binding.AllowedServerInstances.Contains(second.ServerInstance,
                        StringComparer.OrdinalIgnoreCase)))
            throw new LegacyContractException("TARGET_IDENTITY_MISMATCH");
        var contract = new StructuralHashContractV1(1,
            StructuralHashContractV1.SupportedProfile, 1, 1, "SHA-256",
            new(canonicalizer.Document, "SCHEMA_CANONICALIZER_V1", 1),
            new(compatibility.Document, "REGISTRY_V1_COMPATIBILITY_V1", 1));
        var observed = LegacyStructuralEvidence.FromCurrentCaptures(request.TargetId,
            governed.Binding.EndpointReference, first, second, contract, capturedAt);
        DatabaseRegistryDocument? registryDocument;
        try { registryDocument = JsonSerializer.Deserialize<DatabaseRegistryDocument>(
            registry.Bytes, DatabaseStateJson.Compact); }
        catch (JsonException) { throw new LegacyContractException("REGISTRY_SOURCE_INVALID"); }
        var registryValidation = DatabaseRegistryLoader.Validate(registryDocument,
            registryProvenance);
        if (!registryValidation.IsValid)
            throw new LegacyContractException("REGISTRY_VALIDATION_FAILED");
        var preClassificationState = new DatabaseStateEvaluator().Evaluate(registryValidation,
            new DatabaseStateObservation {
                ApplicationId = governed.ApplicationId, Environment = governed.Environment,
                DatabaseName = governed.Binding.DatabaseName,
                ObservedSchemaHash = observed.ObservedSchemaHash,
                SchemaCoverage = observed.Metadata.SchemaCoverage,
                UnsupportedSchemaFeatures = observed.Snapshot.UnsupportedSchemaFeatures,
                CaptureTimestampUtc = observed.CapturedAtUtc,
                RunId = Required("GITHUB_RUN_ID"),
                RunAttempt = Required("GITHUB_RUN_ATTEMPT")
            });
        var repositoryResult = ParseJson(repositoryBytes);
        var sqlResult = ParseJson(sqlBytes);
        var sqlIdentity = ParseJson(sqlIdentityBytes);
        if (sqlIdentity.GetProperty("serverInstance").GetString() != first.ServerInstance
            || sqlIdentity.GetProperty("databaseName").GetString() != first.DatabaseName)
            throw new LegacyContractException("TARGET_IDENTITY_MISMATCH");
        var observedIdentity = new { serverInstance = first.ServerInstance,
            databaseName = first.DatabaseName };
        var composedInput = new {
            environment = "TEST",
            authority,
            stepOutcomes = new { repository = "success", sql = "success", schema = "success" },
            sql = sqlResult,
            sqlIdentity = observedIdentity,
            repository = repositoryResult,
            schema = new {
                status = "SUCCESS", reason = "SCHEMA_CAPTURE_SUCCESS",
                deterministic = observed.CaptureComparison.StructuralMatch ? "true" : "false",
                driftStatus = preClassificationState.DriftStatus,
                registryStatus = preClassificationState.RegistryStatus
            },
            schemaIdentity = observedIdentity,
            provenance = new {
                sql = SourceProvenance(sqlProducer.Document),
                repository = SourceProvenance(repoProducer.Document),
                schema = SourceProvenance(observedProducer.Document)
            }
        };
        var composed = await RunNode(governanceRoot,
            Path.Combine(governanceRoot, hg6.Document.Path.Replace('/', Path.DirectorySeparatorChar)),
            JsonSerializer.Serialize(composedInput), token);
        if (!composed.GetProperty("invokeClassification").GetBoolean())
            throw new LegacyContractException("GOVERNANCE_INVALID");
        var classification = await RunNode(actionsRoot,
            Path.Combine(actionsRoot, classify.Document.Path.Replace('/', Path.DirectorySeparatorChar)),
            composed.GetProperty("evidence").GetRawText(), token);
        var runtime = new LegacyRuntimeContextV1(appRepository, workflow.Document.Path,
            governanceRevision, Required("GITHUB_RUN_ID"),
            Required("GITHUB_RUN_ATTEMPT"), Required("LEGACY_JOB_ID"));
        var sourceEntries = new[] {
            Entry("governance", "GOVERNED_GIT", [governance.Document], hg5.Document,
                Hashing.Sha256(governance.Bytes), request.TargetId),
            Entry("onboarding", "GOVERNED_GIT", [onboarding.Document], hg5.Document,
                Hashing.Sha256(onboarding.Bytes), request.TargetId),
            Entry("registry", "GOVERNED_GIT", [registry.Document], resolverDocument.Document,
                Hashing.Sha256(registry.Bytes), request.TargetId),
            Entry("repositoryDiscovery", "APPLICATION_GIT", [], repoProducer.Document,
                Hashing.Sha256(repositoryEnvelopeBytes), request.TargetId),
            Entry("sqlDiscovery", "CURRENT_SQL_OBSERVATION", [governance.Document], sqlProducer.Document,
                Hashing.Sha256(sqlEnvelopeBytes), request.TargetId),
            Entry("observedSnapshot", "CURRENT_SQL_OBSERVATION", [governance.Document],
                observedProducer.Document, LegacyRuntimeEvidenceHash.Hash(observed), request.TargetId)
        };
        return new(runtime, new(resolverDocument.Document,
                "TRUSTED_LEGACY_RUNTIME_EVIDENCE_V1", 1), sourceEntries,
            appRepository, appCommit, actionsRevision, governed, governance.Bytes,
            onboarded, onboarding.Bytes, registryProvenance, registry.Bytes,
            first, second, capturedAt, classification,
            repositoryEnvelopeBytes, sqlEnvelopeBytes, contract, artifactProducer.Document);
    }

    public async Task VerifyFreshnessAsync(LegacyResolverRequestV1 request,
        CancellationToken token)
    {
        if (governanceRevision is null || actionsRevision is null)
            throw new LegacyContractException("SOURCE_FRESHNESS_UNVERIFIED");
        var governanceGit = new ProcessLegacyGitTransport(governanceRoot);
        var actionsGit = new ProcessLegacyGitTransport(actionsRoot);
        if (await RemoteMain(governanceGit, token) != governanceRevision
            || await RemoteMain(actionsGit, token) != actionsRevision
            || await GitText(actionsGit, ["rev-parse", "HEAD"], token) != actionsRevision
            || await GitText(new ProcessLegacyGitTransport(ApplicationRoot),
                ["rev-parse", "HEAD"], token)
                != request.ArtifactSelection.ExpectedCommit)
            throw new LegacyContractException("SOURCE_FRESHNESS_UNVERIFIED");
        await RequireClean(governanceGit, token);
        await RequireClean(actionsGit, token);
        await RequireClean(new ProcessLegacyGitTransport(ApplicationRoot), token);
    }

    private string ChildRoot(string candidate)
    {
        var path = Path.GetFullPath(candidate);
        if (!path.StartsWith(workspace + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && path != workspace)
            throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
        return path;
    }

    private static string Required(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value
            : throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");

    private static async Task<string> GitText(ILegacyGitTransport git,
        string[] args, CancellationToken token)
    {
        var result = await git.RunAsync(args, 4096, token);
        if (result.ExitCode != 0)
            throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
        return Encoding.UTF8.GetString(result.Output).Trim();
    }

    private static async Task RequireRemote(ILegacyGitTransport git,
        string fullName, CancellationToken token)
    {
        var remote = await GitText(git, ["remote", "get-url", "origin"], token);
        if (!IsExpectedRemote(remote, fullName))
            throw new LegacyContractException("SOURCE_REPOSITORY_MISMATCH");
    }

    internal static bool IsExpectedRemote(string remote, string fullName) =>
        new[] { "https://github.com/", "git@github.com:", "ssh://git@github.com/" }
            .Any(prefix => remote == prefix + fullName
                || remote == prefix + fullName + ".git");

    private static async Task RequireClean(ILegacyGitTransport git, CancellationToken token)
    {
        if ((await GitText(git, ["status", "--porcelain=v1", "--untracked-files=all"],
                token)).Length != 0)
            throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
    }

    private static async Task<string> RemoteMain(ILegacyGitTransport git,
        CancellationToken token)
    {
        var text = await GitText(git, ["ls-remote", "origin", "refs/heads/main"], token);
        var parts = text.Split('\t');
        if (parts.Length != 2 || parts[1] != "refs/heads/main" || !Sha.IsMatch(parts[0]))
            throw new LegacyContractException("SOURCE_FRESHNESS_UNVERIFIED");
        return parts[0];
    }

    private static async Task<(LegacyGitDocumentV1 Document, byte[] Bytes)> Document(
        ILegacyGitTransport git, string root, LegacyRepositoryV1 repository,
        string revision, string path, bool compareWorktree, CancellationToken token)
    {
        LegacyPortablePath.Validate(path);
        var entry = await git.RunAsync(["ls-tree", "-z", revision, "--", path], 4096, token);
        if (entry.ExitCode != 0 || entry.Output.Length == 0
            || entry.Output[^1] != 0)
            throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
        var line = Encoding.UTF8.GetString(entry.Output, 0, entry.Output.Length - 1);
        var tab = line.IndexOf('\t');
        if (tab < 0 || line[(tab + 1)..] != path
            || !Regex.IsMatch(line[..tab], @"\A100(?:644|755) blob (?:[0-9a-f]{40}|[0-9a-f]{64})\z"))
            throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
        var oid = line[..tab].Split(' ')[^1];
        var blob = await git.RunAsync(["cat-file", "blob", oid], 8 * 1024 * 1024, token);
        if (blob.ExitCode != 0) throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
        var bytes = blob.Output;
        if (compareWorktree)
        {
            var worktree = Path.GetFullPath(Path.Combine(root,
                path.Replace('/', Path.DirectorySeparatorChar)));
            if (!worktree.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(worktree)
                || !File.ReadAllBytes(worktree).SequenceEqual(bytes))
                throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
        }
        return (new(repository, revision, path, Hashing.Sha256(bytes)), bytes);
    }

    private static JsonElement ParseJson(byte[] bytes)
    {
        try { return JsonDocument.Parse(bytes).RootElement.Clone(); }
        catch { throw new LegacyContractException("OBSERVED_EVIDENCE_UNVERIFIED"); }
    }

    private async Task<string> RunDiscoveryProducer(string action, string script,
        IReadOnlyDictionary<string, string> variables, CancellationToken token)
    {
        var outputPath = Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo("node") {
                WorkingDirectory = actionsRoot, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(Path.Combine(actionsRoot,
                script.Replace('/', Path.DirectorySeparatorChar)));
            ClearChildSecrets(start);
            start.Environment["GITHUB_ACTION_PATH"] = Path.Combine(actionsRoot, action);
            start.Environment["GITHUB_WORKSPACE"] = workspace;
            start.Environment["GITHUB_OUTPUT"] = outputPath;
            start.Environment["RUNNER_TEMP"] = Required("RUNNER_TEMP");
            start.Environment["ENVIRONMENT_NAME"] = "TEST";
            foreach (var pair in variables) start.Environment[pair.Key] = pair.Value;
            using var process = new Process { StartInfo = start };
            var started = false;
            try
            {
                started = process.Start();
                if (!started) throw new LegacyContractException("TECHNICAL_ERROR");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                await Task.WhenAll(stdout, stderr);
                await process.WaitForExitAsync(deadline.Token);
                if (process.ExitCode != 0 || new FileInfo(outputPath).Length > 4 * 1024 * 1024)
                    throw new LegacyContractException("OBSERVED_EVIDENCE_UNVERIFIED");
                return new UTF8Encoding(false, true).GetString(
                    await File.ReadAllBytesAsync(outputPath, deadline.Token));
            }
            catch (OperationCanceledException) { throw; }
            catch (LegacyContractException) { throw; }
            catch { throw new LegacyContractException("TECHNICAL_ERROR"); }
            finally
            {
                if (started && !process.HasExited)
                    try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
        finally { File.Delete(outputPath); }
    }

    internal static string ReadDiscoveryOutput(string output, string name, string marker)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var heading = name + "<<" + marker;
        var values = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] != heading) continue;
            if (i + 2 >= lines.Length || lines[i + 2] != marker)
                throw new LegacyContractException("OBSERVED_EVIDENCE_UNVERIFIED");
            values.Add(lines[i + 1]);
        }
        if (values.Count != 1 || values[0].Length == 0)
            throw new LegacyContractException("OBSERVED_EVIDENCE_UNVERIFIED");
        return values[0];
    }

    private static void ClearChildSecrets(ProcessStartInfo start)
    {
        start.Environment.Remove("GOVERNANCE_TOKEN");
        start.Environment.Remove("LEGACY_INSPECTION_CONNECTION_STRING");
        start.Environment.Remove("SQL_SERVER_CONNECTION");
    }

    private static async Task<JsonElement> RunNode(string root, string script,
        string input, CancellationToken token)
    {
        var start = new ProcessStartInfo("node") {
            WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(script);
        ClearChildSecrets(start);
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new LegacyContractException("TECHNICAL_ERROR");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(120));
            await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(deadline.Token);
            _ = await process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode is not (0 or 65 or 66 or 70)
                || output.Length > 4 * 1024 * 1024)
                throw new LegacyContractException("TECHNICAL_ERROR");
            return ParseJson(Encoding.UTF8.GetBytes(output));
        }
        catch (OperationCanceledException) { throw; }
        catch (LegacyContractException) { throw; }
        catch { throw new LegacyContractException("TECHNICAL_ERROR"); }
        finally
        {
            if (started && !process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static LegacyGovernanceV1 ParseGovernance(JsonElement selected)
    {
        var value = selected.GetProperty("governance");
        var provenance = selected.GetProperty("sourceProvenance");
        var binding = value.GetProperty("binding");
        var authority = value.GetProperty("authorityReference");
        return new(ParseProvenance(provenance), new(authority.GetProperty("kind").GetString()!,
                authority.GetProperty("policyPath").GetString()!),
            new(binding.GetProperty("endpointReference").GetString()!,
                binding.GetProperty("databaseName").GetString()!,
                binding.GetProperty("serverMatchPolicy").GetString()!,
                binding.GetProperty("allowedServerInstances").EnumerateArray()
                    .Select(x => x.GetString()!).ToArray()),
            value.GetProperty("applicationId").GetString()!,
            value.GetProperty("environment").GetString()!,
            selected.GetProperty("targetId").GetString()!,
            value.GetProperty("databaseLifecycle").GetString()!,
            value.GetProperty("changeManagementMode").GetString()!);
    }

    private static LegacyOnboardingV1 ParseOnboarding(JsonElement selected)
    {
        var authority = selected.GetProperty("authorityReference");
        return new(ParseProvenance(selected.GetProperty("sourceProvenance")),
            new(authority.GetProperty("kind").GetString()!,
                authority.GetProperty("policyPath").GetString()!),
            selected.GetProperty("onboardingState").GetString()!);
    }

    private static LegacySourceProvenanceV1 ParseProvenance(JsonElement value) =>
        new(value.GetProperty("sourceRepository").GetString()!,
            value.GetProperty("sourcePath").GetString()!,
            value.GetProperty("sourceRevision").GetString()!,
            value.GetProperty("sourceSha256").GetString()!);

    private static object SourceProvenance(LegacyGitDocumentV1 document) => new {
        sourceRepository = document.Repository.FullName,
        sourcePath = document.Path,
        sourceRevision = document.Revision,
        sourceSha256 = document.RawSha256
    };

    private static LegacyRuntimeSourceEntryV1 Entry(string key, string authority,
        IReadOnlyList<LegacyGitDocumentV1> inputs, LegacyGitDocumentV1 producer,
        string resultSha, string targetId) => new(key, targetId, authority,
        inputs, new(producer, "LEGACY_RUNTIME_SOURCE_V1", 1), resultSha);
}
