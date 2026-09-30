using System.Text;
using System.Text.Json;
using DatabaseReleaseQualification;

public static class LegacySafetyTests
{
    public static (string Name, Func<Task> Run)[] Cases = [
        ("legacy safety permite SELECT local demostrado", LocalSelect),
        ("legacy safety permite CTE local acotada", LocalCte),
        ("legacy safety bloquea CTE con fuente externa", ExternalCte),
        ("legacy safety bloquea CTE recursiva", RecursiveCte),
        ("legacy safety permite derived table local", LocalDerived),
        ("legacy safety incluye ambas ramas IF", ConditionalBranches),
        ("legacy safety exige THROW en CATCH", TryCatchGuard),
        ("legacy safety bloquea SELECT INTO sin ausencia demostrada", SelectIntoAbsent),
        ("legacy safety bloquea DDL nuevo sin ausencia demostrada", NewDdlAbsent),
        ("legacy safety permite CREATE y DROP con ausencia visible", NewDdlProvedAbsent),
        ("legacy safety permite SELECT INTO con ausencia y fuente local", SelectIntoProvedAbsent),
        ("legacy safety bloquea colisión al crear objeto", NewDdlCollision),
        ("legacy SQL metadata acredita ausencia visible sin permiso de objeto", SqlVisibleAbsence),
        ("legacy safety bloquea derived table externa", ExternalDerived),
        ("legacy safety bloquea SELECT desde view", ViewRead),
        ("legacy safety permite DML local sin efectos indirectos", LocalDml),
        ("legacy safety permite GRANT acotado", BoundedGrant),
        ("legacy safety bloquea permiso incompatible con tabla", IncompatiblePermission),
        ("legacy safety bloquea EXEC", DynamicExecution),
        ("legacy safety bloquea transacciones del autor", Transactions),
        ("legacy safety bloquea cross database", CrossDatabase),
        ("legacy safety bloquea fuentes externas dentro de DML", ExternalDmlSources),
        ("legacy safety bloquea trigger alcanzable", Trigger),
        ("legacy safety bloquea synonym y FK cascade", SynonymAndCascade),
        ("legacy safety bloquea RLS y dependencia externa", RlsAndDependency),
        ("legacy safety bloquea SQLCMD y USE", SqlcmdAndUse),
        ("legacy safety bloquea expansión aun dentro de string", Expansion),
        ("legacy safety bloquea BEGIN END vacío", EmptyBlock),
        ("legacy safety bloquea GO con count", GoCount),
        ("legacy safety permite GO simple y no expande comentario", SimpleGo),
        ("legacy analyzer incluye OUTPUT INTO como segundo target DATA", OutputInto),
        ("legacy catalog expressions rechazan UDF y aceptan built-in", CatalogExpressions),
        ("legacy recovery DATA incluye pérdida rollback-only", RollbackOnlyLoss)
    ];

    private static readonly SchemaSnapshot Snapshot = new();

    private static async Task LocalSelect()
    {
        var result = await Evaluate("SELECT C1 FROM dbo.Widget;", "SELECT C1 FROM dbo.Widget;",
            new FakeSource());
        if (result.State != "PASS" || result.ScopeSafetyHash is null) throw new Exception("Expected PASS");
    }

    private static async Task LocalCte()
    {
        var result = await Evaluate(
            "WITH LocalRows AS (SELECT C1 FROM dbo.Widget) SELECT C1 FROM LocalRows;",
            "SELECT C1 FROM dbo.Widget;", new FakeSource());
        if (result.State != "PASS")
            throw new Exception("Local CTE blocked: " + string.Join(",", result.ReasonCodes));
    }

    private static async Task ExternalCte()
    {
        Blocked(await Evaluate(
            "WITH LocalRows AS (SELECT C1 FROM OtherDb.dbo.Widget) SELECT C1 FROM LocalRows;",
            "SELECT 1;", new FakeSource()));
    }

    private static async Task RecursiveCte()
    {
        Blocked(await Evaluate(
            "WITH RecursiveRows AS (SELECT C1 FROM dbo.Widget UNION ALL SELECT C1 FROM RecursiveRows) SELECT C1 FROM RecursiveRows;",
            "SELECT 1;", new FakeSource()));
    }

    private static async Task LocalDerived()
    {
        var result = await Evaluate(
            "SELECT d.C1 FROM (SELECT C1 FROM dbo.Widget) AS d;",
            "SELECT 1;", new FakeSource());
        if (result.State != "PASS")
            throw new Exception("Local derived table blocked: " + string.Join(",", result.ReasonCodes));
    }

    private static async Task ConditionalBranches()
    {
        var result = await Evaluate(
            "IF 1=1 BEGIN UPDATE dbo.Widget SET C1=2; END ELSE BEGIN DELETE FROM dbo.Widget; END",
            "SELECT 1;", new FakeSource());
        if (result.State != "PASS"
            || !result.ForwardAnalysis.Operations.Any(x => x.Operation == "UPDATE_DATA")
            || !result.ForwardAnalysis.Operations.Any(x => x.Operation == "DELETE_DATA"))
            throw new Exception("IF branches were not both analyzed");
    }

    private static async Task TryCatchGuard()
    {
        Blocked(await Evaluate(
            "BEGIN TRY UPDATE dbo.Widget SET C1=2; END TRY BEGIN CATCH PRINT 'ignored'; END CATCH",
            "SELECT 1;", new FakeSource()));
    }

    private static async Task SelectIntoAbsent()
    {
        var result = await Evaluate("SELECT C1 INTO dbo.NewTable FROM dbo.Widget;",
            "DROP TABLE dbo.NewTable;", new AbsentSource());
        if (result.State != "BLOCKED"
            || !result.ForwardAnalysis.Operations.Any(x => x.Operation == "SELECT_INTO")
            || !result.ReasonCodes.Contains("SECURABLE_ABSENCE_UNPROVEN"))
            throw new Exception("SELECT INTO without absence proof was accepted");
    }

    private static async Task NewDdlAbsent()
    {
        var result = await Evaluate("CREATE TABLE dbo.NewTable(C1 int);",
            "DROP TABLE dbo.NewTable;", new AbsentSource());
        if (result.State != "BLOCKED"
            || !result.ReasonCodes.Contains("SECURABLE_ABSENCE_UNPROVEN"))
            throw new Exception("New DDL without absence proof was accepted");
    }

    private static async Task NewDdlProvedAbsent()
    {
        var result = await Evaluate("CREATE TABLE dbo.NewTable(C1 int);",
            "DROP TABLE dbo.NewTable;", new FakeSource(newObjectAbsent: true));
        if (result.State != "PASS")
            throw new Exception("Visible absence did not permit bounded CREATE/DROP: "
                + string.Join(",", result.ReasonCodes));
    }

    private static async Task SelectIntoProvedAbsent()
    {
        var result = await Evaluate("SELECT C1 INTO dbo.NewTable FROM dbo.Widget;",
            "DROP TABLE dbo.NewTable;", new FakeSource(newObjectAbsent: true));
        if (result.State != "PASS" || !result.Impact.DataRequired)
            throw new Exception("Bounded SELECT INTO lost DATA requirement: "
                + string.Join(",", result.ReasonCodes));
    }

    private static async Task NewDdlCollision()
    {
        var result = await Evaluate("CREATE TABLE dbo.NewTable(C1 int);",
            "DROP TABLE dbo.NewTable;", new FakeSource());
        if (result.State != "BLOCKED"
            || !result.ReasonCodes.Contains("SECURABLE_COLLISION"))
            throw new Exception("CREATE collision was accepted");
    }

    private static async Task SqlVisibleAbsence()
    {
        var source = new SqlLegacyScopeSafetySource(new AbsentCatalogTransport(),
            new SecurityTargetBindingV1("TestDb", "ALLOW_LIST", ["SQL1"]));
        var result = await source.CaptureAsync(
            [new RecoverySecuritySecurable("OBJECT", "dbo", "NewTable")], default);
        if (result.Objects.Single().Kind != "ABSENT" || !result.Complete)
            throw new Exception("Visible catalog absence was not captured");
    }

    private static async Task ExternalDerived()
    {
        Blocked(await Evaluate(
            "SELECT d.C1 FROM (SELECT C1 FROM OtherDb.dbo.Widget) AS d;",
            "SELECT 1;", new FakeSource()));
    }

    private static async Task ViewRead()
    {
        var result = await Evaluate("SELECT C1 FROM dbo.Widget;", "SELECT C1 FROM dbo.Widget;",
            new FakeSource(kind: "VIEW"));
        Blocked(result);
    }

    private static async Task LocalDml()
    {
        var result = await Evaluate("UPDATE dbo.Widget SET C1=2;",
            "UPDATE dbo.Widget SET C1=1;", new FakeSource());
        if (result.State != "PASS" || !result.Impact.DataRequired)
            throw new Exception("Local DML not admitted with DATA requirement");
    }

    private static async Task BoundedGrant()
    {
        var result = await Evaluate("GRANT SELECT ON OBJECT::dbo.Widget TO reader;",
            "REVOKE SELECT ON OBJECT::dbo.Widget FROM reader;", new FakeSource());
        if (result.State != "PASS" || !result.Impact.SecurityRequired)
            throw new Exception("Bounded security SQL not admitted");
    }

    private static async Task IncompatiblePermission()
    {
        var result = await Evaluate("GRANT EXECUTE ON OBJECT::dbo.Widget TO reader;",
            "REVOKE EXECUTE ON OBJECT::dbo.Widget FROM reader;", new FakeSource());
        if (result.State != "BLOCKED"
            || !result.ReasonCodes.Contains("SQL_SECURITY_UNSUPPORTED"))
            throw new Exception("EXECUTE on table was accepted");
    }

    private static async Task DynamicExecution()
    {
        Blocked(await Evaluate("EXEC(N'SELECT 1');", "SELECT 1;", new FakeSource()));
    }

    private static async Task Transactions()
    {
        Blocked(await Evaluate("BEGIN TRAN; SELECT 1; COMMIT;", "SELECT 1;", new FakeSource()));
    }

    private static async Task CrossDatabase()
    {
        Blocked(await Evaluate("SELECT C1 FROM OtherDb.dbo.Widget;", "SELECT 1;", new FakeSource()));
    }

    private static async Task ExternalDmlSources()
    {
        Blocked(await Evaluate(
            "INSERT INTO dbo.Widget(C1) SELECT C1 FROM OtherDb.dbo.Source;",
            "DELETE FROM dbo.Widget;", new FakeSource()));
        Blocked(await Evaluate(
            "MERGE dbo.Widget AS t USING OtherDb.dbo.Source AS s ON t.C1=s.C1 WHEN MATCHED THEN UPDATE SET C1=s.C1;",
            "SELECT 1;", new FakeSource()));
        Blocked(await Evaluate(
            "UPDATE t SET C1=s.C1 FROM dbo.Widget AS t JOIN OtherDb.dbo.Source AS s ON t.C1=s.C1;",
            "SELECT 1;", new FakeSource()));
        Blocked(await Evaluate(
            "DELETE t FROM dbo.Widget AS t JOIN OtherDb.dbo.Source AS s ON t.C1=s.C1;",
            "SELECT 1;", new FakeSource()));
    }

    private static async Task Trigger()
    {
        Blocked(await Evaluate("UPDATE dbo.Widget SET C1=2;", "UPDATE dbo.Widget SET C1=1;",
            new FakeSource(trigger: true)));
    }

    private static async Task SynonymAndCascade()
    {
        Blocked(await Evaluate("SELECT C1 FROM dbo.Widget;", "SELECT 1;",
            new FakeSource(synonym: true)));
        Blocked(await Evaluate("DELETE FROM dbo.Widget;", "INSERT INTO dbo.Widget(C1) VALUES(1);",
            new FakeSource(cascade: true)));
    }

    private static async Task RlsAndDependency()
    {
        Blocked(await Evaluate("SELECT C1 FROM dbo.Widget;", "SELECT 1;",
            new FakeSource(rls: true)));
        Blocked(await Evaluate("SELECT C1 FROM dbo.Widget;", "SELECT 1;",
            new FakeSource(dependency: true)));
    }

    private static async Task SqlcmdAndUse()
    {
        Blocked(await Evaluate(":r external.sql", "SELECT 1;", new FakeSource()));
        Blocked(await Evaluate("USE OtherDb; SELECT 1;", "SELECT 1;", new FakeSource()));
    }

    private static async Task Expansion()
    {
        Blocked(await Evaluate("SELECT '$(outside)';", "SELECT 1;", new FakeSource()));
    }

    private static async Task EmptyBlock()
    {
        Blocked(await Evaluate("BEGIN END", "SELECT 1;", new FakeSource()));
    }

    private static async Task GoCount()
    {
        Blocked(await Evaluate("SELECT 1;\nGO 2\n", "SELECT 2;", new FakeSource()));
    }

    private static async Task SimpleGo()
    {
        var result = await Evaluate("SELECT 1;\nGO\nSELECT 2; -- $(ignored)\n",
            "SELECT 3;", new FakeSource());
        if (result.State != "PASS") throw new Exception("Simple GO blocked: " +
            string.Join(",", result.ReasonCodes));
    }

    private static Task OutputInto()
    {
        var analysis = new SqlScriptAnalyzer().Analyze("forward",
            "UPDATE dbo.Widget SET C1=2 OUTPUT INSERTED.C1 INTO dbo.Audit(C1);", Snapshot);
        if (!analysis.Operations.Any(x => x.Operation == "OUTPUT_INTO"
            && x.Schema == "dbo" && x.Object == "Audit" && x.IsDataMutation))
            throw new Exception("OUTPUT INTO target missing");
        return Task.CompletedTask;
    }

    private static Task CatalogExpressions()
    {
        if (!LegacyStaticSafety.CatalogExpressionSafe("((0))", false)
            || !LegacyStaticSafety.CatalogExpressionSafe("([C1]>(0))", true)
            || LegacyStaticSafety.CatalogExpressionSafe("(dbo.HiddenFunction([C1]))", false)
            || LegacyStaticSafety.CatalogExpressionSafe(null, false))
            throw new Exception("Catalog expression proof incorrect");
        return Task.CompletedTask;
    }

    private static Task RollbackOnlyLoss()
    {
        var forward = new ScriptAnalysis { ScriptRole = "forward" };
        var rollback = new ScriptAnalysis { ScriptRole = "rollback" };
        rollback.Operations.Add(new ScriptOperation {
            Operation = "DROP_COLUMN", AstNodeType = "AlterTableDropTableElementStatement",
            Schema = "dbo", Object = "Widget", HasPotentialDataLoss = true,
            IsSchemaMutation = true
        });
        if (!RecoveryImpact.Derive(forward, rollback).DataRequired)
            throw new Exception("Rollback loss omitted");
        return Task.CompletedTask;
    }

    private static async Task<LegacyStaticSafetyEvidenceV1> Evaluate(
        string forwardSql, string rollbackSql, ILegacyScopeSafetySource source)
    {
        var manifestBytes = Encoding.UTF8.GetBytes("{\"contractVersion\":1,\"releaseId\":\"r\",\"targetId\":\"t\",\"forwardPath\":\"f.sql\",\"rollbackPath\":\"r.sql\",\"changeOrigin\":\"APPLICATION\"}");
        var forwardBytes = Encoding.UTF8.GetBytes(forwardSql);
        var rollbackBytes = Encoding.UTF8.GetBytes(rollbackSql);
        var evidence = new LegacyArtifactValidV1(1, "VALID",
            new("github.com", "1", "team/repo"), "sha1", new string('a', 40), "t", "r",
            new("m.json", new string('b', 40), manifestBytes.Length, Hashing.Sha256(manifestBytes)),
            new("f.sql", new string('c', 40), forwardBytes.Length, Hashing.Sha256(forwardBytes)),
            new("r.sql", new string('d', 40), rollbackBytes.Length, Hashing.Sha256(rollbackBytes)),
            "", []);
        evidence = evidence with { PackageIdentity = LegacyPackageIdentity.Calculate(evidence) };
        var parsed = LegacyManifest.Parse(manifestBytes, "m.json", "t");
        return await new LegacyStaticSafety().EvaluateAsync(
            new LegacyFrozenPackage(evidence, parsed, manifestBytes, forwardBytes, rollbackBytes),
            Snapshot, source);
    }

    private static void Blocked(LegacyStaticSafetyEvidenceV1 result)
    {
        if (result.State != "BLOCKED" || result.ReasonCodes.Count == 0)
            throw new Exception("Expected BLOCKED");
    }

    private sealed class FakeSource(string kind = "TABLE", bool trigger = false,
        bool synonym = false, bool cascade = false, bool rls = false,
        bool dependency = false, bool newObjectAbsent = false) : ILegacyScopeSafetySource
    {
        public Task<LegacyScopeSafetySnapshotV1> CaptureAsync(
            IReadOnlyList<RecoverySecuritySecurable> scope, CancellationToken token)
        {
            var objects = scope.Select(x => new LegacySafetyObject(x.Schema, x.Name,
                newObjectAbsent && x.Name == "NewTable" ? "ABSENT" : kind,
                false, false, synonym, trigger, trigger, cascade, rls, dependency, false)).ToArray();
            var snapshot = new LegacyScopeSafetySnapshotV1(1, true, "SQL1", "TestDb",
                true, true, false, objects, "");
            var hash = Hashing.Sha256(JsonSerializer.Serialize(new {
                snapshot.ContractVersion, snapshot.Complete, snapshot.ServerInstance,
                snapshot.DatabaseName, snapshot.DatabaseDdlTriggersComplete,
                snapshot.ServerDdlTriggersComplete, snapshot.HasEnabledDdlTrigger,
                snapshot.Objects
            }, JsonDefaults.Compact));
            return Task.FromResult(snapshot with { Sha256 = hash });
        }
    }

    private sealed class AbsentSource : ILegacyScopeSafetySource
    {
        public Task<LegacyScopeSafetySnapshotV1> CaptureAsync(
            IReadOnlyList<RecoverySecuritySecurable> scope, CancellationToken token) =>
            throw new LegacyContractException("SECURABLE_ABSENCE_UNPROVEN");
    }

    private sealed class AbsentCatalogTransport : ISecurityCatalogTransport
    {
        public Task<ISecurityCatalogSession> OpenAsync(CancellationToken token) =>
            Task.FromResult<ISecurityCatalogSession>(new Session());

        private sealed class Session : ISecurityCatalogSession
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
                string sql, CancellationToken token)
            {
                static IReadOnlyDictionary<string, object?> Row(
                    params (string Key, object? Value)[] fields) =>
                    fields.ToDictionary(x => x.Key, x => x.Value);
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
                    sql.Contains("SERVERPROPERTY('ServerName')", StringComparison.Ordinal)
                        ? [Row(("serverName", "SQL1"), ("databaseName", "TestDb"),
                            ("majorVersion", 16), ("engineEdition", 3))]
                    : sql.Contains("AS databaseView", StringComparison.Ordinal)
                        ? [Row(("databaseView", 1), ("serverView", 1))]
                    : sql.Contains("AS permitted", StringComparison.Ordinal)
                        ? [Row(("permitted", 1))]
                    : sql.Contains("FROM sys.objects", StringComparison.Ordinal)
                        ? []
                    : sql.Contains("FROM sys.triggers", StringComparison.Ordinal)
                        || sql.Contains("FROM sys.server_triggers", StringComparison.Ordinal)
                        ? []
                    : throw new Exception("Unexpected catalog query");
                return Task.FromResult(rows);
            }
        }
    }
}
