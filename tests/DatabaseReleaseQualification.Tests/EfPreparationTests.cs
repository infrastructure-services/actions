using System.Text;
using System.Text.Json;
using DatabaseReleaseQualification;
using SqlDiscovery.V2;

internal static class EfPreparationTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static string Hash(string s) => Hashing.Sha256(s);
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Compact);
    private static void Assert(bool b, string why) { if (!b) throw new Exception(why); }
    private static T Copy<T>(T v) => JsonSerializer.Deserialize<T>(Bytes(v), JsonDefaults.Compact)!;
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF A2.1: exact EF1009 semantic analysis, recovery still unverified", RealSql),
        ("EF A2.1: direct DDL, PK, index and DML effects across schemas", Semantics),
        ("EF A2.1: unknown syntax, implicit resolution and indirect effects block", SemanticNegatives),
        ("EF A2.1: authorized fake producers compose isolated generic plans", Generalization),
        ("EF A2.1: missing/denied provider, changed bytes and duplicate fields block", ProviderNegatives),
        ("EF A2.1: divergent history, coverage and baseline mismatches block", EvidenceNegatives),
        ("EF A2.1: rehearsal cannot be the target, mismatched clone cannot qualify", Isolation),
        ("EF A2.1: restore and forward-fix evidence remain explicit dependencies", Recovery),
        ("EF A2.1: NO_CHANGES is evidenced and grants no write capability", NoChanges),
        ("EF A2.1: fresh PRE protects revision, endpoint, incarnation and drift", FreshPre),
        ("EF A2.1: actual POST is independent from qualified POST", ActualPost),
        ("EF A2.1: authorized transition candidate supports successive release", SuccessiveRelease),
        ("EF A2.1: initial NEW bootstrap contract remains isolated", New)
    ];

    internal sealed class Provider(EfEvidenceScopeV1 expected) : IEfAuthorizedEvidenceProviderV1
    {
        internal readonly Dictionary<string, byte[]> Content = [];
        internal readonly HashSet<(string Id, string Purpose)> Allowed = [];
        internal bool Changed;
        internal int FreshnessChecks;
        public EvidenceReference Publish<T>(string id, string purpose, T content) => Raw(id, purpose, Bytes(content));
        public EvidenceReference Raw(string id, string purpose, byte[] bytes)
        {
            Content[id] = bytes.ToArray(); Allowed.Add((id, purpose));
            return new() { EvidenceId = id, EvidenceSha256 = Hashing.Sha256(bytes) };
        }
        public void Admit(EvidenceReference reference, string purpose, byte[]? bytes = null)
        {
            var actual = Raw(reference.EvidenceId, purpose, bytes ?? Encoding.UTF8.GetBytes(reference.EvidenceId));
            Assert(actual.EvidenceSha256 == reference.EvidenceSha256, "Fixture reference does not match real bytes");
        }
        public Task<EfAuthorizedContentV1> ReadAuthorizedAsync(EfEvidenceReadV1 request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Scope != expected || !Allowed.Contains((request.Reference.EvidenceId, request.Purpose)))
                throw new UnauthorizedAccessException("Fake role authorization denied");
            return Task.FromResult(new EfAuthorizedContentV1("platform/test-producers", new string('f', 40), Content[request.Reference.EvidenceId].ToArray()));
        }
        public Task VerifyCurrentAsync(EfEvidenceScopeV1 scope, IReadOnlyList<EvidenceReference> evidence, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); FreshnessChecks++;
            if (Changed || scope != expected || evidence.Any(x => !Content.TryGetValue(x.EvidenceId, out var bytes)
                || Hashing.Sha256(bytes) != x.EvidenceSha256)) throw new InvalidOperationException("Fake current pointer/revision changed");
            return Task.CompletedTask;
        }
    }
    internal sealed record Fixture(Provider Provider, EfPreparationSelectionV1 Selection,
        EfReleasePlanTests.Fixture Original, EfGovernedPreparationV1 Governance,
        EfObservationEvidenceV1 Pre, EfQualificationEvidenceV1 Qualification)
    {
        public EfEvidencePreparationV1 Service => new(Provider);
        public Task<EfPreparationResultV1> Run() => Service.PrepareAsync(Selection, Now);
        public Fixture Replace<T>(string kind, T document)
        {
            var r = Provider.Publish("replacement-" + kind, kind, document);
            return this with { Selection = kind switch {
                "GOVERNANCE" => Selection with { Governance = r }, "TARGET_PRE" => Selection with { Pre = r },
                "SOURCE" => Selection with { Source = r }, "ISOLATED_QUALIFICATION" => Selection with { Qualification = r },
                _ => throw new Exception("Unknown fixture document") } };
        }
    }
    internal static async Task<Fixture> Make(string name = "Inventory", int applied = 2, int pending = 1,
        string schema = "dbo", bool initial = false, EfReleasePlanTests.Fixture? original = null)
    {
        var f = original ?? await EfReleasePlanTests.Make(name, applied, pending, initial, schema); var r = f.Request;
        var scope = new EfEvidenceScopeV1(r.Authority.Target.TargetId, r.Source.Repository, r.Source.Revision, "test-session");
        var p = new Provider(scope);
        void Provenance(EfPlanProvenanceV1 v, string purpose) => p.Admit(v.Document, purpose);
        Provenance(r.Authority.Governance, "GOVERNANCE_BYTES"); Provenance(r.Authority.Onboarding, "ONBOARDING_BYTES");
        Provenance(r.Policy.Provenance, "POLICY"); p.Admit(r.Authority.Classification, "CLASSIFICATION_V2");
        p.Admit(r.Source.RepositoryEvidence, "REPOSITORY_DISCOVERY_V2");
        if (r.Authority.CertifiedBaseline is { } baseline) Provenance(baseline.Provenance, "CERTIFIED_BASELINE");
        if (r.Authority.BootstrapEvidence is { } bootstrap) p.Admit(bootstrap, "NEW_BOOTSTRAP");
        if (r.Source.AdoptionProvenance is { } adoption) Provenance(adoption, "ADOPTION");
        EfPlanProvenanceV1 Projection(string id, string purpose) => new("platform/projections", new string('e', 40), p.Raw(id, purpose, Encoding.UTF8.GetBytes(id)));
        var hashContract = p.Publish("hash-contract", "CONTENT_HASH_CONTRACT", new EfContentHashContractV1(1, Hash("fixture-full-content"),
            Projection("data-projection", "DATA_PROJECTION"), Projection("security-projection", "SECURITY_PROJECTION")));
        var resolution = new EfSqlResolutionV1(schema, p.Publish("name-resolution", "SQL_NAME_RESOLUTION", new { defaultSchema = schema }));
        var g = new EfGovernedPreparationV1(r.Authority, r.Policy, resolution, hashContract);
        var lab = r.Authority.Target with { TargetId = "lab-" + name, EndpointReference = "isolated-" + name,
            DatabaseName = "FakeLab", AllowedServerInstances = ["FAKE_SQL"] };
        EfPlanObservationV1 InLab(EfPlanObservationV1 o) => o with { TargetId = lab.TargetId, EndpointReference = lab.EndpointReference,
            DatabaseName = lab.DatabaseName, ServerInstance = "FAKE_SQL", DatabaseIncarnation = "lab-incarnation-" + name };
        var pre = Observe(p, r.Pre, "TARGET_PRE", hashContract);
        var labPre = Observe(p, InLab(r.Pre), "REHEARSAL_PRE", hashContract);
        var post = Observe(p, InLab(r.ExpectedPost), "QUALIFIED_POST", hashContract);
        var pre2 = Observe(p, InLab(r.Recovery.RecoveredPre!), "RECOVERED_PRE", hashContract);
        var post2 = Observe(p, InLab(r.Recovery.ReappliedPost!), "REAPPLIED_POST", hashContract);
        p.Admit(r.Recovery.ExerciseEvidence!, "RECOVERY_EXERCISE", Bytes(r.Recovery.Exercise));
        var approval = p.Publish("recovery-approval", "RECOVERY_APPROVAL", new EfRecoveryApprovalV1(scope.TargetId,
            r.Recovery.PayloadHash, r.Recovery.Strategy, Now.AddMinutes(10)));
        var recovery = r.Recovery with { TargetId = lab.TargetId, StrategyApproval = approval,
            RecoveredPre = pre2.Observation, ReappliedPost = post2.Observation };
        var q = new EfQualificationEvidenceV1(scope.TargetId, scope.SourceRevision, r.Recovery.PayloadHash, lab,
            labPre, post, pre2, post2, recovery);
        var selection = new EfPreparationSelectionV1(scope, r.ReleaseId, r.OperationId,
            p.Publish("governed-input", "GOVERNANCE", g),
            p.Publish("source-input", "SOURCE", new EfSourcePreparationV1(r.Source, f.Forward.Bytes, f.Rollback.Bytes)),
            p.Publish("pre-input", "TARGET_PRE", pre), p.Publish("qualification-input", "ISOLATED_QUALIFICATION", q));
        return new(p, selection, f, g, pre, q);
    }
    internal static EfObservationEvidenceV1 Observe(Provider p, EfPlanObservationV1 o, string phase, EvidenceReference hashContract)
    {
        var raw = p.Publish(phase.ToLowerInvariant() + "-capture", phase, new { o.TargetId, o.ServerInstance, o.DatabaseName,
            o.DatabaseIncarnation, o.Schema, o.DataHash, o.SecurityHash, o.AppliedMigrationIds, o.CapturedAtUtc });
        o = o with { Evidence = raw };
        var physical = o.Physical ?? EmptyForNewEfV1.Complete(o.Schema.Objects.Count(x => x.Kind == "table" && x.Name != "__EFMigrationsHistory"), new long[EmptyForNewEfV1.Categories.Count]);
        var discovery = new SqlDiscoveryResult(new(ConnectionStatus.Succeeded), new(DatabaseLookupStatus.Found),
            new(ConnectionStatus.Succeeded), new(ObservedIdentityStatus.Available, new(o.ServerInstance, o.DatabaseName)),
            new(MetadataStatus.Sufficient), physical, new(Enum.Parse<HistoryStatus>(o.HistoryStatus, true), o.AppliedMigrationIds));
        return new(phase, o, discovery, hashContract);
    }
    private static async Task<EfReleasePlanV1> Consistent(Fixture f)
    {
        var r = await f.Run();
        Assert(r.Status == "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED", "Unexpected: " + string.Join(",", r.Reasons));
        Assert(!r.ExecutionAuthorized && f.Provider.FreshnessChecks > 0, "Missing trust/freshness boundary");
        return r.PlanResult!.Plan!;
    }
    private static async Task Blocked(Fixture f, string? code = null)
    {
        var r = await f.Run();
        Assert(r.Status == "BLOCKED" && !r.ExecutionAuthorized && (code is null || r.Reasons.Contains(code)), "Expected block " + code + ": " + r.Status + "/" + string.Join(",", r.Reasons));
    }

    private static async Task Generalization()
    {
        foreach (var spec in new[] { ("Inventory", 2, 1, "dbo"), ("Payments", 1, 3, "billing"), ("Fleet", 8, 2, "transport") })
        {
            var f = await Make(spec.Item1, spec.Item2, spec.Item3, spec.Item4); var p = await Consistent(f); var m = p.Read();
            Assert(m.PendingMigrationIds.Length == spec.Item3 && m.ExpectedPost.TargetId != m.Pre.TargetId
                && m.ExpectedPost.DatabaseIncarnation != m.Pre.DatabaseIncarnation, "Lab identity was rewritten as production");
            Assert(m.Acquisition!.RehearsalPre.SchemaHash == m.Pre.SchemaHash && m.Recovery.ExerciseVerified,
                "Missing equivalence/qualification binding");
            Assert(m.Acquisition.Producers.Length > 0 && m.Acquisition.Producers.All(x =>
                x.Repository == "platform/test-producers" && x.Revision == new string('f', 40)), "Producer provenance omitted");
            Assert(!p.Json.Contains("CREATE TABLE") && !m.ExecutionAuthorized, "Raw SQL or authority leaked");
        }
    }
    private static Task Semantics()
    {
        foreach (var schema in new[] { "dbo", "billing", "transport" })
        {
            var sql = ReleaseScript.FromText("forward", $"CREATE TABLE [{schema}].[Item] (Id int NOT NULL, Value int NULL, CONSTRAINT PK_Item PRIMARY KEY (Id)); ALTER TABLE [{schema}].[Item] ADD Active int NULL; CREATE INDEX IX_Item ON [{schema}].[Item](Value); INSERT INTO [{schema}].[Item](Id, Value) VALUES(1,2); UPDATE [{schema}].[Item] SET Value=3 WHERE Id=1; DELETE FROM [{schema}].[Item] WHERE Id=1;");
            var r = EfSqlSemanticAnalysisV1.Analyze(sql, new(), null);
            Assert(r.Status == "SEMANTIC_ANALYSIS_COMPLETE", "Supported direct SQL blocked: " + string.Join(",", r.Analysis.ConfidenceReasons)
                + "; fragments: " + string.Join(" | ", sql.Text.Split(';').Where(x => !string.IsNullOrWhiteSpace(x))
                    .Where(x => EfSqlSemanticAnalysisV1.Analyze(ReleaseScript.FromText("forward", x + ";"), new(), null).Status != "SEMANTIC_ANALYSIS_COMPLETE")));
            foreach (var op in new[] { "CREATE_TABLE", "ADD_COLUMN", "ADD_CONSTRAINT", "CREATE_INDEX", "INSERT_DATA", "UPDATE_DATA", "DELETE_DATA" })
                Assert(r.Analysis.Operations.Any(x => x.Operation == op && x.Schema == schema && x.TargetResolved), "Missing semantic effect " + op);
        }
        return Task.CompletedTask;
    }
    private static Task SemanticNegatives()
    {
        foreach (var sql in new[] { "CREATE TABLE T(Id int);", "EXEC dbo.P;", "SELECT 1;", "IF 1=1 DELETE FROM dbo.T;",
            "SET IDENTITY_INSERT dbo.T ON;", "CREATE SEQUENCE dbo.S;", "DELETE FROM remote.db.dbo.T;", "DELETE FROM dbo.T;\nGO 2",
            "INSERT INTO dbo.T SELECT Id FROM dbo.Other;", "UPDATE dbo.T SET Id=dbo.F();", "UPDATE dbo.T SET Id=(SELECT MAX(Id) FROM dbo.Other);",
            "ALTER TABLE dbo.T ADD CONSTRAINT FK_T FOREIGN KEY(Id) REFERENCES dbo.Other(Id);" })
            Assert(EfSqlSemanticAnalysisV1.Analyze(ReleaseScript.FromText("forward", sql), new(), null).Status == "SEMANTIC_ANALYSIS_BLOCKED", "Unsupported SQL admitted: " + sql);
        foreach (var sql in new[] { "INSERT INTO dbo.T VALUES(1); CREATE TABLE dbo.T(Id int);",
            "CREATE TABLE dbo.T(Id int); DROP TABLE dbo.T; INSERT INTO dbo.T VALUES(1);" })
            Assert(EfSqlSemanticAnalysisV1.Analyze(ReleaseScript.FromText("forward", sql), new(), null).Status == "SEMANTIC_ANALYSIS_BLOCKED", "DML table lifetime ignored");
        foreach (var kind in new[] { "trigger", "default-constraint", "check-constraint", "column", "foreign-key-column", "schema-dependency" })
        {
            var snapshot = new SchemaSnapshot();
            snapshot.Objects.Add(new() { Kind = "table", Schema = "dbo", Name = "T" });
            snapshot.Objects.Add(new() { Kind = kind, Schema = "dbo", Parent = kind == "foreign-key-column" ? "Child" : "T", Name = "indirect",
                Properties = new() { ["referencedSchema"] = "dbo", ["referencedTable"] = "T", ["computed"] = "true" } });
            Assert(EfSqlSemanticAnalysisV1.Analyze(ReleaseScript.FromText("forward", "DELETE FROM dbo.T;"), snapshot, null).Status
                == "SEMANTIC_ANALYSIS_BLOCKED", "Indirect catalog effect accepted: " + kind);
        }
        string Toggle(string column, string state) => $"IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'{column}') AND [object_id] = OBJECT_ID(N'[dbo].[T]')) SET IDENTITY_INSERT [dbo].[T] {state};";
        var prefix = "CREATE TABLE dbo.T(Id int IDENTITY, Other int);" + Toggle("Id", "ON");
        var insert = "INSERT INTO dbo.T(Id,Other) VALUES(1,2);";
        Assert(EfSqlSemanticAnalysisV1.Analyze(ReleaseScript.FromText("forward", prefix + insert + Toggle("Id", "OFF")), new(), null).Status
            == "SEMANTIC_ANALYSIS_COMPLETE", "Closed identity pair rejected");
        foreach (var sql in new[] { prefix + insert, prefix + insert + Toggle("Other", "OFF"),
            prefix + "ALTER TABLE dbo.T ADD More int;" + Toggle("Id", "OFF"), Toggle("Id", "OFF") + prefix + insert })
            Assert(EfSqlSemanticAnalysisV1.Analyze(ReleaseScript.FromText("forward", sql), new(), null).Status == "SEMANTIC_ANALYSIS_BLOCKED", "Unproven identity lifetime accepted");
        return Task.CompletedTask;
    }
    private static async Task RealSql()
    {
        var f = await Make(); var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ef1009");
        var up = ReleaseScript.FromFile("forward", Path.Combine(dir, "forward.sql")); var down = ReleaseScript.FromFile("rollback", Path.Combine(dir, "rollback.sql"));
        Assert(up.Sha256 == "309581d85a1525f03f0125337ec39655f89e8e6a2e4a02d48c415a096024b47f"
            && down.Sha256 == "c05451e73ba4ece0e69a21e6b4d8aa9dff227009cd37b1ed7128ea452808f1f9", "Real bytes changed");
        var post = Copy(f.Original.Request.ExpectedPost.Schema);
        post.Objects.Add(new() { Kind = "table", Schema = "dbo", Name = "SEBLOB" });
        post.ImpactMetrics.Add(new() { Schema = "dbo", Table = "SEBLOB", RowCount = 1 });
        var a = EfSqlSemanticAnalysisV1.Analyze(up, f.Original.Request.Pre.Schema, f.Governance.SqlResolution);
        var b = EfSqlSemanticAnalysisV1.Analyze(down, post, f.Governance.SqlResolution);
        Assert(a.Status == "SEMANTIC_ANALYSIS_COMPLETE" && b.Status == "SEMANTIC_ANALYSIS_COMPLETE", "Actual EF fixture lacks semantic coverage");
        Assert(a.Analysis.Operations.Count(x => x.Operation.StartsWith("IDENTITY_INSERT_")) == 2, "Session effects omitted");
        Assert(RecoveryImpact.Derive(a.Analysis, b.Analysis).Complete && b.Analysis.HasPotentialDataLoss, "Recovery impact understated");
        // A real SQL fixture with synthetic observations is still not authenticated recovery.
        var changed = f.Replace("SOURCE", new EfSourcePreparationV1(f.Original.Request.Source, up.Bytes, down.Bytes));
        changed = changed with { Selection = changed.Selection with { Qualification = null } };
        var result = await changed.Run();
        Assert(result.Forward?.Status == "SEMANTIC_ANALYSIS_COMPLETE" && result.Status == "BLOCKED"
            && result.Reasons.Contains("QUALIFIED_POST_REQUIRED"), "Semantic acceptance invented a rehearsal");
    }
    private static async Task ProviderNegatives()
    {
        var f = await Make();
        Assert((await new EfEvidencePreparationV1().PrepareAsync(f.Selection, Now)).Reasons.Contains("AUTHORIZED_PROVIDER_REQUIRED"), "Absent provider accepted");
        f.Provider.Allowed.Remove((f.Selection.Pre.EvidenceId, "TARGET_PRE")); await Blocked(f);
        f = await Make(); f.Provider.Content[f.Selection.Pre.EvidenceId][0] ^= 1; await Blocked(f, "EVIDENCE_HASH_MISMATCH");
        f = await Make(); f.Provider.Changed = true; await Blocked(f);
        f = await Make(); var bytes = Encoding.UTF8.GetBytes("{\"authority\":null,\"Authority\":null}");
        var duplicate = f.Provider.Raw("duplicate", "GOVERNANCE", bytes);
        await Blocked(f with { Selection = f.Selection with { Governance = duplicate } }, "EVIDENCE_DUPLICATE_FIELD");
        f = await Make(); var cancelled = new CancellationToken(true);
        Assert((await f.Service.PrepareAsync(f.Selection, Now, cancelled)).Status == "BLOCKED", "Cancellation ignored");
    }
    private static async Task EvidenceNegatives()
    {
        var f = await Make();
        var o = f.Pre.Observation;
        foreach (var changed in new[] { o with { TargetId = "other" }, o with { EndpointReference = "other" }, o with { DataHash = "" },
            o with { HistoryStatus = "UNKNOWN" }, o with { AppliedMigrationIds = [f.Original.Request.Source.ManagedMigrationIds[1]] },
            o with { MetadataCoverage = "PARTIAL" }, o with { ImpactCoverage = "PARTIAL" } })
            await Blocked(f.Replace("TARGET_PRE", f.Pre with { Observation = changed }));
        var g = f.Governance with { Authority = f.Governance.Authority with { CertifiedBaseline = f.Governance.Authority.CertifiedBaseline! with { SchemaHash = Hash("wrong") } } };
        await Blocked(f.Replace("GOVERNANCE", g));
        var q = f.Qualification with { SourceRevision = new string('a', 40) };
        await Blocked(f.Replace("ISOLATED_QUALIFICATION", q), "QUALIFICATION_BINDING_MISMATCH");
        q = f.Qualification; var badPost = Copy(q.Post.Observation); badPost.Schema.ImpactMetrics.Clear();
        await Blocked(f.Replace("ISOLATED_QUALIFICATION", q with { Post = q.Post with { Observation = badPost } }));
    }
    private static async Task Isolation()
    {
        var f = await Make(); var q = f.Qualification;
        await Blocked(f.Replace("ISOLATED_QUALIFICATION", q with { RehearsalTarget = f.Governance.Authority.Target }));
        await Blocked(f.Replace("ISOLATED_QUALIFICATION", q with { Pre = q.Pre with { Observation = q.Pre.Observation with { DataHash = Hash("different-clone") } } }));
    }
    private static async Task Recovery()
    {
        var f = await Make(); var q = f.Qualification;
        foreach (var strategy in new[] { RollbackCapability.RestoreRequired, RollbackCapability.ForwardFixOnly, RollbackCapability.SchemaOnly })
        {
            var approval = f.Provider.Publish("alternate-approval", "RECOVERY_APPROVAL", new EfRecoveryApprovalV1(f.Selection.Scope.TargetId, q.PayloadHash, strategy, Now.AddMinutes(10)));
            await Blocked(f.Replace("ISOLATED_QUALIFICATION", q with { Recovery = q.Recovery with { Strategy = strategy, StrategyApproval = approval } }), "RECOVERY_STRATEGY_EVIDENCE_ADAPTER_REQUIRED");
        }
        await Blocked(f.Replace("ISOLATED_QUALIFICATION", q with { Recovery = q.Recovery with { Exercise = null, ExerciseEvidence = null } }), "RECOVERY_NOT_VERIFIED");
    }
    private static async Task NoChanges()
    {
        var f = await Make(); var source = f.Original.Request.Source with { ManagedMigrationIds = f.Pre.Observation.AppliedMigrationIds };
        f = f.Replace("SOURCE", new EfSourcePreparationV1(source, [], []));
        var r = await f.Run(); Assert(r.Status == "NO_CHANGES" && r.PlanResult is null && !r.ExecutionAuthorized && r.EvidenceSetHash is not null, "No changes not evidenced");
        await Blocked(f.Replace("SOURCE", new EfSourcePreparationV1(source, f.Original.Forward.Bytes, f.Original.Rollback.Bytes)), "NO_CHANGES_WITH_SQL");
        f.Provider.Changed = true; await Blocked(f);
    }
    private static async Task FreshPre()
    {
        var f = await Make(); var plan = await Consistent(f);
        var fresh = Observe(f.Provider, f.Pre.Observation with { CapturedAtUtc = Now }, "FRESH_PRE", f.Governance.ContentHashContract);
        var reference = f.Provider.Publish("fresh-pre-input", "FRESH_PRE", fresh);
        Assert((await f.Service.RevalidateAsync(plan, plan.Sha256, f.Selection.Scope, reference, f.Selection.Source, Now)).Status == "PRE_MATCH_NOT_AUTHORIZED", "Fresh PRE rejected");
        foreach (var changed in new[] { fresh.Observation with { DataHash = Hash("drift") }, fresh.Observation with { DatabaseIncarnation = "replaced" }, fresh.Observation with { EndpointReference = "switched" } })
        {
            reference = f.Provider.Publish("fresh-pre-input", "FRESH_PRE", fresh with { Observation = changed });
            Assert((await f.Service.RevalidateAsync(plan, plan.Sha256, f.Selection.Scope, reference, f.Selection.Source, Now)).Status == "BLOCKED", "PRE change accepted");
        }
        var swapped = f.Provider.Publish("swapped-source", "SOURCE", new EfSourcePreparationV1(f.Original.Request.Source with { Revision = new string('c', 40) }, f.Original.Forward.Bytes, f.Original.Rollback.Bytes));
        Assert((await f.Service.RevalidateAsync(plan, plan.Sha256, f.Selection.Scope, reference, swapped, Now)).Status == "BLOCKED", "Concurrent revision accepted");
    }
    internal static (EvidenceReference Execution, EvidenceReference Post, EfPlanExecutionEvidenceV1 Receipt, EfObservationEvidenceV1 Observation) Execution(Fixture f, EfReleasePlanV1 plan)
    {
        var m = plan.Read(); var pre = f.Pre.Observation; var qualified = f.Qualification.Post.Observation;
        var actual = qualified with { TargetId = pre.TargetId, EndpointReference = pre.EndpointReference, ServerInstance = pre.ServerInstance,
            DatabaseName = pre.DatabaseName, DatabaseIncarnation = pre.DatabaseIncarnation, CapturedAtUtc = Now.AddSeconds(2) };
        var post = Observe(f.Provider, actual, "ACTUAL_POST", f.Governance.ContentHashContract);
        var receipt = new EfPlanExecutionEvidenceV1(m.OperationId, pre.TargetId, plan.Sha256, m.Payload.PayloadHash, m.Payload.ForwardHash,
            "SUCCEEDED", Now, Now.AddSeconds(1), f.Provider.Publish("execution-raw", "EXECUTION_RECEIPT_BYTES", new { fixture = "never SQL" }));
        return (f.Provider.Publish("execution", "EXECUTION_RECEIPT", receipt), f.Provider.Publish("actual-post", "ACTUAL_POST", post), receipt, post);
    }
    private static async Task ActualPost()
    {
        var f = await Make(); var plan = await Consistent(f); var e = Execution(f, plan);
        Assert((await f.Service.VerifyPostAsync(plan, plan.Sha256, f.Selection.Scope, e.Execution, e.Post, Now.AddSeconds(3))).Status == "POST_MATCH_NOT_CERTIFIED", "Actual POST rejected");
        foreach (var outcome in new[] { "UNKNOWN", "FAILED", "PARTIAL" })
        {
            var reference = f.Provider.Publish("uncertain", "EXECUTION_RECEIPT", e.Receipt with { Outcome = outcome });
            Assert((await f.Service.VerifyPostAsync(plan, plan.Sha256, f.Selection.Scope, reference, e.Post, Now.AddSeconds(3))).Status == "BLOCKED", "Uncertain execution accepted");
        }
        var lab = f.Provider.Publish("lab-as-post", "ACTUAL_POST", f.Qualification.Post with { Phase = "ACTUAL_POST" });
        Assert((await f.Service.VerifyPostAsync(plan, plan.Sha256, f.Selection.Scope, e.Execution, lab, Now.AddSeconds(3))).Status == "BLOCKED", "Lab presented as actual target");
        var changed = e.Observation with { Observation = e.Observation.Observation with { SecurityHash = Hash("lost-grant") } };
        var post = f.Provider.Publish("wrong-post", "ACTUAL_POST", changed);
        Assert((await f.Service.VerifyPostAsync(plan, plan.Sha256, f.Selection.Scope, e.Execution, post, Now.AddSeconds(3))).Status == "BLOCKED", "Security drift accepted");
    }
    private static async Task SuccessiveRelease()
    {
        var f = await Make(); var plan = await Consistent(f); var m = plan.Read(); var e = Execution(f, plan);
        var previous = m.Authority.CertifiedBaseline!.Provenance.Document; f.Provider.Admit(previous, "CURRENT_CERTIFIED_BASELINE");
        var auth = new EfBaselineAuthorizationV1(m.Authority.Target.TargetId, m.OperationId, plan.Sha256, previous, Now, Now.AddMinutes(10),
            new() { PolicyId = "independent-dba-policy", Decision = DeploymentAuthorizationDecision.Authorized,
                Requirement = DeploymentAuthorizationRequirement.DbaApproval, AuthorizationReference = "dba-approval",
                Risk = m.Risk!.FinalRisk, ReleaseQualificationGatePassed = true, AnalysisConfidence = m.Risk.AnalysisConfidence,
                SensitiveChange = true, SchemaRollbackValidity = m.Recovery.SchemaValidity,
                DataRollbackValidity = m.Recovery.DataValidity, RollbackCapability = m.Recovery.Strategy });
        var authority = f.Provider.Publish("transition-consent", "BASELINE_TRANSITION_AUTHORIZATION", auth);
        var result = await f.Service.ProposeBaselineTransitionAsync(plan, plan.Sha256, f.Selection.Scope, e.Execution, e.Post, authority, Now.AddSeconds(3));
        Assert(result.Status == "BASELINE_TRANSITION_CANDIDATE_NOT_COMMITTED" && !result.Committed && !result.ExecutionAuthorized
            && result.PreviousBaseline == previous && result.ProposedSchemaHash == m.ExpectedPost.SchemaHash, "Valid candidate rejected: " + string.Join(",", result.Reasons));
        f.Provider.Allowed.Remove((authority.EvidenceId, "BASELINE_TRANSITION_AUTHORIZATION"));
        Assert((await f.Service.ProposeBaselineTransitionAsync(plan, plan.Sha256, f.Selection.Scope, e.Execution, e.Post, authority, Now.AddSeconds(3))).Status == "BLOCKED", "Unverified consent accepted");

        // Simulate an independently committed governance transition, not a write by this service.
        var r = f.Original.Request; var nextPre = r.ExpectedPost; var nextPost = Copy(nextPre);
        nextPost.Schema.Objects.Add(new() { Kind = "table", Schema = "dbo", Name = "NextItem" });
        nextPost.Schema.ImpactMetrics.Add(new() { Schema = "dbo", Table = "NextItem", RowCount = 0 });
        var nextId = "20260201120000_NextRelease"; nextPost = nextPost with { AppliedMigrationIds = [.. nextPre.AppliedMigrationIds, nextId] };
        var up = ReleaseScript.FromText("forward", $"CREATE TABLE dbo.NextItem(Id int); INSERT INTO dbo.__EFMigrationsHistory(MigrationId,ProductVersion) VALUES(N'{nextId}',N'10.0.9');");
        var down = ReleaseScript.FromText("rollback", $"DROP TABLE dbo.NextItem; DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'{nextId}';");
        var descriptor = new ReleaseDescriptor { ReleaseId = "next-release", Environment = "TEST", SourceKind = "EF", Scenario = "EXISTING_EF", DatabaseLifecycle = "EXISTING" };
        var lab = new EfReleasePlanTests.Lab(nextPre.Schema, nextPost.Schema);
        var rehearsal = await new RehearsalEngine().QualifyAsync(descriptor, new() { ConsistencyStatus = "CONSISTENT", ConsistencyReason = "FAKE_SUCCESSOR" }, up, down, lab, lab);
        Assert(rehearsal.CanProceed, "Successor fake rehearsal failed");
        var exerciseRef = new EvidenceReference { EvidenceId = "successor-exercise", EvidenceSha256 = Hashing.Sha256(Bytes(rehearsal)) };
        var payload = ReleasePayloadBuilder.Build(descriptor, up, down);
        var next = new EfReleasePlanTests.Fixture(r with { ReleaseId = descriptor.ReleaseId,
            Source = r.Source with { Revision = new string('c', 40), ManagedMigrationIds = nextPost.AppliedMigrationIds },
            Authority = r.Authority with { CertifiedBaseline = r.Authority.CertifiedBaseline! with { SchemaHash = result.ProposedSchemaHash! } },
            Pre = nextPre, ExpectedPost = nextPost, Recovery = r.Recovery with { PayloadHash = payload.PayloadHash, Exercise = rehearsal,
                ExerciseEvidence = exerciseRef, RecoveredPre = nextPre, ReappliedPost = nextPost } }, up, down);
        await Consistent(await Make(original: next));
        var stale = next with { Request = next.Request with { Authority = r.Authority } };
        await Blocked(await Make(original: stale));
    }
    private static async Task New()
    {
        var f = await Make("Shipping", 0, 2, initial: true); await Consistent(f);
        await Blocked(f.Replace("GOVERNANCE", f.Governance with { Authority = f.Governance.Authority with { BootstrapEvidence = null } }), "NEW_BOOTSTRAP_EVIDENCE_REQUIRED");
    }
}
