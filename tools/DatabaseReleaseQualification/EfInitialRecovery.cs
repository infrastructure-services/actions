using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record EfRoleMembershipV1(string Role, string Member);
public sealed record EfFullSecurityV1(RecoverySecuritySnapshot Snapshot,
    EfRoleMembershipV1[] RoleMemberships, string DatabaseOwnerSid);
public sealed record EfInitialRecoveryPolicyV1(int Version, string PolicyId, EfEvidenceScopeV1 Scope,
    string ReleaseId, string OperationId, EfPlanTargetV1 RehearsalTarget, string DatabaseIncarnation,
    string PayloadHash, string ForwardHash, string RollbackHash, EvidenceReference Bootstrap,
    EvidenceReference ContentHashContract, SchemaSnapshot HistorySchema,
    RecoverySecurityObjectState HistorySecurity, DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresAtUtc);
public sealed record EfInitialRecoverySelectionV1(EvidenceReference Reference, EfInitialRecoveryPolicyV1 Policy);
public sealed record EfInitialPhaseV1(RecoveryPhase Phase, EfEvidenceScopeV1 Scope,
    EfPlanObservationV1 Observation, EfFullSecurityV1 Security, long? BusinessRowCount,
    EvidenceReference SecurityEvidence);
public sealed record EfInitialRecoveryEvidenceV1(EfInitialRecoverySelectionV1 Selection,
    Dictionary<RecoveryPhase, EfInitialPhaseV1> Phases);

// Registration belongs to the rehearsal host. No CLI/provider selector is added.
public interface IEfInitialRecoveryObserverV1
{
    Task<EfInitialPhaseV1> CaptureAsync(RecoveryPhase phase, SchemaSnapshot capturedSchema, CancellationToken token);
}

public static class EfInitialRecoveryV1
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Compact);
    private static bool Hash(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z");
    internal static void Require(bool valid) { if (!valid) throw new InvalidOperationException("EF_INITIAL_RECOVERY_UNVERIFIED"); }
    private static bool IsHistory(SchemaObject x) => x.Schema == "dbo"
        && (x.Kind == "table" && x.Name == "__EFMigrationsHistory"
            || x.Parent == "__EFMigrationsHistory" && x.Kind is "column" or "key-constraint-column" or "index-column");
    private static bool History(RecoverySecuritySecurable x) => x == new RecoverySecuritySecurable("OBJECT", "dbo", "__EFMigrationsHistory");
    private static string Schema(IEnumerable<SchemaObject> objects) => SchemaCanonicalizer.Canonicalize(new() { Objects = objects.ToList() }).Sha256;
    internal static T Freeze<T>(T value) => JsonSerializer.Deserialize<T>(Json(value), JsonDefaults.Compact)!;

    public static string SecurityHash(EfFullSecurityV1 full)
    {
        var s = full.Snapshot;
        Require(s.Securables.Any(x => x.Securable == new RecoverySecuritySecurable("DATABASE") && x.Exists)
            && s.Securables.Any(x => x.Securable == new RecoverySecuritySecurable("SCHEMA", "dbo") && x.Exists));
        var scope = new RecoverySecurityScope(s.Securables.Select(x => x.Securable).ToArray(), s.Principals.Select(x => x.Name).ToArray());
        var canonical = RecoverySecurityCanonicalizer.Canonicalize(scope, s.Phase, s);
        Require(canonical is not null && Regex.IsMatch(full.DatabaseOwnerSid, @"\A(?:[0-9a-f]{2}){1,85}\z")
            && full.RoleMemberships.Distinct().Count() == full.RoleMemberships.Length
            && full.RoleMemberships.All(x => s.Principals.Any(p => p.Name == x.Role && p.Type == "R")
                && s.Principals.Any(p => p.Name == x.Member)));
        // Physical identity is checked separately. The content projection must
        // remain comparable with an independently identified rehearsal clone.
        using var content = JsonDocument.Parse(canonical!.Json);
        return Hashing.Sha256(Json(new { scopeHash = scope.Sha256,
            securables = content.RootElement.GetProperty("securables"),
            principals = content.RootElement.GetProperty("principals"),
            roles = full.RoleMemberships.OrderBy(x => x.Role, StringComparer.Ordinal).ThenBy(x => x.Member, StringComparer.Ordinal), full.DatabaseOwnerSid }));
    }

    internal static void ValidatePolicy(EfInitialRecoverySelectionV1 selection, ReleaseDescriptor release,
        ReleaseScript forward, ReleaseScript rollback)
    {
        var p = selection.Policy;
        Require(p.Version == 1 && p.PolicyId == "NEW_EF_ABSENT_EMPTY_V1" && release.Scenario == "NEW_EF" && release.SourceKind == "EF"
            && release.DatabaseLifecycle == "NEW" && release.Environment == "TEST" && release.ReleaseId == p.ReleaseId
            && Guid.TryParseExact(p.OperationId, "D", out _) && p.Scope.TargetId != p.RehearsalTarget.TargetId
            && p.Scope.SourceRepository == p.RehearsalTarget.SourceRepository
            && p.PayloadHash == ReleasePayloadBuilder.Build(release, forward, rollback).PayloadHash
            && p.ForwardHash == forward.Sha256 && p.RollbackHash == rollback.Sha256
            && p.NotBeforeUtc < p.ExpiresAtUtc && Hash(p.Bootstrap.EvidenceSha256)
            && Hash(p.ContentHashContract.EvidenceSha256) && Hash(selection.Reference.EvidenceSha256)
            && selection.Reference.StorageLocator is null && selection.Reference.EvidenceSha256 == Hashing.Sha256(Json(p)));
        ValidatePolicyContent(selection);
    }

    private static void ValidatePolicyContent(EfInitialRecoverySelectionV1 selection)
    {
        var p = selection.Policy;
        Require(p.Version == 1 && p.PolicyId == "NEW_EF_ABSENT_EMPTY_V1"
            && Guid.TryParseExact(p.OperationId, "D", out _) && !string.IsNullOrWhiteSpace(p.ReleaseId)
            && !string.IsNullOrWhiteSpace(p.Scope.SessionId) && !string.IsNullOrWhiteSpace(p.DatabaseIncarnation)
            && p.Scope.TargetId != p.RehearsalTarget.TargetId && p.RehearsalTarget.Environment == "TEST"
            && p.Scope.SourceRepository == p.RehearsalTarget.SourceRepository
            && p.NotBeforeUtc < p.ExpiresAtUtc && Hash(p.PayloadHash) && Hash(p.ForwardHash) && Hash(p.RollbackHash)
            && Hash(p.Bootstrap.EvidenceSha256) && Hash(p.ContentHashContract.EvidenceSha256)
            && selection.Reference.StorageLocator is null && !string.IsNullOrWhiteSpace(selection.Reference.EvidenceId)
            && selection.Reference.EvidenceSha256 == Hashing.Sha256(Json(p)));
        var objects = p.HistorySchema.Objects;
        Require(p.HistorySchema.SchemaCoverage == SchemaCoverage.Complete && objects.All(IsHistory)
            && EfPreprovisionedBootstrap.ValidHistorySchema(objects)
            && History(p.HistorySecurity.Securable) && p.HistorySecurity.Exists);
    }

    internal static void ValidatePhase(EfInitialRecoveryPolicyV1 policy, EfInitialPhaseV1 phase, SchemaSnapshot schema)
    {
        var o = phase.Observation;
        Require(phase.Scope == policy.Scope && phase.Phase == phase.Security.Snapshot.Phase
            && o.DatabaseIncarnation == policy.DatabaseIncarnation && o.CapturedAtUtc >= policy.NotBeforeUtc
            && o.CapturedAtUtc < policy.ExpiresAtUtc && o.ImpactCoverage == "COMPLETE"
            && SchemaCanonicalizer.Canonicalize(schema).Sha256 == SchemaCanonicalizer.Canonicalize(o.Schema).Sha256
            && o.SecurityHash == SecurityHash(phase.Security)
            && phase.SecurityEvidence.StorageLocator is null && !string.IsNullOrWhiteSpace(phase.SecurityEvidence.EvidenceId)
            && phase.SecurityEvidence.EvidenceSha256 == Hashing.Sha256(Json(phase.Security))
            && phase.Security.Snapshot.ServerInstance == o.ServerInstance && phase.Security.Snapshot.DatabaseName == o.DatabaseName
            && phase.BusinessRowCount is >= 0);
        _ = EfReleasePlannerV1.Observe(o, policy.RehearsalTarget, o.CapturedAtUtc);
        var securedObjects = phase.Security.Snapshot.Securables.Where(x => x.Securable.Kind == "OBJECT").ToArray();
        var schemaObjects = o.Schema.Objects.Where(x => x.Parent == "" && x.Kind != "schema").ToArray();
        Require(securedObjects.All(x => x.Exists == schemaObjects.Any(s => s.Schema == x.Securable.Schema && s.Name == x.Securable.Name))
            && schemaObjects.All(s => securedObjects.Any(x => x.Exists && x.Securable.Schema == s.Schema && x.Securable.Name == s.Name)));
        if (phase.Phase is RecoveryPhase.Pre or RecoveryPhase.Pre2)
            Require(o.AppliedMigrationIds.Length == 0 && phase.BusinessRowCount == 0
                && o.Schema.Objects.All(x => IsHistory(x) || x.Kind == "schema" && SqlDiscovery.V2.EmptyForNewEfV1.StandardSchemas.Contains(x.Name)));
        if (phase.Phase == RecoveryPhase.Pre)
            Require(o.HistoryStatus == "ABSENT" && !o.Schema.Objects.Any(IsHistory));
        else
        {
            Require(o.HistoryStatus == (phase.Phase == RecoveryPhase.Pre2 ? "EMPTY" : "PRESENT")
                && Schema(o.Schema.Objects.Where(IsHistory)) == Schema(policy.HistorySchema.Objects)
                && Json(phase.Security.Snapshot.Securables.Single(x => History(x.Securable))) == Json(policy.HistorySecurity));
            Require(o.Schema.ImpactMetrics.Count(x => x.Schema == "dbo" && x.Table == "__EFMigrationsHistory"
                && x.RowCount == o.AppliedMigrationIds.Length) == 1);
        }
    }

    internal static bool EquivalentPre(EfInitialRecoveryEvidenceV1 evidence)
    {
        try
        {
            var pre = evidence.Phases[RecoveryPhase.Pre]; var post = evidence.Phases[RecoveryPhase.Post1]; var recovered = evidence.Phases[RecoveryPhase.Pre2];
            foreach (var p in new[] { pre, post, recovered }) ValidatePhase(evidence.Selection.Policy, p, p.Observation.Schema);
            Require(pre.Observation.CapturedAtUtc <= post.Observation.CapturedAtUtc && post.Observation.CapturedAtUtc <= recovered.Observation.CapturedAtUtc);
            Require(Schema(pre.Observation.Schema.Objects) == Schema(recovered.Observation.Schema.Objects.Where(x => !IsHistory(x)))
                && pre.Observation.DataHash == recovered.Observation.DataHash);
            var before = pre.Security.Snapshot.Securables.Single(x => History(x.Securable));
            Require(!before.Exists && before.ExplicitOwner is null && before.Permissions.Count == 0
                && before.SchemaOwner == evidence.Selection.Policy.HistorySecurity.SchemaOwner);
            // Replace only the observed history securable with its observed PRE
            // state. All other owners, permissions, principals and roles stay in
            // the full canonical comparison; nothing is excluded from capture.
            var restored = recovered.Security with { Snapshot = recovered.Security.Snapshot with {
                Securables = recovered.Security.Snapshot.Securables.Select(x => History(x.Securable) ? before : x).ToArray() } };
            return SecurityHash(pre.Security) == SecurityHash(restored);
        }
        catch { return false; }
    }

    internal static bool Complete(EfInitialRecoveryEvidenceV1 evidence)
    {
        try
        {
            ValidatePolicyContent(evidence.Selection);
            Require(evidence.Phases.Count == 4 && Enum.GetValues<RecoveryPhase>().All(evidence.Phases.ContainsKey)
                && evidence.Phases.All(x => x.Key == x.Value.Phase)
                && evidence.Selection.Reference.EvidenceSha256 == Hashing.Sha256(Json(evidence.Selection.Policy)));
            foreach (var p in evidence.Phases.Values) ValidatePhase(evidence.Selection.Policy, p, p.Observation.Schema);
            var post = evidence.Phases[RecoveryPhase.Post1].Observation; var post2 = evidence.Phases[RecoveryPhase.Post2].Observation;
            Require(evidence.Phases.Values.Select(x => x.Observation.Evidence.EvidenceId).Distinct(StringComparer.Ordinal).Count() == 4
                && evidence.Phases.Values.Select(x => x.SecurityEvidence.EvidenceId).Distinct(StringComparer.Ordinal).Count() == 4
                && evidence.Phases[RecoveryPhase.Pre2].Observation.CapturedAtUtc <= post2.CapturedAtUtc
                && post.HistoryStatus == post2.HistoryStatus && post.AppliedMigrationIds.SequenceEqual(post2.AppliedMigrationIds)
                && Schema(post.Schema.Objects) == Schema(post2.Schema.Objects) && post.DataHash == post2.DataHash
                && post.SecurityHash == post2.SecurityHash);
            return EquivalentPre(evidence);
        }
        catch { return false; }
    }

    internal static bool CoversSecurity(EfInitialRecoveryEvidenceV1 evidence, RecoveryPhase phase, CanonicalRecoverySecurity scoped)
    {
        try
        {
            Require(Hashing.Sha256(scoped.Json) == scoped.Sha256);
            var full = evidence.Phases[phase].Security.Snapshot;
            var scope = new RecoverySecurityScope(full.Securables.Select(x => x.Securable).ToArray(), full.Principals.Select(x => x.Name).ToArray());
            var canonical = RecoverySecurityCanonicalizer.Canonicalize(scope, phase, full)!;
            Require(scoped.DatabaseIdentity == canonical.DatabaseIdentity);
            using var actual = JsonDocument.Parse(scoped.Json); using var expected = JsonDocument.Parse(canonical.Json);
            foreach (var key in new[] { "securables", "principals" })
            {
                var values = expected.RootElement.GetProperty(key).EnumerateArray().Select(x => x.GetRawText()).ToHashSet(StringComparer.Ordinal);
                Require(actual.RootElement.GetProperty(key).EnumerateArray().All(x => values.Contains(x.GetRawText())));
            }
            return true;
        }
        catch { return false; }
    }
}

internal sealed class EfInitialRecoverySessionV1(EfInitialRecoverySelectionV1 selection, IEfInitialRecoveryObserverV1 observer)
{
    internal EfInitialRecoveryEvidenceV1 Evidence { get; } = new(EfInitialRecoveryV1.Freeze(selection), []);
    internal async Task Capture(RecoveryPhase phase, SchemaSnapshot schema, CancellationToken token)
    {
        var value = EfInitialRecoveryV1.Freeze(await observer.CaptureAsync(phase, EfInitialRecoveryV1.Freeze(schema), token));
        EfInitialRecoveryV1.Require(value.Phase == phase);
        EfInitialRecoveryV1.ValidatePhase(Evidence.Selection.Policy, value, schema);
        Evidence.Phases.Add(phase, value);
    }
}
