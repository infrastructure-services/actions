using System.Text;
using System.Text.Json;
using DatabaseReleaseQualification;

internal static class LegacyAdoptionTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases = [
        ("legacy adoption populated initial candidate requires independent review", Candidate),
        ("legacy adoption contradictory lineage identity coverage and stale sources block", Negative),
        ("legacy adoption registration and existing certification stay separate", States),
        ("legacy adoption CLI rejects caller approvals and cannot certify", Cli),
        .. LegacyContinuityTests.Cases
    ];
    internal static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    internal static readonly LegacyAdoptionRequestV1 Request = new(1, "target1", new("github.com", "1234", "team/repo"), new string('a', 40));
    internal static async Task<LegacyAcquiredRuntimeV1> Acquired(string state = "BASELINE_REQUIRED")
    {
        var raw = await new LegacyRuntimeResolverTests.FakeAcquisition { Mutate = "uncertified" }.AcquireAsync(
            new(1, Request.TargetId, new(1, "unused.json", Request.Commit, Request.Repository)), default);
        var target = new DatabaseTarget
        {
            ApplicationId = "app1",
            Environment = "TEST",
            DatabaseName = "TestDb",
            Lifecycle = "EXISTING",
            CertificationStatus = state,
            CertifiedSchemaHash = state == "CERTIFIED" ? SchemaCanonicalizer.Canonicalize(raw.FirstCapture.Snapshot).Sha256 : null
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DatabaseRegistryDocument
        {
            RegistryFormatVersion = 1,
            Targets = state == "ABSENT" ? [] : [target]
        }, DatabaseStateJson.Compact);
        return raw with
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Onboarding = raw.Onboarding with { Status = "REQUIRED" },
            RegistryBytes = bytes,
            RegistryProvenance = new()
            {
                RegistryRepository = raw.RegistryProvenance.RegistryRepository,
                RegistryRef = raw.RegistryProvenance.RegistryRef,
                RegistryCommitSha = raw.RegistryProvenance.RegistryCommitSha,
                RegistryFilePath = raw.RegistryProvenance.RegistryFilePath,
                RegistryFileSha256 = Hashing.Sha256(bytes)
            },
            RepositoryDiscoveryBytes = Envelope(new { status = "ABSENT" }, "REPOSITORY"),
            SqlDiscoveryBytes = Envelope(new
            {
                connectionSource = new { status = "SUCCEEDED" },
                databaseLookupSource = new { status = "FOUND" },
                targetConnectionSource = new { status = "SUCCEEDED" },
                metadataSource = new { status = "SUFFICIENT" },
                physicalSource = new { status = "OBSERVED", businessObjectCount = 1000 },
                historySource = new { status = "ABSENT" }
            }, "SQL")
        };
    }
    private static byte[] Envelope(object value, string name) => Encoding.UTF8.GetBytes(
        "evidence-json<<" + name + "_DISCOVERY_V2_EOF\n" + JsonSerializer.Serialize(value) + "\n" + name + "_DISCOVERY_V2_EOF\n");
    private static async Task Candidate()
    {
        var plan = LegacyAdoptionPlanner.Evaluate(Request, await Acquired(), DateTimeOffset.UtcNow);
        Assert(plan.Status == "PENDING_INDEPENDENT_REVIEW", string.Join(",", plan.Reasons));
        Assert(plan.Candidate is { Data: "NOT_VERIFIED", Security: "NOT_VERIFIED", Incarnation: "NOT_VERIFIED" }
            && plan.CertificationDecision?.Decision == CertificationDecision.ReadyForHumanApproval
            && !plan.Certified && !plan.ExecutionAuthorized && plan.Onboarding?.IsManaged == false, "Candidate acquired authority");
        Assert(plan.Candidate!.CandidateHash == LegacyAdoptionPlanner.CalculateHash(plan.Candidate), "Unbound candidate");
    }
    private static async Task Negative()
    {
        var raw = await Acquired();
        foreach (var mutation in new[] {
            raw with { CapturedAtUtc=DateTimeOffset.UtcNow.AddHours(-1) },
            raw with { Governance=raw.Governance with { TargetId="other" } },
            raw with { Onboarding=raw.Onboarding with { Status="BLOCKED" } },
            raw with { RepositoryDiscoveryBytes=Envelope(new { status="PRESENT_VALID" },"REPOSITORY") },
            raw with { SqlDiscoveryBytes=Encoding.UTF8.GetBytes("unverified") },
            raw with { RegistryBytes=Encoding.UTF8.GetBytes("{}") }
        }) Assert(LegacyAdoptionPlanner.Evaluate(Request, mutation, DateTimeOffset.UtcNow).Status == "BLOCKED", "Adoption accepted contradictory sources");
        raw.FirstCapture.Snapshot.UnsupportedSchemaFeatures.Add("unknown");
        Assert(LegacyAdoptionPlanner.Evaluate(Request, raw, DateTimeOffset.UtcNow).Status == "BLOCKED", "Partial schema admitted");
    }
    private static async Task States()
    {
        Assert(LegacyAdoptionPlanner.Evaluate(Request, await Acquired("ABSENT"), DateTimeOffset.UtcNow).Status == "REGISTRATION_REQUIRED", "Unregistered target promoted");
        var plan = LegacyAdoptionPlanner.Evaluate(Request, await Acquired("CERTIFIED"), DateTimeOffset.UtcNow);
        Assert(plan.Status == "CERTIFIED_STATE_VERIFICATION_REQUIRED" && !plan.Certified && !plan.ExecutionAuthorized, "Registry flag minted authority");
    }
    private static async Task Cli()
    {
        var dir = Path.Combine(Path.GetTempPath(), "legacy-adoption-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "request.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            contractVersion = 1,
            targetId = Request.TargetId,
            repository = Request.Repository,
            commit = Request.Commit,
            approved = true
        }, JsonDefaults.Compact));
        Assert(await LegacyPackageCli.RunAsync(["--adoption-request", file, "--output", Path.Combine(dir, "result")]) == 66,
            "Caller approval accepted");
        Assert(!Directory.Exists(Path.Combine(dir, "result")), "Untrusted CLI created result");
    }
}
