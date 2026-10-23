using System.Text;
using System.Text.Json;
using DatabaseReleaseQualification;
using SqlDiscovery.V2;

// Logic/contract fixtures only. No database, credentials, network or live producer.
internal static class EfBootstrapTests
{
    private static readonly DateTimeOffset Now = EfPreparationTests.Now;
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF bootstrap: generic preprovisioned EMPTY with one/multiple initial migrations", Empty),
        ("EF bootstrap: ABSENT admitted independently of EMPTY, qualification still required", Absent),
        ("EF bootstrap: opaque reference, creation receipt and unknown version rejected", Shape),
        ("EF bootstrap: scope/session/source/governance/policy/projection replay blocked", Bindings),
        ("EF bootstrap: incarnation/schema/data/security/capture substitutions blocked", State),
        ("EF bootstrap: expiry, future evidence and excessive validity blocked", Time),
        ("EF bootstrap: history policies are explicit and not interchangeable", History),
        ("EF bootstrap: EMPTY requires complete columns and primary key", HistoryStructure),
        ("EF bootstrap: business data, partial data and omitted count blocked", Data),
        ("EF bootstrap: unexpected objects and unmeasured history blocked", Physical),
        ("EF bootstrap: role denial, modified bytes, cancellation and stale provider block", Trust),
        ("EF bootstrap: EXISTING cannot reuse bootstrap as certified baseline", Existing),
        ("EF bootstrap: ABSENT to EMPTY does not weaken strict recovery", Recovery)
    ];

    internal static EfPreprovisionedBootstrapV1 Document(EfPreparationTests.Fixture f)
    {
        var g = f.Governance; var o = f.Pre.Observation;
        return new(1, "EF_PREPROVISIONED_BOOTSTRAP", f.Selection.Scope,
            g.Authority.Governance.Document, g.Authority.Onboarding.Document, g.Policy.Provenance.Document,
            f.Selection.Source, f.Selection.Pre, g.ContentHashContract,
            new(o.TargetId, o.Environment, o.EndpointReference, o.ServerInstance, o.DatabaseName,
                o.DatabaseIncarnation, o.HistoryStatus, o.AppliedMigrationIds,
                SchemaCanonicalizer.Canonicalize(o.Schema).Sha256, o.DataHash, o.SecurityHash, o.Evidence, o.CapturedAtUtc),
            "COMPLETE", 0, o.HistoryStatus == "ABSENT" ? "HISTORY_ABSENT_V1" : "EMPTY_HISTORY_ATTESTED_V1",
            Now.AddMinutes(-2), Now.AddMinutes(8));
    }
    internal static EfPreparationTests.Fixture Admit(EfPreparationTests.Fixture f, EfPreprovisionedBootstrapV1? document = null)
    {
        var reference = f.Provider.Publish("preprovisioned-bootstrap", "NEW_BOOTSTRAP", document ?? Document(f));
        var g = f.Governance with { Authority = f.Governance.Authority with { BootstrapEvidence = reference } };
        return f.Replace("GOVERNANCE", g) with { Governance = g };
    }
    private static Task<EfPreparationTests.Fixture> Make() => EfPreparationTests.Make("BootstrapApp", 0, 2, initial: true);
    private static async Task HistoryStructure()
    {
        foreach (var change in new[] { "missing-pk", "nullable", "length", "missing-column" })
        {
            var f = await Make();
            var schema = JsonSerializer.Deserialize<SchemaSnapshot>(JsonSerializer.Serialize(f.Pre.Observation.Schema, JsonDefaults.Compact), JsonDefaults.Compact)!;
            if (change == "missing-pk") schema.Objects.RemoveAll(x => x.Kind == "key-constraint-column");
            else if (change == "missing-column") schema.Objects.RemoveAll(x => x.Kind == "column" && x.Name == "ProductVersion");
            else schema.Objects.Single(x => x.Kind == "column" && x.Name == "MigrationId").Properties[change == "nullable" ? "nullable" : "maxLength"] = change == "nullable" ? "true" : "200";
            var pre = EfPreparationTests.Observe(f.Provider, f.Pre.Observation with { Schema = schema }, "TARGET_PRE", f.Governance.ContentHashContract);
            f = f.Replace("TARGET_PRE", pre) with { Pre = pre };
            await Block(Admit(f), "BOOTSTRAP_HISTORY_STRUCTURE_INVALID");
        }
    }
    private static async Task Block(EfPreparationTests.Fixture f, string? reason = null)
    {
        var r = await f.Run();
        Assert(r.Status == "BLOCKED" && !r.ExecutionAuthorized && (reason is null || r.Reasons.Contains(reason)),
            "Expected " + reason + "; received " + r.Status + ": " + string.Join(",", r.Reasons));
        Assert(r.PlanResult?.Plan is null, "Rejected bootstrap reached plan creation");
    }
    private static async Task Empty()
    {
        foreach (var (name, count) in new[] { ("Catalog", 1), ("Delivery", 3) })
        {
            var f = await EfPreparationTests.Make(name, 0, count, initial: true);
            var r = await f.Run();
            Assert(r.Status == "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED", string.Join(",", r.Reasons));
            var m = r.PlanResult!.Plan!.Read();
            Assert(m.PendingMigrationIds.Length == count && !m.ExecutionAuthorized && !r.ExecutionAuthorized, "Authority or pending set changed");
            Assert(m.Acquisition!.Producers.Any(x => x.Purpose == "NEW_BOOTSTRAP"
                && x.Evidence == f.Governance.Authority.BootstrapEvidence), "Bootstrap receipt missing from frozen plan");
        }
    }
    private static EfPreparationTests.Fixture WithoutHistory(EfPreparationTests.Fixture f)
    {
        var o = f.Pre.Observation with { HistoryStatus = "ABSENT", Schema = new SchemaSnapshot() };
        var pre = EfPreparationTests.Observe(f.Provider, o, "TARGET_PRE", f.Governance.ContentHashContract);
        f = f.Replace("TARGET_PRE", pre) with { Pre = pre };
        return Admit(f);
    }
    private static async Task Absent()
    {
        var f = WithoutHistory(await Make());
        // Only admission is asserted: no invented physical recovery evidence.
        await Block(f with { Selection = f.Selection with { Qualification = null } }, "QUALIFIED_POST_REQUIRED");
        await Block(Admit(f, Document(f) with { HistoryPolicy = "EMPTY_HISTORY_ATTESTED_V1" }), "INITIAL_HISTORY_STATE_UNVERIFIED");
    }
    private static async Task Shape()
    {
        var f = await Make();
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes("opaque-bootstrap"), Encoding.UTF8.GetBytes("{\"creationReceipt\":\"old\"}"),
            Encoding.UTF8.GetBytes("{\"version\":1,\"Version\":1}") })
        {
            var reference = f.Provider.Raw("invalid-bootstrap", "NEW_BOOTSTRAP", bytes);
            await Block(f.Replace("GOVERNANCE", f.Governance with { Authority = f.Governance.Authority with { BootstrapEvidence = reference } }));
        }
        foreach (var b in new[] { Document(f) with { Version = 2 }, Document(f) with { Kind = "CREATE_DATABASE_RECEIPT" } })
            await Block(Admit(f, b), "BOOTSTRAP_CONTRACT_INVALID");
    }
    private static async Task Bindings()
    {
        var f = await Make(); var b = Document(f); var other = new EvidenceReference { EvidenceId = "other", EvidenceSha256 = Hashing.Sha256("other") };
        foreach (var changed in new[] {
            b with { Scope = b.Scope with { TargetId = "other" } }, b with { Scope = b.Scope with { SessionId = "replayed" } },
            b with { Scope = b.Scope with { SourceRevision = new string('a', 40) } }, b with { Scope = b.Scope with { SourceRepository = "other/app" } },
            b with { Governance = other }, b with { Onboarding = other }, b with { Policy = other },
            b with { Source = other }, b with { Pre = other }, b with { ContentHashContract = other } })
            await Block(Admit(f, changed), "BOOTSTRAP_BINDING_MISMATCH");
    }
    private static async Task State()
    {
        var f = await Make(); var b = Document(f); var s = b.ObservedState;
        foreach (var changed in new[] { s with { DatabaseIncarnation = "replacement" }, s with { SchemaHash = Hashing.Sha256("drift") },
            s with { DataHash = Hashing.Sha256("data") }, s with { SecurityHash = Hashing.Sha256("grants") },
            s with { ServerInstance = "other" }, s with { DatabaseName = "other" }, s with { Environment = "other" },
            s with { EndpointReference = "other" }, s with { CapturedAtUtc = Now }, s with { HistoryStatus = "ABSENT" } })
            await Block(Admit(f, b with { ObservedState = changed }), "BOOTSTRAP_PRE_MISMATCH");
    }
    private static async Task Time()
    {
        var f = await Make(); var b = Document(f);
        foreach (var changed in new[] { b with { ExpiresAtUtc = Now }, b with { NotBeforeUtc = Now.AddSeconds(1) },
            b with { NotBeforeUtc = Now }, b with { ExpiresAtUtc = Now.AddHours(1) } })
            await Block(Admit(f, changed), "BOOTSTRAP_EXPIRED_OR_STALE");
    }
    private static async Task History()
    {
        var f = await Make(); var b = Document(f);
        foreach (var policy in new[] { "HISTORY_ABSENT_V1", "", "ACCEPT_ALL", "EMPTY_HISTORY_ATTESTED_V2" })
            await Block(Admit(f, b with { HistoryPolicy = policy }), "INITIAL_HISTORY_STATE_UNVERIFIED");
    }
    private static async Task Data()
    {
        var f = await Make(); var b = Document(f);
        foreach (var changed in new[] { b with { BusinessRowCount = 1 }, b with { BusinessRowCount = -1 },
            b with { BusinessRowCount = null }, b with { DataCoverage = "PARTIAL" } })
            await Block(Admit(f, changed), "BOOTSTRAP_DATA_NOT_EMPTY_OR_UNVERIFIED");
    }
    private static async Task Physical()
    {
        var f = await Make(); var o = f.Pre.Observation;
        foreach (var physical in new[] { EmptyForNewEfV1.Complete(1, new long[8]), EmptyForNewEfV1.Complete(0, 1, 0, 0, 0, 0, 0, 0, 0) })
        {
            var pre = f.Pre with { Observation = o with { Physical = physical }, Discovery = f.Pre.Discovery with { Physical = physical } };
            await Block(Admit(f.Replace("TARGET_PRE", pre) with { Pre = pre }), "BOOTSTRAP_PHYSICAL_STATE_INVALID");
        }
        foreach (var kind in new[] { "trigger", "view", "table" })
        {
            f = await Make(); o = f.Pre.Observation;
            o.Schema.Objects.Add(new() { Kind = kind, Schema = "dbo", Name = "Unexpected", Parent = "__EFMigrationsHistory" });
            var pre = EfPreparationTests.Observe(f.Provider, o, "TARGET_PRE", f.Governance.ContentHashContract);
            await Block(Admit(f.Replace("TARGET_PRE", pre) with { Pre = pre }), "BOOTSTRAP_SCHEMA_INVALID");
        }
        f = await Make(); f.Pre.Observation.Schema.ImpactMetrics.Clear();
        var missing = EfPreparationTests.Observe(f.Provider, f.Pre.Observation, "TARGET_PRE", f.Governance.ContentHashContract);
        await Block(Admit(f.Replace("TARGET_PRE", missing) with { Pre = missing }), "BOOTSTRAP_DATA_NOT_EMPTY_OR_UNVERIFIED");
    }
    private static async Task Trust()
    {
        var f = await Make(); f.Provider.Allowed.Remove((f.Governance.Authority.BootstrapEvidence!.EvidenceId, "NEW_BOOTSTRAP")); await Block(f);
        f = await Make(); f.Provider.Content[f.Governance.Authority.BootstrapEvidence!.EvidenceId][0] ^= 1;
        await Block(f, "EVIDENCE_HASH_MISMATCH");
        f = await Make(); f.Provider.Changed = true; await Block(f);
        f = await Make(); var r = await f.Service.PrepareAsync(f.Selection, Now, new CancellationToken(true));
        Assert(r.Reasons.Contains("EVIDENCE_ACQUISITION_CANCELLED"), "Cancellation lost");
        Assert((await new EfEvidencePreparationV1().PrepareAsync(f.Selection, Now)).Reasons.Contains("AUTHORIZED_PROVIDER_REQUIRED"), "No provider accepted");
    }
    private static async Task Existing()
    {
        var f = await EfPreparationTests.Make(); var boot = await Make();
        await Block(f.Replace("GOVERNANCE", f.Governance with { Authority = f.Governance.Authority with {
            BootstrapEvidence = boot.Governance.Authority.BootstrapEvidence } }), "UNEXPECTED_BOOTSTRAP_EVIDENCE");
    }
    private static async Task Recovery()
    {
        var absent = WithoutHistory(await Make()); var initial = absent.Qualification;
        var labPre = EfPreparationTests.Observe(absent.Provider, initial.Pre.Observation with {
            HistoryStatus = "ABSENT", Schema = new SchemaSnapshot() }, "REHEARSAL_PRE", absent.Governance.ContentHashContract);
        await Block(absent.Replace("ISOLATED_QUALIFICATION", initial with { Pre = labPre }), "RECOVERY_PRE_MISMATCH");
        var f = await Make(); var q = f.Qualification;
        q = q with { RecoveredPre = q.RecoveredPre with { Observation = q.RecoveredPre.Observation with { HistoryStatus = "ABSENT" },
            Discovery = q.RecoveredPre.Discovery with { History = new(HistoryStatus.Absent) } } };
        q = q with { Recovery = q.Recovery with { RecoveredPre = q.RecoveredPre.Observation } };
        await Block(f.Replace("ISOLATED_QUALIFICATION", q), "RECOVERY_PRE_MISMATCH");
    }
}
