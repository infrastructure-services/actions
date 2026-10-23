using System.Text.Json;
using SqlDiscovery.V2;

namespace DatabaseReleaseQualification;

// Acquired only through the host's NEW_BOOTSTRAP producer role. These bytes are
// evidence, never a caller-selected authority or a database creation receipt.
public sealed record EfPreprovisionedBootstrapV1(int Version, string Kind,
    EfEvidenceScopeV1 Scope, EvidenceReference Governance, EvidenceReference Onboarding,
    EvidenceReference Policy, EvidenceReference Source, EvidenceReference Pre,
    EvidenceReference ContentHashContract, EfPlanStateV1 ObservedState,
    string DataCoverage, long? BusinessRowCount, string HistoryPolicy,
    DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresAtUtc);

internal sealed class EfBootstrapException(string code) : Exception(code);

internal static class EfPreprovisionedBootstrap
{
    private static void Require(bool condition, string code)
    { if (!condition) throw new EfBootstrapException(code); }

    internal static void Validate(EfPreprovisionedBootstrapV1 b, EfPreparationSelectionV1 selection,
        EfGovernedPreparationV1 governance, EfObservationEvidenceV1 pre, DateTimeOffset now)
    {
        Require(b.Version == 1 && b.Kind == "EF_PREPROVISIONED_BOOTSTRAP", "BOOTSTRAP_CONTRACT_INVALID");
        var a = governance.Authority; var o = pre.Observation;
        Require(a.Scenario == "NEW_EF" && a.Lifecycle == "NEW" && a.ChangeManagementMode == "EF_MIGRATIONS"
            && a.OnboardingState == "NOT_REQUIRED" && a.CertifiedBaseline is null, "BOOTSTRAP_GOVERNANCE_INVALID");
        Require(b.Scope == selection.Scope && b.Governance == a.Governance.Document
            && b.Onboarding == a.Onboarding.Document && b.Policy == governance.Policy.Provenance.Document
            && b.Source == selection.Source && b.Pre == selection.Pre
            && b.ContentHashContract == governance.ContentHashContract, "BOOTSTRAP_BINDING_MISMATCH");
        Require(governance.Policy.MaximumValiditySeconds > 0 && b.NotBeforeUtc <= now && now < b.ExpiresAtUtc
            && b.NotBeforeUtc <= o.CapturedAtUtc && o.CapturedAtUtc <= now
            && (b.ExpiresAtUtc - b.NotBeforeUtc).TotalSeconds <= governance.Policy.MaximumValiditySeconds
            && (now - o.CapturedAtUtc).TotalSeconds <= governance.Policy.MaximumValiditySeconds,
            "BOOTSTRAP_EXPIRED_OR_STALE");
        var observed = EfReleasePlannerV1.Observe(o, a.Target, now);
        Require(JsonSerializer.Serialize(b.ObservedState, JsonDefaults.Compact)
            == JsonSerializer.Serialize(observed, JsonDefaults.Compact), "BOOTSTRAP_PRE_MISMATCH");
        Require(pre.Discovery.Physical is { Status: PhysicalStatus.Complete, BusinessObjectCount: 0, TechnicalObjectCount: 0 } physical
            && EmptyForNewEfV1.Valid(physical), "BOOTSTRAP_PHYSICAL_STATE_INVALID");
        Require(o.AppliedMigrationIds.Length == 0 && o.HistoryStatus is "ABSENT" or "EMPTY", "BOOTSTRAP_HISTORY_INVALID");
        Require(o.HistoryStatus == "ABSENT" ? b.HistoryPolicy == "HISTORY_ABSENT_V1"
            : b.HistoryPolicy == "EMPTY_HISTORY_ATTESTED_V1", "INITIAL_HISTORY_STATE_UNVERIFIED");
        // No broad history exclusion: ABSENT cannot contain history objects, and
        // EMPTY requires exactly one history table with measured zero rows.
        var history = o.Schema.Objects.Where(x => x.Kind == "table" && x.Schema == "dbo"
            && x.Name == "__EFMigrationsHistory").ToArray();
        Require(history.Length == (o.HistoryStatus == "EMPTY" ? 1 : 0)
            && o.Schema.Objects.All(x => x.Kind == "schema" && EmptyForNewEfV1.StandardSchemas.Contains(x.Name, StringComparer.Ordinal)
                || o.HistoryStatus == "EMPTY" && x.Schema == "dbo"
                    && (x.Kind == "table" && x.Name == "__EFMigrationsHistory"
                        || x.Parent == "__EFMigrationsHistory" && x.Kind is "column" or "key-constraint-column" or "index-column")),
            "BOOTSTRAP_SCHEMA_INVALID");
        Require(o.HistoryStatus != "EMPTY" || ValidHistorySchema(o.Schema.Objects.Where(x => x.Schema == "dbo"
            && (x.Name == "__EFMigrationsHistory" || x.Parent == "__EFMigrationsHistory"))), "BOOTSTRAP_HISTORY_STRUCTURE_INVALID");
        Require(b.DataCoverage == "COMPLETE" && b.BusinessRowCount == 0
            && o.Schema.ImpactMetrics.Count == history.Length
            && o.Schema.ImpactMetrics.All(x => x.Schema == "dbo" && x.Table == "__EFMigrationsHistory" && x.RowCount == 0),
            "BOOTSTRAP_DATA_NOT_EMPTY_OR_UNVERIFIED");
    }

    internal static bool ValidHistorySchema(IEnumerable<SchemaObject> history)
    {
        var objects = history.ToArray();
        return objects.Count(x => x.Kind == "table" && x.Name == "__EFMigrationsHistory") == 1
            && objects.Count(x => x.Kind == "column") == 2
            && new[] { ("MigrationId", "300"), ("ProductVersion", "64") }.All(spec =>
                objects.Count(x => x.Kind == "column" && x.Name == spec.Item1 && x.Parent == "__EFMigrationsHistory"
                    && x.Properties.GetValueOrDefault("dataType") == "nvarchar"
                    && x.Properties.GetValueOrDefault("maxLength") == spec.Item2
                    && x.Properties.GetValueOrDefault("nullable") == "false") == 1)
            && objects.Count(x => x.Kind == "key-constraint-column") == 1
            && objects.Any(x => x.Kind == "key-constraint-column" && x.Parent == "__EFMigrationsHistory"
                && !string.IsNullOrWhiteSpace(x.Properties.GetValueOrDefault("constraint"))
                && x.Properties.GetValueOrDefault("constraintType") == "PK"
                && x.Properties.GetValueOrDefault("column") == "MigrationId"
                && x.Properties.GetValueOrDefault("ordinal") == "1")
            && objects.Count(x => x.Kind == "index-column") == 1
            && objects.Any(x => x.Kind == "index-column" && x.Parent == "__EFMigrationsHistory"
                && x.Properties.GetValueOrDefault("primaryKey") == "true"
                && x.Properties.GetValueOrDefault("unique") == "true"
                && x.Properties.GetValueOrDefault("disabled") == "false"
                && x.Properties.GetValueOrDefault("filter") == ""
                && x.Properties.GetValueOrDefault("role") == "KEY"
                && x.Properties.GetValueOrDefault("column") == "MigrationId"
                && x.Properties.GetValueOrDefault("ordinal") == "1"
                && x.Properties.GetValueOrDefault("index") == objects.Single(k => k.Kind == "key-constraint-column").Properties.GetValueOrDefault("constraint"));
    }
}
