using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DatabaseReleaseQualification;

internal static class EfOperationalTests
{
    private static readonly DateTimeOffset Now = EfPreparationTests.Now;
    private const string EngineRevision = "ffffffffffffffffffffffffffffffffffffffff";
    private static void Assert(bool value, string why) { if (!value) throw new Exception(why); }
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Compact);
    private static EfOperationalRequestV1 Request(EfPreparationTests.Fixture f) => new(1, "PLAN",
        f.Governance.Authority.Target.ApplicationId, "TEST", f.Governance.Authority.Governance.Revision, EngineRevision, f.Selection);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Factory(IEfAuthorizedEvidenceProviderV1 provider, bool wait = false) : IEfOperationalSessionFactoryV1
    {
        public async Task<EfOperationalSessionV1> OpenAsync(EfOperationalRequestV1 r, CancellationToken token)
        {
            if (wait) await Task.Delay(Timeout.Infinite, token);
            return r.ActionsRevision == EngineRevision ? new(provider, [], []) : new(null, [], ["HOST_REVISION_MISMATCH"]);
        }
    }
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF host: real production process command fails closed without run authority", ProductionProcess),
        ("EF host: production process rejects caller authentication and fixture injection", ProductionBoundary),
        ("EF host: real CLI with explicit test registration produces PLAN and NO_CHANGES", PositiveProcesses),
        ("EF host: reusable contexts and preprovisioned NEW preserve no write authority", Contexts),
        ("EF host: strict requests, bounded files, redaction and append-only artifacts", InputBoundary),
        ("EF host: untrusted producer, changed code/baseline/history/SQL block", EvidenceChanges),
        ("EF host: continuation authenticates frozen plan, fresh PRE and actual POST", Continuations),
        ("EF host: timeout and cancellation retain distinct exit semantics", Cancellation),
        ("EF host: authenticated Git subset never attests missing observation producers", GitSources),
        ("EF host: Git credential, content, target and revision failures block", GitNegatives)
    ];
    private static async Task<EfOperationalOutcomeV1> Run(EfPreparationTests.Fixture f, EfOperationalRequestV1? r = null)
        => await EfOperationalHostV1.RunAsync(r ?? Request(f), new Factory(f.Provider), Now);

    internal static async Task<int> Child(string[] args)
    {
        // This mode exists in the test executable only, never the shipped CLI.
        var f = await EfPreparationTests.Make();
        var noChanges = args[0] == "NO_CHANGES";
        if (noChanges) f = f.Replace("SOURCE", new EfSourcePreparationV1(f.Original.Request.Source with {
            ManagedMigrationIds = f.Pre.Observation.AppliedMigrationIds }, [], []));
        await File.WriteAllBytesAsync(args[1], Bytes(Request(f)));
        return await EfOperationalCli.RunAsync(["--request", args[1], "--output", args[2], "--timeout-seconds", "1"],
            new Factory(f.Provider, args[0] == "WAIT"), clock: new Clock());
    }
    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ef-host-test-" + Guid.NewGuid().ToString("N"));
        internal Workspace() => Directory.CreateDirectory(Root);
        internal string FilePath(string name) => Path.Combine(Root, name);
        public void Dispose()
        {
            // The unique test-owned root is checked before recursive cleanup.
            Assert(Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(Root).StartsWith("ef-host-test-", StringComparison.Ordinal), "Unsafe fixture cleanup");
            Directory.Delete(Root, true);
        }
    }
    private static async Task<(int Code, string Output)> Process(string dll, string[] args,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(dll); foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GITHUB_ACTIONS"] = "false";
        start.Environment.Remove("EF_GOVERNANCE_READ_TOKEN");
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = System.Diagnostics.Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            Assert((await stderr).Length == 0, "Unsanitized process stderr");
            return (process.ExitCode, await stdout);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    private static async Task ProductionProcess()
    {
        using var w = new Workspace(); var f = await EfPreparationTests.Make();
        await File.WriteAllBytesAsync(w.FilePath("request.json"), Bytes(Request(f)));
        var r = await Process(typeof(EfOperationalCli).Assembly.Location,
            ["prepare-ef-release", "--request", w.FilePath("request.json"), "--output", w.FilePath("result")]);
        Assert(r.Code == 66 && r.Output.Contains("RUN_CONTEXT_UNVERIFIED"), "Production entrypoint missing or permissive");
        Assert(File.Exists(w.FilePath("result/ef-result.json")) && !File.Exists(w.FilePath("result/ef-plan-review.json")), "Blocked artifact contract failed");
    }
    private static async Task ProductionBoundary()
    {
        using var w = new Workspace(); var f = await EfPreparationTests.Make(); var request = Request(f);
        var engine = typeof(EfOperationalCli).Assembly;
        Assert(!engine.GetReferencedAssemblies().Any(x => x.Name == typeof(EfOperationalTests).Assembly.GetName().Name)
            && !engine.GetTypes().Any(x => x.Name == nameof(EfOperationalTests)), "Test provider shipped in production engine");
        // Caller-controlled files, matching runner variables and invented plugin/
        // authority switches never install a provider. No credential or HTTP is used.
        await File.WriteAllBytesAsync(w.FilePath("fixture.json"), Bytes(f.Pre));
        var spoof = new Dictionary<string, string> {
            ["GITHUB_ACTIONS"] = "true", ["GITHUB_API_URL"] = "https://api.github.com",
            ["GITHUB_REPOSITORY"] = request.Selection.Scope.SourceRepository,
            ["GITHUB_SHA"] = request.Selection.Scope.SourceRevision,
            ["EF_HOST_AUTHENTICATED"] = "true", ["EF_EXECUTION_AUTHORIZED"] = "true",
            ["EF_PROVIDER_ASSEMBLY"] = typeof(EfOperationalTests).Assembly.Location,
            ["EF_TEST_MODE"] = "PLAN", ["EF_EVIDENCE_DIRECTORY"] = w.Root
        };
        var index = 0;
        async Task<(int Code, string Output)> Invoke(string json, string[]? extra = null)
        {
            var path = w.FilePath("request-" + index + ".json"); var output = w.FilePath("out-" + index++);
            await File.WriteAllTextAsync(path, json);
            var result = await Process(engine.Location,
                new[] { "prepare-ef-release", "--request", path, "--output", output }.Concat(extra ?? []).ToArray(), spoof);
            Assert(result.Code != 0 && result.Output.Contains("\"executionAuthorized\":false")
                && result.Output.Contains("\"certified\":false") && !File.Exists(Path.Combine(output, "ef-plan-review.json")),
                "Caller reached a prepared plan or write authority: " + result.Output);
            return result;
        }
        foreach (var operation in new[] { "PLAN", "REVALIDATE", "VERIFY_POST" })
        {
            var r = request with { Operation = operation, Continuation = operation switch {
                "REVALIDATE" => new(request.Selection.Governance, request.Selection.Pre, request.Selection.Source),
                "VERIFY_POST" => new(request.Selection.Governance, Execution: request.Selection.Source, ActualPost: request.Selection.Pre),
                _ => null
            } };
            var result = await Invoke(JsonSerializer.Serialize(r, JsonDefaults.Compact));
            Assert(result.Code == 66 && result.Output.Contains("GOVERNANCE_CREDENTIAL_REQUIRED"),
                "Environment or fixture spoof bypassed the fixed production factory");
        }
        var valid = JsonSerializer.Serialize(request, JsonDefaults.Compact);
        foreach (var field in new[] { "\"trusted\":true,", "\"executionAuthorized\":true,",
            "\"provider\":\"fixture\",", "\"apply\":true," })
            Assert((await Invoke(valid.Insert(1, field))).Code == 65, "JSON supplied authority");
        Assert((await Invoke(valid.Replace("\"PLAN\"", "\"APPLY\""))).Code == 65, "APPLY accepted");
        foreach (var extra in new[] { new[] { "--provider", "fixture" }, new[] { "--ef-host-child", "PLAN" } })
            Assert((await Invoke(valid, extra)).Code == 65, "CLI selected a test provider");
    }
    private static async Task PositiveProcesses()
    {
        foreach (var mode in new[] { "PLAN", "NO_CHANGES" })
        {
            using var w = new Workspace();
            var result = await Process(typeof(EfOperationalTests).Assembly.Location,
                ["--ef-host-child", mode, w.FilePath("request.json"), w.FilePath("result")]);
            var status = mode == "PLAN" ? "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED" : mode;
            Assert(result.Code == 0 && result.Output.Contains(status), "Child CLI failed: " + result.Output);
            var text = File.ReadAllText(w.FilePath("result/ef-result.json"));
            Assert(text.Contains("\"executionAuthorized\":false") && text.Contains("\"certified\":false"), "CLI granted authority");
            Assert(File.Exists(w.FilePath("result/ef-plan-review.json")) == (mode == "PLAN"), "Review artifact presence incorrect");
            foreach (var file in Directory.GetFiles(w.FilePath("result")))
            {
                var artifact = File.ReadAllText(file);
                Assert(!artifact.Contains("CREATE TABLE") && !artifact.Contains("serverInstance") && !artifact.Contains("endpointReference")
                    && !artifact.Contains("projectPath") && !artifact.Contains("applicationId"), "Sensitive metadata published");
            }
        }
    }
    private static async Task Contexts()
    {
        foreach (var spec in new[] { ("Orders", 2, 1, "sales", false), ("Fleet", 5, 2, "transport", false), ("NewApp", 0, 1, "dbo", true) })
        {
            var f = await EfPreparationTests.Make(spec.Item1, spec.Item2, spec.Item3, spec.Item4, spec.Item5);
            var r = await Run(f);
            Assert(r.Result.Status == "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED" && !r.Result.ExecutionAuthorized
                && r.Review!.PendingCount == spec.Item3, "Generic host composition blocked: " + string.Join(",", r.Result.Reasons));
        }
    }
    private static async Task<(int Code, string Output)> Cli(string[] args, IEfOperationalSessionFactoryV1 host, CancellationToken token = default)
    {
        var previous = Console.Out; using var buffer = new StringWriter(); Console.SetOut(buffer);
        try { var code = await EfOperationalCli.RunAsync(args, host, token, new Clock()); return (code, buffer.ToString()); }
        finally { Console.SetOut(previous); }
    }
    private static async Task InputBoundary()
    {
        var f = await EfPreparationTests.Make(); var json = JsonSerializer.Serialize(Request(f), JsonDefaults.Compact);
        foreach (var content in new[] { json.Insert(1, "\"trusted\":true,"), json.Insert(1, "\"ContractVersion\":1,"),
            json.Replace("\"PLAN\"", "\"APPLY\""), json.Replace("\"TEST\"", "\"PROD\""), "{}", "null", new string('x', 65537),
            "{\"private\":\"SECRET_SENTINEL_PASSWORD\"}" })
        {
            using var w = new Workspace(); await File.WriteAllTextAsync(w.FilePath("request"), content);
            var r = await Cli(["--request", w.FilePath("request"), "--output", w.FilePath("out")], new Factory(f.Provider));
            Assert(r.Code == 65 && !r.Output.Contains("SECRET_SENTINEL"), "Malformed input not sanitized/invalid: " + r.Output);
        }
        using var collision = new Workspace(); Directory.CreateDirectory(collision.FilePath("out"));
        File.WriteAllText(collision.FilePath("out/ef-result.json"), "preserve-user-evidence");
        var failed = await Cli(["--request", "missing", "--output", collision.FilePath("out")], new Factory(f.Provider));
        Assert(failed.Code == 65 && File.ReadAllText(collision.FilePath("out/ef-result.json")) == "preserve-user-evidence", "Prior evidence overwritten");
    }
    private static async Task EvidenceChanges()
    {
        var f = await EfPreparationTests.Make(); var req = Request(f);
        foreach (var changed in new[] { req with { ApplicationId = "another-app" }, req with { ActionsRevision = new string('e', 40) },
            req with { GovernanceRevision = new string('e', 40) }, req with { Selection = req.Selection with {
                Scope = req.Selection.Scope with { TargetId = "other-target" } } } })
            Assert((await Run(f, changed)).Result.Status == "BLOCKED", "Changed context accepted");
        f.Provider.Allowed.Remove((f.Selection.Pre.EvidenceId, "TARGET_PRE"));
        Assert((await Run(f)).Result.Status == "BLOCKED", "Unauthorized producer accepted");
        f = await EfPreparationTests.Make(); f.Provider.Content[f.Selection.Source.EvidenceId][0] ^= 1;
        Assert((await Run(f)).Result.Status == "BLOCKED", "SQL/evidence bytes changed");
        f = await EfPreparationTests.Make(); f.Provider.Changed = true;
        Assert((await Run(f)).Result.Status == "BLOCKED", "Current baseline/revision race accepted");
        f = await EfPreparationTests.Make();
        f = f.Replace("TARGET_PRE", f.Pre with { Observation = f.Pre.Observation with { AppliedMigrationIds = ["20260103120000_Diverged"] } });
        Assert((await Run(f)).Result.Status == "BLOCKED", "History divergence accepted");
        f = await EfPreparationTests.Make();
        Assert((await Run(f, Request(f) with { Selection = f.Selection with { Qualification = null } })).Result.Reasons.Contains("QUALIFIED_POST_REQUIRED"), "Missing recovery became success");
    }
    private static async Task Continuations()
    {
        var f = await EfPreparationTests.Make(); var plan = (await f.Run()).PlanResult!.Plan!;
        var planRef = f.Provider.Raw("prepared-plan", "EF_PREPARED_PLAN", Encoding.UTF8.GetBytes(plan.Json));
        var fresh = EfPreparationTests.Observe(f.Provider, f.Pre.Observation with { CapturedAtUtc = Now }, "FRESH_PRE", f.Governance.ContentHashContract);
        var freshRef = f.Provider.Publish("fresh-input", "FRESH_PRE", fresh);
        var r = Request(f) with { Operation = "REVALIDATE", Continuation = new(planRef, freshRef, f.Selection.Source) };
        Assert((await Run(f, r)).Result.Status == "PRE_MATCH_NOT_AUTHORIZED", "Revalidation blocked");
        var e = EfPreparationTests.Execution(f, plan);
        r = Request(f) with { Operation = "VERIFY_POST", Continuation = new(planRef, Execution: e.Execution, ActualPost: e.Post) };
        var post = await EfOperationalHostV1.RunAsync(r, new Factory(f.Provider), Now.AddSeconds(3));
        Assert(post.Result.Status == "POST_MATCH_NOT_CERTIFIED" && !post.Result.Certified, "Actual POST failed");
        f.Provider.Allowed.Remove((planRef.EvidenceId, "EF_PREPARED_PLAN"));
        Assert((await Run(f, r)).Result.Status == "BLOCKED", "Caller hash authorized its own plan");
    }
    private static async Task Cancellation()
    {
        using var w = new Workspace(); var f = await EfPreparationTests.Make();
        await File.WriteAllBytesAsync(w.FilePath("request"), Bytes(Request(f)));
        var cancelled = await Cli(["--request", w.FilePath("request"), "--output", w.FilePath("cancel")], new Factory(f.Provider), new(true));
        Assert(cancelled.Code == 130 && cancelled.Output.Contains("CANCELLED"), "Cancellation became ordinary block");
        var timed = await Process(typeof(EfOperationalTests).Assembly.Location,
            ["--ef-host-child", "WAIT", w.FilePath("wait-request"), w.FilePath("timeout")]);
        Assert(timed.Code == 124 && timed.Output.Contains("TIMEOUT"), "Deadline lost distinct semantics");
    }

    private sealed class GitHandler(EfOperationalRequestV1 r, Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        internal string? Fault;
        internal int Calls;
        internal int MainCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.GetLeftPart(UriPartial.Authority) == "https://api.github.com"
                && request.Headers.Authorization?.Parameter == "FAKE_READ_ONLY_CREDENTIAL", "Unbounded authority or mutating HTTP");
            if (Fault == "denied") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (Fault == "redirect") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect));
            if (Fault == "large") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) });
            var uri = request.RequestUri!;
            object body;
            if (uri.AbsolutePath.EndsWith("/git/ref/heads/main", StringComparison.Ordinal))
            {
                MainCalls++;
                var sha = uri.AbsolutePath.Contains("/workflow/") ? r.GovernanceRevision : uri.AbsolutePath.Contains("/actions/") ? r.ActionsRevision : r.Selection.Scope.SourceRevision;
                if (Fault == "revision" || Fault == "race" && MainCalls > 3) sha = new string('0', 40);
                body = new { @ref = "refs/heads/main", @object = new { type = "commit", sha } };
            }
            else
            {
                Assert(uri.Query == "?ref=" + r.GovernanceRevision, "Floating content revision");
                var path = uri.AbsolutePath.Split("/contents/")[1]; var bytes = files[path];
                var sha = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"blob {bytes.Length}\0").Concat(bytes).ToArray()));
                body = new { type = "file", path, encoding = "base64", size = bytes.Length, sha = Fault == "hash" ? new string('0', 40) : sha, content = Convert.ToBase64String(bytes) };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(body)) });
        }
    }
    private static (EfGitHubSourceHostV1 Host, GitHandler Http) GitHost(EfOperationalRequestV1 r, string? fault = null, string? resolverRoot = null)
    {
        var efSource = new { sourceRepository = r.Selection.Scope.SourceRepository, sourceRevision = new string('c', 40),
            projectPath = "src/Infra/Infra.csproj", managedMigrationStartId = "20260101120000_First", preAdoptionArtifacts = Array.Empty<object>() };
        var binding = new { endpointReference = "endpoint", databaseName = "ExampleDb", serverMatchPolicy = "ALLOW_LIST", allowedServerInstances = new[] { "ExampleServer" } };
        var authority = new { kind = "REGISTRY_GIT_PR", policyPath = "docs/policy.md" };
        var record = new { targetId = fault == "target" ? "wrong-target" : r.Selection.Scope.TargetId, applicationId = r.ApplicationId,
            environment = "TEST", binding, databaseLifecycle = "EXISTING", changeManagementMode = "EF_MIGRATIONS", authorityReference = authority, efSource };
        var files = new Dictionary<string, byte[]> {
            ["database-registry/governance-targets.json"] = Bytes(new { governanceFormatVersion = 1, targets = new[] { record } }),
            ["database-registry/onboarding-targets.json"] = Bytes(new { onboardingFormatVersion = 1, targets = new[] { new {
                targetId = r.Selection.Scope.TargetId, status = "MANAGED", authorityReference = new { kind = "ONBOARDING_GIT_PR", policyPath = "docs/policy.md" } } } }),
            ["database-registry/targets.json"] = Bytes(new { registryFormatVersion = 1, targets = Array.Empty<object>() })
        };
        // Unit fixture modules are only an HTTP/host-process test, not governance
        // authentication. A separate explicit mode runs the unchanged real resolver.
        var response = new { status = "PRESENT", registryGovernance = new { targetId = record.targetId, governance = record }, onboarding = new { onboardingState = "MANAGED" } };
        foreach (var name in new[] { "resolve-governance-runtime.mjs", "validate-ef-source.mjs" })
            files["database-registry/" + name] = resolverRoot is null
                ? Encoding.UTF8.GetBytes(name.StartsWith("resolve") ? "export function resolveGovernanceRuntime() { return {result:" + JsonSerializer.Serialize(response) + "}; }" : "// HTTP fixture")
                : File.ReadAllBytes(Path.Combine(resolverRoot, "database-registry", name));
        var http = new GitHandler(r, files) { Fault = fault };
        string? EnvironmentValue(string key) => key switch {
            "GITHUB_ACTIONS" => "true", "GITHUB_API_URL" => "https://api.github.com", "GITHUB_REPOSITORY" => r.Selection.Scope.SourceRepository,
            "GITHUB_SHA" => r.Selection.Scope.SourceRevision, "EF_GOVERNANCE_READ_TOKEN" => fault == "credential" ? null : "FAKE_READ_ONLY_CREDENTIAL", _ => null
        };
        return (new(EnvironmentValue, () => http), http);
    }
    private static async Task GitSources()
    {
        var f = await EfPreparationTests.Make(); var r = Request(f); var test = GitHost(r);
        var outcome = await test.Host.OpenAsync(r, CancellationToken.None);
        Assert(outcome.Provider is null && outcome.VerifiedGitSources.Length == 5 && test.Http.MainCalls == 6
            && outcome.BlockingReasons.Contains("A2_1_NORMALIZED_PRODUCER_ADAPTER_REQUIRED"), "Git bytes impersonated operational evidence");
        Assert(!JsonSerializer.Serialize(outcome, JsonDefaults.Compact).Contains("FAKE_READ_ONLY_CREDENTIAL"), "Credential leaked");
    }
    private static async Task GitNegatives()
    {
        var r = Request(await EfPreparationTests.Make());
        foreach (var fault in new[] { "credential", "denied", "redirect", "revision", "race", "hash", "large", "target" })
        {
            var test = GitHost(r, fault); var outcome = await test.Host.OpenAsync(r, CancellationToken.None);
            Assert(outcome.Provider is null && outcome.BlockingReasons.Length == 1
                && !outcome.BlockingReasons.Contains("A2_1_NORMALIZED_PRODUCER_ADAPTER_REQUIRED"), "Source flaw accepted: " + fault);
            if (fault == "credential") Assert(test.Http.Calls == 0, "Missing credential contacted service");
        }
    }
    internal static async Task<int> RealResolver(string root)
    {
        var r = Request(await EfPreparationTests.Make()); var test = GitHost(r, resolverRoot: root);
        var outcome = await test.Host.OpenAsync(r, CancellationToken.None);
        Assert(outcome.BlockingReasons.Contains("A2_1_NORMALIZED_PRODUCER_ADAPTER_REQUIRED"), "Real resolver integration failed: " + string.Join(",", outcome.BlockingReasons));
        Console.WriteLine("PASS: unchanged governance resolver + synthetic authenticated HTTP transport; external authentication NOT executed");
        return 0;
    }
}
