using System.Text.Json;
using DatabaseReleaseQualification;

internal static class EfUnifiedClassificationTests
{
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF unified classification: versioned consumer binds V3 V4 V5 to evidence", Positive),
        ("EF unified classification: variant input and authority substitutions block", Negative)
    ];
    private static void Assert(bool b, string why) { if (!b) throw new Exception(why); }
    internal static EfPreparationTests.Fixture Admit(EfPreparationTests.Fixture f, string mutation = "")
    {
        var source = JsonSerializer.Deserialize<EfSourcePreparationV1>(f.Provider.Content[f.Selection.Source.EvidenceId], JsonDefaults.Compact)!;
        var g = f.Governance; var o = f.Pre.Observation;
        var initial = g.Authority.Scenario == "NEW_EF"; var pending = o.AppliedMigrationIds.Length < source.Source.ManagedMigrationIds.Length;
        var variant = initial ? o.HistoryStatus == "ABSENT" ? "V2" : "V3" : pending ? "V4" : "V5";
        var reason = variant switch { "V2" => "INITIAL_MIGRATIONS_PENDING", "V3" => "EMPTY_HISTORY_INITIAL_STATE", "V4" => "PENDING_MIGRATIONS", _ => "NO_CHANGES" };
        var result = JsonSerializer.SerializeToElement(new {
            contractVersion = 2, rulesVersion = "2",
            declarations = new { databaseLifecycle = g.Authority.Lifecycle, changeManagementMode = "EF_MIGRATIONS" },
            observations = new { physicalExistence = "EXISTS", metadataVisibility = "SUFFICIENT",
                physicalState = f.Pre.Discovery.Physical.BusinessObjectCount > 0 ? "POPULATED" : "EMPTY", repositoryState = "PRESENT_VALID",
                historyState = o.HistoryStatus, repoHistoryRelation = initial ? "NOT_APPLICABLE" : pending ? "VALID_PREFIX" : "EXACT_MATCH",
                schemaRelation = initial ? "NOT_EVALUATED" : "CONSISTENT", registryState = initial ? "NOT_EVALUATED" : "CERTIFIED",
                onboardingState = g.Authority.OnboardingState },
            inferences = new { scenario = g.Authority.Scenario, hasPendingMigrations = pending },
            decision = new { status = "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE", primaryBlock = (string?)null, blocks = Array.Empty<string>() },
            warnings = Array.Empty<string>(), unknownFields = Array.Empty<string>(),
            ef = new { variant = mutation == "variant" ? "V1" : variant, reason,
                initialHistoryPolicy = initial ? o.HistoryStatus == "ABSENT" ? "HISTORY_ABSENT_V1" : "EMPTY_HISTORY_ATTESTED_V1" : "NOT_VERIFIED",
                executionAuthorized = mutation == "authority", allowedStages = variant == "V5" ? new[] { "NO_CHANGES" } : ["PLAN", "QUALIFICATION", "REVALIDATE"],
                forbiddenStages = new[] { "APPLY", "CERTIFY", "CREATE_DATABASE" } } });
        var envelope = new EfUnifiedClassificationEvidenceV1(f.Selection.Scope,
            mutation == "input" ? f.Selection.Source : f.Selection.Pre, f.Selection.Source, g.Authority.BootstrapEvidence, result);
        var reference = f.Provider.Publish("unified-classification", "CLASSIFICATION_V2", envelope);
        g = g with { ClassificationRulesVersion = 2, Authority = g.Authority with { Classification = reference } };
        return f.Replace("GOVERNANCE", g) with { Governance = g };
    }
    private static async Task Positive()
    {
        foreach (var variant in new[] { "V3", "V4", "V5" })
        {
            var f = variant == "V3" ? await EfPreparationTests.Make("Initial", 0, 1, initial: true) : await EfPreparationTests.Make();
            if (variant == "V5") f = f.Replace("SOURCE", new EfSourcePreparationV1(
                f.Original.Request.Source with { ManagedMigrationIds = f.Pre.Observation.AppliedMigrationIds }, [], []));
            var r = await Admit(f).Run();
            Assert(r.Status == (variant == "V5" ? "NO_CHANGES" : "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED"), variant + ": " + string.Join(",", r.Reasons));
        }
    }
    private static async Task Negative()
    {
        foreach (var mutation in new[] { "variant", "input", "authority" })
        {
            var f = Admit(await EfPreparationTests.Make(), mutation); var r = await f.Run();
            Assert(r.Status == "BLOCKED", mutation + " admitted");
        }
    }
}
