using System.Text.Json;
using DatabaseReleaseQualification;
using SqlDiscovery.V2;

internal static class EfReleasePlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static string Hash(string s) => Hashing.Sha256(s);
    private static EvidenceReference Ref(string id, string? hash = null) => new() { EvidenceId = id, EvidenceSha256 = hash ?? Hash(id) };
    private static EfPlanProvenanceV1 Provenance(string id) => new("platform/governance", new string('a', 40), Ref(id));
    private static void Assert(bool value, string why) { if (!value) throw new Exception(why); }
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonDefaults.Compact), JsonDefaults.Compact)!;
    internal sealed record Fixture(EfPlanRequestV1 Request, ReleaseScript Forward, ReleaseScript Rollback)
    {
        public EfPlanResultV1 Build() => EfReleasePlannerV1.Build(Request, Forward, Rollback, Now);
    }
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF generic plan: unrelated applications and variable histories", Generalization),
        ("EF generic plan: NEW after independently evidenced bootstrap", New),
        ("EF generic plan: historical adoption differs from caller", Adoption),
        ("EF generic plan: exact payload and immutable manifest", Immutability),
        ("EF generic plan: HA binding still requires the governed endpoint", Endpoint),
        ("EF generic plan: target, governance, lifecycle and source mismatches block", BindingNegatives),
        ("EF generic plan: unknown, divergent, duplicate and reordered history blocks", HistoryNegatives),
        ("EF generic plan: incomplete schema, metrics and data/security evidence block", CoverageNegatives),
        ("EF generic plan: recovery is bound to payload, state and evidence bytes", RecoveryNegatives),
        ("EF generic plan: alternate recovery strategies require an evidence adapter", Strategies),
        ("EF generic plan: pure revalidation detects payload and PRE changes", Revalidation),
        ("EF generic plan: POST needs exact history, schema, data, security and receipt", Post),
        ("EF generic plan: real EF1009 bytes remain blocked by missing semantic evidence", Pilot),
        ("EF generic plan: invalid SQL, encoding and null evidence fail closed", Malformed)
    ];

    internal static async Task<Fixture> Make(string name = "Orders", int applied = 2, int pending = 1, bool initial = false, string schema = "dbo")
    {
        var all = Enumerable.Range(1, applied + pending).Select(i => $"202601{i:00}120000_Change{i}").ToArray();
        var tables = Enumerable.Range(applied + 1, pending).Select(i => $"{name}Item{i}").ToArray();
        var forward = ReleaseScript.FromText("forward", string.Join("\n", tables.Zip(all.Skip(applied), (t, id) =>
            $"CREATE TABLE {schema}.{t} (Id int NOT NULL); INSERT INTO {schema}.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (N'{id}', N'10.0.9');")));
        var rollback = ReleaseScript.FromText("rollback", string.Join("\n", tables.Zip(all.Skip(applied), (t, id) =>
            $"DROP TABLE {schema}.{t}; DELETE FROM {schema}.__EFMigrationsHistory WHERE MigrationId=N'{id}';").Reverse()));
        var preSchema = Snapshot([], schema); var postSchema = Snapshot(tables, schema);
        if (initial) preSchema.ImpactMetrics[0] = new() { Schema = schema, Table = "__EFMigrationsHistory", RowCount = 0 };
        var descriptor = new ReleaseDescriptor { ReleaseId = "release-" + name, Environment = "TEST", SourceKind = "EF", Scenario = initial ? "NEW_EF" : "EXISTING_EF", DatabaseLifecycle = initial ? "NEW" : "EXISTING" };
        var payload = ReleasePayloadBuilder.Build(descriptor, forward, rollback);
        var lab = new Lab(preSchema, postSchema);
        var exercise = await new RehearsalEngine().QualifyAsync(descriptor,
            new() { ConsistencyStatus = "CONSISTENT", ConsistencyReason = "SYNTHETIC_TEST" }, forward, rollback, lab, lab);
        Assert(exercise.CanProceed, "Existing rehearsal engine rejected synthetic fixture: " + exercise.QualificationStatus);
        var target = new EfPlanTargetV1("target-" + name, "app-" + name, "TEST", "listener-" + name,
            "db-" + name, "ALLOW_LIST", ["node-" + name], "business/" + name.ToLowerInvariant());
        EfPlanObservationV1 Observation(string id, SchemaSnapshot schema, string[] history) => new(target.TargetId,
            "TEST", target.EndpointReference, target.AllowedServerInstances[0], target.DatabaseName, "incarnation-" + name,
            history.Length == 0 ? "EMPTY" : "PRESENT", history, schema, "COMPLETE", "COMPLETE", Hash(id.StartsWith("pre") ? "pre-data" : "post-data"),
            Hash(id.StartsWith("pre") ? "pre-security" : "post-security"), Ref(id), Now.AddMinutes(-1),
            initial && history.Length == 0 ? EmptyForNewEfV1.Complete(0, new long[EmptyForNewEfV1.Categories.Count]) : null);
        var pre = Observation("pre", preSchema, all.Take(applied).ToArray());
        var post = Observation("post", postSchema, all);
        var recovery = new EfPlanRecoveryV1(RollbackCapability.FullReversible, Ref("strategy-approval"),
            Ref("exercise", Hashing.Sha256(JsonSerializer.Serialize(exercise, JsonDefaults.Compact))), target.TargetId,
            payload.PayloadHash, exercise, pre with { Evidence = Ref("pre2") }, post with { Evidence = Ref("post2") });
        return new(new(1, descriptor.ReleaseId, "8457c2e1-6a4d-4d04-86b4-2ad10f093bdd", Now, Now.AddMinutes(10),
            new("test-policy", Provenance("policy"), 600),
            new(target, descriptor.DatabaseLifecycle, "EF_MIGRATIONS", initial ? "NOT_REQUIRED" : "MANAGED",
                Provenance("governance"), Provenance("onboarding"), descriptor.Scenario, Ref("classification"), initial ? Ref("bootstrap") : null,
                initial ? null : new(SchemaCanonicalizer.Canonicalize(preSchema).Sha256, Provenance("baseline"))),
            new(target.SourceRepository, new string('b', 40), "src/Infrastructure/Infrastructure.csproj", "src/Host/Host.csproj", all, Ref("repository"), null),
            pre, post, recovery), forward, rollback);
    }
    private static SchemaSnapshot Snapshot(string[] tables, string schema = "dbo") => new() {
        Objects = tables.Select(t => new SchemaObject { Kind = "table", Schema = schema, Name = t })
            .Concat(EfInitialRecoveryTests.History().Objects.Select(x => new SchemaObject {
                Kind = x.Kind, Schema = schema, Parent = x.Parent, Name = x.Name, Properties = x.Properties })).ToList(),
        ImpactMetrics = tables.Prepend("__EFMigrationsHistory").Select(t => new TableImpactMetric { Schema = schema, Table = t, RowCount = 4 }).ToList()
    };
    internal sealed class Lab(SchemaSnapshot pre, SchemaSnapshot post) : IRehearsalDatabase, IRecoverySecurityEvidenceProvider,
        IDataRollbackValidationContract, IDataReapplyValidationContract
    {
        private int writes;
        public Task<SchemaSnapshot> CaptureSchemaAsync(CancellationToken token = default) => Task.FromResult(writes % 2 == 0 ? pre : post);
        public Task ExecuteSqlAsync(ReleaseScript script, string hash, CancellationToken token = default)
        { Assert(script.Sha256 == hash, "Synthetic exact-byte contract failed"); writes++; return Task.CompletedTask; }
        public Task<RecoverySecuritySnapshot> CaptureSecurityAsync(RecoverySecurityScope scope, RecoveryPhase phase, CancellationToken token = default)
            => Task.FromResult(RecoveryCoverageTests.Security(scope, phase, writes % 2 == 0 ? pre : post));
        public Task CapturePreDataAsync(CancellationToken t = default) => Task.CompletedTask;
        public Task CapturePostDataAsync(CancellationToken t = default) => Task.CompletedTask;
        public Task<DataRollbackValidity> ValidateRollbackDataAsync(CancellationToken t = default) => Task.FromResult(DataRollbackValidity.Valid);
        public Task<DataRollbackValidity> ValidateReapplyDataAsync(CancellationToken t = default) => Task.FromResult(DataRollbackValidity.Valid);
    }
    private static EfReleasePlanV1 Consistent(Fixture f)
    {
        var result = f.Build();
        Assert(result.Status == "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED" && !result.ExecutionAuthorized,
            "Unexpected plan result: " + string.Join(",", result.Reasons));
        return result.Plan!;
    }
    private static void Block(Fixture f, string code)
    {
        var result = f.Build();
        Assert(result.Status == "BLOCKED" && result.Reasons.Contains(code), "Expected " + code + "; got " + string.Join(",", result.Reasons));
        Assert(!result.ExecutionAuthorized, "Blocked input authorized execution");
    }
    private static async Task Generalization()
    {
        foreach (var (name, applied, pending) in new[] { ("Orders", 2, 1), ("Billing", 1, 4), ("Warehouse", 9, 2) })
        {
            var f = await Make(name, applied, pending); var p = Consistent(f).Read();
            Assert(p.PendingMigrationIds.Length == pending && p.ExpectedPost.AppliedMigrationIds.Length == applied + pending, "Lineage was hardcoded");
            Assert(p.Risk!.FinalRisk == RiskLevel.High && p.Risk.RequiresDbaApproval, "DROP recovery risk must contribute");
            Assert(p.Recovery.PotentialDataLoss && p.Recovery.DataValidationRequired && p.Recovery.ExerciseVerified,
                "Recovery dimensions were conflated");
        }
    }
    private static async Task New()
    {
        var f = await Make("Shipping", 0, 2, true); Consistent(f);
        Block(f with { Request = f.Request with { Authority = f.Request.Authority with { BootstrapEvidence = null } } }, "NEW_BOOTSTRAP_EVIDENCE_REQUIRED");
        Block(f with { Request = f.Request with { Pre = f.Request.Pre with { Physical = EmptyForNewEfV1.Complete(1, new long[EmptyForNewEfV1.Categories.Count]) } } }, "NEW_EMPTY_EVIDENCE_REQUIRED");
        Block(f with { Request = f.Request with { Pre = f.Request.Pre with {
            Physical = f.Request.Pre.Physical! with { Status = PhysicalStatus.Partial } } } }, "NEW_EMPTY_EVIDENCE_REQUIRED");
        var nonempty = Copy(f.Request.Pre);
        nonempty.Schema.Objects.Add(new() { Kind = "table", Schema = "dbo", Name = "ExistingBusinessObject" });
        Block(f with { Request = f.Request with { Pre = nonempty } }, "NEW_EMPTY_EVIDENCE_REQUIRED");
    }
    private static async Task Adoption()
    {
        var f = await Make(); Consistent(f with { Request = f.Request with { Source = f.Request.Source with {
            AdoptionProvenance = new(f.Request.Source.Repository, new string('c', 40), Ref("historical-adoption")) } } });
    }
    private static async Task Immutability()
    {
        var f = await Make(); var plan = Consistent(f); var original = plan.Json;
        f.Request.Source.ManagedMigrationIds[0] = "tampered";
        var read = plan.Read(); read.PendingMigrationIds[0] = "tampered";
        Assert(plan.Json == original && Hashing.Sha256(plan.Json) == plan.Sha256, "Plan retained mutable input");
        Assert(!plan.Json.Contains("CREATE TABLE") && !plan.Read().ExecutionAuthorized, "Manifest exposed SQL/authority");
    }
    private static async Task Endpoint()
    {
        var f = await Make(); var target = f.Request.Authority.Target with { ServerMatchPolicy = "MANAGED_ENDPOINT", AllowedServerInstances = [] };
        f = f with { Request = f.Request with { Authority = f.Request.Authority with { Target = target } } };
        var plan = Consistent(f);
        var failover = f.Request.Pre with { ServerInstance = "another-node", CapturedAtUtc = Now };
        Assert(EfReleasePlannerV1.Revalidate(plan, plan.Sha256, failover, f.Forward, f.Rollback, Now).Status == "PRE_MATCH_NOT_AUTHORIZED", "Managed failover rejected");
        Assert(EfReleasePlannerV1.Revalidate(plan, plan.Sha256, failover with { EndpointReference = "other" }, f.Forward, f.Rollback, Now).Status == "BLOCKED", "Endpoint bypass");
    }
    private static async Task BindingNegatives()
    {
        var f = await Make(); var r = f.Request;
        foreach (var request in new[] {
            r with { Authority = r.Authority with { OnboardingState = "PENDING" } },
            r with { Authority = r.Authority with { Lifecycle = "NEW" } },
            r with { Authority = r.Authority with { ChangeManagementMode = "LEGACY_UNMANAGED" } }
        }) Block(f with { Request = request }, "GOVERNANCE_BLOCKED");
        Block(f with { Request = r with { Pre = r.Pre with { ServerInstance = "unapproved" } } }, "OBSERVED_TARGET_MISMATCH");
        Block(f with { Request = r with { Source = r.Source with { Repository = "other/repo" } } }, "SOURCE_BINDING_INVALID");
        Block(f with { Request = r with { Source = r.Source with { ProjectPath = "../escape.csproj" } } }, "SOURCE_BINDING_INVALID");
        Block(f with { Request = r with { Authority = r.Authority with { CertifiedBaseline = null } } }, "CERTIFIED_PRE_MISMATCH");
        Block(f with { Request = r with { ExpiresAtUtc = Now.AddHours(1) } }, "PLAN_VALIDITY_INVALID");
        Block(f with { Request = r with { Policy = r.Policy with { Provenance = r.Policy.Provenance with { Revision = "main" } } } }, "PROVENANCE_INVALID");
    }
    private static async Task HistoryNegatives()
    {
        var f = await Make(); var r = f.Request;
        Block(f with { Request = r with { Pre = r.Pre with { HistoryStatus = "UNKNOWN" } } }, "HISTORY_UNVERIFIED");
        Block(f with { Request = r with { Pre = r.Pre with { AppliedMigrationIds = [r.Source.ManagedMigrationIds[1]] } } }, "HISTORY_NOT_VALID_PREFIX");
        foreach (var ids in new[] { new[] { r.Source.ManagedMigrationIds[0], r.Source.ManagedMigrationIds[0] }, r.Source.ManagedMigrationIds.Reverse().ToArray() })
            Block(f with { Request = r with { Source = r.Source with { ManagedMigrationIds = ids } } }, "MIGRATION_SEQUENCE_INVALID");
    }
    private static async Task CoverageNegatives()
    {
        var f = await Make(); var r = f.Request;
        Block(f with { Request = r with { Pre = r.Pre with { MetadataCoverage = "PARTIAL" } } }, "SCHEMA_COVERAGE_REQUIRED");
        Block(f with { Request = r with { ExpectedPost = r.ExpectedPost with { DataHash = "" } } }, "DATA_SECURITY_EVIDENCE_REQUIRED");
        var incomplete = f with { Request = r with { Pre = r.Pre with { ImpactCoverage = "UNKNOWN" } } };
        Block(incomplete, "IMPACT_COVERAGE_REQUIRED");
        Assert(incomplete.Build().Plan!.Read().Risk is null, "Unknown impact received a final risk rating");
        var post = Copy(r.ExpectedPost); post.Schema.ImpactMetrics.Clear();
        Block(f with { Request = r with { ExpectedPost = post } }, "IMPACT_COVERAGE_REQUIRED");
        var duplicate = Copy(r.Pre);
        duplicate.Schema.Objects.Add(new() { Kind = " table ", Schema = "dbo", Name = " __EFMigrationsHistory " });
        Block(f with { Request = r with { Pre = duplicate } }, "SCHEMA_OBJECTS_INVALID");
        var noOperations = f with { Forward = ReleaseScript.FromText("forward", "SELECT 1;"), Rollback = ReleaseScript.FromText("rollback", "SELECT 1;") };
        var payload = ReleasePayloadBuilder.Build(new() { ReleaseId = r.ReleaseId, Environment = "TEST", SourceKind = "EF", Scenario = "EXISTING_EF", DatabaseLifecycle = "EXISTING" }, noOperations.Forward, noOperations.Rollback);
        noOperations = noOperations with { Request = r with { Recovery = r.Recovery with { PayloadHash = payload.PayloadHash } } };
        Block(noOperations, "SQL_ANALYSIS_INCOMPLETE");
        Assert(noOperations.Build().Plan!.Read().Risk is null, "Unqualified SQL received a final risk rating");
    }
    private static async Task RecoveryNegatives()
    {
        var f = await Make(); var r = f.Request;
        Block(f with { Request = r with { Recovery = r.Recovery with { PayloadHash = Hash("swapped") } } }, "RECOVERY_PAYLOAD_BINDING_MISMATCH");
        Block(f with { Request = r with { Recovery = r.Recovery with { ExerciseEvidence = null } } }, "RECOVERY_EXERCISE_REQUIRED");
        Block(f with { Request = r with { Recovery = r.Recovery with { StrategyApproval = null } } }, "RECOVERY_APPROVAL_REQUIRED");
        Block(f with { Request = r with { Recovery = r.Recovery with { ExerciseEvidence = Ref("exercise", Hash("forged")) } } }, "RECOVERY_NOT_VERIFIED");
        Block(f with { Request = r with { Recovery = r.Recovery with { RecoveredPre = r.Recovery.RecoveredPre! with { DataHash = Hash("lost") } } } }, "RECOVERY_PRE_MISMATCH");
        Block(f with { Request = r with { Recovery = r.Recovery with { ReappliedPost = r.Recovery.ReappliedPost! with { AppliedMigrationIds = r.Pre.AppliedMigrationIds } } } }, "RECOVERY_REAPPLY_MISMATCH");
        var inconsistent = Copy(r.Recovery.Exercise!);
        inconsistent.Pre!.Document.Objects[0].Properties.Add("contradiction", "not-in-canonical-json");
        Block(f with { Request = r with { Recovery = r.Recovery with { Exercise = inconsistent,
            ExerciseEvidence = Ref("exercise", Hashing.Sha256(JsonSerializer.Serialize(inconsistent, JsonDefaults.Compact))) } } }, "RECOVERY_NOT_VERIFIED");
        Block(f with { Request = r with { Recovery = r.Recovery with { RecoveredPre = r.Recovery.RecoveredPre! with {
            CapturedAtUtc = r.ExpectedPost.CapturedAtUtc.AddSeconds(-1) } } } }, "QUALIFICATION_TIME_INVALID");
    }
    private static async Task Strategies()
    {
        var f = await Make();
        foreach (var strategy in new[] { RollbackCapability.RestoreRequired, RollbackCapability.ForwardFixOnly, RollbackCapability.SchemaOnly })
            Block(f with { Request = f.Request with { Recovery = f.Request.Recovery with { Strategy = strategy } } }, "RECOVERY_STRATEGY_EVIDENCE_ADAPTER_REQUIRED");
        Block(f with { Request = f.Request with { Recovery = f.Request.Recovery with { Strategy = RollbackCapability.Unknown } } }, "RECOVERY_STRATEGY_REQUIRED");
    }
    private static async Task Revalidation()
    {
        var f = await Make(); var p = Consistent(f); var current = f.Request.Pre with { CapturedAtUtc = Now, Evidence = Ref("fresh-pre") };
        Assert(EfReleasePlannerV1.Revalidate(p, p.Sha256, current, f.Forward, f.Rollback, Now).Status == "PRE_MATCH_NOT_AUTHORIZED", "Valid PRE rejected");
        foreach (var state in new[] { current with { DataHash = Hash("drift") }, current with { SecurityHash = Hash("grant drift") }, current with { DatabaseIncarnation = "replacement" } })
            Assert(EfReleasePlannerV1.Revalidate(p, p.Sha256, state, f.Forward, f.Rollback, Now).Status == "BLOCKED", "PRE drift accepted");
        Assert(EfReleasePlannerV1.Revalidate(p, Hash("forged"), current, f.Forward, f.Rollback, Now).Status == "BLOCKED", "Trusted hash ignored");
        Assert(EfReleasePlannerV1.Revalidate(p, p.Sha256, current, ReleaseScript.FromText("forward", f.Forward.Text + " "), f.Rollback, Now).Status == "BLOCKED", "Byte swap accepted");
        Assert(EfReleasePlannerV1.Revalidate(p, p.Sha256, current, f.Forward, f.Rollback, Now.AddHours(1)).Status == "BLOCKED", "Expired plan accepted");
    }
    private static async Task Post()
    {
        var f = await Make(); var p = Consistent(f); var manifest = p.Read();
        var receipt = new EfPlanExecutionEvidenceV1(f.Request.OperationId, f.Request.Authority.Target.TargetId, p.Sha256,
            manifest.Payload.PayloadHash, manifest.Payload.ForwardHash, "SUCCEEDED", Now, Now.AddSeconds(1), Ref("execution"));
        var post = f.Request.ExpectedPost with { Evidence = Ref("independent-post"), CapturedAtUtc = Now.AddSeconds(2) };
        EfPlanResultV1 Verify(EfPlanExecutionEvidenceV1 e, EfPlanObservationV1 o) => EfReleasePlannerV1.VerifyPost(p, p.Sha256, e, o, Now.AddSeconds(3));
        Assert(Verify(receipt, post).Status == "POST_MATCH_NOT_CERTIFIED", "Valid POST rejected");
        foreach (var outcome in new[] { "UNKNOWN", "FAILED", "PARTIAL" }) Assert(Verify(receipt with { Outcome = outcome }, post).Status == "BLOCKED", "Uncertain execution accepted");
        foreach (var observation in new[] { post with { AppliedMigrationIds = f.Request.Pre.AppliedMigrationIds }, post with { DataHash = Hash("bad") }, post with { SecurityHash = Hash("bad") }, post with { MetadataCoverage = "PARTIAL" }, post with { Evidence = f.Request.ExpectedPost.Evidence } })
            Assert(Verify(receipt, observation).Status == "BLOCKED", "Partial/mismatched POST accepted");
        var changed = Copy(post); changed.Schema.Objects.Add(new() { Kind = "table", Schema = "dbo", Name = "Unapproved" });
        Assert(Verify(receipt, changed).Status == "BLOCKED", "Unapproved schema drift accepted");
        Assert(Verify(receipt with { ForwardHash = Hash("wrong") }, post).Status == "BLOCKED", "Different execution accepted");
    }
    private static async Task Pilot()
    {
        var f = await Make("CICDV3", 7, 1); var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ef1009");
        var up = ReleaseScript.FromFile("forward", Path.Combine(dir, "forward.sql"));
        var down = ReleaseScript.FromFile("rollback", Path.Combine(dir, "rollback.sql"));
        Assert(up.Sha256 == "309581d85a1525f03f0125337ec39655f89e8e6a2e4a02d48c415a096024b47f", "Real fixture changed");
        Assert(down.Sha256 == "c05451e73ba4ece0e69a21e6b4d8aa9dff227009cd37b1ed7128ea452808f1f9", "Real rollback fixture changed");
        _ = EfTestMigrationCli.ExactBatches(new System.Text.UTF8Encoding(false, true).GetString(up.Bytes));
        // Recorded managed range; all observations/recovery below remain synthetic.
        // This proves a BLOCK, not a live CICDV3 certification or current DB history.
        string[] managed = ["20260616182600_AjusteModeloNet10", "20260618202000_AddActivoToPerson",
            "20260619160000_RemoveActivoFromPerson", "20260619184141_AjusteModeloPendiente",
            "20260624000000_AddTablaPrueba", "20260624000001_AddIndexPrueba",
            "20260630141200_AddTablaPrueba2", "20260701161500_AddSeblobTable"];
        var r = f.Request;
        f = f with { Request = r with {
            Source = r.Source with { ManagedMigrationIds = managed },
            Pre = r.Pre with { AppliedMigrationIds = managed[..7] },
            ExpectedPost = r.ExpectedPost with { AppliedMigrationIds = managed },
            Recovery = r.Recovery with {
                RecoveredPre = r.Recovery.RecoveredPre! with { AppliedMigrationIds = managed[..7] },
                ReappliedPost = r.Recovery.ReappliedPost! with { AppliedMigrationIds = managed } } } };
        var payload = ReleasePayloadBuilder.Build(new() { ReleaseId = f.Request.ReleaseId, Environment = "TEST", SourceKind = "EF", Scenario = "EXISTING_EF", DatabaseLifecycle = "EXISTING" }, up, down);
        f = f with { Forward = up, Rollback = down, Request = f.Request with { Recovery = f.Request.Recovery with { PayloadHash = payload.PayloadHash } } };
        var result = f.Build();
        Assert(result.Status == "BLOCKED" && result.Reasons.Contains("SQL_ANALYSIS_INCOMPLETE") && result.Reasons.Contains("RECOVERY_NOT_VERIFIED"), "Parser acceptance masqueraded as qualification");
        Assert(result.Plan!.Read().PendingMigrationIds.SequenceEqual(["20260701161500_AddSeblobTable"]), "Recorded managed prefix changed");
    }
    private static async Task Malformed()
    {
        var f = await Make();
        Block(f with { Request = f.Request with { Authority = null! } }, "EF_PLAN_INPUT_INVALID");
        Block(f with { Forward = new("forward", [0xff]) }, "EF_PLAN_INPUT_INVALID");
        Block(f with { Forward = ReleaseScript.FromText("rollback", f.Forward.Text) }, "SQL_ROLE_INVALID");
        Block(f with { Forward = ReleaseScript.FromText("forward", "\uFEFF\uFEFFSELECT 1;") }, "SQL_ENCODING_INVALID");
        Block(f with { Request = f.Request with { Source = f.Request.Source with { RepositoryEvidence = Ref("repo") with { StorageLocator = "https://example/secret" } } } }, "EVIDENCE_LOCATOR_NOT_SUPPORTED");
    }
}
