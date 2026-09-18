using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlDiscovery.V2;

public sealed class SqlClientDiscoveryTransportV2(int connectionTimeoutSeconds = 15, int commandTimeoutSeconds = 30) : ISqlDiscoveryTransport
{
    private const string ServerCatalog = "master";
    private const string HistorySchema = "dbo";
    private const string HistoryTable = "__EFMigrationsHistory";

    public int ConnectionTimeoutSeconds { get; } = RequirePositive(connectionTimeoutSeconds, nameof(connectionTimeoutSeconds));
    public int CommandTimeoutSeconds { get; } = RequirePositive(commandTimeoutSeconds, nameof(commandTimeoutSeconds));
    public int RetryCount => 0;

    public async Task ConnectServerAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(target, ServerCatalog);
        await OpenAsync(connection, cancellationToken);
    }

    public async Task<DatabaseLookupResult> LookupDatabaseAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(target, ServerCatalog);
        await OpenAsync(connection, cancellationToken);
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

    public async Task ConnectTargetAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(target, target.DatabaseName);
        await OpenAsync(connection, cancellationToken);
    }

    public async Task<MetadataResult> InspectMetadataAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(target, target.DatabaseName);
        await OpenAsync(connection, cancellationToken);
        await using var command = CreateCommand(connection, "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION') = 1 THEN 1 ELSE 0 END;");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value) == 1 ? new(MetadataStatus.Sufficient) : new(MetadataStatus.Insufficient, new("METADATA", "VISIBILITY_INSUFFICIENT"));
    }

    public async Task<PhysicalResult> ObservePhysicalAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(target, target.DatabaseName);
        await OpenAsync(connection, cancellationToken);
        await using var command = CreateCommand(connection, """
            SELECT COUNT_BIG(*)
            FROM sys.objects AS o
            WHERE o.is_ms_shipped = 0
              AND NOT (SCHEMA_NAME(o.schema_id) = N'dbo' AND OBJECT_NAME(o.object_id) = N'__EFMigrationsHistory')
              AND NOT (o.parent_object_id = OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U'));
            """);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return new(PhysicalStatus.Complete, count);
    }

    public async Task<HistoryResult> ObserveHistoryAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(target, target.DatabaseName);
        await OpenAsync(connection, cancellationToken);
        await using var structure = CreateCommand(connection, """
            SELECT
                CASE WHEN OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL THEN 0 ELSE 1 END,
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
                ) THEN 1 ELSE 0 END;
            """);
        await using var reader = await structure.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(HistoryStatus.TechnicalError, diagnostic: new("HISTORY", "EMPTY_STRUCTURE_RESULT"));
        if (reader.GetInt32(0) == 0) return new(HistoryStatus.Absent);
        if (reader.GetInt32(1) == 0) return new(HistoryStatus.Unreadable, diagnostic: new("HISTORY", "SELECT_INSUFFICIENT"));
        if (reader.GetInt32(2) == 0) return new(HistoryStatus.InvalidStructure, diagnostic: new("HISTORY", "STRUCTURE_INVALID"));
        await reader.CloseAsync();

        await using var rows = CreateCommand(connection, "SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId ASC;");
        await using var historyReader = await rows.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await historyReader.ReadAsync(cancellationToken)) ids.Add(historyReader.GetString(0));
        return ids.Count == 0 ? new(HistoryStatus.Empty, ids) : new(HistoryStatus.Present, ids);
    }

    private SqlConnection CreateConnection(SqlDiscoveryTarget target, string catalog)
    {
        var builder = new SqlConnectionStringBuilder(target.ServerConnectionString)
        {
            InitialCatalog = catalog,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "cicd-sql-discovery-v2",
            ConnectTimeout = ConnectionTimeoutSeconds,
            ConnectRetryCount = 0,
            Encrypt = SqlConnectionEncryptOption.Strict,
            TrustServerCertificate = false
        };
        return new SqlConnection(builder.ConnectionString);
    }

    private async Task OpenAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try { await connection.OpenAsync(cancellationToken); }
        catch (SqlException exception) when (exception.Number == 18456) { throw new SqlDiscoveryAuthenticationException(); }
    }

    private SqlCommand CreateCommand(SqlConnection connection, string sql) => new(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
    private static int RequirePositive(int value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name);
}
