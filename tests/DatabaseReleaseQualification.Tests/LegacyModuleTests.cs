using System.Text;
using DatabaseReleaseQualification;

internal static class LegacyModuleTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases = [
        ("legacy modules analyzer opt-in preserves default EF rejection", ProfileIsolation),
        ("legacy modules schema captures definitions and persisted properties", CaptureProperties),
        ("legacy modules bounded positive SQL families", PositiveFamilies),
        ("legacy modules unsafe definitions remain blocked", NegativeFamilies),
        ("legacy module qualification and phased recovery connected", PhasedRecovery),
        ("legacy function and view qualification and phased recovery connected", FunctionViewRecovery),
        ("legacy mixed module DDL DML and permission recovery connected", MixedRecovery),
        ("legacy view capture and positive families require persisted properties", ViewFamilies),
        ("legacy absent object security requires stable visible proof", AbsentSecurity),
        ("legacy qualification blocks disappearing DATA rowsets", DataLifecycle),
        ("legacy actual executor blocks packages with no mutation intent", NoMutation),
        .. LegacyAdoptionTests.Cases,
        .. LegacyExecutionFenceTests.Cases
    ];
    private static void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static LegacyModuleDefinitionV1 Definition(string sql, string type = "P") =>
        new("dbo", "M", type, sql, "true", "true", "false", "false", "false", "false", "CALLER", "false");
    private static SchemaSnapshot Snapshot(string sql = "CREATE PROCEDURE dbo.M AS SELECT 1;", string type = "P")
    {
        var snapshot = new SchemaSnapshot();
        LegacyModuleSchema.Add(snapshot, Definition(sql, type));
        return snapshot;
    }
    private static Task ProfileIsolation()
    {
        Assert(Activator.CreateInstance<SqlScriptAnalyzer>() is not null
            && Activator.CreateInstance<SqlServerSchemaReader>() is not null, "Default constructor ABI changed");
        foreach (var sql in new[] { "CREATE PROCEDURE dbo.M AS SELECT 1;",
            "ALTER FUNCTION dbo.M() RETURNS int AS BEGIN RETURN 1; END;" })
        {
            var original = new SqlScriptAnalyzer().Analyze("forward", sql, new());
            var explicitDefault = new SqlScriptAnalyzer(LegacySqlProfile.Default).Analyze("forward", sql, new());
            var legacy = new SqlScriptAnalyzer(LegacySqlProfile.ModulesV1).Analyze("forward", sql, new());
            Assert(LegacyRuntimeEvidenceHash.Hash(original) == LegacyRuntimeEvidenceHash.Hash(explicitDefault), "Default analysis changed");
            Assert(original.Confidence != AnalysisConfidence.Complete && legacy.Confidence == AnalysisConfidence.Complete,
                "Module support escaped opt-in or was not enabled");
            Assert(legacy.Operations.Count == 1 && !legacy.HasDataMutations, "Definition was treated as execution");
        }
        Assert(SqlServerSchemaReader.UnsupportedForProfile(LegacySqlProfile.Default).Contains("N'D', N'TR')"), "EF unsupported catalog changed");
        Assert(SqlServerSchemaReader.UnsupportedForProfile(LegacySqlProfile.ModulesV1).Contains("N'FN', N'IF', N'TF')"), "Legacy capture not opted in");
        SqlServerSchemaReader.EnsureSelectOnlySql(SqlServerSchemaReader.LegacyModulesSql);
        return Task.CompletedTask;
    }
    private static Task CaptureProperties()
    {
        var original = Definition("CREATE PROCEDURE dbo.M AS SELECT 1;");
        string Hash(LegacyModuleDefinitionV1 value) { var s = new SchemaSnapshot(); LegacyModuleSchema.Add(s, value); return SchemaCanonicalizer.Canonicalize(s).Sha256; }
        foreach (var changed in new[] { original with { Definition = "CREATE PROCEDURE dbo.M AS SELECT 2;" },
            original with { AnsiNulls = "false" }, original with { QuotedIdentifier = "false" },
            original with { SchemaBound = "true" }, original with { ExecuteAs = "OWNER" },
            original with { NullOnNullInput = "true" }, original with { Recompiled = "true" },
            original with { InlineType = "true" }, original with { Inlineable = "true" } })
            Assert(Hash(original) != Hash(changed), "Module property omitted from structural hash");
        foreach (var bad in new[] { original with { Definition = "" }, original with { NativeOrSigned = "true" },
            original with { AnsiNulls = "" }, original with { ExecuteAs = "UNRESOLVED" } })
        {
            var s = new SchemaSnapshot(); LegacyModuleSchema.Add(s, bad);
            Assert(s.UnsupportedSchemaFeatures.Count != 0, "Incomplete/signed module accepted");
        }
        return Task.CompletedTask;
    }
    private static async Task<LegacyStaticSafetyEvidenceV1> Safety(string forward, string rollback, string kind, bool absent = false)
    {
        var package = await new LegacyArtifactDiscovery(LegacyArtifactTests.Transport(
            Encoding.UTF8.GetBytes(forward), Encoding.UTF8.GetBytes(rollback)))
            .DiscoverAsync(new(1, "db/releases/m.json", new string('a', 40), new("github.com", "1234", "team/repo")), "target1");
        return await new LegacyStaticSafety().EvaluateAsync(package,
            absent ? new() : Snapshot(type: kind == "FUNCTION" ? "FN" : "P"), new Scope(kind, absent));
    }
    private static async Task PositiveFamilies()
    {
        foreach (var pair in new[] {
            ("CREATE PROCEDURE dbo.M AS SELECT 1;", "DROP PROCEDURE dbo.M;", "PROCEDURE", true),
            ("ALTER PROCEDURE dbo.M AS SELECT 2;", "ALTER PROCEDURE dbo.M AS SELECT 1;", "PROCEDURE", false),
            ("CREATE OR ALTER PROCEDURE dbo.M AS SELECT 2;", "ALTER PROCEDURE dbo.M AS SELECT 1;", "PROCEDURE", false),
            ("CREATE FUNCTION dbo.M() RETURNS int AS BEGIN RETURN 1; END;", "DROP FUNCTION dbo.M;", "FUNCTION", true),
            ("CREATE FUNCTION dbo.M() RETURNS TABLE AS RETURN (SELECT 1 AS Value);", "DROP FUNCTION dbo.M;", "FUNCTION", true),
            ("ALTER FUNCTION dbo.M() RETURNS int AS BEGIN RETURN 2; END;", "ALTER FUNCTION dbo.M() RETURNS int AS BEGIN RETURN 1; END;", "FUNCTION", false),
            ("ALTER PROCEDURE dbo.M AS SELECT 2;\nGO\nGRANT EXECUTE ON OBJECT::dbo.M TO reader;", "REVOKE EXECUTE ON OBJECT::dbo.M FROM reader;\nGO\nALTER PROCEDURE dbo.M AS SELECT 1;", "PROCEDURE", false)
        })
        {
            var result = await Safety(pair.Item1, pair.Item2, pair.Item3, pair.Item4);
            Assert(result.State == "PASS" && result.Impact.SecurityRequired && !result.Impact.DataRequired,
                pair.Item1 + ": " + string.Join(",", result.ReasonCodes));
        }
    }
    private static async Task NegativeFamilies()
    {
        foreach (var sql in new[] {
            "ALTER PROCEDURE dbo.M WITH ENCRYPTION AS SELECT 1;",
            "ALTER PROCEDURE dbo.M WITH EXECUTE AS OWNER AS SELECT 1;",
            "ALTER PROCEDURE dbo.M AS EXEC(N'SELECT 1');",
            "ALTER PROCEDURE dbo.M AS SELECT * FROM OtherDb.dbo.T;",
            "ALTER PROCEDURE dbo.M AS BEGIN TRAN; SELECT 1; COMMIT;",
            "ALTER PROCEDURE dbo.M AS SELECT dbo.Hidden();",
            "ALTER PROCEDURE dbo.M AS CREATE TABLE dbo.Hidden(C int);",
            "ALTER PROCEDURE dbo.M AS GRANT CONTROL TO reader;"
        }) Assert((await Safety(sql, "ALTER PROCEDURE dbo.M AS SELECT 1;", "PROCEDURE")).State == "BLOCKED", "Unsafe module admitted: " + sql);
        Assert((await Safety("ALTER PROCEDURE dbo.M AS SELECT 2;", "ALTER PROCEDURE dbo.M AS SELECT 1;", "TABLE")).State == "BLOCKED", "Object kind mismatch accepted");
    }
    private sealed class Scope(string kind, bool absent, string fault = "") : ILegacyScopeSafetySource
    {
        private int captures;
        public Task<LegacyScopeSafetySnapshotV1> CaptureAsync(IReadOnlyList<RecoverySecuritySecurable> scope, CancellationToken token)
        {
            captures++;
            var s = new LegacyScopeSafetySnapshotV1(1, fault != "partial", "SQL1", "TestDb", true, true, false,
                scope.Select(x => new LegacySafetyObject(x.Schema, x.Name, absent && !(fault == "changed" && captures > 1) ? "ABSENT" : kind, false, false, false, false, false, false, false, false, false)).ToArray(), "");
            return Task.FromResult(s with
            {
                Sha256 = LegacyRuntimeEvidenceHash.Hash(new
                {
                    s.ContractVersion,
                    s.Complete,
                    s.ServerInstance,
                    s.DatabaseName,
                    s.DatabaseDdlTriggersComplete,
                    s.ServerDdlTriggersComplete,
                    s.HasEnabledDdlTrigger,
                    s.Objects
                })
            });
        }
    }
    private static async Task AbsentSecurity()
    {
        var scope = new RecoverySecurityScope([new("OBJECT", "dbo", "M")], []);
        foreach (var fault in new[] { "", "partial", "changed" })
        {
            var reader = new LegacyRecoverySecurityReader(new SecurityReader(), new Scope("PROCEDURE", true, fault));
            try
            {
                var result = await reader.ReadAsync(scope, RecoveryPhase.Pre);
                Assert(fault == "", "Unproved absence was accepted");
                Assert(!result.Securables.Single().Exists && result.Securables.Single().SchemaOwner == "dbo"
                    && RecoverySecurityCanonicalizer.Canonicalize(scope, RecoveryPhase.Pre, result) is not null,
                    "Absence lacks schema ownership coverage");
            }
            catch (LegacyContractException) when (fault != "") { }
        }
    }
    private static async Task DataLifecycle()
    {
        var result = await LegacyReadinessTests.SyntheticQualification(
            Encoding.UTF8.GetBytes("DROP TABLE dbo.M;"), Encoding.UTF8.GetBytes("CREATE TABLE dbo.M(C int);"),
            new SchemaSnapshot { Objects = [new SchemaObject { Kind = "table", Schema = "dbo", Name = "M" }] });
        Assert(result.Readiness?.Handoff is null
            && result.Readiness?.ReasonCodes.Contains("DATA_OBJECT_LIFECYCLE_UNSUPPORTED") == true,
            "A disappearing table reached rehearsal without a supported DATA lifecycle");
    }
    private static async Task NoMutation()
    {
        var q = await LegacyReadinessTests.SyntheticQualification();
        try { SqlLegacyRehearsalRuntimeV1.RequireEffectiveChange(q.StaticSafety!); throw new Exception("Read-only package accepted as a mutation"); }
        catch (LegacyContractException e) when (e.Code == "NO_EFFECTIVE_CHANGE_NO_SQL_WRITE") { }
        var module = await ModuleCycle(1, 2);
        SqlLegacyRehearsalRuntimeV1.RequireEffectiveChange(module.Qualification.StaticSafety!);
    }
    private sealed class SecurityReader : IRecoverySecurityCatalogReader
    {
        public Task<RecoverySecuritySnapshot> ReadAsync(RecoverySecurityScope scope, RecoveryPhase phase, CancellationToken cancellationToken = default) =>
            Task.FromResult(RecoveryCoverageTests.Security(scope, phase, new()) with { ServerInstance = "SQL1", DatabaseName = "TestDb" });
    }
    private static async Task PhasedRecovery()
    {
        await ModuleCycle(1, 2);
    }
    private static async Task FunctionViewRecovery()
    { await ModuleCycle(1, 2, "FUNCTION"); await ModuleCycle(1, 2, "VIEW"); }
    private static async Task MixedRecovery() { await ModuleCycle(1, 2, "MIXED"); }
    private static SchemaSnapshot View(string sql)
    {
        var s = new SchemaSnapshot
        {
            Objects = [new SchemaObject { Kind="view",Schema="dbo",Name="M",
            Properties=new(StringComparer.Ordinal) { ["definitionSha256"]=Hashing.Sha256(sql),["schemaBound"]="false" } }]
        };
        LegacySchemaCapture.Enrich(s, [new("dbo", "M", true, true)]); return s;
    }
    private static async Task ViewFamilies()
    {
        SqlServerSchemaReader.EnsureSelectOnlySql(LegacySchemaCapture.ViewPropertiesSql);
        foreach (var forward in new[] { "ALTER VIEW dbo.M AS SELECT 2 AS N;", "CREATE OR ALTER VIEW dbo.M AS SELECT 2 AS N;", "DROP VIEW dbo.M;" })
        {
            var rollback = forward.StartsWith("DROP") ? "CREATE VIEW dbo.M AS SELECT 1 AS N;" : "ALTER VIEW dbo.M AS SELECT 1 AS N;";
            var package = await new LegacyArtifactDiscovery(LegacyArtifactTests.Transport(Encoding.UTF8.GetBytes(forward), Encoding.UTF8.GetBytes(rollback)))
                .DiscoverAsync(new(1, "db/releases/m.json", new string('a', 40), new("github.com", "1234", "team/repo")), "target1");
            var before = View(rollback);
            var result = await new LegacyStaticSafety().EvaluateAsync(package, before, new Scope("VIEW", false));
            Assert(result.State == "PASS", string.Join(",", result.ReasonCodes));
            before.Objects[0].Properties["legacy-quotedidentifier"] = "false";
            Assert((await new LegacyStaticSafety().EvaluateAsync(package, before, new Scope("VIEW", false))).State == "BLOCKED", "View with unsupported session properties accepted");
        }
        var empty = new SchemaSnapshot { Objects = [new SchemaObject { Kind = "view", Schema = "dbo", Name = "M" }] };
        try { LegacySchemaCapture.Enrich(empty, []); throw new Exception("Missing view properties accepted"); }
        catch (LegacyContractException e) when (e.Code == "VIEW_PROPERTIES_UNVERIFIED") { }
    }
    internal static async Task<(LegacyQualificationOutcomeV1 Qualification, LegacyRehearsalReceiptV1 Receipt)> ModuleCycle(int initialValue, int nextValue, string family = "PROCEDURE")
    {
        string DefinitionSql(int value) => family switch
        {
            "FUNCTION" => $"ALTER FUNCTION dbo.M() RETURNS int AS BEGIN RETURN {value}; END;",
            "VIEW" => $"ALTER VIEW dbo.M AS SELECT {value} AS N;",
            _ => $"ALTER PROCEDURE dbo.M AS SELECT {value};"
        };
        var forward = DefinitionSql(nextValue);
        var rollback = DefinitionSql(initialValue);
        // Same definition form at PRE/PRE2: SQL stores exact module text.
        var before = family == "VIEW" ? View(rollback) : Snapshot(rollback, family == "FUNCTION" ? "FN" : "P");
        var after = family == "VIEW" ? View(forward) : Snapshot(forward, family == "FUNCTION" ? "FN" : "P");
        if (family == "MIXED")
        {
            foreach (var snapshot in new[] { before, after })
            {
                snapshot.Objects.Add(new SchemaObject { Kind = "table", Schema = "dbo", Name = "T" });
                snapshot.Objects.Add(new SchemaObject
                {
                    Kind = "column",
                    Schema = "dbo",
                    Parent = "T",
                    Name = "C",
                    Properties = new(StringComparer.Ordinal) { ["type"] = "int" }
                });
            }
            after.Objects.Add(new SchemaObject
            {
                Kind = "index",
                Schema = "dbo",
                Parent = "T",
                Name = "IX",
                Properties = new(StringComparer.Ordinal) { ["columns"] = "C", ["unique"] = "false" }
            });
            forward += "\nGO\nCREATE INDEX IX ON dbo.T(C);\nGO\nUPDATE dbo.T SET C = C + 1;\nGO\nGRANT EXECUTE ON OBJECT::dbo.M TO AppReader;";
            rollback = "REVOKE EXECUTE ON OBJECT::dbo.M FROM AppReader;\nGO\nUPDATE dbo.T SET C = C - 1;\nGO\nDROP INDEX IX ON dbo.T;\nGO\n" + rollback;
        }
        LegacyDataValidationDescriptorV1? descriptor = null;
        if (family == "MIXED")
        {
            var analyzer = new SqlScriptAnalyzer(LegacySqlProfile.ModulesV1);
            var (scope, hash) = LegacyReadinessHash.DataScope(analyzer.Analyze("forward", forward, before), analyzer.Analyze("rollback", rollback, before));
            var definition = new LegacyDataContractDefinitionV1(1, "mixed-rowset", 1, "target1", hash, "SYNTHETIC", "EXACT_CONTENT_HASH_V1",
                new(new("github.com", "1", "synthetic/contracts"), new string('a', 40), "synthetic/data.json", new string('b', 64)), scope);
            descriptor = new(definition.Selector, 1, hash, new LegacyDataValidationProviderV1(definition, new MixedDataReader()));
        }
        var q = await LegacyReadinessTests.SyntheticQualification(Encoding.UTF8.GetBytes(forward), Encoding.UTF8.GetBytes(rollback), before, descriptor, releaseId: "release-" + initialValue);
        Assert(q.Readiness?.Handoff is not null, "Module qualification blocked: " + string.Join(",", q.Readiness?.ReasonCodes ?? []));
        var runtime = new ModuleRuntime(q, before, after, "legacy_pre", before, mixed: family == "MIXED");
        var data = descriptor?.Provider as LegacyDataValidationProviderV1;
        var checkpoint = await new LegacyPhasedRehearsalV1(runtime, new Authority()).CapturePreAsync(q, data, default);
        var fence = new InMemoryLegacyExecutionFence();
        var handoff = q.Readiness!.Handoff!;
        foreach (var (phase, job, initial) in new[] { ("FORWARD1", "legacy_forward1", before), ("ROLLBACK", "legacy_rollback", after), ("FORWARD2", "legacy_forward2", before) })
        {
            var lease = new LegacyExecutionLeaseV1(fence, new(handoff.TargetId, handoff.Governance.Binding.EndpointReference,
                handoff.Governance.Binding.DatabaseName, "offline-incarnation", handoff.ReleaseId, handoff.PackageIdentity,
                q.TrustedRuntime!.Runtime!.RunId, phase, checkpoint.CheckpointHash));
            var result = await new LegacyPhasedRehearsalV1(new ModuleRuntime(q, before, after, job, initial, lease, family == "MIXED"), new Authority())
                .RunNextAsync(checkpoint, checkpoint.CheckpointHash, phase, q.Package!,
                    data is null ? null : new LegacyDataValidationProviderV1(data.Definition, new MixedDataReader()), default);
            if (result.Checkpoint is not null)
            { checkpoint = result.Checkpoint; lease.Confirm(checkpoint.CheckpointHash); }
            else
            {
                Assert(result.Receipt?.Evaluation?.RecoveryCoverage?.Complete == true, "Module recovery incomplete");
                Assert(result.Receipt!.Observations[RecoveryPhase.Post2].ObservedSchemaHash != q.ObservedSnapshot!.ObservedSchemaHash, "No effective module delta exercised");
                try { LegacyPromotionFreeze.CreateFromTrustedProducer(result.Receipt); throw new Exception("Synthetic module freeze accepted"); }
                catch (LegacyContractException e) when (e.Code == "PROMOTION_FREEZE_BLOCKED") { }
                lease.Confirm(result.Receipt.ReceiptHash);
                return (q, result.Receipt);
            }
        }
        throw new Exception("Module cycle did not reach final receipt");
    }
    private sealed class Authority : ILegacyRehearsalAuthorityV1
    { public Task VerifyAsync(LegacyHandoffV1 h, string phase, CancellationToken token) => Task.CompletedTask; }
    private sealed class MixedDataReader : ILegacyDataEvidenceReaderV1
    {
        public Task<LegacyDataPhaseEvidenceV1> CaptureAsync(LegacyDataContractDefinitionV1 d, RecoveryPhase phase, CancellationToken token) =>
            Task.FromResult(new LegacyDataPhaseEvidenceV1(1, d.Selector, d.Version, d.TargetId, d.ScopeHash, phase,
                DateTimeOffset.UtcNow, true, Hashing.Sha256(phase is RecoveryPhase.Pre or RecoveryPhase.Pre2 ? "full-rowset-C=1" : "full-rowset-C=2")));
    }
    private sealed class ModuleRuntime(LegacyQualificationOutcomeV1 q, SchemaSnapshot before, SchemaSnapshot after,
        string job, SchemaSnapshot initial, LegacyExecutionLeaseV1? lease = null, bool mixed = false) : ILegacyRehearsalRuntimeV1
    {
        private SchemaSnapshot current = initial;
        public string EvidenceKind => "SYNTHETIC";
        public LegacyRuntimeContextV1 Context => q.TrustedRuntime!.Runtime! with { JobId = job };
        public Task RevalidateAsync(LegacyHandoffV1 h, CancellationToken t) => Task.CompletedTask;
        public Task VerifyRollbackAgainstPost1Async(ReleaseScript script, ObservedCurrentSnapshotV1 observed, CancellationToken t) => Task.CompletedTask;
        public Task ApplyExactAsync(ReleaseScript script, string hash, string policy, ObservedCurrentSnapshotV1 pre, CancellationToken t)
        {
            Assert(script.Sha256 == hash, "Wrong exact bytes");
            lease?.Begin(q.Readiness!.Handoff!, Context, job switch { "legacy_forward1" => "FORWARD1", "legacy_rollback" => "ROLLBACK", _ => "FORWARD2" });
            current = script.Role == "rollback" ? before : after; return Task.CompletedTask;
        }
        public Task<ObservedCurrentSnapshotV1> CaptureAsync(CancellationToken t)
        {
            var original = q.ObservedSnapshot!; var hash = SchemaCanonicalizer.Canonicalize(current).Sha256;
            return Task.FromResult(original with
            {
                Snapshot = current,
                ObservedSchemaHash = hash,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                CaptureComparison = original.CaptureComparison with { FirstSchemaHash = hash, SecondSchemaHash = hash }
            });
        }
        public Task<RecoverySecuritySnapshot> CaptureSecurityAsync(RecoverySecurityScope scope, RecoveryPhase phase, CancellationToken cancellationToken = default)
        {
            var value = RecoveryCoverageTests.Security(scope, phase, current) with { ServerInstance = "SQL1", DatabaseName = "TestDb" };
            if (mixed && ReferenceEquals(current, after)) value = value with
            {
                Securables = value.Securables.Select(s =>
                s.Securable.Name == "M" ? s with { Permissions = [new("EXECUTE", "G", "AppReader", "dbo")] } : s).ToArray()
            };
            return Task.FromResult(value);
        }
    }
}
