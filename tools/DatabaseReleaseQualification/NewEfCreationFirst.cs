using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlDiscovery.V2;

namespace DatabaseReleaseQualification;

public sealed record NewEfPlanV1(int ContractVersion, string Kind, string TargetId,
    string ApplicationId, string Environment, string DatabaseName, string EndpointReference,
    string ServerInstance, string ConnectionDataSource, string TlsMode, string SourceRepository,
    string SourceRevision, string WorkflowRevision, string ActionsRevision, string GovernanceRevision,
    string GovernanceHash, string OnboardingRevision, string OnboardingHash, string RunId,
    string RunAttempt, string OperationId, DateTimeOffset PreparedAtUtc, DateTimeOffset ExpiresAtUtc,
    string[] MigrationIds, string UpHash, string DownHash, string PayloadHash,
    string ProvisioningIntent, string RecoveryStrategy, int TimeoutSeconds, string PlanHash);

public static class NewEfContract
{
    public const string TargetId = "8d0c7254-5c54-4a93-aeec-a75f8a5a6fc3";
    public const string Database = "CICD_NEW_EF_TEST";
    public const string MigrationId = "20261006205713_InitialMigrationTestItems";
    public const string Recovery = "MIGRATION_EMPTY_EQUIVALENT_WITH_EF_HISTORY_V1";
    public static string Hash<T>(T value) => Hashing.Sha256(JsonSerializer.Serialize(value, JsonDefaults.Compact));
    public static NewEfPlanV1 Bind(NewEfPlanV1 plan) => plan with { PlanHash = Hash(plan with { PlanHash = "" }) };
    public static string PayloadHash(string up, string down) => Hash(new { kind = "NEW_EF_INITIAL_SQL_V1", up, down });
    public static void Require(bool condition, string reason)
    {
        if (!condition) throw new LegacyContractException(reason);
    }
    public static TimeSpan RemainingDeadline(NewEfPlanV1 p) {
        var remaining=p.ExpiresAtUtc-DateTimeOffset.UtcNow;
        Require(remaining>TimeSpan.Zero,"NEW_EF_GRANT_EXPIRED");
        return remaining<TimeSpan.FromSeconds(p.TimeoutSeconds)?remaining:TimeSpan.FromSeconds(p.TimeoutSeconds);
    }
    public static void Verify(NewEfPlanV1 p, string trustedHash, byte[] up, byte[] down, DateTimeOffset now)
    {
        Require(p.ContractVersion == 1 && p.Kind == "NEW_EF_CREATION_FIRST_PLAN"
            && p.TargetId == TargetId && p.ApplicationId == "3602" && p.Environment == "TEST"
            && p.DatabaseName == Database && p.EndpointReference == "DBCICDV3TEST"
            && p.ServerInstance == "sqlv1testdcsrv1" && !string.IsNullOrWhiteSpace(p.ConnectionDataSource)
            && p.ConnectionDataSource.Length <= 255 && !p.ConnectionDataSource.Any(char.IsControl)
            && p.TlsMode is "STRICT" or "TEST_UNTRUSTED_CERTIFICATE"
            && p.SourceRepository == "infrastructure-services/devops-prueba-migraciones-api"
            && new[] { p.SourceRevision, p.WorkflowRevision, p.ActionsRevision, p.GovernanceRevision, p.OnboardingRevision }
                .All(x => Regex.IsMatch(x ?? "", "\\A[0-9a-f]{40}\\z"))
            && p.GovernanceRevision == p.WorkflowRevision && p.OnboardingRevision == p.WorkflowRevision
            && new[] { p.GovernanceHash, p.OnboardingHash, p.UpHash, p.DownHash, p.PayloadHash, trustedHash }
                .All(x => Regex.IsMatch(x ?? "", "\\A[0-9a-f]{64}\\z"))
            && Regex.IsMatch(p.RunId ?? "", "\\A[0-9]+\\z") && p.RunAttempt == "1"
            && Guid.TryParseExact(p.OperationId, "D", out _)
            && p.PreparedAtUtc <= now && now < p.ExpiresAtUtc
            && p.ExpiresAtUtc > p.PreparedAtUtc && p.ExpiresAtUtc - p.PreparedAtUtc <= TimeSpan.FromHours(1)
            && p.TimeoutSeconds is >= 60 and <= 1200
            && p.MigrationIds.SequenceEqual(new[] { MigrationId })
            && p.ProvisioningIntent == "CREATE_ONLY_IF_CONFIRMED_ABSENT"
            && p.RecoveryStrategy == Recovery
            && p.UpHash == Hashing.Sha256(up) && p.DownHash == Hashing.Sha256(down)
            && p.PayloadHash == PayloadHash(p.UpHash, p.DownHash)
            && p.PlanHash == trustedHash && Bind(p).PlanHash == trustedHash, "NEW_EF_PLAN_INVALID");
        NewEfSql.Validate(up, "UP"); NewEfSql.Validate(down, "DOWN");
    }
}

public sealed record NewEfServerEvidence(string Server, string Catalog, string Principal,
    bool CanSeeDatabases, bool DatabaseExists, bool CanCreate, bool CanSeeDefinitions, bool EnabledDdlTrigger);
public static class NewEfCreationGuard
{
    public static void Verify(NewEfPlanV1 p, NewEfServerEvidence e, bool write)
    {
        NewEfContract.Require(p.Environment=="TEST" && p.TargetId==NewEfContract.TargetId
            && p.ApplicationId=="3602" && p.DatabaseName==NewEfContract.Database
            && p.ServerInstance=="sqlv1testdcsrv1", "NEW_EF_TARGET_INVALID");
        NewEfContract.Require(e.Server.Equals(p.ServerInstance,StringComparison.OrdinalIgnoreCase)
            && e.Catalog=="master" && !string.IsNullOrWhiteSpace(e.Principal), "NEW_EF_SERVER_MISMATCH");
        NewEfContract.Require(e.CanSeeDatabases && e.CanSeeDefinitions,"NEW_EF_SERVER_VISIBILITY_REQUIRED");
        NewEfContract.Require(!e.DatabaseExists,"NEW_EF_DATABASE_ALREADY_EXISTS");
        NewEfContract.Require(!e.EnabledDdlTrigger,"NEW_EF_SERVER_DDL_TRIGGER_BLOCKED");
        NewEfContract.Require(!write || e.CanCreate,"NEW_EF_CREATE_AUTHORITY_REQUIRED");
    }
}

// Pilot-specific closed SQL shape. AST regeneration is used only for validation;
// execution retains the original byte-derived spans. Arbitrary SQL is never accepted.
public static class NewEfSql
{
    public const string UpTemplate = """
        IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
        BEGIN
            CREATE TABLE [__EFMigrationsHistory] (
                [MigrationId] nvarchar(150) NOT NULL,
                [ProductVersion] nvarchar(32) NOT NULL,
                CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
            );
        END;
        GO
        CREATE TABLE [dbo].[MigrationTestItems] (
            [Id] int NOT NULL IDENTITY,
            [Name] nvarchar(200) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_MigrationTestItems] PRIMARY KEY ([Id])
        );
        GO
        INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
        VALUES (N'20261006205713_InitialMigrationTestItems', N'10.0.12');
        GO
        """;
    public const string DownTemplate = """
        DROP TABLE [dbo].[MigrationTestItems];
        GO
        DELETE FROM [__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261006205713_InitialMigrationTestItems';
        GO
        """;
    public static IReadOnlyList<string> Validate(byte[] bytes, string phase)
    {
        NewEfContract.Require(bytes.Length is > 0 and <= 65536 && phase is "UP" or "DOWN", "NEW_EF_SQL_INVALID");
        var sql = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        var parsed = SqlScriptAnalyzer.Parse(sql);
        NewEfContract.Require(parsed.Errors.Count == 0 && parsed.Fragment is TSqlScript, "NEW_EF_SQL_UNSUPPORTED");
        var ast = (TSqlScript)parsed.Fragment;
        foreach (var token in ast.ScriptTokenStream.Where(t => t.TokenType.ToString() == "Go"))
            NewEfContract.Require(sql.Split('\n')[token.Line - 1].Trim().Equals("GO", StringComparison.OrdinalIgnoreCase), "NEW_EF_GO_UNSUPPORTED");
        var expected = SqlScriptAnalyzer.Parse(phase == "UP" ? UpTemplate : DownTemplate);
        var generator = new Sql160ScriptGenerator();
        generator.GenerateScript(ast, out var actualSql);
        generator.GenerateScript(expected.Fragment, out var expectedSql);
        NewEfContract.Require(actualSql == expectedSql, "NEW_EF_SQL_SHAPE_MISMATCH");
        return ast.Batches.Where(b => b.Statements.Count != 0)
            .Select(b => sql.Substring(b.StartOffset, b.FragmentLength)).ToArray();
    }
}

public sealed record NewEfCreationReceipt(string TargetId, string ApplicationId, string Environment,
    string ServerInstance, string DatabaseName, string OperationId, string AuthorizationReference,
    string CreatorPrincipal, DateTimeOffset CreatedAtUtc, string DatabaseIncarnation,
    string GovernanceRevision, string SourceRevision, string WorkflowRevision,
    string ActionsRevision, string PlanHash, string Result, string ReceiptHash);
public sealed record NewEfObservation(SqlDiscoveryResult Discovery, SchemaSnapshot Snapshot,
    string SchemaHash, string BusinessSchemaHash, string HistorySchemaHash,
    string BusinessSecurityHash, string HistorySecurityHash, string DatabaseIncarnation,
    bool DataEmpty, bool HistoryProductVersionValid);
public sealed record NewEfPhaseEvidence(string Phase, string Status, string? ScriptHash,
    string? SchemaHash, string? HistoryState, DateTimeOffset AtUtc,
    string? BusinessSchemaHash = null, string? HistorySchemaHash = null,
    string? BusinessSecurityHash = null, string? HistorySecurityHash = null,
    string? DatabaseIncarnation = null, bool? DataEmpty = null,
    bool? HistoryProductVersionValid = null, IReadOnlyList<string>? MigrationIds = null);
public sealed record NewEfCycleReceipt(string Kind, string Status, string PlanHash, string CreationReceiptHash,
    string RecoveryStrategy, bool RecoveryVerified, bool ReapplyVerified,
    IReadOnlyList<NewEfPhaseEvidence> Phases, string? Reason, string ReceiptHash);

public interface INewEfRuntime
{
    Task<NewEfCreationReceipt> CreateAsync(NewEfPlanV1 plan, string authorization, CancellationToken token);
    Task<NewEfObservation> CaptureAsync(NewEfPlanV1 plan, CancellationToken token);
    Task VerifyClassificationAsync(NewEfPlanV1 plan, CancellationToken token);
    Task ExecuteAsync(NewEfPlanV1 plan, byte[] bytes, string phase, NewEfObservation expected, CancellationToken token);
}

public sealed class NewEfCycle(INewEfRuntime runtime, Action<NewEfPhaseEvidence>? journal = null)
{
    private int started;
    public async Task<NewEfCycleReceipt> RunAsync(NewEfPlanV1 plan, string trustedHash,
        byte[] up, byte[] down, NewEfCreationReceipt creation, string authorization, CancellationToken token)
    {
        NewEfContract.Require(Interlocked.Exchange(ref started, 1) == 0, "NEW_EF_RESUME_FORBIDDEN");
        NewEfContract.Verify(plan, trustedHash, up, down, DateTimeOffset.UtcNow);
        NewEfContract.Require(creation.Result == "DATABASE_CREATED" && creation.TargetId == plan.TargetId
            && creation.ApplicationId == plan.ApplicationId && creation.Environment == "TEST"
            && creation.DatabaseName == plan.DatabaseName && creation.ServerInstance == plan.ServerInstance
            && creation.OperationId == plan.OperationId && creation.PlanHash == trustedHash
            && creation.AuthorizationReference == authorization && !string.IsNullOrWhiteSpace(creation.CreatorPrincipal)
            && creation.GovernanceRevision == plan.GovernanceRevision && creation.SourceRevision == plan.SourceRevision
            && creation.WorkflowRevision == plan.WorkflowRevision && creation.ActionsRevision == plan.ActionsRevision
            && creation.CreatedAtUtc >= plan.PreparedAtUtc && creation.CreatedAtUtc <= DateTimeOffset.UtcNow
            && Guid.TryParse(creation.DatabaseIncarnation, out _)
            && NewEfContract.Hash(creation with { ReceiptHash = "" }) == creation.ReceiptHash, "NEW_EF_CREATION_RECEIPT_INVALID");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(NewEfContract.RemainingDeadline(plan));
        var phases = new List<NewEfPhaseEvidence>();
        bool recovery = false, reapply = false;
        void Record(string phase, string status, NewEfObservation? observation = null, string? hash = null)
        {
            var item = new NewEfPhaseEvidence(phase, status, hash, observation?.SchemaHash,
                observation?.Discovery.History.Status.ToString(), DateTimeOffset.UtcNow,
                observation?.BusinessSchemaHash, observation?.HistorySchemaHash,
                observation?.BusinessSecurityHash, observation?.HistorySecurityHash,
                observation?.DatabaseIncarnation, observation?.DataEmpty,
                observation?.HistoryProductVersionValid, observation?.Discovery.History.MigrationIds.ToArray());
            phases.Add(item); journal?.Invoke(item);
        }
        async Task<NewEfObservation> Observe()
        {
            var o = await runtime.CaptureAsync(plan, deadline.Token);
            NewEfContract.Require(o.DatabaseIncarnation == creation.DatabaseIncarnation
                && o.Discovery.DatabaseLookup.Status == DatabaseLookupStatus.Found
                && o.Discovery.TargetConnection.Status == ConnectionStatus.Succeeded
                && o.Discovery.Metadata.Status == MetadataStatus.Sufficient
                && o.Discovery.ObservedIdentity.Identity is { } id && id.DatabaseName == plan.DatabaseName
                && id.ServerInstance.Equals(plan.ServerInstance, StringComparison.OrdinalIgnoreCase)
                && o.Snapshot.SchemaCoverage == SchemaCoverage.Complete
                && o.SchemaHash == SchemaCanonicalizer.Canonicalize(o.Snapshot).Sha256, "NEW_EF_OBSERVATION_INVALID");
            return o;
        }
        async Task Apply(string phase, byte[] bytes, NewEfObservation expected)
        {
            NewEfContract.Verify(plan, trustedHash, up, down, DateTimeOffset.UtcNow);
            Record(phase, "STARTED", hash: Hashing.Sha256(bytes));
            await runtime.ExecuteAsync(plan, bytes, phase == "DOWN" ? "DOWN" : "UP", expected, deadline.Token);
            Record(phase, "APPLIED", hash: Hashing.Sha256(bytes));
        }
        try
        {
            var pre = await Observe();
            Empty(pre, HistoryStatus.Absent); Record("PRE", "CAPTURED", pre);
            await runtime.VerifyClassificationAsync(plan, deadline.Token);
            Record("NEW_EF", "CLASSIFIED");
            await Apply("UP1", up, pre);
            var post1 = await Observe(); Post(post1, plan); Record("POST1", "CAPTURED", post1);
            await Apply("DOWN", down, post1);
            var pre2 = await Observe(); Empty(pre2, HistoryStatus.Empty); Record("PRE2", "CAPTURED", pre2);
            NewEfContract.Require(pre.BusinessSchemaHash == pre2.BusinessSchemaHash
                && pre.BusinessSecurityHash == pre2.BusinessSecurityHash
                && post1.HistorySchemaHash == pre2.HistorySchemaHash
                && post1.HistorySecurityHash == pre2.HistorySecurityHash, "NEW_EF_RECOVERY_MISMATCH");
            recovery = true;
            await Apply("UP2", up, pre2);
            var post2 = await Observe(); Post(post2, plan); Record("POST2", "CAPTURED", post2);
            NewEfContract.Require(post1.SchemaHash == post2.SchemaHash
                && post1.BusinessSecurityHash == post2.BusinessSecurityHash
                && post1.HistorySecurityHash == post2.HistorySecurityHash, "NEW_EF_REAPPLY_MISMATCH");
            reapply = true;
            return Finish("NEW_EF_REHEARSAL_COMPLETE_NOT_CERTIFIED", null);
        }
        catch (Exception e)
        {
            var reason = e is LegacyContractException c ? c.Code : e is OperationCanceledException
                ? "NEW_EF_CANCELLED_OR_TIMEOUT" : "NEW_EF_EXECUTION_UNCERTAIN";
            Record("STOP", "BLOCKED");
            return Finish("BLOCKED", reason);
        }
        NewEfCycleReceipt Finish(string status, string? reason)
        {
            var receipt = new NewEfCycleReceipt("NEW_EF_CREATION_FIRST_RECEIPT", status, plan.PlanHash,
                creation.ReceiptHash, plan.RecoveryStrategy, recovery, reapply, phases, reason, "");
            return receipt with { ReceiptHash = NewEfContract.Hash(receipt) };
        }
    }
    private static void Empty(NewEfObservation o, HistoryStatus history) => NewEfContract.Require(
        EmptyForNewEfV1.Valid(o.Discovery.Physical) && o.Discovery.Physical.BusinessObjectCount == 0
        && o.Discovery.Physical.TechnicalObjectCount == 0 && o.Discovery.History.Status == history
        && o.Discovery.History.MigrationIds.Count == 0 && o.DataEmpty, "NEW_EF_EMPTY_OR_HISTORY_REQUIRED");
    private static void Post(NewEfObservation o, NewEfPlanV1 plan) => NewEfContract.Require(
        EmptyForNewEfV1.Valid(o.Discovery.Physical) && o.Discovery.Physical.BusinessObjectCount > 0
        && o.Discovery.Physical.TechnicalObjectCount == 0 && o.Discovery.History.Status == HistoryStatus.Present
        && o.Discovery.History.MigrationIds.SequenceEqual(plan.MigrationIds)
        && o.DataEmpty && o.HistoryProductVersionValid, "NEW_EF_POST_INVALID");
}
