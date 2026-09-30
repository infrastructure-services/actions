using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DatabaseReleaseQualification;

public sealed record EfTestMigrationPlanV1(int ContractVersion, string Kind,
    string TargetId, string ApplicationId, string Environment,
    string EndpointReference, string DatabaseName, string ServerInstance,
    string ConnectionDataSource,
    string SecretName, string ApplicationCommit, string WorkflowRevision,
    string ActionsRevision, string RunId, string RunAttempt,
    IReadOnlyList<string> MigrationIds, string ScriptSha256,
    string RecoveryPlanReference, DateTimeOffset AnalyzedAtUtc, string PlanHash);

public static class EfTestMigrationPlan
{
    private static readonly Regex Sha = new(@"\A[0-9a-f]{40}\z");
    private static readonly Regex Hash = new(@"\A[0-9a-f]{64}\z");
    private static readonly Regex Migration = new(@"\A[0-9]{14}_[A-Za-z0-9_]+\z");
    public static EfTestMigrationPlanV1 Bind(EfTestMigrationPlanV1 value) =>
        value with { PlanHash = LegacyRuntimeEvidenceHash.Hash(value with { PlanHash = "" }) };

    public static void Verify(EfTestMigrationPlanV1 plan, string trustedHash,
        string scriptHash, string expectedServer, string expectedDataSource)
    {
        if (plan.ContractVersion != 1 || plan.Kind != "EF_TEST_MIGRATION_PLAN"
            || plan.TargetId != "6e43bc5f-0d27-4e5d-9af8-3709ab69d5f5"
            || plan.ApplicationId != "3602" || plan.Environment != "TEST"
            || plan.EndpointReference != "DBCICDV3TEST" || plan.DatabaseName != "CICDV3"
            || plan.SecretName != "3602-TEST--DataAccessRegistry--Owner"
            || !string.Equals(plan.ServerInstance, expectedServer, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(plan.ConnectionDataSource)
            || !string.Equals(plan.ConnectionDataSource, expectedDataSource,
                StringComparison.OrdinalIgnoreCase)
            || !Sha.IsMatch(plan.ApplicationCommit) || !Sha.IsMatch(plan.WorkflowRevision)
            || !Sha.IsMatch(plan.ActionsRevision) || !Hash.IsMatch(plan.ScriptSha256)
            || plan.MigrationIds.Count == 0 || plan.MigrationIds.Count > 100
            || plan.MigrationIds.Any(x => !Migration.IsMatch(x))
            || plan.MigrationIds.Distinct(StringComparer.Ordinal).Count() != plan.MigrationIds.Count
            || plan.ScriptSha256 != scriptHash || !Hash.IsMatch(trustedHash)
            || plan.PlanHash != trustedHash || Bind(plan).PlanHash != trustedHash
            || plan.RunId != Environment.GetEnvironmentVariable("GITHUB_RUN_ID")
            || plan.RunAttempt != "1"
            || Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") != "1"
            || plan.ApplicationCommit != Environment.GetEnvironmentVariable("GITHUB_SHA")
            || plan.WorkflowRevision != Environment.GetEnvironmentVariable("EF_TEST_WORKFLOW_SHA")
            || plan.ActionsRevision != Environment.GetEnvironmentVariable("EF_TEST_ACTIONS_SHA")
            || Environment.GetEnvironmentVariable("GITHUB_JOB") != "apply_ef_test"
            || Environment.GetEnvironmentVariable("EF_TEST_PROTECTED_ENVIRONMENT") != "ef-migration-test"
            || string.IsNullOrWhiteSpace(plan.RecoveryPlanReference)
            || plan.AnalyzedAtUtc > DateTimeOffset.UtcNow
            || DateTimeOffset.UtcNow - plan.AnalyzedAtUtc > TimeSpan.FromHours(24))
            throw new LegacyContractException("EF_TEST_PLAN_INVALID");
    }
}

public static class EfTestMigrationCli
{
    public static async Task<int> ValidateAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--script") return 65;
        try
        {
            var bytes = await File.ReadAllBytesAsync(args[1]);
            if (bytes.Length is < 1 or > 8 * 1024 * 1024)
                throw new LegacyContractException("EF_TEST_SCRIPT_INVALID");
            _ = ExactBatches(new UTF8Encoding(false, true).GetString(bytes));
            Console.WriteLine("EF_TEST_SCRIPT_VALIDATED");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("EF_TEST_SCRIPT_BLOCKED");
            return 66;
        }
    }

    public static async Task<int> BindAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--input" || args[2] != "--output") return 65;
        try
        {
            var bytes = await File.ReadAllBytesAsync(args[1]);
            if (bytes.Length is < 1 or > 65536)
                throw new LegacyContractException("EF_TEST_PLAN_INVALID");
            var plan = LegacyRehearsalCli.Parse<EfTestMigrationPlanV1>(bytes);
            var connectionString = Environment.GetEnvironmentVariable("EF_TEST_ANALYSIS_CONNECTION")
                ?? throw new LegacyContractException("EF_TEST_CONNECTION_REQUIRED");
            var connection = new SqlConnectionStringBuilder(connectionString);
            if (connection.InitialCatalog != "CICDV3" || string.IsNullOrWhiteSpace(connection.DataSource)
                || connection.Encrypt != SqlConnectionEncryptOption.Strict
                || connection.TrustServerCertificate || connection.AttachDBFilename.Length != 0)
                throw new LegacyContractException("EF_TEST_CONNECTION_UNVERIFIED");
            if (plan.PlanHash != "" || plan.ConnectionDataSource != ""
                || plan.ContractVersion != 1
                || plan.Kind != "EF_TEST_MIGRATION_PLAN" || plan.Environment != "TEST"
                || plan.TargetId != "6e43bc5f-0d27-4e5d-9af8-3709ab69d5f5"
                || plan.MigrationIds.Count == 0 || string.IsNullOrWhiteSpace(plan.RecoveryPlanReference))
                throw new LegacyContractException("EF_TEST_PLAN_INVALID");
            using var stream = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(stream,
                EfTestMigrationPlan.Bind(plan with { ConnectionDataSource = connection.DataSource }),
                JsonDefaults.Compact);
            await stream.FlushAsync();
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("EF_TEST_PLAN_BLOCKED");
            return 66;
        }
    }
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 6 || args[0] != "--plan" || args[2] != "--plan-hash"
            || args[4] != "--script") return 65;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var token = deadline.Token;
            var planBytes = await File.ReadAllBytesAsync(args[1], token);
            if (planBytes.Length is < 1 or > 65536)
                throw new LegacyContractException("EF_TEST_PLAN_INVALID");
            var plan = LegacyRehearsalCli.Parse<EfTestMigrationPlanV1>(planBytes);
            var scriptBytes = await File.ReadAllBytesAsync(args[5], token);
            if (scriptBytes.Length is < 1 or > 8 * 1024 * 1024)
                throw new LegacyContractException("EF_TEST_SCRIPT_INVALID");
            var scriptHash = Convert.ToHexString(SHA256.HashData(scriptBytes)).ToLowerInvariant();
            var connectionString = Environment.GetEnvironmentVariable("EF_TEST_MUTATION_CONNECTION_STRING")
                ?? throw new LegacyContractException("EF_TEST_CONNECTION_REQUIRED");
            // The resolved Key Vault value is the one connection string used for
            // identity inspection and every SQL batch. Never normalize a wrong DB.
            var options = new SqlConnectionStringBuilder(connectionString);
            EfTestMigrationPlan.Verify(plan, args[3], scriptHash, "sqlv1testdcsrv1",
                options.DataSource);
            if (options.InitialCatalog != plan.DatabaseName
                || !string.Equals(options.DataSource, plan.ConnectionDataSource, StringComparison.OrdinalIgnoreCase)
                || options.Encrypt != SqlConnectionEncryptOption.Strict
                || options.TrustServerCertificate || options.AttachDBFilename.Length != 0)
                throw new LegacyContractException("EF_TEST_CONNECTION_UNVERIFIED");
            var sql = new UTF8Encoding(false, true).GetString(scriptBytes);
            var batches = ExactBatches(sql);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            await VerifyIdentity(connection, plan, token);
            foreach (var batch in batches)
            {
                token.ThrowIfCancellationRequested();
                await using var command = connection.CreateCommand();
                command.CommandText = batch;
                command.CommandTimeout = 60;
                await command.ExecuteNonQueryAsync(token);
                await VerifyIdentity(connection, plan, token);
            }
            Console.WriteLine("EF_TEST_APPLIED:" + plan.PlanHash);
            return 0;
        }
        catch (Exception error)
        {
            var code = error is LegacyContractException contract ? contract.Code
                : error is OperationCanceledException ? "EF_TEST_TIMEOUT" : "EF_TEST_EXECUTION_UNCERTAIN";
            Console.Error.WriteLine("EF_TEST_BLOCKED:" + code);
            return 66;
        }
        finally
        {
            Environment.SetEnvironmentVariable("EF_TEST_MUTATION_CONNECTION_STRING", null);
        }
    }

    private static async Task VerifyIdentity(SqlConnection connection,
        EfTestMigrationPlanV1 plan, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')), DB_NAME()";
        command.CommandTimeout = 15;
        await using var row = await command.ExecuteReaderAsync(token);
        if (!await row.ReadAsync(token) || row.IsDBNull(0) || row.IsDBNull(1)
            || !string.Equals(row.GetString(0), plan.ServerInstance, StringComparison.OrdinalIgnoreCase)
            || row.GetString(1) != plan.DatabaseName)
            throw new LegacyContractException("EF_TEST_CONNECTION_TARGET_MISMATCH");
    }

    internal static IReadOnlyList<string> ExactBatches(string sql)
    {
        if (Regex.IsMatch(sql,
                @"\bUSE\b|\bEXEC(?:UTE)?\b|\bOPEN(?:QUERY|ROWSET|DATASOURCE)\b|\bBULK\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new LegacyContractException("EF_TEST_SCRIPT_UNSUPPORTED");
        var parsed = SqlScriptAnalyzer.Parse(sql);
        if (parsed.Errors.Count != 0 || parsed.Fragment is not TSqlScript script)
            throw new LegacyContractException("EF_TEST_SCRIPT_UNSUPPORTED");
        var boundary = new EfTestSqlBoundaryVisitor();
        script.Accept(boundary);
        if (boundary.Unsafe) throw new LegacyContractException("EF_TEST_SCRIPT_UNSUPPORTED");
        var batches = script.Batches.Where(x => x.Statements.Count != 0)
            .Select(x => sql.Substring(x.StartOffset, x.FragmentLength)).ToArray();
        if (batches.Length == 0 || batches.Length > 1000)
            throw new LegacyContractException("EF_TEST_SCRIPT_UNSUPPORTED");
        foreach (var token in script.ScriptTokenStream.Where(x => x.TokenType.ToString() == "Go"))
            if (!string.Equals(sql.Split('\n')[token.Line - 1].Trim(), "GO",
                    StringComparison.OrdinalIgnoreCase))
                throw new LegacyContractException("EF_TEST_SCRIPT_UNSUPPORTED");
        var supported = new HashSet<string>(StringComparer.Ordinal) {
            "CreateTableStatement", "AlterTableAddTableElementStatement",
            "AlterTableAlterColumnStatement", "AlterTableDropTableElementStatement",
            "CreateIndexStatement", "DropIndexStatement", "DropTableStatement",
            "InsertStatement", "UpdateStatement", "DeleteStatement",
            "CreateSequenceStatement", "AlterSequenceStatement", "DropSequenceStatement"
        };
        if (script.Batches.SelectMany(x => x.Statements)
            .Any(x => !supported.Contains(x.GetType().Name)))
            throw new LegacyContractException("EF_TEST_SCRIPT_UNSUPPORTED");
        return batches;
    }

    private sealed class EfTestSqlBoundaryVisitor : TSqlFragmentVisitor
    {
        public bool Unsafe { get; private set; }
        public override void Visit(SchemaObjectName node)
        {
            if (node.DatabaseIdentifier is not null || node.ServerIdentifier is not null)
                Unsafe = true;
        }
        public override void Visit(MultiPartIdentifier node)
        {
            if (node.Identifiers.Count > 2) Unsafe = true;
        }
    }
}
