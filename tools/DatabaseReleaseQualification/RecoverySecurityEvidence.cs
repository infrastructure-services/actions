using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

// Numeric catalog IDs are lookup keys only, never evidence identity. SID is kept
// because recreating a same-named user must not silently preserve its identity.
public sealed record RecoverySecurityPrincipal(string Name, string Type, string SidHex,
    string AuthenticationType, string? DefaultSchema, string? OwnerName, bool IsFixedRole);
public sealed record RecoverySecurityPermission(string Permission, string State,
    string Grantee, string Grantor, string? Column = null);
public sealed record RecoverySecurityObjectState(RecoverySecuritySecurable Securable,
    bool Exists, string? ExplicitOwner, string? SchemaOwner,
    IReadOnlyList<RecoverySecurityPermission> Permissions);
public sealed record RecoverySecuritySnapshot(
    int ContractVersion, RecoveryPhase Phase, string ScopeHash,
    string ServerInstance, string DatabaseName,
    RecoveryEvidenceCoverage Coverage, bool MetadataVisibilityComplete,
    IReadOnlyList<RecoverySecurityObjectState> Securables,
    IReadOnlyList<RecoverySecurityPrincipal> Principals);
public sealed record CanonicalRecoverySecurity(string DatabaseIdentity, string Sha256, string Json);

public interface IRecoverySecurityEvidenceProvider
{
    Task<RecoverySecuritySnapshot> CaptureSecurityAsync(RecoverySecurityScope scope,
        RecoveryPhase phase, CancellationToken cancellationToken = default);
}

// Read-only source boundary: no script, command, mutation or connection input.
// A future catalog adapter must satisfy the documented visibility and scope contract.
// This increment supplies the provider/canonicalization and fake adapters, not SQL runtime.
public interface IRecoverySecurityCatalogReader
{
    Task<RecoverySecuritySnapshot> ReadAsync(RecoverySecurityScope scope,
        RecoveryPhase phase, CancellationToken cancellationToken = default);
}

public sealed class ReadOnlyRecoverySecurityProvider(IRecoverySecurityCatalogReader reader) : IRecoverySecurityEvidenceProvider
{
    public async Task<RecoverySecuritySnapshot> CaptureSecurityAsync(RecoverySecurityScope scope,
        RecoveryPhase phase, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await reader.ReadAsync(scope, phase, cancellationToken);
        // Validate before returning; a source cannot label malformed evidence COMPLETE.
        _ = RecoverySecurityCanonicalizer.Canonicalize(scope, phase, value);
        return value;
    }
}

public static class RecoverySecurityCanonicalizer
{
    private static bool Name(string? value) => value is { Length: > 0 and <= 128 } && !value.Any(char.IsControl);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Compact);
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid)
    { if (!valid) throw new InvalidOperationException("RECOVERY_SECURITY_EVIDENCE_INVALID"); }

    public static CanonicalRecoverySecurity? Canonicalize(RecoverySecurityScope scope, RecoveryPhase phase, RecoverySecuritySnapshot evidence)
    {
        Require(evidence is not null && evidence.ContractVersion == 1 && evidence.Phase == phase
            && evidence.ScopeHash == scope.Sha256 && Enum.IsDefined(evidence.Coverage)
            && ObservedDatabaseIdentityContract.IsValid(evidence.ServerInstance, evidence.DatabaseName));
        Require(scope.Securables.Count > 0 && scope.Securables.Distinct().Count() == scope.Securables.Count);
        Require(scope.RequiredPrincipals.All(Name) && scope.RequiredPrincipals.Distinct(StringComparer.Ordinal).Count() == scope.RequiredPrincipals.Count);
        foreach (var item in scope.Securables)
            Require(item.Kind switch {
                "OBJECT" => Name(item.Schema) && Name(item.Name),
                "SCHEMA" => Name(item.Schema) && item.Name == "",
                "DATABASE" => item.Schema == "" && item.Name == "",
                _ => false
            });
        if (evidence.Coverage != RecoveryEvidenceCoverage.Complete || !evidence.MetadataVisibilityComplete) return null;
        Require(evidence.Securables is not null && evidence.Principals is not null);
        Require(evidence.Securables.Count == scope.Securables.Count
            && evidence.Securables.Select(x => x.Securable).Distinct().Count() == scope.Securables.Count
            && evidence.Securables.All(x => scope.Securables.Contains(x.Securable)));
        var principals = evidence.Principals.ToDictionary(x => x.Name, StringComparer.Ordinal);
        foreach (var principal in principals.Values)
        {
            Require(Name(principal.Name) && new[] { "S", "U", "G", "R", "A", "C", "K", "E", "X" }.Contains(principal.Type)
                && Regex.IsMatch(principal.SidHex, @"\A(?:[0-9a-fA-F]{2}){1,85}\z")
                && new[] { "NONE", "INSTANCE", "DATABASE", "WINDOWS", "EXTERNAL" }.Contains(principal.AuthenticationType)
                && (principal.DefaultSchema is null || Name(principal.DefaultSchema))
                && (principal.OwnerName is null || Name(principal.OwnerName)));
        }
        var needed = new HashSet<string>(scope.RequiredPrincipals, StringComparer.Ordinal);
        foreach (var state in evidence.Securables)
        {
            Require(state.Permissions is not null);
            if (!state.Exists) Require(state.Securable.Kind == "OBJECT" && state.ExplicitOwner is null && state.Permissions.Count == 0);
            if (state.Securable.Kind is "OBJECT" or "SCHEMA")
            { Require(Name(state.SchemaOwner)); needed.Add(state.SchemaOwner!); }
            else Require(state.Exists && state.SchemaOwner is null && state.ExplicitOwner is null);
            if (state.ExplicitOwner is not null) { Require(Name(state.ExplicitOwner)); needed.Add(state.ExplicitOwner); }
            // Capture all permission rows on the affected securable, including column
            // exceptions (R), grant option (W), and the grantor dimension.
            Require(state.Permissions.Select(x => Serialize(new { x.Permission, x.Grantee, x.Grantor, x.Column })).Distinct().Count() == state.Permissions.Count);
            foreach (var permission in state.Permissions)
            {
                Require(Regex.IsMatch(permission.Permission, @"\A[A-Z][A-Z ]{0,127}\z")
                    && new[] { "G", "W", "D", "R" }.Contains(permission.State)
                    && (permission.State != "R" || permission.Column is not null)
                    && (permission.Column is null || state.Securable.Kind == "OBJECT" && Name(permission.Column)));
                needed.Add(permission.Grantee); needed.Add(permission.Grantor);
            }
        }
        // Include principal ownership closure without relying on catalog numeric IDs.
        var queue = new Queue<string>(needed);
        while (queue.TryDequeue(out var name))
        {
            Require(principals.TryGetValue(name, out var principal));
            if (principal!.OwnerName is not null && needed.Add(principal.OwnerName)) queue.Enqueue(principal.OwnerName);
        }
        Require(needed.SetEquals(principals.Keys)); // No unrelated full-database inventory.
        var identity = Serialize(new { evidence.ServerInstance, evidence.DatabaseName });
        var json = Serialize(new {
            version = 1, databaseIdentity = identity, scopeHash = scope.Sha256,
            securables = evidence.Securables.Select(x => new {
                x.Securable, x.Exists, x.ExplicitOwner, x.SchemaOwner,
                permissions = x.Permissions.OrderBy(Serialize, StringComparer.Ordinal).ToArray()
            }).OrderBy(Serialize, StringComparer.Ordinal).ToArray(),
            principals = evidence.Principals.Select(x => x with { SidHex = x.SidHex.ToLowerInvariant() }).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray()
        });
        return new(identity, Hashing.Sha256(json), json);
    }
}
