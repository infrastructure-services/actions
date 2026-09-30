using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DatabaseReleaseQualification;

public sealed class SecurityCatalogException(string code, string stage) : Exception(code)
{
    public string Code { get; } = code;
    public string Stage { get; } = stage;
    public object Diagnostic => new { contractVersion = 1, code = Code, stage = Stage };
}

public sealed record SecurityTargetBindingV1(
    string DatabaseName, string ServerMatchPolicy, IReadOnlyList<string> AllowedServerInstances);

public interface ISecurityCatalogSession : IAsyncDisposable
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, CancellationToken token);
}

public interface ISecurityCatalogTransport
{
    Task<ISecurityCatalogSession> OpenAsync(CancellationToken token);
}

public static class SecuritySqlLiteral
{
    public static string Sysname(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128
            || value.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
            throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "SCOPE");
        return "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    public static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);
}

public sealed class SqlClientSecurityCatalogTransport(Func<string> trustedConnectionResolver,
    SecurityTargetBindingV1 binding) : ISecurityCatalogTransport
{
    public async Task<ISecurityCatalogSession> OpenAsync(CancellationToken token)
    {
        SqlConnection? connection = null;
        try
        {
            var builder = BuildConnectionOptions(trustedConnectionResolver(), binding);
            connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token);
            return new Session(connection);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (connection is not null) await connection.DisposeAsync();
            throw new SecurityCatalogException("SECURITY_ADAPTER_FAILURE", "CONNECT");
        }
    }

    internal static SqlConnectionStringBuilder BuildConnectionOptions(string trustedConnection,
        SecurityTargetBindingV1 binding)
    {
        try
        {
            return new SqlConnectionStringBuilder(trustedConnection) {
                InitialCatalog = binding.DatabaseName,
                Encrypt = SqlConnectionEncryptOption.Strict,
                TrustServerCertificate = false,
                ConnectTimeout = 15,
                ConnectRetryCount = 0,
                Pooling = false,
                Enlist = false,
                MultipleActiveResultSets = false,
                ApplicationIntent = ApplicationIntent.ReadWrite,
                ApplicationName = "legacy-package-qualification-security-v1"
            };
        }
        catch { throw new SecurityCatalogException("SECURITY_ADAPTER_FAILURE", "CONNECT"); }
    }

    private sealed class Session(SqlConnection connection) : ISecurityCatalogSession
    {
        private long observedBytes;
        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
            string sql, CancellationToken token)
        {
            SecuritySqlGuard.Validate(sql);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandType = System.Data.CommandType.Text;
            command.CommandTimeout = 30;
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(token);
                do
                {
                    while (await reader.ReadAsync(token))
                    {
                        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            observedBytes += value switch {
                                byte[] bytes => bytes.Length,
                                string text => System.Text.Encoding.UTF8.GetByteCount(text),
                                _ => 16
                            };
                            if (observedBytes > 32L * 1024 * 1024)
                                throw new SecurityCatalogException("RESOURCE_LIMIT", "PERMISSIONS");
                            row.Add(reader.GetName(i), value);
                        }
                        rows.Add(row);
                        if (rows.Count > 100000) throw new SecurityCatalogException("RESOURCE_LIMIT", "PERMISSIONS");
                    }
                } while (await reader.NextResultAsync(token));
                return rows;
            }
            catch (SecurityCatalogException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new SecurityCatalogException("SECURITY_ADAPTER_FAILURE", "PERMISSIONS"); }
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

public static class SecuritySqlGuard
{
    private static readonly HashSet<string> Catalogs = new(StringComparer.OrdinalIgnoreCase) {
        "sys.schemas", "sys.objects", "sys.columns", "sys.database_permissions",
        "sys.database_principals", "sys.tables", "sys.synonyms", "sys.triggers",
        "sys.server_triggers", "sys.foreign_keys", "sys.default_constraints",
        "sys.check_constraints", "sys.computed_columns",
        "sys.sql_expression_dependencies", "sys.security_policies",
        "sys.security_predicates", "sys.indexes"
    };
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase) {
        "SERVERPROPERTY", "DB_NAME", "DATABASEPROPERTYEX", "HAS_PERMS_BY_NAME",
        "CONVERT", "COUNT"
    };

    public static void Validate(string sql)
    {
        var parser = new TSql180Parser(true, SqlEngineType.Standalone);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count != 0 || fragment is not TSqlScript script
            || script.Batches.Count != 1 || script.Batches[0].Statements.Count != 1
            || script.Batches[0].Statements[0] is not SelectStatement select || select.Into is not null)
            throw new SecurityCatalogException("SECURITY_ADAPTER_FAILURE", "SCOPE");
        Inspect(fragment, new HashSet<TSqlFragment>(ReferenceEqualityComparer.Instance));
    }

    private static void Inspect(TSqlFragment node, HashSet<TSqlFragment> visited)
    {
        if (!visited.Add(node)) return;
        if (node is TableReference table)
        {
            var type = table.GetType().Name;
            if (type is "NamedTableReference" or "SchemaObjectFunctionTableReference")
            {
                var name = table.GetType().GetProperty("SchemaObject")?.GetValue(table) as SchemaObjectName;
                var parts = name?.Identifiers.Select(x => x.Value).ToArray();
                var qualified = parts is null ? "" : string.Join(".", parts);
                if (type == "NamedTableReference" && !Catalogs.Contains(qualified)
                    || type == "SchemaObjectFunctionTableReference"
                        && !qualified.Equals("sys.fn_builtin_permissions", StringComparison.OrdinalIgnoreCase))
                    throw new SecurityCatalogException("SECURITY_ADAPTER_FAILURE", "SCOPE");
            }
            else if (type is not ("QualifiedJoin" or "UnqualifiedJoin" or "JoinParenthesisTableReference"))
                throw new SecurityCatalogException("SECURITY_ADAPTER_FAILURE", "SCOPE");
        }
        if (node is FunctionCall call && !Functions.Contains(call.FunctionName.Value))
            throw new SecurityCatalogException("SECURITY_ADAPTER_FAILURE", "SCOPE");
        if (node is SelectStatement select && select.Into is not null)
            throw new SecurityCatalogException("SECURITY_ADAPTER_FAILURE", "SCOPE");
        foreach (var property in node.GetType().GetProperties(System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.Name is "ScriptTokenStream" or "FirstTokenIndex" or "LastTokenIndex")
                continue;
            object? value;
            try { value = property.GetValue(node); }
            catch { continue; }
            if (value is TSqlFragment child) Inspect(child, visited);
            else if (value is System.Collections.IEnumerable items && value is not string)
                foreach (var item in items)
                    if (item is TSqlFragment fragment) Inspect(fragment, visited);
        }
    }
}

public sealed class SqlRecoverySecurityCatalogReader(
    ISecurityCatalogTransport transport, SecurityTargetBindingV1 binding) : IRecoverySecurityCatalogReader
{
    private const string IdentitySql = "SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')) AS serverName, DB_NAME() AS databaseName, CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) AS majorVersion, CONVERT(int, SERVERPROPERTY('EngineEdition')) AS engineEdition, CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(),'Collation')) AS collation";
    private const string DatabaseProofSql = "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') AS viewDefinition, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW SECURITY DEFINITION') AS viewSecurityDefinition";

    public async Task<RecoverySecuritySnapshot> ReadAsync(RecoverySecurityScope scope,
        RecoveryPhase phase, CancellationToken cancellationToken = default)
    {
        if (scope.Securables.Count is < 1 or > 1024 || scope.RequiredPrincipals.Count > 4096)
            throw new SecurityCatalogException("RESOURCE_LIMIT", "SCOPE");
        if (string.IsNullOrWhiteSpace(binding.DatabaseName)
            || binding.ServerMatchPolicy is not ("ALLOW_LIST" or "MANAGED_ENDPOINT")
            || binding.ServerMatchPolicy == "ALLOW_LIST" && binding.AllowedServerInstances.Count == 0)
            throw new SecurityCatalogException("TARGET_IDENTITY_MISMATCH", "IDENTITY");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await using var session = await transport.OpenAsync(deadline.Token);
            var first = await Identity(session, deadline.Token);
            var a = await Projection(session, scope, phase, first, deadline.Token);
            var b = await Projection(session, scope, phase, first, deadline.Token);
            var last = await Identity(session, deadline.Token);
            if (first != last || RecoverySecurityCanonicalizer.Canonicalize(scope, phase, a)?.Sha256
                != RecoverySecurityCanonicalizer.Canonicalize(scope, phase, b)?.Sha256)
                throw new SecurityCatalogException("SECURITY_CAPTURE_CHANGED", "STABILITY");
            return a;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new SecurityCatalogException("CANCELLED", "CONNECT"); }
        catch (OperationCanceledException)
        { throw new SecurityCatalogException("TIMEOUT", "CONNECT"); }
    }

    private sealed record IdentityValue(string Server, string Database, int Major, int Edition, string Collation);

    private async Task<IdentityValue> Identity(ISecurityCatalogSession session, CancellationToken token)
    {
        var row = One(await session.QueryAsync(IdentitySql, token), "IDENTITY");
        var identity = new IdentityValue(Str(row, "serverName", "IDENTITY"),
            Str(row, "databaseName", "IDENTITY"), Num(row, "majorVersion", "IDENTITY"),
            Num(row, "engineEdition", "IDENTITY"), Str(row, "collation", "IDENTITY"));
        if (identity.Major is not (16 or 17) || identity.Edition is not (2 or 3 or 4))
            throw new SecurityCatalogException("SECURITY_ENGINE_UNSUPPORTED", "IDENTITY");
        if (!identity.Database.Equals(binding.DatabaseName, StringComparison.Ordinal)
            || binding.ServerMatchPolicy == "ALLOW_LIST"
                && !binding.AllowedServerInstances.Contains(identity.Server, StringComparer.OrdinalIgnoreCase))
            throw new SecurityCatalogException("TARGET_IDENTITY_MISMATCH", "IDENTITY");
        return identity;
    }

    private async Task<RecoverySecuritySnapshot> Projection(ISecurityCatalogSession session,
        RecoverySecurityScope scope, RecoveryPhase phase, IdentityValue identity, CancellationToken token)
    {
        var dbProof = One(await session.QueryAsync(DatabaseProofSql, token), "VISIBILITY");
        Proof(dbProof, "viewDefinition");
        Proof(dbProof, "viewSecurityDefinition");
        var schemaHasSecurityDefinition = Capability(await session.QueryAsync(
            "SELECT COUNT(*) AS capabilityCount FROM sys.fn_builtin_permissions('SCHEMA') WHERE permission_name = 'VIEW SECURITY DEFINITION'", token));
        var objectHasSecurityDefinition = Capability(await session.QueryAsync(
            "SELECT COUNT(*) AS capabilityCount FROM sys.fn_builtin_permissions('OBJECT') WHERE permission_name = 'VIEW SECURITY DEFINITION'", token));
        var states = new List<RecoverySecurityObjectState>();
        var neededIds = new HashSet<int>();
        var neededNames = new HashSet<string>(scope.RequiredPrincipals, StringComparer.Ordinal);
        var permissionCount = 0;
        foreach (var securable in scope.Securables)
        {
            var schemaName = securable.Schema;
            var objectName = securable.Name;
            int majorId;
            int classId;
            string? schemaOwner = null;
            string? explicitOwner = null;
            Dictionary<int, string>? columns = null;
            if (securable.Kind == "DATABASE")
            {
                classId = 0; majorId = 0;
            }
            else
            {
                var schemaRows = await session.QueryAsync(
                    "SELECT schema_id, name, principal_id FROM sys.schemas WHERE name = " +
                    SecuritySqlLiteral.Sysname(schemaName), token);
                if (schemaRows.Count == 0)
                    throw new SecurityCatalogException("SECURABLE_ABSENCE_UNPROVEN", "SCOPE");
                var schema = One(schemaRows, "SCOPE");
                Exact(Str(schema, "name", "SCOPE"), schemaName);
                majorId = Num(schema, "schema_id", "SCOPE");
                var schemaOwnerId = Num(schema, "principal_id", "PRINCIPALS");
                neededIds.Add(schemaOwnerId);
                var schemaPermission = await session.QueryAsync(
                    "SELECT HAS_PERMS_BY_NAME(" + SecuritySqlLiteral.Sysname(schemaName) +
                    ", 'SCHEMA', 'VIEW DEFINITION') AS permitted", token);
                Proof(One(schemaPermission, "VISIBILITY"), "permitted");
                if (schemaHasSecurityDefinition)
                    Proof(One(await session.QueryAsync(
                        "SELECT HAS_PERMS_BY_NAME(" + SecuritySqlLiteral.Sysname(schemaName) +
                        ", 'SCHEMA', 'VIEW SECURITY DEFINITION') AS permitted", token), "VISIBILITY"), "permitted");
                if (securable.Kind == "SCHEMA")
                {
                    classId = 3;
                    schemaOwner = await PrincipalName(session, schemaOwnerId, token);
                }
                else if (securable.Kind == "OBJECT")
                {
                    var objectRows = await session.QueryAsync(
                        "SELECT o.object_id, s.name AS schemaName, o.name, o.type, o.principal_id, o.is_ms_shipped FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name = " +
                        SecuritySqlLiteral.Sysname(schemaName) + " AND o.name = " +
                        SecuritySqlLiteral.Sysname(objectName), token);
                    if (objectRows.Count == 0)
                        throw new SecurityCatalogException("SECURABLE_ABSENCE_UNPROVEN", "SCOPE");
                    var objectRow = One(objectRows, "SCOPE");
                    Exact(Str(objectRow, "schemaName", "SCOPE"), schemaName);
                    Exact(Str(objectRow, "name", "SCOPE"), objectName);
                    if (Num(objectRow, "is_ms_shipped", "SCOPE") != 0)
                        throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "SCOPE");
                    majorId = Num(objectRow, "object_id", "SCOPE");
                    classId = 1;
                    schemaOwner = await PrincipalName(session, schemaOwnerId, token);
                    if (objectRow["principal_id"] is not null)
                    {
                        var ownerId = Num(objectRow, "principal_id", "PRINCIPALS");
                        neededIds.Add(ownerId);
                        explicitOwner = await PrincipalName(session, ownerId, token);
                    }
                    var qualified = schemaName + "." + objectName;
                    Proof(One(await session.QueryAsync(
                        "SELECT HAS_PERMS_BY_NAME(" + SecuritySqlLiteral.Sysname(qualified) +
                        ", 'OBJECT', 'VIEW DEFINITION') AS permitted", token), "VISIBILITY"), "permitted");
                    if (objectHasSecurityDefinition)
                        Proof(One(await session.QueryAsync(
                            "SELECT HAS_PERMS_BY_NAME(" + SecuritySqlLiteral.Sysname(qualified) +
                            ", 'OBJECT', 'VIEW SECURITY DEFINITION') AS permitted", token), "VISIBILITY"), "permitted");
                    var columnRows = await session.QueryAsync(
                        "SELECT column_id, name FROM sys.columns WHERE object_id = " +
                        SecuritySqlLiteral.Integer(majorId), token);
                    columns = columnRows.ToDictionary(x => Num(x, "column_id", "SCOPE"),
                        x => Str(x, "name", "SCOPE"));
                }
                else throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "SCOPE");
            }

            var permissions = new List<RecoverySecurityPermission>();
            var rows = await session.QueryAsync(
                "SELECT class, major_id, minor_id, permission_name, state, grantee_principal_id, grantor_principal_id FROM sys.database_permissions WHERE class = " +
                SecuritySqlLiteral.Integer(classId) + " AND major_id = " +
                SecuritySqlLiteral.Integer(majorId), token);
            permissionCount += rows.Count;
            if (permissionCount > 100000) throw new SecurityCatalogException("RESOURCE_LIMIT", "PERMISSIONS");
            foreach (var row in rows)
            {
                if (Num(row, "class", "PERMISSIONS") != classId || Num(row, "major_id", "PERMISSIONS") != majorId)
                    throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "PERMISSIONS");
                var minor = Num(row, "minor_id", "PERMISSIONS");
                var granteeId = Num(row, "grantee_principal_id", "PRINCIPALS");
                var grantorId = Num(row, "grantor_principal_id", "PRINCIPALS");
                neededIds.Add(granteeId); neededIds.Add(grantorId);
                string? column = null;
                if (minor != 0)
                {
                    if (columns is null || !columns.TryGetValue(minor, out column))
                        throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "PERMISSIONS");
                }
                permissions.Add(new(Str(row, "permission_name", "PERMISSIONS"),
                    Str(row, "state", "PERMISSIONS"),
                    await PrincipalName(session, granteeId, token),
                    await PrincipalName(session, grantorId, token), column));
            }
            states.Add(new(securable, true, explicitOwner, schemaOwner, permissions));
        }
        var principals = await PrincipalClosure(session, neededIds, neededNames, token);
        var snapshot = new RecoverySecuritySnapshot(1, phase, scope.Sha256,
            identity.Server, identity.Database, RecoveryEvidenceCoverage.Complete, true,
            states, principals);
        try
        {
            _ = RecoverySecurityCanonicalizer.Canonicalize(scope, phase, snapshot)
                ?? throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "STABILITY");
        }
        catch (InvalidOperationException)
        { throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "STABILITY"); }
        return snapshot;
    }

    private static bool Capability(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var count = Num(One(rows, "VISIBILITY"), "capabilityCount", "VISIBILITY");
        if (count is not (0 or 1))
            throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "VISIBILITY");
        return count == 1;
    }

    private static async Task<string> PrincipalName(ISecurityCatalogSession session, int id, CancellationToken token)
    {
        var row = One(await session.QueryAsync(
            "SELECT principal_id, name FROM sys.database_principals WHERE principal_id = " +
            SecuritySqlLiteral.Integer(id), token), "PRINCIPALS");
        if (Num(row, "principal_id", "PRINCIPALS") != id)
            throw new SecurityCatalogException("PRINCIPAL_NOT_VISIBLE", "PRINCIPALS");
        return Str(row, "name", "PRINCIPALS");
    }

    private static async Task<IReadOnlyList<RecoverySecurityPrincipal>> PrincipalClosure(
        ISecurityCatalogSession session, HashSet<int> ids, HashSet<string> names, CancellationToken token)
    {
        var principals = new Dictionary<int, RecoverySecurityPrincipal>();
        var pending = new Queue<int>(ids);
        foreach (var name in names)
        {
            var rows = await session.QueryAsync("SELECT principal_id FROM sys.database_principals WHERE name = " +
                SecuritySqlLiteral.Sysname(name), token);
            if (rows.Count == 0) throw new SecurityCatalogException("PRINCIPAL_NOT_VISIBLE", "PRINCIPALS");
            pending.Enqueue(Num(One(rows, "PRINCIPALS"), "principal_id", "PRINCIPALS"));
        }
        while (pending.TryDequeue(out var id))
        {
            if (principals.ContainsKey(id)) continue;
            if (principals.Count >= 4096) throw new SecurityCatalogException("RESOURCE_LIMIT", "PRINCIPALS");
            var row = One(await session.QueryAsync(
                "SELECT principal_id, name, type, sid, authentication_type_desc, default_schema_name, owning_principal_id, is_fixed_role FROM sys.database_principals WHERE principal_id = " +
                SecuritySqlLiteral.Integer(id), token), "PRINCIPALS");
            if (row["sid"] is not byte[] sid || sid.Length == 0)
                throw new SecurityCatalogException("PRINCIPAL_NOT_VISIBLE", "PRINCIPALS");
            var ownerId = row["owning_principal_id"] is null ? (int?)null
                : Num(row, "owning_principal_id", "PRINCIPALS");
            var ownerName = ownerId is null ? null : await PrincipalName(session, ownerId.Value, token);
            var principal = new RecoverySecurityPrincipal(
                Str(row, "name", "PRINCIPALS"), Str(row, "type", "PRINCIPALS"),
                Convert.ToHexStringLower(sid), Str(row, "authentication_type_desc", "PRINCIPALS"),
                row["default_schema_name"] as string, ownerName,
                Num(row, "is_fixed_role", "PRINCIPALS") != 0);
            principals.Add(id, principal);
            if (ownerId is not null) pending.Enqueue(ownerId.Value);
        }
        foreach (var name in names)
            if (!principals.Values.Any(x => x.Name == name))
                throw new SecurityCatalogException("SECURITY_NAME_ALIAS", "PRINCIPALS");
        return principals.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyDictionary<string, object?> One(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string stage)
    {
        if (rows.Count != 1) throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", stage);
        return rows[0];
    }

    private static string Str(IReadOnlyDictionary<string, object?> row, string field, string stage)
    {
        if (!row.TryGetValue(field, out var value) || value is not string text || text.Length == 0)
            throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", stage);
        return text;
    }

    private static int Num(IReadOnlyDictionary<string, object?> row, string field, string stage)
    {
        if (!row.TryGetValue(field, out var value) || value is null)
            throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", stage);
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", stage); }
    }

    private static void Proof(IReadOnlyDictionary<string, object?> row, string field)
    {
        if (!row.TryGetValue(field, out var value) || value is null
            || !int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var number)
            || number != 1)
            throw new SecurityCatalogException("METADATA_PERMISSION_INSUFFICIENT", "VISIBILITY");
    }

    private static void Exact(string actual, string expected)
    {
        if (actual != expected) throw new SecurityCatalogException("SECURITY_NAME_ALIAS", "SCOPE");
    }
}
