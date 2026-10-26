namespace DatabaseReleaseQualification;

// LEGACY-only composition: independently prove visible object absence and use
// the shared security reader for schema owners and principal ownership closure.
internal sealed class LegacyRecoverySecurityReader(
    IRecoverySecurityCatalogReader reader, ILegacyScopeSafetySource safety) : IRecoverySecurityCatalogReader
{
    internal static IRecoverySecurityCatalogReader Create(ISecurityCatalogTransport transport,
        SecurityTargetBindingV1 binding) => new LegacyRecoverySecurityReader(
            new SqlRecoverySecurityCatalogReader(transport, binding),
            new SqlLegacyScopeSafetySource(transport, binding));

    public async Task<RecoverySecuritySnapshot> ReadAsync(RecoverySecurityScope scope,
        RecoveryPhase phase, CancellationToken cancellationToken = default)
    {
        var objects = scope.Securables.Where(x => x.Kind == "OBJECT").ToArray();
        if (objects.Length == 0) return await reader.ReadAsync(scope, phase, cancellationToken);
        var first = await safety.CaptureAsync(objects, cancellationToken);
        Validate(first, objects);
        var absent = objects.Where(x => first.Objects.Single(o => o.Schema == x.Schema && o.Name == x.Name).Kind == "ABSENT").ToArray();
        if (absent.Length == 0) return await reader.ReadAsync(scope, phase, cancellationToken);
        var expanded = new RecoverySecurityScope(scope.Securables.Except(absent)
            .Concat(absent.Select(x => new RecoverySecuritySecurable("SCHEMA", x.Schema))).Distinct().ToArray(), scope.RequiredPrincipals);
        var raw = await reader.ReadAsync(expanded, phase, cancellationToken);
        if (RecoverySecurityCanonicalizer.Canonicalize(expanded, phase, raw) is null
            || raw.ServerInstance != first.ServerInstance || raw.DatabaseName != first.DatabaseName)
            throw new LegacyContractException("SECURITY_EVIDENCE_INCOMPLETE");
        var last = await safety.CaptureAsync(objects, cancellationToken);
        Validate(last, objects);
        if (first.Sha256 != last.Sha256) throw new LegacyContractException("SECURITY_CAPTURE_CHANGED");
        var states = raw.Securables.Where(x => scope.Securables.Contains(x.Securable)).Concat(absent.Select(x =>
            new RecoverySecurityObjectState(x, false, null,
                raw.Securables.Single(s => s.Securable == new RecoverySecuritySecurable("SCHEMA", x.Schema)).SchemaOwner, []))).ToArray();
        var needed = new HashSet<string>(scope.RequiredPrincipals, StringComparer.Ordinal);
        foreach (var state in states)
        {
            if (state.SchemaOwner is not null) needed.Add(state.SchemaOwner);
            if (state.ExplicitOwner is not null) needed.Add(state.ExplicitOwner);
            foreach (var permission in state.Permissions) { needed.Add(permission.Grantee); needed.Add(permission.Grantor); }
        }
        var queue = new Queue<string>(needed);
        while (queue.TryDequeue(out var name))
        {
            var principal = raw.Principals.Single(x => x.Name == name);
            if (principal.OwnerName is not null && needed.Add(principal.OwnerName)) queue.Enqueue(principal.OwnerName);
        }
        var result = raw with
        {
            ScopeHash = scope.Sha256,
            Securables = states,
            Principals = raw.Principals.Where(x => needed.Contains(x.Name)).ToArray()
        };
        if (RecoverySecurityCanonicalizer.Canonicalize(scope, phase, result) is null)
            throw new LegacyContractException("SECURITY_EVIDENCE_INCOMPLETE");
        return result;
    }
    private static void Validate(LegacyScopeSafetySnapshotV1 value, IReadOnlyList<RecoverySecuritySecurable> objects)
    {
        var hash = LegacyRuntimeEvidenceHash.Hash(new
        {
            value.ContractVersion,
            value.Complete,
            value.ServerInstance,
            value.DatabaseName,
            value.DatabaseDdlTriggersComplete,
            value.ServerDdlTriggersComplete,
            value.HasEnabledDdlTrigger,
            value.Objects
        });
        if (value.ContractVersion != 1 || !value.Complete || value.Sha256 != hash
            || value.Objects.Count != objects.Count || objects.Any(x => value.Objects.Count(o => o.Schema == x.Schema && o.Name == x.Name) != 1))
            throw new LegacyContractException("SECURABLE_ABSENCE_UNPROVEN");
    }
}
