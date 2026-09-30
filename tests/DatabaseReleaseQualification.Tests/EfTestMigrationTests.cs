using DatabaseReleaseQualification;

internal static class EfTestMigrationTests
{
    public static readonly (string Name, Func<Task> Run)[] Cases = [
        ("ef TEST exact plan accepts same run and script", () => Check("valid")),
        ("ef TEST rejects QA and PROD", () => Check("environment")),
        ("ef TEST rejects wrong target", () => Check("target")),
        ("ef TEST rejects changed app commit", () => Check("app")),
        ("ef TEST rejects changed connection source", () => Check("source")),
        ("ef TEST rejects script swap", () => Check("script")),
        ("ef TEST rejects rehashed artifact without trusted output", () => Check("rehash")),
        ("ef TEST rejects missing protected job authority", () => Check("authority")),
        ("ef TEST rejects stale analysis", () => Check("stale")),
        ("ef TEST SQL runner rejects cross-database and unsupported statements", ScriptGuard),
        ("ef TEST plan validates exact SQL before approval", ScriptValidationCli)
    ];
    private static Task Check(string fault)
    {
        var original = new Dictionary<string, string?> {
            ["GITHUB_RUN_ID"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            ["GITHUB_RUN_ATTEMPT"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            ["GITHUB_SHA"] = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            ["GITHUB_JOB"] = Environment.GetEnvironmentVariable("GITHUB_JOB"),
            ["EF_TEST_WORKFLOW_SHA"] = Environment.GetEnvironmentVariable("EF_TEST_WORKFLOW_SHA"),
            ["EF_TEST_ACTIONS_SHA"] = Environment.GetEnvironmentVariable("EF_TEST_ACTIONS_SHA"),
            ["EF_TEST_PROTECTED_ENVIRONMENT"] = Environment.GetEnvironmentVariable("EF_TEST_PROTECTED_ENVIRONMENT")
        };
        try
        {
            var a = new string('a', 40);
            var b = new string('b', 40);
            var c = new string('c', 40);
            foreach (var (key, value) in new Dictionary<string, string> {
                ["GITHUB_RUN_ID"] = "123", ["GITHUB_RUN_ATTEMPT"] = "1",
                ["GITHUB_SHA"] = a, ["GITHUB_JOB"] = "apply_ef_test",
                ["EF_TEST_WORKFLOW_SHA"] = b, ["EF_TEST_ACTIONS_SHA"] = c,
                ["EF_TEST_PROTECTED_ENVIRONMENT"] = "ef-migration-test"
            }) Environment.SetEnvironmentVariable(key, value);
            var hash = new string('d', 64);
            var plan = EfTestMigrationPlan.Bind(new(1, "EF_TEST_MIGRATION_PLAN",
                "6e43bc5f-0d27-4e5d-9af8-3709ab69d5f5", "3602", "TEST",
                "DBCICDV3TEST", "CICDV3", "sqlv1testdcsrv1", "sqlv1testdcsrv1",
                "3602-TEST--DataAccessRegistry--Owner", a, b, c, "123", "1",
                ["20260101000000_Initial"], hash, "RECOVERY-123",
                DateTimeOffset.UtcNow, ""));
            var trusted = plan.PlanHash;
            switch (fault)
            {
                case "environment": plan = EfTestMigrationPlan.Bind(plan with { Environment = "QA", PlanHash = "" }); break;
                case "target": plan = EfTestMigrationPlan.Bind(plan with { TargetId = "other", PlanHash = "" }); break;
                case "app": plan = EfTestMigrationPlan.Bind(plan with { ApplicationCommit = new string('e', 40), PlanHash = "" }); break;
                case "source": plan = EfTestMigrationPlan.Bind(plan with { ConnectionDataSource = "qa-server", PlanHash = "" }); break;
                case "rehash": plan = EfTestMigrationPlan.Bind(plan with { RecoveryPlanReference = "forged", PlanHash = "" }); break;
                case "stale": plan = EfTestMigrationPlan.Bind(plan with { AnalyzedAtUtc = DateTimeOffset.UtcNow.AddDays(-2), PlanHash = "" }); break;
                case "authority": Environment.SetEnvironmentVariable("GITHUB_JOB", "other"); break;
            }
            var scriptHash = fault == "script" ? new string('e', 64) : hash;
            if (fault == "source") trusted = plan.PlanHash;
            try
            {
                EfTestMigrationPlan.Verify(plan, trusted, scriptHash,
                    "sqlv1testdcsrv1", "sqlv1testdcsrv1");
                if (fault != "valid") throw new Exception("EF boundary accepted " + fault);
            }
            catch (LegacyContractException)
            {
                if (fault == "valid") throw;
            }
            return Task.CompletedTask;
        }
        finally
        {
            foreach (var (key, value) in original)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
    private static Task ScriptGuard()
    {
        if (EfTestMigrationCli.ExactBatches("CREATE TABLE dbo.X (Id int);\nGO\n").Count != 1)
            throw new Exception("Expected one exact batch");
        foreach (var sql in new[] {
            "USE QA;\nGO\n", "EXEC(N'DROP TABLE dbo.X');\nGO\n",
            "INSERT INTO [QA].dbo.X VALUES (1);\nGO\n",
            "UPDATE [linked].[QA].dbo.X SET Id = 1;\nGO\n",
            "INSERT INTO dbo.X SELECT Id FROM OPENROWSET('x','y','z');\nGO\n",
            "CREATE SYNONYM dbo.X FOR [QA].dbo.X;\nGO\n"
        })
        {
            try { EfTestMigrationCli.ExactBatches(sql); throw new Exception("Unsafe SQL accepted"); }
            catch (LegacyContractException) { }
        }
        return Task.CompletedTask;
    }

    private static async Task ScriptValidationCli()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(),
            ".ef-validation-" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            await File.WriteAllTextAsync(path, "CREATE TABLE dbo.X (Id int);\nGO\n");
            if (await EfTestMigrationCli.ValidateAsync(["--script", path]) != 0)
                throw new Exception("Expected preapproval validation");
            await File.WriteAllTextAsync(path, "INSERT INTO QA.dbo.X VALUES (1);\nGO\n");
            if (await EfTestMigrationCli.ValidateAsync(["--script", path]) == 0)
                throw new Exception("Unsafe preapproval SQL accepted");
        }
        finally { File.Delete(path); }
    }
}
