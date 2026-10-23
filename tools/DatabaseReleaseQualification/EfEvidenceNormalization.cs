using System.Text.Json;
using SqlDiscovery.V2;

namespace DatabaseReleaseQualification;

// Selectors do not confer authority. Every input is read through the host's
// purpose-aware provider and participates in freshness/replay verification.
public sealed record EfObservationSourcesV1(EvidenceReference Discovery, EvidenceReference Schema,
    EvidenceReference Data, EvidenceReference Security);
public sealed record EfObservationContextV1(int Version, EfEvidenceScopeV1 Scope, string Phase,
    string Environment, string EndpointReference, string DatabaseIncarnation,
    DateTimeOffset CapturedAtUtc, EvidenceReference ContentHashContract);
public sealed record EfDiscoveryDocumentV1(EfObservationContextV1 Context, SqlDiscoveryResult Discovery);
public sealed record EfSchemaDocumentV1(EfObservationContextV1 Context, SchemaSnapshot Schema);
public sealed record EfContentProjectionV1(EfObservationContextV1 Context, string Coverage,
    EfPlanProvenanceV1 Projection, string ContentHash);

public sealed partial class EfEvidencePreparationV1
{
    private async Task<EfObservationEvidenceV1> NormalizeObservation(EfPreparationSelectionV1 selection,
        EfGovernedPreparationV1 governance, DateTimeOffset now, EvidenceLog used, CancellationToken token)
    {
        var scope = selection.Scope; var sources = selection.PreSources!;
        var discovery = await Document<EfDiscoveryDocumentV1>(scope, sources.Discovery, "SQL_DISCOVERY_V2", used, token);
        var c = discovery.Context; var d = discovery.Discovery;
        Require(c.Version == 1 && c.Scope == scope && c.Phase == "TARGET_PRE"
            && c.Environment == governance.Authority.Target.Environment
            && c.EndpointReference == governance.Authority.Target.EndpointReference
            && c.ContentHashContract == governance.ContentHashContract, "NORMALIZATION_CONTEXT_MISMATCH");
        Require(governance.Policy.MaximumValiditySeconds > 0 && c.CapturedAtUtc <= now
            && (now - c.CapturedAtUtc).TotalSeconds <= governance.Policy.MaximumValiditySeconds,
            "PRE_EVIDENCE_STALE");
        // Preserve acquisition semantics; missing permissions never mean absence.
        Require(d.ServerConnection.Status == ConnectionStatus.Succeeded,
            "DISCOVERY_SERVER_" + d.ServerConnection.Status.ToString().ToUpperInvariant());
        Require(d.DatabaseLookup.Status != DatabaseLookupStatus.NotFoundConfirmed, "DATABASE_PROVISIONING_REQUIRED");
        Require(d.DatabaseLookup.Status == DatabaseLookupStatus.Found,
            "DISCOVERY_LOOKUP_" + d.DatabaseLookup.Status.ToString().ToUpperInvariant());
        Require(d.TargetConnection.Status == ConnectionStatus.Succeeded,
            "DISCOVERY_TARGET_" + d.TargetConnection.Status.ToString().ToUpperInvariant());
        Require(d.Metadata.Status == MetadataStatus.Sufficient,
            "DISCOVERY_METADATA_" + d.Metadata.Status.ToString().ToUpperInvariant());
        Require(d.Physical.Status == PhysicalStatus.Complete && EmptyForNewEfV1.Valid(d.Physical), "DISCOVERY_PHYSICAL_INCOMPLETE");
        Require(d.History.Status is HistoryStatus.Absent or HistoryStatus.Empty or HistoryStatus.Present,
            "DISCOVERY_HISTORY_" + d.History.Status.ToString().ToUpperInvariant());
        Require(d.ObservedIdentity is { Status: ObservedIdentityStatus.Available, Identity: not null }, "DISCOVERY_IDENTITY_UNAVAILABLE");
        var schema = await Document<EfSchemaDocumentV1>(scope, sources.Schema, "SCHEMA_CAPTURE", used, token);
        var data = await Document<EfContentProjectionV1>(scope, sources.Data, "DATA_PROJECTION_RESULT", used, token);
        var security = await Document<EfContentProjectionV1>(scope, sources.Security, "SECURITY_PROJECTION_RESULT", used, token);
        Require(Equal(c, schema.Context) && Equal(c, data.Context) && Equal(c, security.Context), "NORMALIZATION_CROSS_SOURCE_MISMATCH");
        var contract = await Document<EfContentHashContractV1>(scope, c.ContentHashContract, "CONTENT_HASH_CONTRACT", used, token);
        Require(contract.Version == 1 && Hash(contract.ScopeHash) && data.Projection == contract.DataProjection
            && security.Projection == contract.SecurityProjection, "NORMALIZATION_PROJECTION_MISMATCH");
        Require(data.Coverage == "COMPLETE" && security.Coverage == "COMPLETE"
            && Hash(data.ContentHash) && Hash(security.ContentHash), "DATA_SECURITY_EVIDENCE_REQUIRED");
        var observation = new EfPlanObservationV1(scope.TargetId, c.Environment, c.EndpointReference,
            d.ObservedIdentity.Identity!.ServerInstance, d.ObservedIdentity.Identity.DatabaseName,
            c.DatabaseIncarnation, d.History.Status.ToString().ToUpperInvariant(), d.History.MigrationIds.ToArray(),
            schema.Schema, "COMPLETE", "COMPLETE", data.ContentHash, security.ContentHash, sources.Schema,
            c.CapturedAtUtc, d.Physical);
        _ = EfReleasePlannerV1.Observe(observation, governance.Authority.Target, now);
        var normalized = new EfObservationEvidenceV1(c.Phase, observation, d, c.ContentHashContract);
        // The selection identifies the exact normalized bytes. Never rewrite its
        // hash or claim an authenticated producer for locally generated content.
        Require(Hashing.Sha256(JsonSerializer.SerializeToUtf8Bytes(normalized, Strict)) == selection.Pre.EvidenceSha256,
            "NORMALIZED_EVIDENCE_HASH_MISMATCH");
        return normalized;
    }
}
