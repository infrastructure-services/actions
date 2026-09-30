using DatabaseReleaseQualification;
using Microsoft.Data.SqlClient;

public static class LegacySecurityReaderTests
{
    public static (string Name, Func<Task> Run)[] Cases = [
        ("security reader conserva grant y grantor explícitos", Grant),
        ("security reader conserva DENY y excepción REVOKE", DenyAndRevoke),
        ("security reader acepta objeto visible sin permisos", EmptyPermissions),
        ("security reader no infiere ausencia de rowset vacío", EmptyObject),
        ("security reader bloquea proof metadata insuficiente", Visibility),
        ("security reader bloquea target distinto", TargetMismatch),
        ("security reader bloquea proyecciones cambiantes", ChangingCapture),
        ("security reader bloquea SID ausente y timeout", SidAndTimeout),
        ("security SQL guard admite SELECT y bloquea mutación", SelectOnly),
        ("security transport exige TLS strict y cero retry", TransportOptions),
        ("security SQL literal encierra inyección como valor", LiteralInjection)
    ];

    private static readonly RecoverySecurityScope Scope =
        new([new("OBJECT", "dbo", "Widget")], []);
    private static readonly SecurityTargetBindingV1 Binding = new("TestDb", "ALLOW_LIST", ["SQL1"]);

    private static async Task Grant()
    {
        var snapshot = await new SqlRecoverySecurityCatalogReader(new FakeTransport(), Binding)
            .ReadAsync(Scope, RecoveryPhase.Pre);
        if (!snapshot.MetadataVisibilityComplete || snapshot.Coverage != RecoveryEvidenceCoverage.Complete)
            throw new Exception("Not complete");
        var permission = snapshot.Securables.Single().Permissions.Single();
        if (permission.Permission != "SELECT" || permission.State != "W"
            || permission.Grantee != "reader" || permission.Grantor != "dbo"
            || permission.Column != "C1")
            throw new Exception("Permission dimensions lost");
    }

    private static async Task EmptyPermissions()
    {
        var snapshot = await new SqlRecoverySecurityCatalogReader(new FakeTransport(noPermissions: true), Binding)
            .ReadAsync(Scope, RecoveryPhase.Pre);
        if (!snapshot.Securables.Single().Exists || snapshot.Securables.Single().Permissions.Count != 0)
            throw new Exception("Visible object with empty permissions not represented");
    }

    private static async Task DenyAndRevoke()
    {
        foreach (var state in new[] { "D", "R" })
        {
            var snapshot = await new SqlRecoverySecurityCatalogReader(
                new FakeTransport(permissionState: state), Binding)
                .ReadAsync(Scope, RecoveryPhase.Pre);
            if (snapshot.Securables.Single().Permissions.Single().State != state)
                throw new Exception("Permission state lost");
        }
    }

    private static async Task EmptyObject()
    {
        await Blocks("SECURABLE_ABSENCE_UNPROVEN", new FakeTransport(noObject: true));
    }

    private static async Task Visibility()
    {
        await Blocks("METADATA_PERMISSION_INSUFFICIENT", new FakeTransport(noVisibility: true));
    }

    private static async Task TargetMismatch()
    {
        await Blocks("TARGET_IDENTITY_MISMATCH", new FakeTransport(server: "SQL2"));
    }

    private static async Task ChangingCapture()
    {
        await Blocks("SECURITY_CAPTURE_CHANGED", new FakeTransport(changeOnSecondProjection: true));
    }

    private static async Task SidAndTimeout()
    {
        await Blocks("PRINCIPAL_NOT_VISIBLE", new FakeTransport(nullSid: true));
        await Blocks("TIMEOUT", new TimeoutTransport());
    }

    private static Task SelectOnly()
    {
        SecuritySqlGuard.Validate("SELECT name FROM sys.schemas");
        SecuritySqlGuard.Validate("SELECT COUNT(*) AS capabilityCount FROM sys.fn_builtin_permissions('SCHEMA') WHERE permission_name='VIEW SECURITY DEFINITION'");
        try { SecuritySqlGuard.Validate("SELECT 1; DELETE FROM dbo.T"); }
        catch (SecurityCatalogException) { return Task.CompletedTask; }
        throw new Exception("Mutation passed SQL guard");
    }

    private static Task LiteralInjection()
    {
        var literal = SecuritySqlLiteral.Sysname("O'Hare'; DROP TABLE dbo.T;--");
        SecuritySqlGuard.Validate("SELECT name FROM sys.schemas WHERE name = " + literal);
        return Task.CompletedTask;
    }

    private static Task TransportOptions()
    {
        var builder = SqlClientSecurityCatalogTransport.BuildConnectionOptions(
            "Server=SQL1;Database=WrongDb;Integrated Security=true;Encrypt=false;TrustServerCertificate=true;Connect Retry Count=3;Pooling=true;MultipleActiveResultSets=true",
            Binding);
        if (builder.InitialCatalog != "TestDb"
            || builder.Encrypt != SqlConnectionEncryptOption.Strict
            || builder.TrustServerCertificate || builder.ConnectTimeout != 15
            || builder.ConnectRetryCount != 0 || builder.Pooling || builder.Enlist
            || builder.MultipleActiveResultSets || builder.ApplicationIntent != ApplicationIntent.ReadWrite)
            throw new Exception("Transport weakened connection profile");
        return Task.CompletedTask;
    }

    private static async Task Blocks(string code, ISecurityCatalogTransport transport)
    {
        try { await new SqlRecoverySecurityCatalogReader(transport, Binding).ReadAsync(Scope, RecoveryPhase.Pre); }
        catch (SecurityCatalogException exception) when (exception.Code == code) { return; }
        throw new Exception("Expected " + code);
    }

    private sealed class TimeoutTransport : ISecurityCatalogTransport
    {
        public Task<ISecurityCatalogSession> OpenAsync(CancellationToken token) =>
            throw new OperationCanceledException();
    }

    private sealed class FakeTransport(bool noPermissions = false, bool noObject = false,
        bool noVisibility = false, bool changeOnSecondProjection = false,
        bool nullSid = false, string permissionState = "W", string server = "SQL1")
        : ISecurityCatalogTransport
    {
        public Task<ISecurityCatalogSession> OpenAsync(CancellationToken token) =>
            Task.FromResult<ISecurityCatalogSession>(new Session(noPermissions, noObject, noVisibility,
                changeOnSecondProjection, nullSid, permissionState, server));

        private sealed class Session(bool noPermissions, bool noObject, bool noVisibility,
            bool changeOnSecondProjection, bool nullSid, string permissionState,
            string server) : ISecurityCatalogSession
        {
            private int permissionReads;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, CancellationToken token)
            {
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
                if (sql.Contains("SERVERPROPERTY('ServerName')", StringComparison.Ordinal))
                    rows = [Row(("serverName", server), ("databaseName", "TestDb"), ("majorVersion", 16),
                        ("engineEdition", 3), ("collation", "Latin1_General_100_CI_AS"))];
                else if (sql.Contains("VIEW SECURITY DEFINITION') AS viewSecurityDefinition", StringComparison.Ordinal))
                    rows = [Row(("viewDefinition", noVisibility ? 0 : 1),
                        ("viewSecurityDefinition", noVisibility ? 0 : 1))];
                else if (sql.Contains("sys.fn_builtin_permissions", StringComparison.Ordinal))
                    rows = [Row(("capabilityCount", 0))];
                else if (sql.Contains("FROM sys.schemas", StringComparison.Ordinal))
                    rows = [Row(("schema_id", 1), ("name", "dbo"), ("principal_id", 1))];
                else if (sql.Contains("FROM sys.objects", StringComparison.Ordinal))
                    rows = noObject ? [] : [Row(("object_id", 10), ("schemaName", "dbo"),
                        ("name", "Widget"), ("type", "U"), ("principal_id", null),
                        ("is_ms_shipped", 0))];
                else if (sql.Contains("HAS_PERMS_BY_NAME", StringComparison.Ordinal))
                    rows = [Row(("permitted", 1))];
                else if (sql.Contains("FROM sys.columns", StringComparison.Ordinal))
                    rows = [Row(("column_id", 1), ("name", "C1"))];
                else if (sql.Contains("FROM sys.database_permissions", StringComparison.Ordinal))
                {
                    permissionReads++;
                    rows = noPermissions || changeOnSecondProjection && permissionReads > 1 ? [] :
                        [Row(("class", 1), ("major_id", 10), ("minor_id", 1), ("permission_name", "SELECT"),
                            ("state", permissionState), ("grantee_principal_id", 2), ("grantor_principal_id", 1))];
                }
                else if (sql.Contains("FROM sys.database_principals", StringComparison.Ordinal))
                {
                    var reader = sql.Contains("= 2", StringComparison.Ordinal);
                    if (sql.Contains("SELECT principal_id, name FROM", StringComparison.Ordinal))
                        rows = [Row(("principal_id", reader ? 2 : 1), ("name", reader ? "reader" : "dbo"))];
                    else
                        rows = [Row(("principal_id", reader ? 2 : 1), ("name", reader ? "reader" : "dbo"),
                            ("type", "S"), ("sid", nullSid ? null
                                : reader ? new byte[] { 2 } : new byte[] { 1 }),
                            ("authentication_type_desc", "DATABASE"), ("default_schema_name", null),
                            ("owning_principal_id", null), ("is_fixed_role", 0))];
                }
                else throw new Exception("Unexpected query");
                return Task.FromResult(rows);
            }

            private static IReadOnlyDictionary<string, object?> Row(
                params (string Name, object? Value)[] entries) =>
                entries.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
        }
    }
}
