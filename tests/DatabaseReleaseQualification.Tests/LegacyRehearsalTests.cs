using System.Text;
using System.Text.Json;
using DatabaseReleaseQualification;

internal static class LegacyRehearsalTests
{
    public static readonly (string Name, Func<Task> Run)[] Cases = [
        ("macro2 synthetic complete production orchestration", Complete),
        ("macro2 missing authorization blocks before mutation", () => Negative("authority", 0)),
        ("macro2 mutated package qualification blocks", () => Negative("package", 0)),
        ("macro2 wrong target blocks", () => Negative("target", 0)),
        ("macro2 stale baseline blocks", () => Negative("baseline", 0)),
        ("macro2 drift before PRE blocks", () => Negative("drift", 0)),
        ("macro2 stale trusted observation blocks", () => Negative("stale", 0)),
        ("macro2 incomplete PRE blocks", () => Negative("pre", 0)),
        ("macro2 incomplete POST1 blocks without rollback", () => Negative("post1", 1)),
        ("macro2 forward failure stops without automatic rollback", () => Negative("forward", 1)),
        ("macro2 rollback failure stops without second forward", () => Negative("rollback", 2)),
        ("macro2 reapply failure is not FullReversible", () => Negative("reapply", 3)),
        ("macro2 PRE2 mismatch stops second forward", () => Negative("pre2", 2)),
        ("macro2 POST2 mismatch is not success", () => Negative("post2", 3)),
        ("macro2 mutable evidence detected between phases", () => Negative("mutation", 1)),
        ("macro2 late unsupported feature blocks", () => Negative("unsupported", 1)),
        ("macro2 cancellation retains terminal receipt", () => Negative("cancel", 1)),
        ("macro2 timeout retains terminal receipt", () => Negative("timeout", 1)),
        ("macro2 state ordering skipping duplicate and terminal resume", Ordering),
        ("macro2 harness single use prohibits resume", Resume),
        ("macro2 synthetic and forged receipt never freeze", FakeReceipt),
        ("macro2 governed authorization rejects missing consent wrong package actor stale window", Authorization),
        ("macro2 exact batches preserve source text and reject GO count", Batches),
        ("macro2 DATA valid explicit governed provider", () => Data("valid")),
        ("macro2 DATA unknown provider blocked", () => Data("unknown")),
        ("macro2 DATA incomplete evidence blocked", () => Data("incomplete")),
        ("macro2 DATA recovery mismatch invalid", () => Data("recovery")),
        ("macro2 DATA reapply mismatch invalid", () => Data("reapply")),
        ("macro2 DATA wrong scope blocked", () => Data("scope")),
        ("macro2 DATA stale phase evidence blocked", () => Data("stale")),
        ("macro2 full DATA_REQUIRED valid provider", () => RequiredData("valid")),
        ("macro2 full DATA_REQUIRED provider changed after authorization", () => RequiredData("changed")),
        ("macro2 full DATA_REQUIRED absent provider", () => RequiredData("absent")),
        ("macro2 full DATA_REQUIRED incomplete PRE", () => RequiredData("incomplete")),
        ("macro2 full DATA_REQUIRED recovery mismatch", () => RequiredData("recovery")),
        ("macro2 full DATA_REQUIRED reapply mismatch", () => RequiredData("reapply")),
        ("macro2 full SECURITY_REQUIRED lost GRANT witness", () => Security(true)),
        ("macro2 full SECURITY_REQUIRED restored GRANT", () => Security(false)),
        ("macro2 wrong script hash rejected by actual executor boundary", WrongScriptHash),
        ("macro2 phase contract version mismatch", () => Negative("version",0)),
        ("macro2 evidenceSetHash rebinding cannot hide mismatch", () => Negative("evidenceSet",0)),
        ("macro2 request parser rejects truth claims and duplicate properties", RequestParsing),
        ("macro2 public CLI default cannot start execution", DefaultCli)
    ];
    private static void Assert(bool condition, string message = "Macro2 assertion failed")
    { if (!condition) throw new Exception(message); }
    private static async Task Throws(Func<Task> action)
    {
        try { await action(); } catch (LegacyContractException) { return; }
        throw new Exception("Expected closed gate");
    }
    private static async Task Complete()
    {
        var q = await LegacyReadinessTests.SyntheticQualification();
        var lab = new Lab(q);
        var events = new List<LegacyPhaseReceiptV1>();
        var receipt = await new LegacyRehearsalHarnessV1(lab, new Authority(), events.Add).RunAsync(q, TimeSpan.FromSeconds(10));
        Assert(receipt.Status == "REHEARSAL_COMPLETE", string.Join(",", receipt.ReasonCodes));
        Assert(receipt.EvidenceKind == "SYNTHETIC" && !receipt.CanProceedToPromotion
            && receipt.Evaluation?.RecoveryCoverage?.Complete == true && lab.Writes == 3);
        Assert(receipt.Phases.Select(x => x.Phase).SequenceEqual(new[] { "PRE", "FORWARD1", "POST1", "ROLLBACK", "PRE2", "FORWARD2", "POST2" }));
        Assert(events.Count(x => x.Status == "STARTED") == 3 && LegacyRehearsalReceiptHash.VerifyIntegrity(receipt));
        ExportSynthetic("complete",receipt);
    }
    private static async Task Negative(string fault, int expectedWrites)
    {
        var q = await LegacyReadinessTests.SyntheticQualification();
        if (fault == "package") q = q with { Readiness = q.Readiness! with { PackageIdentity = "forged" } };
        if (fault == "baseline") q = q with { CertifiedBaseline = q.CertifiedBaseline! with { CertifiedSchemaHash = new string('0',64) } };
        if (fault == "evidenceSet") q = q with { Readiness = LegacyReadinessHash.Bind(q.Readiness! with {
            Handoff=q.Readiness!.Handoff! with {EvidenceSetHash=new string('0',64)} }) };
        var lab = new Lab(q, fault);
        using var cancel = new CancellationTokenSource();
        lab.Cancel = cancel;
        var receipt = await new LegacyRehearsalHarnessV1(lab, new Authority(fault == "authority"))
            .RunAsync(q, fault == "timeout" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10), token: cancel.Token);
        Assert(receipt.Status == "REHEARSAL_BLOCKED" && receipt.RecoveryClass != "FULL_REVERSIBLE"
            && !receipt.CanProceedToPromotion, fault + " false success");
        Assert(lab.Writes == expectedWrites, fault + " writes " + lab.Writes + " reasons " + string.Join(",",receipt.ReasonCodes));
        if (fault is "timeout" or "cancel") Assert(receipt.State == (fault == "timeout"
            ? LegacyRehearsalStateV1.TimedOut : LegacyRehearsalStateV1.Cancelled));
        ExportSynthetic("blocked-" + fault,receipt);
    }
    private static void ExportSynthetic(string name,LegacyRehearsalReceiptV1 receipt)
    {
        if (Environment.GetEnvironmentVariable("LEGACY_SYNTHETIC_EVIDENCE_OUTPUT") is not { Length: > 0 } root) return;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"synthetic-" + name + ".json"),JsonSerializer.Serialize(receipt,JsonDefaults.Compact));
    }
    private static Task Ordering()
    {
        foreach (var next in Enum.GetValues<LegacyRehearsalStateV1>().Where(x => x != LegacyRehearsalStateV1.PreCaptured))
        {
            var state = new LegacyRehearsalStateMachineV1();
            try { state.Advance(next); throw new Exception("Accepted out of order"); }
            catch (LegacyContractException) { }
        }
        var machine = new LegacyRehearsalStateMachineV1();
        machine.Advance(LegacyRehearsalStateV1.PreCaptured);
        try { machine.Advance(LegacyRehearsalStateV1.PreCaptured); throw new Exception("Duplicate phase"); } catch (LegacyContractException) { }
        machine.Stop(LegacyRehearsalStateV1.Failed);
        try { machine.Advance(LegacyRehearsalStateV1.Forward1Applied); throw new Exception("Resume"); } catch (LegacyContractException) { }
        return Task.CompletedTask;
    }
    private static async Task Resume()
    {
        var q = await LegacyReadinessTests.SyntheticQualification();
        var harness = new LegacyRehearsalHarnessV1(new Lab(q), new Authority());
        await harness.RunAsync(q, TimeSpan.FromSeconds(10));
        await Throws(async () => { await harness.RunAsync(q, TimeSpan.FromSeconds(10)); });
    }
    private static async Task FakeReceipt()
    {
        var q = await LegacyReadinessTests.SyntheticQualification();
        var receipt = await new LegacyRehearsalHarnessV1(new Lab(q), new Authority()).RunAsync(q, TimeSpan.FromSeconds(10));
        await Throws(() => { LegacyPromotionFreeze.CreateFromTrustedProducer(receipt); return Task.CompletedTask; });
        Assert(!LegacyRehearsalReceiptHash.VerifyIntegrity(receipt with { EvidenceKind = "REAL_TEST" }));
        var forged = LegacyRehearsalReceiptHash.Bind(receipt with { EvidenceKind = "REAL_TEST" });
        await Throws(() => { LegacyPromotionFreeze.CreateFromTrustedProducer(forged); return Task.CompletedTask; });
        var partial = LegacyRehearsalReceiptHash.Bind(receipt with { EvidenceKind = "REAL_TEST", Phases = receipt.Phases.Take(3).ToArray() });
        await Throws(() => { LegacyPromotionFreeze.CreateFromTrustedProducer(partial); return Task.CompletedTask; });
    }
    private static async Task Authorization()
    {
        var q = await LegacyReadinessTests.SyntheticQualification();
        var h = q.Readiness!.Handoff!;
        var source = q.TrustedRuntime!.Resolver!.Source;
        var now = DateTimeOffset.UtcNow;
        var grant = new LegacyRehearsalAuthorizationV1(1,"synthetic",h.TargetId,h.PackageIdentity,
            GovernedLegacyRehearsalAuthorityV1.PreconditionIdentity(h),h.EngineCommit,"TEST","synthetic-actor",now.AddMinutes(-1),now.AddMinutes(10),
            ["FORWARD1","ROLLBACK","FORWARD2"],true,"SYNTHETIC-APPROVAL");
        GovernedLegacyRehearsalAuthorityV1 Make(LegacyRehearsalAuthorizationV1 g, bool explicitConsent = true) =>
            new(g, source, source.Repository, source.Revision,"synthetic-actor",explicitConsent,_ => Task.CompletedTask);
        await Make(grant).VerifyAsync(h,"PRE",default);
        foreach (var bad in new[] { grant with { ExecutionAuthorized = false }, grant with { PackageIdentity = "wrong" },
            grant with { Actor = "other" }, grant with { ExpiresAtUtc = now.AddMinutes(-1) },
            grant with { ApprovedPhases = ["FORWARD1"] }, grant with { Environment = "OTHER" } })
            await Throws(() => Make(bad).VerifyAsync(h,"FORWARD1",default));
        await Throws(() => Make(grant,false).VerifyAsync(h,"PRE",default));
        await Throws(() => Make(grant with {DataContractIdentity=new string('a',64)}).VerifyAsync(h,"PRE",default));
    }
    private static async Task WrongScriptHash()
    {
        var q = await LegacyReadinessTests.SyntheticQualification();
        var h = q.Readiness!.Handoff!;
        await Throws(() => { SqlLegacyRehearsalRuntimeV1.VerifyExactScript(h,
            new ReleaseScript("forward",q.Package!.ForwardBytes),new string('0',64),h.TransactionPolicy); return Task.CompletedTask; });
        await Throws(() => { SqlLegacyRehearsalRuntimeV1.VerifyExactScript(h,
            new ReleaseScript("forward",Encoding.UTF8.GetBytes("SELECT 999;")),h.Artifacts.Forward.Sha256,h.TransactionPolicy); return Task.CompletedTask; });
    }
    private static async Task RequestParsing()
    {
        foreach (var invalid in new[] { "{\"contractVersion\":1,\"contractVersion\":1}", "{\"ready\":true}", "{}" })
        {
            try { LegacyRehearsalCli.Parse<LegacyRehearsalRequestV1>(Encoding.UTF8.GetBytes(invalid)); }
            catch (Exception e) when (e is LegacyContractException or JsonException) { continue; }
            throw new Exception("Accepted duplicate or caller truth claim");
        }
        await Task.CompletedTask;
    }
    private static async Task DefaultCli()
    {
        var q=await LegacyReadinessTests.SyntheticQualification();
        var root=Path.Combine(Path.GetTempPath(),"synthetic-legacy-default-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var request=new LegacyRehearsalRequestV1(1,q.TrustedRuntime!.Request.ArtifactSelection,q.Readiness!.TargetId,
                q.Package!.Evidence.PackageIdentity,"synthetic",null);
            var path=Path.Combine(root,"request.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(request,JsonDefaults.Compact))!.AsObject();
            json.Remove("executionAuthorized");
            json.Remove("dataContractSelector");
            Assert(!LegacyRehearsalCli.Parse<LegacyRehearsalRequestV1>(Encoding.UTF8.GetBytes(json.ToJsonString())).ExecutionAuthorized);
            await File.WriteAllTextAsync(path,json.ToJsonString());
            var output=Path.Combine(root,"output");
            Assert(await LegacyRehearsalCli.RunAsync(["--request",path,"--output",output])==66);
            Assert(!File.Exists(Path.Combine(output,"phase-journal.jsonl")));
            Assert(File.ReadAllText(Path.Combine(output,"legacy-rehearsal-blocked.json")).Contains("EXECUTION_AUTHORIZATION_INVALID"));
        } finally { Directory.Delete(root,true); }
    }
    private static Task Batches()
    {
        var script = new ReleaseScript("forward",Encoding.UTF8.GetBytes("SELECT 'GO';\r\nGO\r\nSELECT 2;"));
        var batches = SqlLegacyRehearsalRuntimeV1.ExactBatches(script);
        Assert(batches.Count == 2 && batches[0].Contains("'GO'") && batches[1].Contains("SELECT 2;"));
        try { SqlLegacyRehearsalRuntimeV1.ExactBatches(new("forward",Encoding.UTF8.GetBytes("SELECT 1;\nGO 2"))); throw new Exception("GO count allowed"); }
        catch (LegacyContractException) { }
        return Task.CompletedTask;
    }
    private static async Task Data(string fault)
    {
        var source = new LegacyGitDocumentV1(new("github.com","1","synthetic/contracts"),new string('a',40),"synthetic/data.json",new string('b',64));
        var definition = new LegacyDataContractDefinitionV1(1,"synthetic-data",1,"target1",new string('c',64),"SYNTHETIC","EXACT_CONTENT_HASH_V1",source);
        var resolver = new LegacyDataContractResolverV1([definition],new Dictionary<string,Func<LegacyDataContractDefinitionV1,ILegacyDataEvidenceReaderV1>> {
            ["SYNTHETIC"] = _ => new DataReader(fault)
        });
        if (fault is "unknown" or "scope")
        {
            await Throws(() => { resolver.Resolve(fault == "unknown" ? "unknown" : definition.Selector,definition.TargetId,
                fault == "scope" ? "wrong" : definition.ScopeHash,source.Repository,source.Revision); return Task.CompletedTask; }); return;
        }
        var provider = (LegacyDataValidationProviderV1)resolver.Resolve(definition.Selector,definition.TargetId,definition.ScopeHash,source.Repository,source.Revision).Provider;
        if (fault is "incomplete" or "stale") { await Throws(() => provider.CapturePreDataAsync()); return; }
        await provider.CapturePreDataAsync(); await provider.CapturePostDataAsync();
        Assert(await provider.ValidateRollbackDataAsync() == (fault == "recovery" ? DataRollbackValidity.Invalid : DataRollbackValidity.Valid));
        Assert(await provider.ValidateReapplyDataAsync() == (fault == "reapply" ? DataRollbackValidity.Invalid : DataRollbackValidity.Valid));
        Assert(provider.Evidence.Count == 4);
    }
    private sealed class DataReader(string fault) : ILegacyDataEvidenceReaderV1
    {
        public Task<LegacyDataPhaseEvidenceV1> CaptureAsync(LegacyDataContractDefinitionV1 d, RecoveryPhase phase, CancellationToken token) =>
            Task.FromResult(new LegacyDataPhaseEvidenceV1(1,d.Selector,d.Version,d.TargetId,d.ScopeHash,phase,
                fault == "stale" ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow, fault != "incomplete",
                Hashing.Sha256(fault == "recovery" && phase == RecoveryPhase.Pre2 || fault == "reapply" && phase == RecoveryPhase.Post2
                    ? "mismatch" : phase is RecoveryPhase.Pre or RecoveryPhase.Pre2 ? "pre" : "post")));
    }
    private static async Task RequiredData(string fault)
    {
        var snapshot = new SchemaSnapshot { Objects = [new() {Kind="table",Schema="dbo",Name="T"},
            new() {Kind="column",Schema="dbo",Parent="T",Name="Value"}],
            ImpactMetrics = [new() {Schema="dbo",Table="T",RowCount=1}] };
        var forward = Encoding.UTF8.GetBytes("UPDATE dbo.T SET Value = Value + 1;");
        var rollback = Encoding.UTF8.GetBytes("UPDATE dbo.T SET Value = Value - 1;");
        var analyzer = new SqlScriptAnalyzer();
        var (_,hash) = LegacyReadinessHash.DataScope(analyzer.Analyze("forward",Encoding.UTF8.GetString(forward),snapshot),
            analyzer.Analyze("rollback",Encoding.UTF8.GetString(rollback),snapshot));
        var definition = new LegacyDataContractDefinitionV1(1,"synthetic-data",1,"target1",hash,"SYNTHETIC","EXACT_CONTENT_HASH_V1",
            new(new("github.com","1","synthetic/contracts"),new string('a',40),"synthetic/data.json",new string('b',64)));
        var data = new LegacyDataValidationDescriptorV1(definition.Selector,1,hash,new LegacyDataValidationProviderV1(definition,new DataReader(fault)));
        var q = await LegacyReadinessTests.SyntheticQualification(forward,rollback,snapshot,data);
        Assert(q.Readiness?.Handoff is not null,"DATA Macro1 blocked: " + string.Join(",",q.Readiness?.ReasonCodes ?? q.Rejected?.ReasonCodes ?? []));
        var lab = new Lab(q);
        ILegacyRehearsalAuthorityV1 authority = new Authority();
        if (fault == "changed")
        {
            var h = q.Readiness!.Handoff!;
            var source = q.TrustedRuntime!.Resolver!.Source;
            var original = (LegacyDataValidationProviderV1)data.Provider;
            var now = DateTimeOffset.UtcNow;
            var grant = new LegacyRehearsalAuthorizationV1(1,"synthetic",h.TargetId,h.PackageIdentity,
                GovernedLegacyRehearsalAuthorityV1.PreconditionIdentity(h),h.EngineCommit,"TEST","synthetic-actor",
                now.AddMinutes(-1),now.AddMinutes(10),["FORWARD1","ROLLBACK","FORWARD2"],true,
                "SYNTHETIC-APPROVAL",original.ApprovalIdentity);
            var changed = new LegacyDataValidationProviderV1(definition with {
                Source = definition.Source with { RawSha256 = new string('d',64) }
            },new DataReader("valid"));
            data = data with { Provider = changed };
            authority = new GovernedLegacyRehearsalAuthorityV1(grant,source,source.Repository,source.Revision,
                "synthetic-actor",true,_ => Task.CompletedTask,changed.ApprovalIdentity);
        }
        var receipt = await new LegacyRehearsalHarnessV1(lab,authority).RunAsync(q,TimeSpan.FromSeconds(10),fault == "absent" ? null : data);
        Assert((receipt.Status == "REHEARSAL_COMPLETE") == (fault == "valid"),fault + ": " + string.Join(",",receipt.ReasonCodes));
        Assert((receipt.RecoveryClass == "FULL_REVERSIBLE") == (fault == "valid"));
        Assert(lab.Writes == (fault is "absent" or "incomplete" or "changed" ? 0 : fault == "recovery" ? 2 : 3),"Unexpected DATA writes: " + lab.Writes);
        if (fault == "changed") Assert(receipt.ReasonCodes.Contains("EXECUTION_AUTHORIZATION_INVALID"));
        if (fault == "valid") Assert(receipt.DataEvidence.Count == 4);
    }
    private static async Task Security(bool lost)
    {
        var snapshot = new SchemaSnapshot {Objects=[new() {Kind="view",Schema="dbo",Name="V",Properties=new(StringComparer.Ordinal) { ["definitionSha256"]=Hashing.Sha256("SELECT 1 AS N") }}]};
        var q = await LegacyReadinessTests.SyntheticQualification(Encoding.UTF8.GetBytes("DROP VIEW dbo.V;"),
            Encoding.UTF8.GetBytes("CREATE VIEW dbo.V AS SELECT 1 AS N;"),snapshot);
        Assert(q.Readiness?.Handoff is not null,"SECURITY Macro1 blocked: " + string.Join(",",q.Readiness?.ReasonCodes ?? q.Rejected?.ReasonCodes ?? []));
        var lab = new Lab(q,lost ? "lostGrant" : "restoreGrant");
        var receipt = await new LegacyRehearsalHarnessV1(lab,new Authority()).RunAsync(q,TimeSpan.FromSeconds(10));
        Assert((receipt.Status == "REHEARSAL_COMPLETE") == !lost,string.Join(",",receipt.ReasonCodes));
        Assert(lab.Writes == (lost ? 2 : 3),"Unexpected SECURITY writes: " + lab.Writes);
        if (lost) Assert(receipt.RecoveryClass != "FULL_REVERSIBLE" && receipt.RecoveryClass != "SCHEMA_ONLY");
    }
    private sealed class Authority(bool denied = false) : ILegacyRehearsalAuthorityV1
    {
        public Task VerifyAsync(LegacyHandoffV1 handoff,string phase,CancellationToken token) =>
            denied ? throw new LegacyContractException("EXECUTION_AUTHORIZATION_INVALID") : Task.CompletedTask;
    }
    private sealed class Lab(LegacyQualificationOutcomeV1 q, string fault = "") : ILegacyRehearsalRuntimeV1
    {
        public string EvidenceKind => "SYNTHETIC";
        public LegacyRuntimeContextV1 Context => q.TrustedRuntime!.Runtime!;
        public int Writes;
        private int captures;
        public CancellationTokenSource? Cancel;
        public Task RevalidateAsync(LegacyHandoffV1 h,CancellationToken token) => Task.CompletedTask;
        public Task<ObservedCurrentSnapshotV1> CaptureAsync(CancellationToken token)
        {
            captures++;
            var original = q.ObservedSnapshot!;
            var snapshot = JsonSerializer.Deserialize<SchemaSnapshot>(JsonSerializer.Serialize(original.Snapshot,JsonDefaults.Compact),JsonDefaults.Compact)!;
            if ((fault is "lostGrant" or "restoreGrant") && (Writes is 1 or 3)) snapshot.Objects.Clear();
            if (fault == "drift" || fault == "pre2" && Writes == 2 || fault == "post2" && Writes == 3)
                snapshot.Objects.Add(new() { Kind="table",Schema="dbo",Name="different" });
            if (fault == "unsupported" && Writes == 1) snapshot.UnsupportedSchemaFeatures.Add("UNKNOWN_FEATURE");
            var hash = SchemaCanonicalizer.Canonicalize(snapshot).Sha256;
            var observed = original with { Snapshot = snapshot, ObservedSchemaHash=hash,
                ContractVersion=fault=="version" ? 2 : 1,
                TargetId = fault == "target" ? "other" : original.TargetId,
                CapturedAtUtc = fault == "stale" ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow,
                CaptureComparison = original.CaptureComparison with { FirstSchemaHash=hash, SecondSchemaHash=hash },
                Metadata = fault == "pre" || fault == "post1" && Writes == 1
                    ? new("PARTIAL","COMPLETE","COMPLETE") : original.Metadata };
            return Task.FromResult(observed);
        }
        public async Task ApplyExactAsync(ReleaseScript script,string hash,string policy,ObservedCurrentSnapshotV1 expectedPre,CancellationToken token)
        {
            Assert(script.Sha256 == hash && policy == "HARNESS_OWNS_PHASE_TRANSACTIONS_V1");
            Writes++;
            if (fault == "forward" || fault == "rollback" && Writes == 2 || fault == "reapply" && Writes == 3)
                throw new Exception("sensitive database error MUST NOT BE EXPORTED");
            if (fault == "mutation") q.ObservedSnapshot!.Snapshot.Objects.Add(new() {Kind="table",Schema="dbo",Name="tamper"});
            if (fault == "cancel") { Cancel!.Cancel(); token.ThrowIfCancellationRequested(); }
            if (fault == "timeout") await Task.Delay(TimeSpan.FromMinutes(1),token);
        }
        public Task<RecoverySecuritySnapshot> CaptureSecurityAsync(RecoverySecurityScope scope,RecoveryPhase phase,CancellationToken cancellationToken=default)
        {
            var snapshot = (fault is "lostGrant" or "restoreGrant") && (Writes is 1 or 3) ? new SchemaSnapshot() : q.ObservedSnapshot!.Snapshot;
            var security = RecoveryCoverageTests.Security(scope,phase,snapshot) with {ServerInstance="SQL1",DatabaseName="TestDb"};
            if (fault is "lostGrant" or "restoreGrant")
                security = security with { Securables=security.Securables.Select(s => s with {
                    Permissions=s.Exists && !(fault == "lostGrant" && phase == RecoveryPhase.Pre2)
                        ? [new RecoverySecurityPermission("SELECT","G","dbo","dbo")] : [] }).ToArray() };
            return Task.FromResult(security);
        }
    }
}
