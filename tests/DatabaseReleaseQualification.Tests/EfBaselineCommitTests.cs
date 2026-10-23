using DatabaseReleaseQualification;

internal static class EfBaselineCommitTests
{
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF baseline: POST candidate connects existing commit protocol and store", Positive),
        ("EF baseline: first commit then governed pending release then no changes", Successor),
        ("EF baseline: missing host consent lineage and changed POST block", Negative)
    ];
    private static void Assert(bool b, string why) { if (!b) throw new Exception(why); }
    private sealed record Fixture(EfPreparationTests.Fixture F, EfReleasePlanV1 Plan, EvidenceReference Execution,
        EvidenceReference Post, EvidenceReference Transition, EvidenceReference Commit, InMemoryCertificationCommitStore Store)
    {
        public Task<EfBaselineCommitResultV1> Run(bool store = true) => new EfEvidencePreparationV1(F.Provider,
            certificationStore: store ? Store : null).CommitBaselineAsync(Plan, Plan.Sha256, F.Selection.Scope,
                Execution, Post, Transition, Commit, EfPreparationTests.Now.AddSeconds(3));
    }
    private static async Task<Fixture> Make(string mutation = "", EfPreparationTests.Fixture? existing = null,
        InMemoryCertificationCommitStore? store = null, ExpectedCertificationPointer? predecessor = null)
    {
        var f = existing ?? await EfPreparationTests.Make("First", 0, 1, initial: true);
        var r = await f.Run(); var plan = r.PlanResult!.Plan!; var p = plan.Read();
        var e = EfPreparationTests.Execution(f, plan); var now = EfPreparationTests.Now;
        var transition = f.Provider.Publish("transition-authorization", "BASELINE_TRANSITION_AUTHORIZATION",
            new EfBaselineAuthorizationV1(p.Authority.Target.TargetId, p.OperationId, plan.Sha256,
                p.Authority.CertifiedBaseline?.Provenance.Document, now, now.AddMinutes(8), new() {
                PolicyId = "offline-dba-policy", Decision = DeploymentAuthorizationDecision.Authorized,
                Requirement = DeploymentAuthorizationRequirement.DbaApproval, AuthorizationReference = "offline-approval",
                Risk = p.Risk!.FinalRisk, ReleaseQualificationGatePassed = true, AnalysisConfidence = p.Risk.AnalysisConfidence,
                SensitiveChange = true, SchemaRollbackValidity = p.Recovery.SchemaValidity,
                DataRollbackValidity = p.Recovery.DataValidity, RollbackCapability = p.Recovery.Strategy }));
        var lineage = f.Provider.Publish("certified-lineage", "CERTIFIED_EF_LINEAGE", new EfCertifiedLineageV1(f.Selection.Scope,
            p.OperationId, plan.Sha256, mutation == "lineage" ? [] : p.ExpectedPost.AppliedMigrationIds, p.ExpectedPost.SchemaHash));
        var context = new EfBaselineCommitContextV1(1, f.Selection.Scope, p.OperationId, plan.Sha256,
            predecessor is null ? "ad3ea9da-6ba8-4a7b-87b1-ac41627d5e7e" : "bd3ea9da-6ba8-4a7b-87b1-ac41627d5e7e", predecessor, transition, new() {
                RegistryRepository = "platform/governance", RegistryRef = new string('a', 40), RegistryCommitSha = new string('a', 40),
                RegistryFilePath = "registry/test.json", RegistryFileSha256 = Hashing.Sha256("offline-registry") }, lineage,
            now, now.AddMinutes(5), new("offline-actor", "offline-test", "offline-policy", "offline-commit-approval"));
        var commit = f.Provider.Publish("commit-authorization", "CERTIFICATION_COMMIT_AUTHORIZATION", context);
        if (mutation == "consent") f.Provider.Allowed.Remove((commit.EvidenceId, "CERTIFICATION_COMMIT_AUTHORIZATION"));
        if (mutation == "post") f.Provider.Content[e.Post.EvidenceId] = [1, 2, 3];
        return new(f, plan, e.Execution, e.Post, transition, commit, store ?? new());
    }
    private static async Task Positive()
    {
        var f = await Make(); var r = await f.Run();
        Assert(r.Status == "BASELINE_COMMITTED_GOVERNANCE_TRANSITION_REQUIRED", "initial commit: " + string.Join(",", r.Reasons));
        Assert(r.Commit!.Record!.CanonicalSchemaHash == f.Plan.Read().ExpectedPost.SchemaHash
            && f.Store.ListCertifiedHistory(r.Commit.DatabaseIdentity).Count == 1 && f.Store.PreparedRecordCount == 0,
            "baseline record not persisted in memory");
        r = await f.Run(); Assert(r.Commit?.Status == CertificationCommitStatus.IdempotentReplay
            && f.Store.ListCertifiedHistory(r.Commit.DatabaseIdentity).Count == 1,
            "same operation did not reuse existing protocol idempotency: " + string.Join(",", r.Reasons));
    }
    private static async Task Negative()
    {
        foreach (var mutation in new[] { "host", "consent", "lineage", "post" })
        {
            var f = await Make(mutation); var r = await f.Run(mutation != "host");
            Assert(r.Status == "BLOCKED" && f.Store.PreparedRecordCount == 0, mutation + " committed");
        }
    }
    private static async Task Successor()
    {
        var first = await Make(); var committed = await first.Run(); var previous = committed.Commit!.Record!;
        var pointer = committed.Commit.CurrentPointer!; var original = first.F.Original.Request;
        var pre = original.ExpectedPost;
        var post = System.Text.Json.JsonSerializer.Deserialize<EfPlanObservationV1>(
            System.Text.Json.JsonSerializer.Serialize(pre, JsonDefaults.Compact), JsonDefaults.Compact)!;
        post.Schema.Objects.Add(new() { Kind = "table", Schema = "dbo", Name = "NextItem" });
        post.Schema.ImpactMetrics.Add(new() { Schema = "dbo", Table = "NextItem", RowCount = 0 });
        var id = "20260201120000_Next";
        post = post with { AppliedMigrationIds = [.. pre.AppliedMigrationIds, id] };
        var up = ReleaseScript.FromText("forward", $"CREATE TABLE dbo.NextItem(Id int); INSERT INTO dbo.__EFMigrationsHistory(MigrationId,ProductVersion) VALUES(N'{id}',N'10.0.9');");
        var down = ReleaseScript.FromText("rollback", $"DROP TABLE dbo.NextItem; DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'{id}';");
        var release = new ReleaseDescriptor { ReleaseId = "second", Environment = "TEST", SourceKind = "EF", Scenario = "EXISTING_EF", DatabaseLifecycle = "EXISTING" };
        var lab = new EfReleasePlanTests.Lab(pre.Schema, post.Schema);
        var exercise = await new RehearsalEngine().QualifyAsync(release, new() { ConsistencyStatus = "CONSISTENT", ConsistencyReason = "OFFLINE_SUCCESSOR" }, up, down, lab, lab);
        var payload = ReleasePayloadBuilder.Build(release, up, down);
        var recordBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(previous, JsonDefaults.Compact);
        var baseline = new EvidenceReference { EvidenceId = "first-certified-record", EvidenceSha256 = Hashing.Sha256(recordBytes) };
        var exerciseRef = new EvidenceReference { EvidenceId = "second-exercise", EvidenceSha256 = Hashing.Sha256(System.Text.Json.JsonSerializer.Serialize(exercise, JsonDefaults.Compact)) };
        var request = original with { ReleaseId = "second", OperationId = "00000000-0000-0000-0000-000000000002",
            Source = original.Source with { Revision = new string('c', 40), ManagedMigrationIds = post.AppliedMigrationIds },
            Authority = original.Authority with { Scenario = "EXISTING_EF", Lifecycle = "EXISTING", OnboardingState = "MANAGED", BootstrapEvidence = null,
                CertifiedBaseline = new(previous.CanonicalSchemaHash, new("platform/governance", new string('d', 40), baseline)) },
            Pre = pre, ExpectedPost = post, Recovery = original.Recovery with { PayloadHash = payload.PayloadHash,
                Exercise = exercise, ExerciseEvidence = exerciseRef, RecoveredPre = pre, ReappliedPost = post } };
        var f = await EfPreparationTests.Make(original: new(request, up, down), previous: previous);
        f.Provider.Admit(baseline, "CURRENT_CERTIFIED_BASELINE", recordBytes);
        var second = await Make(existing: f, store: first.Store, predecessor: new(pointer.CertificationId, pointer.CertificationEvidenceHash, pointer.Version));
        var result = await second.Run();
        Assert(result.Status == "BASELINE_COMMITTED_GOVERNANCE_TRANSITION_REQUIRED"
            && first.Store.ListCertifiedHistory(previous.DatabaseIdentity).Count == 2, "successor commit: " + string.Join(",", result.Reasons));
        var current = result.Commit!.Record!;
        // Governance transition is simulated explicitly; no Registry is written.
        var currentRef = f.Provider.Publish("second-certified-record", "CERTIFIED_BASELINE", current);
        var nextPre = EfPreparationTests.Observe(f.Provider, post, "TARGET_PRE", f.Governance.ContentHashContract);
        f = f.Replace("TARGET_PRE", nextPre);
        f = f.Replace("SOURCE", new EfSourcePreparationV1(request.Source, [], []));
        f = f.Replace("GOVERNANCE", f.Governance with { Authority = request.Authority with {
            CertifiedBaseline = new(current.CanonicalSchemaHash, new("platform/governance", new string('e', 40), currentRef)) } });
        var noChanges = await f.Run(); Assert(noChanges.Status == "NO_CHANGES", "successive NO_CHANGES: " + string.Join(",", noChanges.Reasons));
    }
}
