using System.Text.Json;
using DatabaseReleaseQualification;

internal static class EfInitialRecoveryTests
{
    private static readonly DateTimeOffset Now = EfPreparationTests.Now;
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Compact);
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(Json(value), JsonDefaults.Compact)!;
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static EvidenceReference Ref(string id, object value) => new() { EvidenceId = id, EvidenceSha256 = Hashing.Sha256(Json(value)) };
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF initial recovery: legitimate ABSENT to EMPTY uses existing rehearsal", Positive),
        ("EF initial recovery: authenticated A2.1 acquisition reaches A1 plan", Integrated),
        ("EF initial recovery: grants denies owners roles and unrelated objects block", SecurityNegatives),
        ("EF initial recovery: tampered missing partial replay and reapply evidence block", EvidenceNegatives),
        ("EF initial recovery: strict default and other scenarios cannot opt in", DefaultStrict)
    ];

    internal static SchemaSnapshot History() => new() { Objects = [
        new() { Kind = "table", Schema = "dbo", Name = "__EFMigrationsHistory" },
        new() { Kind = "column", Schema = "dbo", Parent = "__EFMigrationsHistory", Name = "MigrationId",
            Properties = new() { ["dataType"] = "nvarchar", ["maxLength"] = "300", ["nullable"] = "false" } },
        new() { Kind = "column", Schema = "dbo", Parent = "__EFMigrationsHistory", Name = "ProductVersion",
            Properties = new() { ["dataType"] = "nvarchar", ["maxLength"] = "64", ["nullable"] = "false" } },
        new() { Kind = "key-constraint-column", Schema = "dbo", Parent = "__EFMigrationsHistory", Name = "PK_History",
            Properties = new() { ["constraintType"] = "PK", ["column"] = "MigrationId", ["ordinal"] = "1" } }
    ], ImpactMetrics = [new() { Schema = "dbo", Table = "__EFMigrationsHistory", RowCount = 0 }] };

    private sealed class Lab : IRehearsalDatabase, IRecoverySecurityEvidenceProvider,
        IDataRollbackValidationContract, IDataReapplyValidationContract, IEfInitialRecoveryObserverV1
    {
        public int Writes;
        public Func<EfInitialPhaseV1, EfInitialPhaseV1>? Mutate;
        public readonly SchemaSnapshot[] Schemas;
        public EfInitialRecoverySelectionV1 Selection;
        public ReleaseDescriptor Release = new() { ReleaseId = "initial", Environment = "TEST", SourceKind = "EF", Scenario = "NEW_EF", DatabaseLifecycle = "NEW" };
        public readonly ReleaseScript Up = ReleaseScript.FromText("forward", "CREATE TABLE dbo.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL, ProductVersion nvarchar(32) NOT NULL); CREATE TABLE dbo.Items (Id int NOT NULL); INSERT INTO dbo.__EFMigrationsHistory VALUES (N'20260101120000_Init',N'10.0.9');");
        public readonly ReleaseScript Down = ReleaseScript.FromText("rollback", "DROP TABLE dbo.Items; DELETE FROM dbo.__EFMigrationsHistory;");
        private static readonly RecoverySecuritySecurable HistoryObject = new("OBJECT", "dbo", "__EFMigrationsHistory");
        public Lab()
        {
            var history = History(); var post = Copy(history);
            post.ImpactMetrics[0] = new() { Schema = "dbo", Table = "__EFMigrationsHistory", RowCount = 1 };
            post.Objects.Add(new() { Kind = "table", Schema = "dbo", Name = "Items" });
            post.ImpactMetrics.Add(new() { Schema = "dbo", Table = "Items", RowCount = 0 });
            Schemas = [new(), post, history, Copy(post)];
            var target = new EfPlanTargetV1("lab", "app", "TEST", "lab-endpoint", "FakeLab", "ALLOW_LIST", ["FAKE_SQL"], "business/app");
            var policy = new EfInitialRecoveryPolicyV1(1, "NEW_EF_ABSENT_EMPTY_V1",
                new("production-target", "business/app", new string('a', 40), "session"), "initial",
                "8457c2e1-6a4d-4d04-86b4-2ad10f093bdd", target, "lab-incarnation",
                ReleasePayloadBuilder.Build(Release, Up, Down).PayloadHash, Up.Sha256, Down.Sha256,
                Ref("bootstrap", "bootstrap"), Ref("contract", "contract"), history,
                new(HistoryObject, true, null, "dbo", []), Now.AddMinutes(-2), Now.AddMinutes(10));
            Selection = new(Ref("initial-policy", policy), policy);
        }
        public Task<SchemaSnapshot> CaptureSchemaAsync(CancellationToken token = default) => Task.FromResult(Copy(Schemas[Writes]));
        public Task ExecuteSqlAsync(ReleaseScript script, string hash, CancellationToken token = default)
        { Assert(script.Sha256 == hash && Writes < 3, "exact bytes or retry violation"); Writes++; return Task.CompletedTask; }
        private EfFullSecurityV1 Full(RecoveryPhase phase)
        {
            var scope = new RecoverySecurityScope([new("DATABASE"), new("SCHEMA", "dbo"), HistoryObject, new("OBJECT", "dbo", "Items")], ["dbo", "reader"]);
            var s = RecoveryCoverageTests.Security(scope, phase, Schemas[(int)phase]);
            s = s with { Securables = s.Securables.Select(x => x.Securable.Kind == "DATABASE"
                ? x with { Permissions = [new("SELECT", "G", "reader", "dbo")] } : x).ToArray() };
            return new(s, [new("reader", "dbo")], "01");
        }
        public Task<EfInitialPhaseV1> CaptureAsync(RecoveryPhase phase, SchemaSnapshot schema, CancellationToken token)
        {
            var security = Full(phase);
            var o = new EfPlanObservationV1("lab", "TEST", "lab-endpoint", "FAKE_SQL", "FakeLab", "lab-incarnation",
                phase == RecoveryPhase.Pre ? "ABSENT" : phase == RecoveryPhase.Pre2 ? "EMPTY" : "PRESENT",
                phase is RecoveryPhase.Pre or RecoveryPhase.Pre2 ? [] : ["20260101120000_Init"], schema,
                "COMPLETE", "COMPLETE", Hashing.Sha256("empty-business-data"), EfInitialRecoveryV1.SecurityHash(security),
                Ref("capture-" + phase, schema), Now.AddSeconds(-4 + (int)phase));
            var p = new EfInitialPhaseV1(phase, Selection.Policy.Scope, o, security, 0, Ref("security-" + phase, security));
            return Task.FromResult(Mutate?.Invoke(p) ?? p);
        }
        public Task<RecoverySecuritySnapshot> CaptureSecurityAsync(RecoverySecurityScope scope, RecoveryPhase phase, CancellationToken token = default)
            => Task.FromResult(RecoveryCoverageTests.Security(scope, phase, Schemas[(int)phase]));
        public Task CapturePreDataAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task CapturePostDataAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<DataRollbackValidity> ValidateRollbackDataAsync(CancellationToken token = default) => Task.FromResult(DataRollbackValidity.Valid);
        public Task<DataRollbackValidity> ValidateReapplyDataAsync(CancellationToken token = default) => Task.FromResult(DataRollbackValidity.Valid);
        public Task<RehearsalResult> Run(bool strict = false, ReleaseDescriptor? release = null) => strict
            ? new RehearsalEngine().QualifyAsync(release ?? Release, new() { ConsistencyStatus = "CONSISTENT", ConsistencyReason = "OFFLINE_FIXTURE" }, Up, Down, this, this)
            : new RehearsalEngine().QualifyEfInitialAsync(release ?? Release, new() { ConsistencyStatus = "CONSISTENT", ConsistencyReason = "OFFLINE_FIXTURE" }, Up, Down, this, Selection, this, this);
    }
    private static async Task Positive()
    {
        var lab = new Lab(); var r = await lab.Run();
        Assert(r.CanProceed && r.RecoveryCoverage!.Complete && lab.Writes == 3, "legitimate initial recovery blocked: " + r.QualificationStatus);
        Assert(r.RollbackDiff is { IsEquivalent: false } && r.ReapplyDiff is { IsEquivalent: true }, "raw diffs were hidden");
        var copy = Copy(r.RecoveryCoverage);
        Assert(copy!.Complete, "serialized evidence lost validity");
    }
    private static EfInitialPhaseV1 Rehash(EfInitialPhaseV1 p, EfFullSecurityV1 security) => p with {
        Security = security, SecurityEvidence = Ref("security-" + p.Phase, security),
        Observation = p.Observation with { SecurityHash = EfInitialRecoveryV1.SecurityHash(security) } };
    private static async Task Integrated()
    {
        var f = await EfPreparationTests.Make("Initial", 0, 1, initial: true);
        var lab = new Lab();
        lab.Release = new() { ReleaseId = f.Selection.ReleaseId, Environment = "TEST", SourceKind = "EF", Scenario = "NEW_EF", DatabaseLifecycle = "NEW" };
        var source = f.Original.Request.Source with { ManagedMigrationIds = ["20260101120000_Init"] };
        var sourceRef = f.Provider.Publish("initial-source", "SOURCE", new EfSourcePreparationV1(source, lab.Up.Bytes, lab.Down.Bytes));
        f = f with { Selection = f.Selection with { Source = sourceRef } };
        var template = await lab.CaptureAsync(RecoveryPhase.Pre, lab.Schemas[0], default);
        var target = f.Governance.Authority.Target;
        var targetPre = template.Observation with { TargetId = target.TargetId, EndpointReference = target.EndpointReference,
            ServerInstance = target.AllowedServerInstances[0], DatabaseName = target.DatabaseName,
            DatabaseIncarnation = "target-incarnation", Physical = SqlDiscovery.V2.EmptyForNewEfV1.Complete(0, new long[SqlDiscovery.V2.EmptyForNewEfV1.Categories.Count]) };
        var pre = EfPreparationTests.Observe(f.Provider, targetPre, "TARGET_PRE", f.Governance.ContentHashContract);
        f = f.Replace("TARGET_PRE", pre) with { Pre = pre };
        f = EfBootstrapTests.Admit(f);
        var policy = lab.Selection.Policy with { Scope = f.Selection.Scope, ReleaseId = f.Selection.ReleaseId,
            OperationId = f.Selection.OperationId, Bootstrap = f.Governance.Authority.BootstrapEvidence!,
            ContentHashContract = f.Governance.ContentHashContract,
            PayloadHash = ReleasePayloadBuilder.Build(lab.Release, lab.Up, lab.Down).PayloadHash,
            RehearsalTarget = lab.Selection.Policy.RehearsalTarget with { SourceRepository = source.Repository } };
        lab.Selection = new(f.Provider.Publish("initial-policy", "EF_INITIAL_RECOVERY_POLICY", policy), policy);
        var captures = new Dictionary<RecoveryPhase, EfObservationEvidenceV1>();
        lab.Mutate = p => {
            var purpose = p.Phase switch { RecoveryPhase.Pre => "REHEARSAL_PRE", RecoveryPhase.Post1 => "QUALIFIED_POST",
                RecoveryPhase.Pre2 => "RECOVERED_PRE", _ => "REAPPLIED_POST" };
            var o = EfPreparationTests.Observe(f.Provider, p.Observation, purpose, f.Governance.ContentHashContract);
            captures[p.Phase] = o;
            return p with { Observation = o.Observation, SecurityEvidence = f.Provider.Publish("security-" + p.Phase,
                "EF_INITIAL_" + p.Phase.ToString().ToUpperInvariant(), p.Security) };
        };
        var exercise = await lab.Run(); Assert(exercise.CanProceed, "integration rehearsal failed: " + exercise.QualificationStatus);
        var approval = f.Provider.Publish("initial-approval", "RECOVERY_APPROVAL", new EfRecoveryApprovalV1(target.TargetId,
            policy.PayloadHash, RollbackCapability.FullReversible, Now.AddMinutes(5)));
        var recovery = new EfPlanRecoveryV1(RollbackCapability.FullReversible, approval,
            f.Provider.Publish("initial-exercise", "RECOVERY_EXERCISE", exercise), "lab", policy.PayloadHash,
            exercise, captures[RecoveryPhase.Pre2].Observation, captures[RecoveryPhase.Post2].Observation);
        var q = new EfQualificationEvidenceV1(target.TargetId, source.Revision, policy.PayloadHash, policy.RehearsalTarget,
            captures[RecoveryPhase.Pre], captures[RecoveryPhase.Post1], captures[RecoveryPhase.Pre2], captures[RecoveryPhase.Post2], recovery);
        f = f.Replace("ISOLATED_QUALIFICATION", q);
        f = EfUnifiedClassificationTests.Admit(f);
        var result = await f.Run();
        Assert(result.Status == "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED", "A2.1 initial recovery: " + string.Join(",", result.Reasons));
        Assert(result.PlanResult!.Plan!.Read().Acquisition!.InitialRecovery is not null && !result.ExecutionAuthorized, "initial evidence/authority boundary lost");
        f.Provider.Allowed.Remove((lab.Selection.Reference.EvidenceId, "EF_INITIAL_RECOVERY_POLICY"));
        result = await f.Run(); Assert(result.Status == "BLOCKED", "untrusted policy reached plan");
    }
    private static async Task SecurityNegatives()
    {
        foreach (var mutation in new[] { "grant", "deny", "owner", "database-owner", "role", "principal", "schema-permission", "hidden-object" })
        {
            var lab = new Lab { Mutate = p => {
                if (p.Phase != RecoveryPhase.Pre2) return p;
                var s = p.Security; var snapshot = s.Snapshot;
                if (mutation == "database-owner") s = s with { DatabaseOwnerSid = "02" };
                else if (mutation == "role") s = s with { RoleMemberships = [] };
                else if (mutation == "principal") s = s with { Snapshot = snapshot with { Principals = snapshot.Principals.Select(x => x.Name == "reader" ? x with { SidHex = "03" } : x).ToArray() } };
                else if (mutation == "hidden-object") s = s with { Snapshot = snapshot with { Securables = snapshot.Securables.Select(x => x.Securable.Name == "Items" ? x with { Exists = true } : x).ToArray() } };
                else s = s with { Snapshot = snapshot with { Securables = snapshot.Securables.Select(x =>
                    mutation != "owner" && x.Securable.Kind == (mutation == "schema-permission" ? "SCHEMA" : "DATABASE")
                        ? x with { Permissions = [new(mutation == "grant" ? "UPDATE" : "SELECT", mutation == "deny" ? "D" : "G", "reader", "dbo")] }
                        : mutation == "owner" && x.Securable.Kind == "SCHEMA" ? x with { ExplicitOwner = "reader" } : x).ToArray() } };
                return Rehash(p, s);
            } };
            var r = await lab.Run(); Assert(!r.CanProceed && lab.Writes <= 2, mutation + " was accepted");
        }
    }
    private static async Task EvidenceNegatives()
    {
        foreach (var mutation in new[] { "hash", "missing", "partial", "identity", "replay", "other-object", "data", "post2" })
        {
            var lab = new Lab { Mutate = p => {
                if (mutation == "post2" && p.Phase == RecoveryPhase.Post2) return p with { Observation = p.Observation with { DataHash = Hashing.Sha256("divergence") } };
                if (p.Phase != RecoveryPhase.Pre2) return p;
                return mutation switch {
                    "hash" => p with { Observation = p.Observation with { SecurityHash = new string('0', 64) } },
                    "missing" => throw new InvalidOperationException("missing capture"),
                    "partial" => p with { Observation = p.Observation with { MetadataCoverage = "PARTIAL" } },
                    "identity" => p with { Observation = p.Observation with { DatabaseIncarnation = "other" } },
                    "replay" => p with { Phase = RecoveryPhase.Pre },
                    "other-object" => p with { Observation = p.Observation with { Schema = new() { Objects = [new() { Kind = "table", Schema = "dbo", Name = "Unexpected" }] } } },
                    "data" => p with { BusinessRowCount = 1 }, _ => p };
            } };
            var r = await lab.Run(); Assert(!r.CanProceed && r.QualificationStatus.StartsWith("BLOCKED"), mutation + " was accepted");
        }
    }
    private static async Task DefaultStrict()
    {
        var lab = new Lab(); var r = await lab.Run(strict: true);
        Assert(!r.CanProceed && lab.Writes == 2, "default strict recovery relaxed");
        foreach (var scenario in new[] { "EXISTING_EF", "EXISTING_LEGACY", "TRADITIONAL" })
        {
            lab = new(); r = await lab.Run(release: new() { ReleaseId = "initial", Environment = "TEST", SourceKind = "EF", Scenario = scenario, DatabaseLifecycle = "EXISTING" });
            Assert(!r.CanProceed && lab.Writes == 0, scenario + " entered exception");
        }
    }
}
