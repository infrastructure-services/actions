using System.Text.Json;
using DatabaseReleaseQualification;
using SqlDiscovery.V2;

internal static class EfNormalizationTests
{
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF normalization: authorized raw sources compose plan and no changes", Positive),
        ("EF normalization: transport absence partial identity hashes and roles fail closed", Negative)
    ];
    private static void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
    private static async Task<EfPreparationTests.Fixture> Make(string mutation = "", int pending = 1)
    {
        var f = await EfPreparationTests.Make();
        if (pending == 0) f = f.Replace("SOURCE", new EfSourcePreparationV1(
            f.Original.Request.Source with { ManagedMigrationIds = f.Pre.Observation.AppliedMigrationIds }, [], []));
        var o = f.Pre.Observation; var d = f.Pre.Discovery;
        var c = new EfObservationContextV1(1, f.Selection.Scope, "TARGET_PRE", o.Environment,
            o.EndpointReference, o.DatabaseIncarnation, o.CapturedAtUtc, f.Governance.ContentHashContract);
        var contract = JsonSerializer.Deserialize<EfContentHashContractV1>(f.Provider.Content[f.Governance.ContentHashContract.EvidenceId], JsonDefaults.Compact)!;
        if (mutation == "connection") d = d with { ServerConnection = new(ConnectionStatus.TransportFailed) };
        if (mutation == "absent") d = d with { DatabaseLookup = new(DatabaseLookupStatus.NotFoundConfirmed) };
        if (mutation == "partial") d = d with { Metadata = new(MetadataStatus.Insufficient) };
        var discovery = f.Provider.Publish("raw-discovery", "SQL_DISCOVERY_V2", new EfDiscoveryDocumentV1(c, d));
        var schema = f.Provider.Publish("raw-schema", "SCHEMA_CAPTURE", new EfSchemaDocumentV1(
            mutation == "session" ? c with { Scope = c.Scope with { SessionId = "replay" } } : c, o.Schema));
        var data = f.Provider.Publish("raw-data", "DATA_PROJECTION_RESULT", new EfContentProjectionV1(c,
            mutation == "data" ? "PARTIAL" : "COMPLETE", contract.DataProjection, o.DataHash));
        var security = f.Provider.Publish("raw-security", "SECURITY_PROJECTION_RESULT", new EfContentProjectionV1(c,
            "COMPLETE", contract.SecurityProjection, o.SecurityHash));
        var normalized = f.Pre with { Observation = o with { Evidence = schema, Physical = d.Physical }, Discovery = d };
        var expected = new EvidenceReference { EvidenceId = "normalized-pre", EvidenceSha256 = Hashing.Sha256(JsonSerializer.Serialize(normalized, JsonDefaults.Compact)) };
        if (mutation == "hash") expected = expected with { EvidenceSha256 = new string('0', 64) };
        if (mutation == "role") f.Provider.Allowed.Remove((security.EvidenceId, "SECURITY_PROJECTION_RESULT"));
        return f with { Selection = f.Selection with { Pre = expected, PreSources = new(discovery, schema, data, security) } };
    }
    private static async Task Positive()
    {
        foreach (var pending in new[] { 1, 0 })
        {
            var f = await Make(pending: pending); var result = await f.Run();
            Assert(result.Status == (pending == 0 ? "NO_CHANGES" : "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED"), string.Join(",", result.Reasons));
            Assert(!result.ExecutionAuthorized && f.Provider.FreshnessChecks > 0, "normalization granted authority");
        }
    }
    private static async Task Negative()
    {
        foreach (var mutation in new[] { "connection", "absent", "partial", "session", "data", "hash", "role" })
        {
            var f = await Make(mutation); var r = await f.Run();
            Assert(r.Status == "BLOCKED", mutation + " admitted");
            if (mutation == "absent") Assert(r.Reasons.Contains("DATABASE_PROVISIONING_REQUIRED"), "absence lost its reason");
        }
    }
}
