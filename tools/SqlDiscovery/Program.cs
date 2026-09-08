using System.Text.Json;
using Microsoft.Data.SqlClient;

var rawConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION");
var connectionOpened = false;

if (string.IsNullOrWhiteSpace(rawConnectionString))
{
    Console.Error.WriteLine("DB_CONNECTION_REQUIRED");
    return 2;
}

try
{
    var builder = new SqlConnectionStringBuilder(rawConnectionString)
    {
        ApplicationIntent = ApplicationIntent.ReadWrite,
        ApplicationName = "cicd-discover-db-scenario"
    };

    await using var connection = new SqlConnection(builder.ConnectionString);
    await connection.OpenAsync();
    connectionOpened = true;

    var databaseName = await ScalarStringAsync(connection, "SELECT DB_NAME();");
    var metadataVisible = await ScalarLongAsync(connection,
        "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION') = 1 THEN 1 ELSE 0 END;") == 1;

    if (!metadataVisible)
    {
        Console.Error.WriteLine("FAIL_METADATA_VISIBILITY");
        return 5;
    }

    var historyExists = await ScalarLongAsync(connection,
        "SELECT CASE WHEN OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL THEN 0 ELSE 1 END;") == 1;

    if (historyExists)
    {
        var historySelectable = await ScalarLongAsync(connection,
            "SELECT CASE WHEN HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'SELECT') = 1 THEN 1 ELSE 0 END;") == 1;

        if (!historySelectable)
        {
            Console.Error.WriteLine("FAIL_METADATA_VISIBILITY");
            return 5;
        }
    }

    const string structuralCountsSql = """
        SELECT
            (
                SELECT COUNT_BIG(*)
                FROM sys.objects AS o
                INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                WHERE o.is_ms_shipped = 0
                  AND s.name NOT IN (N'sys', N'INFORMATION_SCHEMA', N'cicd')
                  AND (
                        OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
                        OR (
                            o.object_id <> OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U')
                            AND o.parent_object_id <> OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U')
                        )
                  )
            ) AS schemaScopedObjects,
            (
                SELECT COUNT_BIG(*)
                FROM sys.schemas AS s
                LEFT JOIN sys.database_principals AS p ON p.principal_id = s.principal_id
                WHERE s.schema_id > 4
                  AND s.name <> N'cicd'
                  AND NOT (p.type = N'R' AND p.is_fixed_role = 1)
            ) AS userSchemas,
            (
                SELECT COUNT_BIG(*)
                FROM sys.types AS t
                INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                WHERE t.is_user_defined = 1
                  AND s.name NOT IN (N'sys', N'INFORMATION_SCHEMA', N'cicd')
            ) AS userDefinedTypes,
            (
                SELECT COUNT_BIG(*)
                FROM sys.assemblies AS a
                WHERE a.is_user_defined = 1
            ) AS userAssemblies,
            (
                SELECT COUNT_BIG(*)
                FROM sys.triggers AS tr
                WHERE tr.parent_class = 0
                  AND tr.is_ms_shipped = 0
            ) AS databaseTriggers,
            (
                SELECT COUNT_BIG(*)
                FROM sys.xml_schema_collections AS x
                INNER JOIN sys.schemas AS s ON s.schema_id = x.schema_id
                WHERE x.xml_collection_id > 0
                  AND s.name NOT IN (N'sys', N'INFORMATION_SCHEMA', N'cicd')
            ) AS xmlSchemaCollections,
            (SELECT COUNT_BIG(*) FROM sys.partition_functions) AS partitionFunctions,
            (SELECT COUNT_BIG(*) FROM sys.partition_schemes) AS partitionSchemes,
            (SELECT COUNT_BIG(*) FROM sys.fulltext_catalogs) AS fullTextCatalogs;
        """;

    await using var countsCommand = connection.CreateCommand();
    countsCommand.CommandText = structuralCountsSql;
    countsCommand.CommandTimeout = 30;

    await using var countsReader = await countsCommand.ExecuteReaderAsync();
    if (!await countsReader.ReadAsync())
    {
        Console.Error.WriteLine("SQL_DISCOVERY_FAILED:EMPTY_METADATA_RESULT");
        return 4;
    }

    var schemaScopedObjects = countsReader.GetInt64(0);
    var userSchemas = countsReader.GetInt64(1);
    var userDefinedTypes = countsReader.GetInt64(2);
    var userAssemblies = countsReader.GetInt64(3);
    var databaseTriggers = countsReader.GetInt64(4);
    var xmlSchemaCollections = countsReader.GetInt64(5);
    var partitionFunctions = countsReader.GetInt64(6);
    var partitionSchemes = countsReader.GetInt64(7);
    var fullTextCatalogs = countsReader.GetInt64(8);

    await countsReader.CloseAsync();

    var businessObjectCount = schemaScopedObjects
        + userSchemas
        + userDefinedTypes
        + userAssemblies
        + databaseTriggers
        + xmlSchemaCollections
        + partitionFunctions
        + partitionSchemes
        + fullTextCatalogs;

    const string businessTableCountSql = """
        SELECT COUNT_BIG(*)
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
          AND s.name NOT IN (N'sys', N'INFORMATION_SCHEMA', N'cicd')
          AND NOT (s.name = N'dbo' AND t.name = N'__EFMigrationsHistory');
        """;

    var businessTableCount = await ScalarLongAsync(connection, businessTableCountSql);
    var efHistory = new List<string>();

    if (historyExists)
    {
        await using var historyCommand = connection.CreateCommand();
        historyCommand.CommandText = "SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId ASC;";
        historyCommand.CommandTimeout = 30;

        await using var reader = await historyCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            efHistory.Add(reader.GetString(0));
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        databaseName,
        metadataVisibilityVerified = true,
        efHistoryExists = historyExists,
        efHistory,
        businessObjectCount,
        businessTableCount,
        emptyForNew = businessObjectCount == 0,
        structuralCounts = new
        {
            schemaScopedObjects,
            userSchemas,
            userDefinedTypes,
            userAssemblies,
            databaseTriggers,
            xmlSchemaCollections,
            partitionFunctions,
            partitionSchemes,
            fullTextCatalogs
        },
        technicalSchemaExclusions = new[] { "sys", "INFORMATION_SCHEMA", "cicd" },
        technicalObjectExclusions = new[] { "dbo.__EFMigrationsHistory" },
        accessMode = "SELECT_ONLY_PRIMARY"
    }));

    return 0;
}
catch (SqlException exception) when (exception.Number == 229)
{
    Console.Error.WriteLine("FAIL_METADATA_VISIBILITY");
    return 5;
}
catch (SqlException exception)
{
    Console.Error.WriteLine($"SQL_DISCOVERY_FAILED:{exception.Number}");
    return connectionOpened ? 4 : 3;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"SQL_DISCOVERY_FAILED:{exception.GetType().Name}");
    return 4;
}

static async Task<string> ScalarStringAsync(SqlConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 30;
    return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
}

static async Task<long> ScalarLongAsync(SqlConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 30;
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}
