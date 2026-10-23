using System.Text.Json;
using DatabaseReleaseQualification;

internal static class EfUnifiedClassificationTests
{
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF unified classification: versioned consumer binds V3 V4 V5 to evidence", Positive),
        ("EF unified classification: variant input and authority substitutions block", Negative),
        ("EF unified classification: actual Node/Bash output consumed by A2.1", ActualProducer)
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
    private static async Task ActualProducer()
    {
        foreach (var variant in new[] { "V3", "V4", "V5" })
        {
            var f = variant == "V3" ? await EfPreparationTests.Make("Initial", 0, 1, initial: true) : await EfPreparationTests.Make();
            if (variant == "V5") f = f.Replace("SOURCE", new EfSourcePreparationV1(
                f.Original.Request.Source with { ManagedMigrationIds = f.Pre.Observation.AppliedMigrationIds }, [], []));
            var source = JsonSerializer.Deserialize<EfSourcePreparationV1>(f.Provider.Content[f.Selection.Source.EvidenceId], JsonDefaults.Compact)!;
            var initial = variant == "V3"; var o = f.Pre.Observation;
            // Use the producer's taxonomy, not a second list of SQL categories.
            var adapter = Path.GetFullPath("../../../../../scripts/adapt-classification-evidence-v2.mjs", AppContext.BaseDirectory);
            var taxonomy = Path.Combine(Path.GetDirectoryName(adapter)!, "empty-for-new-ef-v1.mjs");
            var psi = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--input-type=module"); psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("import fs from 'node:fs'; import {pathToFileURL} from 'node:url'; const adapterPath=process.argv[1]; process.argv[1]='offline-driver'; const {adaptEvidence}=await import(pathToFileURL(adapterPath)); const {TECHNICAL_CATEGORIES}=await import(pathToFileURL(process.argv[2])); const r=JSON.parse(fs.readFileSync(0,'utf8')); r.physical.taxonomy={version:1,coverage:'COMPLETE',counts:Object.fromEntries(TECHNICAL_CATEGORIES.map(k=>[k,0]))}; const v=adaptEvidence(r); process.stdout.write(JSON.stringify(v));");
            psi.ArgumentList.Add(adapter); psi.ArgumentList.Add(taxonomy);
            var raw = new { contractVersion = 2, efInitialHistoryPolicy = initial ? "EMPTY_HISTORY_ATTESTED_V1" : "NOT_VERIFIED",
                declarations = new { databaseLifecycle = f.Governance.Authority.Lifecycle, changeManagementMode = "EF_MIGRATIONS" },
                connection = new { status = "SUCCEEDED" }, databaseLookup = new { status = "FOUND" }, targetConnection = new { status = "SUCCEEDED" },
                metadata = new { status = "SUFFICIENT" }, physical = new { status = "OBSERVED", businessObjectCount = f.Pre.Discovery.Physical.BusinessObjectCount, technicalObjectCount = f.Pre.Discovery.Physical.TechnicalObjectCount },
                history = new { status = "PRESENT", migrationCount = o.AppliedMigrationIds.Length, migrationIds = o.AppliedMigrationIds },
                repository = new { status = "PRESENT_VALID", migrations = new { count = source.Source.ManagedMigrationIds.Length, ids = source.Source.ManagedMigrationIds } },
                registry = new { status = initial ? "NOT_EVALUATED" : "CERTIFIED" }, schema = new { status = initial ? "NOT_EVALUATED" : "CONSISTENT" },
                onboarding = new { status = initial ? "NOT_REQUIRED" : "MANAGED" } };
            using var child = System.Diagnostics.Process.Start(psi)!;
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.StandardInput.WriteAsync(JsonSerializer.Serialize(raw)); child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            using var parsed = JsonDocument.Parse(await output);
            Assert(child.ExitCode == 0 && parsed.RootElement.GetProperty("exitCode").GetInt32() == 0, await error);
            var result = parsed.RootElement.GetProperty("result").GetProperty("classificationResult");
            var reference = f.Provider.Publish("actual-classifier", "CLASSIFICATION_V2", new EfUnifiedClassificationEvidenceV1(
                f.Selection.Scope, f.Selection.Pre, f.Selection.Source, f.Governance.Authority.BootstrapEvidence, result));
            f = f.Replace("GOVERNANCE", f.Governance with { ClassificationRulesVersion = 2,
                Authority = f.Governance.Authority with { Classification = reference } });
            var prepared = await f.Run();
            Assert(prepared.Status == (variant == "V5" ? "NO_CHANGES" : "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED"),
                variant + ": " + string.Join(",", prepared.Reasons));
        }
    }
}
