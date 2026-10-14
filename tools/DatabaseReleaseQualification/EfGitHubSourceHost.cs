using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DatabaseReleaseQualification;

// Corporate repository authority is fixed in host code, never supplied by a
// request/manifest. This adapter authenticates Git sources only. Existing V2
// artifacts are NOT promoted into authenticated A2.1 observations by this class.
public sealed class EfGitHubSourceHostV1 : IEfOperationalSessionFactoryV1
{
    private const string GovernanceRepository = "infrastructure-services/workflow";
    private const string ActionsRepository = "infrastructure-services/actions";
    private readonly Func<string, string?> environment;
    private readonly Func<HttpMessageHandler> handler;
    public EfGitHubSourceHostV1() : this(Environment.GetEnvironmentVariable,
        () => new HttpClientHandler { AllowAutoRedirect = false }) { }
    // Typed injection is for embedding/tests, never a CLI or environment plugin.
    public EfGitHubSourceHostV1(Func<string, string?> environment, Func<HttpMessageHandler> handler)
    { this.environment = environment; this.handler = handler; }

    public async Task<EfOperationalSessionV1> OpenAsync(EfOperationalRequestV1 request, CancellationToken token)
    {
        var verified = new List<EfVerifiedGitSourceV1>();
        EfOperationalSessionV1 Block(params string[] reasons) => new(null, verified.ToArray(), reasons);
        try
        {
            EfHostContract.Validate(request);
            var scope = request.Selection.Scope;
            // Runner variables are correlation checks, not authentication evidence.
            EfHostContract.Require(environment("GITHUB_ACTIONS") == "true" && environment("GITHUB_API_URL") == "https://api.github.com"
                && environment("GITHUB_REPOSITORY") == scope.SourceRepository && environment("GITHUB_SHA") == scope.SourceRevision,
                "RUN_CONTEXT_UNVERIFIED");
            var credential = environment("EF_GOVERNANCE_READ_TOKEN");
            EfHostContract.Require(!string.IsNullOrWhiteSpace(credential), "GOVERNANCE_CREDENTIAL_REQUIRED");
            using var client = new HttpClient(handler()) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ef-operational-host-v1");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            async Task CheckCurrent()
            {
                foreach (var (repository, revision) in new[] { (GovernanceRepository, request.GovernanceRevision),
                    (ActionsRepository, request.ActionsRevision), (scope.SourceRepository, scope.SourceRevision) })
                {
                    using var doc = await Get(client, $"repos/{repository}/git/ref/heads/main", token);
                    EfHostContract.Require(doc.RootElement.GetProperty("ref").GetString() == "refs/heads/main"
                        && doc.RootElement.GetProperty("object").GetProperty("type").GetString() == "commit"
                        && doc.RootElement.GetProperty("object").GetProperty("sha").GetString() == revision,
                        "INTEGRATED_SOURCE_CHANGED");
                }
            }
            await CheckCurrent();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var name in new[] { "resolve-governance-runtime.mjs", "validate-ef-source.mjs",
                "governance-targets.json", "onboarding-targets.json", "targets.json" })
            {
                var path = "database-registry/" + name;
                using var doc = await Get(client, $"repos/{GovernanceRepository}/contents/{path}?ref={request.GovernanceRevision}", token);
                var item = doc.RootElement;
                EfHostContract.Require(item.GetProperty("type").GetString() == "file" && item.GetProperty("path").GetString() == path
                    && item.GetProperty("encoding").GetString() == "base64" && !item.TryGetProperty("submodule_git_url", out _), "GIT_CONTENT_INVALID");
                var bytes = Convert.FromBase64String(item.GetProperty("content").GetString()!);
                EfHostContract.Require(bytes.Length is > 0 and <= 1024 * 1024
                    && item.GetProperty("size").GetInt32() == bytes.Length, "GIT_CONTENT_SIZE_INVALID");
                var blobBytes = Encoding.UTF8.GetBytes($"blob {bytes.Length}\0").Concat(bytes).ToArray();
                EfHostContract.Require(Convert.ToHexStringLower(SHA1.HashData(blobBytes)) == item.GetProperty("sha").GetString(), "GIT_CONTENT_HASH_MISMATCH");
                if (name.EndsWith(".json", StringComparison.Ordinal))
                { using var parsed = JsonDocument.Parse(bytes); EfHostContract.Unique(parsed.RootElement); }
                files.Add(path, bytes);
                verified.Add(new(GovernanceRepository, request.GovernanceRevision, path, Hashing.Sha256(bytes)));
            }
            var governed = await ResolveGovernance(files, request, token);
            EfHostContract.Require(governed.GetProperty("status").GetString() == "PRESENT", "GOVERNED_TARGET_UNAVAILABLE");
            var selected = governed.GetProperty("registryGovernance");
            var governance = selected.GetProperty("governance");
            var onboarding = governed.GetProperty("onboarding");
            EfHostContract.Require(selected.GetProperty("targetId").GetString() == scope.TargetId
                && governance.GetProperty("applicationId").GetString() == request.ApplicationId
                && governance.GetProperty("environment").GetString() == request.Environment
                && governance.GetProperty("changeManagementMode").GetString() == "EF_MIGRATIONS", "GOVERNED_CONTEXT_MISMATCH");
            var lifecycle = governance.GetProperty("databaseLifecycle").GetString();
            EfHostContract.Require(lifecycle == "EXISTING" && onboarding.GetProperty("onboardingState").GetString() == "MANAGED"
                || lifecycle == "NEW" && onboarding.GetProperty("onboardingState").GetString() == "NOT_REQUIRED", "ONBOARDING_NOT_READY");
            if (governance.TryGetProperty("efSource", out var adoption))
                EfHostContract.Require(adoption.GetProperty("sourceRepository").GetString() == scope.SourceRepository, "ADOPTION_REPOSITORY_MISMATCH");
            await CheckCurrent(); // Detect changes during acquisition, including baseline commits.
            // Git provenance cannot attest SQL observations, generated SQL, an
            // isolated exercise, effective approval or the executing job identity.
            // Registering those independent producers is a required next increment.
            return Block("EXECUTED_HOST_ATTESTATION_REQUIRED", "A2_1_NORMALIZED_PRODUCER_ADAPTER_REQUIRED",
                "CONTENT_PROJECTION_PRODUCER_REQUIRED", "ISOLATED_QUALIFICATION_PRODUCER_REQUIRED");
        }
        catch (OperationCanceledException) { throw; }
        catch (EfHostException e) { return Block(e.Message); }
        catch (HttpRequestException) { return Block("GITHUB_SOURCE_UNAVAILABLE"); }
        catch { return Block("GITHUB_SOURCE_UNVERIFIABLE"); }
    }

    private static async Task<JsonDocument> Get(HttpClient client, string relative, CancellationToken token)
    {
        using var response = await client.GetAsync("https://api.github.com/" + relative, HttpCompletionOption.ResponseHeadersRead, token);
        EfHostContract.Require(response.StatusCode == HttpStatusCode.OK,
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "GITHUB_ACCESS_DENIED"
                : response.StatusCode == HttpStatusCode.NotFound ? "GITHUB_SOURCE_NOT_FOUND" : "GITHUB_SOURCE_UNAVAILABLE");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = await Bounded(stream, 2 * 1024 * 1024, token);
        var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        try { EfHostContract.Unique(doc.RootElement); return doc; }
        catch { doc.Dispose(); throw; }
    }
    private static async Task<byte[]> Bounded(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token); if (count == 0) break;
            EfHostContract.Require(output.Length + count <= limit, "PRODUCER_OUTPUT_TOO_LARGE");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task<JsonElement> ResolveGovernance(Dictionary<string, byte[]> files,
        EfOperationalRequestV1 request, CancellationToken token)
    {
        // Execute the existing pure resolver from authenticated immutable bytes,
        // not a caller path or a mutable checkout. No copy of its policy logic.
        var root = Path.Combine(Path.GetTempPath(), "ef-governance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string[] names = ["resolve-governance-runtime.mjs", "validate-ef-source.mjs", "invoke.mjs"];
        const string driver = """
            import fs from 'node:fs';
            import { resolveGovernanceRuntime } from './resolve-governance-runtime.mjs';
            const i = JSON.parse(fs.readFileSync(0, 'utf8'));
            const reader = {
              repositoryIdentity: () => i.repository,
              resolveRevision: revision => { if (revision !== i.revision) throw new Error(); return i.revision; },
              isWorktreeClean: () => true,
              readFile: (revision, path) => { if (revision !== i.revision || !Object.hasOwn(i.files, path)) throw new Error(); return Buffer.from(i.files[path], 'base64'); }
            };
            const outcome = resolveGovernanceRuntime(i.request, reader);
            process.stdout.write(JSON.stringify(outcome.result));
            """;
        try
        {
            foreach (var name in names.Take(2)) await File.WriteAllBytesAsync(Path.Combine(root, name), files["database-registry/" + name], token);
            await File.WriteAllTextAsync(Path.Combine(root, names[2]), driver, new UTF8Encoding(false), token);
            object Claim(string name) => new { sourcePath = "database-registry/" + name, sourceSha256 = Hashing.Sha256(files["database-registry/" + name]) };
            var input = JsonSerializer.Serialize(new {
                repository = GovernanceRepository, revision = request.GovernanceRevision,
                files = files.Where(x => x.Key.EndsWith(".json", StringComparison.Ordinal)).ToDictionary(x => x.Key, x => Convert.ToBase64String(x.Value)),
                request = new { runtimeContractVersion = 1, target = new { targetId = request.Selection.Scope.TargetId },
                    provenance = new { sourceRepository = GovernanceRepository, requestedRevision = request.GovernanceRevision,
                        sourceRevision = request.GovernanceRevision, governance = Claim("governance-targets.json"), onboarding = Claim("onboarding-targets.json") } }
            });
            var start = new ProcessStartInfo("node") { WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(Path.Combine(root, names[2]));
            // No token, SQL connection, NODE_OPTIONS or application-controlled code
            // is inherited by the pure governance subprocess.
            start.Environment.TryGetValue("PATH", out var path);
            start.Environment.TryGetValue("SystemRoot", out var systemRoot);
            start.Environment.Clear(); start.Environment["PATH"] = path;
            if (systemRoot is not null) start.Environment["SystemRoot"] = systemRoot;
            using var process = new Process { StartInfo = start };
            var started = false;
            try
            {
                started = process.Start(); EfHostContract.Require(started, "GOVERNANCE_RESOLVER_UNAVAILABLE");
                var stdout = Bounded(process.StandardOutput.BaseStream, 2 * 1024 * 1024, token);
                var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
                await process.StandardInput.WriteAsync(input.AsMemory(), token); process.StandardInput.Close();
                var bytes = await stdout; await stderr;
                await process.WaitForExitAsync(token);
                EfHostContract.Require(process.ExitCode == 0, "GOVERNANCE_RESOLVER_FAILED");
                using var result = JsonDocument.Parse(bytes); EfHostContract.Unique(result.RootElement);
                return result.RootElement.Clone();
            }
            finally { if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        finally
        {
            // Only files created above; no recursive cleanup of computed paths.
            foreach (var name in names) File.Delete(Path.Combine(root, name));
            Directory.Delete(root, recursive: false);
        }
    }
}
