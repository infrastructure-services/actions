using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DatabaseReleaseQualification;

internal static class RecoverySecurityAnalysis
{
    // Deliberately bounded permissions. Server-level and ambiguous forms never
    // become database-scoped by absence of an ON clause.
    private static readonly HashSet<string> DatabasePermissions = new(StringComparer.Ordinal) {
        "CONNECT", "CREATE TABLE", "CREATE VIEW", "VIEW DEFINITION", "CONTROL"
    };
    private static readonly HashSet<string> SecurablePermissions = new(StringComparer.Ordinal) {
        "SELECT", "INSERT", "UPDATE", "DELETE", "REFERENCES", "EXECUTE", "VIEW DEFINITION", "ALTER", "CONTROL", "TAKE OWNERSHIP"
    };

    public static ScriptOperation? Analyze(SecurityStatement statement)
    {
        if (statement is not (GrantStatement or DenyStatement or RevokeStatement)
            || statement is DenyStatement { CascadeOption: true } or RevokeStatement { CascadeOption: true }) return null;
        var target = statement.SecurityTargetObject;
        var identifiers = target?.ObjectName?.MultiPartIdentifier?.Identifiers.Select(x => x.Value).ToArray() ?? [];
        RecoverySecuritySecurable scope;
        if (target is null) scope = new("DATABASE");
        else if (target.ObjectKind.ToString() is "Object" or "NotSpecified" && identifiers.Length == 2)
            scope = new("OBJECT", identifiers[0], identifiers[1]);
        else if (target.ObjectKind.ToString() == "Schema" && identifiers.Length == 1)
            scope = new("SCHEMA", identifiers[0]);
        else return null;
        if (statement.Permissions.Count == 0 || statement.Principals.Count == 0) return null;
        foreach (var permission in statement.Permissions)
        {
            var name = string.Join(" ", permission.Identifiers.Select(x => x.Value.ToUpperInvariant()));
            if (!(scope.Kind == "DATABASE" ? DatabasePermissions : SecurablePermissions).Contains(name)) return null;
            if (scope.Kind != "OBJECT" && permission.Columns.Count > 0) return null;
        }
        if (scope.Kind != "OBJECT" && target?.Columns.Count > 0) return null;
        var principals = statement.Principals.Select(x => x.Identifier?.Value).ToList();
        if (statement.AsClause is not null) principals.Add(statement.AsClause.Value);
        if (principals.Any(x => string.IsNullOrWhiteSpace(x))) return null;
        return new ScriptOperation {
            Operation = "DATABASE_SECURITY", AstNodeType = statement.GetType().Name,
            Schema = scope.Schema, Object = scope.Name, IsSensitive = true, TargetResolved = true,
            SecuritySecurable = scope,
            SecurityPermissions = statement.Permissions.Select(x => string.Join(" ",
                x.Identifiers.Select(identifier => identifier.Value.ToUpperInvariant())))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            SecurityHasColumns = statement.Permissions.Any(x => x.Columns.Count > 0)
                || target?.Columns.Count > 0,
            SecurityPrincipals = principals.Select(x => x!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        };
    }
}
