using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlDiscovery.V2;

public sealed class SqlClientDiscoveryTransportV2(
    int connectionTimeoutSeconds = 15,
    int commandTimeoutSeconds = 30,
    string environmentName = "TEST",
    bool allowTestUntrustedCertificateFallback = false) : ISqlDiscoveryTransport
{
    private const string ServerCatalog = "master";
    private const string HistorySchema = "dbo";
    private const string HistoryTable = "__EFMigrationsHistory";

    public int ConnectionTimeoutSeconds { get; } = RequirePositive(connectionTimeoutSeconds, nameof(connectionTimeoutSeconds));
    public int CommandTimeoutSeconds { get; } = RequirePositive(commandTimeoutSeconds, nameof(commandTimeoutSeconds));
    public int RetryCount => 0;
    private readonly TestTlsFallbackPolicy tlsPolicy = new(environmentName, allowTestUntrustedCertificateFallback);
    public TlsDiscoveryEvidence TlsEvidence => tlsPolicy.Evidence;

    public async Task ConnectServerAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(target, ServerCatalog, cancellationToken);
    }

    public async Task<DatabaseLookupResult> LookupDatabaseAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(target, ServerCatalog, cancellationToken);
        await using var command = CreateCommand(connection, """
            SELECT
                CASE WHEN EXISTS (SELECT 1 FROM sys.databases WHERE name = @databaseName) THEN 1 ELSE 0 END,
                CASE WHEN HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE') = 1 THEN 1 ELSE 0 END;
            """);
        command.Parameters.Add(new SqlParameter("@databaseName", SqlDbType.NVarChar, 128) { Value = target.DatabaseName });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new(DatabaseLookupStatus.TechnicalError, new("DATABASE_LOOKUP", "EMPTY_RESULT"));
        }
        if (reader.GetInt32(0) == 1) return new(DatabaseLookupStatus.Found);
        return reader.GetInt32(1) == 1
            ? new(DatabaseLookupStatus.NotFoundConfirmed)
            : new(DatabaseLookupStatus.VisibilityInsufficient, new("DATABASE_LOOKUP", "VISIBILITY_INSUFFICIENT"));
    }

    public async Task<ObservedIdentityResult> ConnectTargetAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(target, target.DatabaseName, cancellationToken);
        try
        {
            await using var command = CreateCommand(connection, """
                SELECT
                    CONVERT(nvarchar(128), SERVERPROPERTY(N'ServerName')),
                    DB_NAME();
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || reader.IsDBNull(1))
                return IdentityUnavailable();
            var identity = new ObservedDatabaseIdentity(reader.GetString(0), reader.GetString(1));
            return ObservedIdentityValidator.IsValid(identity)
                ? new(ObservedIdentityStatus.Available, identity)
                : IdentityUnavailable();
        }
        catch
        {
            return IdentityUnavailable();
        }
    }

    public async Task<MetadataResult> InspectMetadataAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(target, target.DatabaseName, cancellationToken);
        await using var command = CreateCommand(connection, "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION') = 1 THEN 1 ELSE 0 END;");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value) == 1 ? new(MetadataStatus.Sufficient) : new(MetadataStatus.Insufficient, new("METADATA", "VISIBILITY_INSUFFICIENT"));
    }

    public async Task<PhysicalResult> ObservePhysicalAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(target, target.DatabaseName, cancellationToken);
        await using var command = CreateCommand(connection, EmptyForNewEfV1.Sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await EmptyForNewEfV1.ReadAsync(reader, cancellationToken);
    }

    public async Task<HistoryResult> ObserveHistoryAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(target, target.DatabaseName, cancellationToken);
        await using var structure = CreateCommand(connection, """
            SELECT
                CASE WHEN OBJECT_ID(N'dbo.__EFMigrationsHistory') IS NULL THEN 0 ELSE 1 END,
                CASE WHEN HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'SELECT') = 1 THEN 1 ELSE 0 END,
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM sys.columns AS c
                    WHERE c.object_id = OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U')
                      AND c.name = N'MigrationId' AND TYPE_NAME(c.user_type_id) = N'nvarchar'
                      AND c.max_length = 300 AND c.is_nullable = 0
                ) AND EXISTS (
                    SELECT 1
                    FROM sys.columns AS c
                    WHERE c.object_id = OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U')
                      AND c.name = N'ProductVersion' AND TYPE_NAME(c.user_type_id) = N'nvarchar'
                      AND c.max_length = 64 AND c.is_nullable = 0
                ) AND EXISTS (
                    SELECT 1
                    FROM sys.indexes AS i
                    INNER JOIN sys.index_columns AS ic
                        ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                    INNER JOIN sys.columns AS c
                        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE i.object_id = OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U')
                      AND i.is_primary_key = 1 AND c.name = N'MigrationId'
                ) THEN 1 ELSE 0 END,
                CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION') = 1 THEN 1 ELSE 0 END;
            """);
        await using var reader = await structure.ExecuteReaderAsync(cancellationToken);
        var structureResult = await ReadHistoryStructureAsync(reader, cancellationToken);
        if (structureResult is not null) return structureResult;
        await reader.CloseAsync();

        await using var rows = CreateCommand(connection, "SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId ASC;");
        await using var historyReader = await rows.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await historyReader.ReadAsync(cancellationToken)) ids.Add(historyReader.GetString(0));
        return ids.Count == 0 ? new(HistoryStatus.Empty, ids) : new(HistoryStatus.Present, ids);
    }

    // Pure reader boundary: tests exercise structure/permissions without SQL access.
    public static async Task<HistoryResult?> ReadHistoryStructureAsync(DbDataReader reader, CancellationToken token)
    {
        if (!await reader.ReadAsync(token) || reader.FieldCount != 4 || Enumerable.Range(0, 4).Any(reader.IsDBNull))
            return new(HistoryStatus.TechnicalError, diagnostic: new("HISTORY", "STRUCTURE_RESULT_INVALID"));
        var flags = Enumerable.Range(0, 4).Select(reader.GetInt32).ToArray();
        if (flags.Any(value => value is not (0 or 1)) || await reader.ReadAsync(token) || await reader.NextResultAsync(token))
            return new(HistoryStatus.TechnicalError, diagnostic: new("HISTORY", "STRUCTURE_RESULT_INVALID"));
        if (flags[0] == 0 && (flags[1] != 0 || flags[2] != 0))
            return new(HistoryStatus.TechnicalError, diagnostic: new("HISTORY", "STRUCTURE_RESULT_INVALID"));
        if (flags[3] != 1) return new(HistoryStatus.Unreadable, diagnostic: new("HISTORY", "METADATA_INSUFFICIENT"));
        if (flags[0] == 0) return new(HistoryStatus.Absent);
        if (flags[1] == 0) return new(HistoryStatus.Unreadable, diagnostic: new("HISTORY", "SELECT_INSUFFICIENT"));
        if (flags[2] == 0) return new(HistoryStatus.InvalidStructure, diagnostic: new("HISTORY", "STRUCTURE_INVALID"));
        return null;
    }

    private SqlConnection CreateConnection(SqlDiscoveryTarget target, string catalog, SqlTlsMode tlsMode)
    {
        var builder = new SqlConnectionStringBuilder(target.ServerConnectionString)
        {
            InitialCatalog = catalog,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "cicd-sql-discovery-v2",
            ConnectTimeout = ConnectionTimeoutSeconds,
            ConnectRetryCount = 0,
            Encrypt = tlsMode == SqlTlsMode.Strict ? SqlConnectionEncryptOption.Strict : SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = tlsMode == SqlTlsMode.TestUntrustedCertificate
        };
        return new SqlConnection(builder.ConnectionString);
    }

    private Task<SqlConnection> OpenConnectionAsync(SqlDiscoveryTarget target, string catalog, CancellationToken cancellationToken) =>
        tlsPolicy.ExecuteAsync(async (mode, token) =>
        {
            var connection = CreateConnection(target, catalog, mode);
            try
            {
                await OpenAsync(connection, token);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }, cancellationToken);

    private static async Task OpenAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try { await connection.OpenAsync(cancellationToken); }
        catch (SqlException exception) when (exception.Number == 18456) { throw new SqlDiscoveryAuthenticationException(); }
    }

    private SqlCommand CreateCommand(SqlConnection connection, string sql) => new(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
    private static ObservedIdentityResult IdentityUnavailable() =>
        new(ObservedIdentityStatus.Unavailable, Diagnostic: new("OBSERVED_DATABASE_IDENTITY", "OBSERVED_DATABASE_IDENTITY_UNAVAILABLE"));
    private static int RequirePositive(int value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name);
}

internal static partial class ObservedIdentityValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\\-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerInstancePattern();

    public static bool IsValid(ObservedDatabaseIdentity identity) =>
        ServerInstancePattern().IsMatch(identity.ServerInstance)
        && identity.DatabaseName.Length is >= 1 and <= 128
        && !identity.DatabaseName.Any(character => character < ' ' || character == '\u007f');
}
