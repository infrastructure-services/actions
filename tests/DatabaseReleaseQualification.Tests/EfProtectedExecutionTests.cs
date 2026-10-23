using DatabaseReleaseQualification;

internal static class EfProtectedExecutionTests
{
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF protected execution: exact bytes, receipt and no duplicate write", Positive),
        ("EF protected execution: concurrent requests claim one simulated write", Concurrent),
        ("EF protected execution: produced receipt is consumed by POSTCHECK", Postcheck),
        ("EF protected execution: no authority, drift and payload substitutions block", Negative),
        ("EF protected execution: timeout cancellation and receipt failure never retry", Uncertain)
    ];
    private static void Assert(bool b, string why) { if (!b) throw new Exception(why); }
    private sealed class Host : IEfProtectedExecutionHostV1, IRehearsalDatabase
    {
        public int Writes; public string? Hash; public string? Fault; public readonly List<string> Outcomes = [];
        public EfPreparationTests.Provider? Provider;
        public TaskCompletionSource? WriteEntered;
        public TaskCompletionSource? ReleaseWrite;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> started = new();
        public Task<IRehearsalDatabase?> BeginOnceAsync(EfExecutionStartV1 start, CancellationToken token)
            => Task.FromResult<IRehearsalDatabase?>(started.TryAdd(start.OperationId, 0) ? this : null);
        public Task<EvidenceReference> RecordAsync(EfExecutionStartV1 start, string outcome, DateTimeOffset completedAtUtc)
        {
            if (Fault == "receipt") throw new IOException("fixture receipt unavailable");
            Outcomes.Add(outcome);
            if (Provider is not null)
            {
                var raw = Provider.Publish("protected-execution-event", "EXECUTION_RECEIPT_BYTES", new { start, outcome, completedAtUtc });
                return Task.FromResult(Provider.Publish("protected-execution-receipt", "EXECUTION_RECEIPT",
                    new EfPlanExecutionEvidenceV1(start.OperationId, start.Scope.TargetId, start.PlanHash,
                        start.PayloadHash, start.ForwardHash, outcome, start.StartedAtUtc, completedAtUtc, raw)));
            }
            return Task.FromResult(new EvidenceReference { EvidenceId = "offline-receipt", EvidenceSha256 = Hashing.Sha256(outcome) });
        }
        public Task<SchemaSnapshot> CaptureSchemaAsync(CancellationToken token = default) => throw new Exception("execution must not run a rehearsal");
        public async Task ExecuteSqlAsync(ReleaseScript script, string hash, CancellationToken token = default)
        {
            Interlocked.Increment(ref Writes); Assert(script.Sha256 == hash, "different bytes"); Hash = hash;
            WriteEntered?.TrySetResult();
            if (ReleaseWrite is not null) await ReleaseWrite.Task.WaitAsync(token);
            if (Fault == "timeout") throw new TimeoutException("fixture write uncertain");
            if (Fault == "cancel") throw new OperationCanceledException();
        }
    }
    private sealed record Fixture(EfPreparationTests.Fixture F, EfReleasePlanV1 Plan, EvidenceReference Pre,
        EvidenceReference Approval, Host Host)
    {
        public Task<EfProtectedExecutionResultV1> Run(bool host = true) => new EfEvidencePreparationV1(F.Provider, host ? Host : null)
            .ApplyProtectedAsync(Plan, Plan.Sha256, F.Selection.Scope, Pre, F.Selection.Source, Approval, EfPreparationTests.Now);
    }
    private static async Task<Fixture> Make(string change = "")
    {
        var f = await EfPreparationTests.Make(); var result = await f.Run(); var plan = result.PlanResult!.Plan!;
        var p = plan.Read();
        var fresh = EfPreparationTests.Observe(f.Provider, f.Pre.Observation with { CapturedAtUtc = EfPreparationTests.Now,
            DataHash = change == "drift" ? Hashing.Sha256("drift") : f.Pre.Observation.DataHash }, "FRESH_PRE", f.Governance.ContentHashContract);
        var reference = f.Provider.Publish("fresh-document", "FRESH_PRE", fresh);
        var consent = new EfApplyAuthorizationV1(1, f.Selection.Scope, p.OperationId, plan.Sha256,
            p.Payload.PayloadHash, p.Payload.ForwardHash, p.Pre.DatabaseIncarnation,
            EfPreparationTests.Now.AddMinutes(-1), EfPreparationTests.Now.AddMinutes(5), new() {
                PolicyId = "offline-policy", Risk = p.Risk!.FinalRisk, Requirement = DeploymentAuthorizationRequirement.DbaApproval,
                Decision = change == "denied" ? DeploymentAuthorizationDecision.NotAuthorized : DeploymentAuthorizationDecision.Authorized,
                AuthorizationReference = "offline-approval", ReleaseQualificationGatePassed = true,
                AnalysisConfidence = p.Risk.AnalysisConfidence, SchemaRollbackValidity = p.Recovery.SchemaValidity,
                DataRollbackValidity = p.Recovery.DataValidity, RollbackCapability = p.Recovery.Strategy });
        if (change == "expired") consent = consent with { ExpiresAtUtc = EfPreparationTests.Now };
        if (change == "wrong-plan") consent = consent with { PlanHash = new string('0', 64) };
        if (change == "target") consent = consent with { Scope = consent.Scope with { TargetId = "another-target" } };
        if (change == "incarnation") consent = consent with { DatabaseIncarnation = "replaced" };
        var approval = f.Provider.Publish("apply-approval", "EF_APPLY_AUTHORIZATION", consent);
        if (change == "role") f.Provider.Allowed.Remove((approval.EvidenceId, "EF_APPLY_AUTHORIZATION"));
        if (change == "payload") f = f.Replace("SOURCE", new EfSourcePreparationV1(f.Original.Request.Source, [1, 2], f.Original.Rollback.Bytes));
        return new(f, plan, reference, approval, new() { Provider = f.Provider });
    }
    private static async Task Positive()
    {
        var f = await Make(); var r = await f.Run();
        Assert(r.Status == "EXECUTED_POSTCHECK_REQUIRED" && f.Host.Writes == 1
            && f.Host.Hash == f.Plan.Read().Payload.ForwardHash && f.Host.Outcomes.SequenceEqual(["SUCCEEDED"]), "execution contract failed");
        r = await f.Run(); Assert(r.Status == "BLOCKED" && f.Host.Writes == 1, "duplicate execution retried");
    }
    private static async Task Negative()
    {
        foreach (var mutation in new[] { "denied", "expired", "wrong-plan", "target", "incarnation", "role", "drift", "payload", "host" })
        {
            var f = await Make(mutation); var r = await f.Run(mutation != "host");
            Assert(r.Status == "BLOCKED" && f.Host.Writes == 0, mutation + " executed");
        }
    }
    private static async Task Uncertain()
    {
        foreach (var fault in new[] { "timeout", "cancel", "receipt" })
        {
            var f = await Make(); f.Host.Fault = fault; var r = await f.Run();
            Assert(r.Status == "EXECUTION_UNCERTAIN" && f.Host.Writes == 1, fault + " lost uncertainty");
            r = await f.Run(); Assert(r.Status == "BLOCKED" && f.Host.Writes == 1, fault + " retried");
        }
    }
    private static async Task Concurrent()
    {
        var f = await Make();
        f.Host.WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Host.ReleaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Run();
        await f.Host.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await f.Run();
        Assert(second.Status == "BLOCKED" && f.Host.Writes == 1, "concurrent operation wrote twice");
        f.Host.ReleaseWrite.TrySetResult();
        Assert((await first).Status == "EXECUTED_POSTCHECK_REQUIRED", "original operation lost claim");
    }
    private static async Task Postcheck()
    {
        var f = await Make(); var execution = await f.Run();
        var post = EfPreparationTests.Execution(f.F, f.Plan);
        var result = await f.F.Service.VerifyPostAsync(f.Plan, f.Plan.Sha256, f.F.Selection.Scope,
            execution.Receipt!, post.Post, EfPreparationTests.Now.AddSeconds(3));
        Assert(result.Status == "POST_MATCH_NOT_CERTIFIED", "produced receipt failed POSTCHECK: " + string.Join(",", result.Reasons));
        var divergent = post.Observation with { Observation = post.Observation.Observation with { SecurityHash = Hashing.Sha256("changed grants") } };
        var reference = f.F.Provider.Publish("divergent-post", "ACTUAL_POST", divergent);
        result = await f.F.Service.VerifyPostAsync(f.Plan, f.Plan.Sha256, f.F.Selection.Scope,
            execution.Receipt!, reference, EfPreparationTests.Now.AddSeconds(3));
        Assert(result.Status == "BLOCKED", "divergent POST passed");
    }
}
