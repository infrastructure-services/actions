using System.Collections.ObjectModel;
using System.Data.Common;

namespace SqlDiscovery.V2;

public static class EmptyForNewEfV1
{
    public const int Version = 1;
    public const long MaxSafeCount = 9007199254740991;
    // SQL Server's four built-in schemas and nine shipped fixed-role schemas.
    // Exact names only: ownership never exempts another schema.
    public static IReadOnlyList<string> StandardSchemas { get; } = Array.AsReadOnly(new[] {
        "dbo", "guest", "sys", "INFORMATION_SCHEMA", "db_accessadmin", "db_backupoperator",
        "db_datareader", "db_datawriter", "db_ddladmin", "db_denydatareader", "db_denydatawriter",
        "db_owner", "db_securityadmin"
    });
    public static IReadOnlyList<string> Categories { get; } = Array.AsReadOnly(new[] {
        "customSchemas", "userDefinedTypes", "databaseTriggers", "partitionFunctions",
        "partitionSchemes", "userAssemblies", "xmlSchemaCollections", "fullTextCatalogs"
    });

    public static PhysicalResult Complete(long business, params long[] complementary)
    {
        if (!ValidCount(business) || complementary.Length != Categories.Count || complementary.Any(value => !ValidCount(value)))
            return new(PhysicalStatus.Partial, Diagnostic: new("PHYSICAL", "TAXONOMY_COUNTS_INVALID"));
        long sum = 0;
        foreach (var value in complementary)
        {
            if (value > MaxSafeCount - sum) return new(PhysicalStatus.Partial, Diagnostic: new("PHYSICAL", "TAXONOMY_COUNTS_INVALID"));
            sum += value;
        }
        var counts = new ReadOnlyDictionary<string, long>(Categories.Select((key, index) => (key, complementary[index]))
            .ToDictionary(item => item.key, item => item.Item2, StringComparer.Ordinal));
        return new(PhysicalStatus.Complete, business, TechnicalObjectCount: sum,
            Taxonomy: new(Version, "COMPLETE", counts));
    }

    public static bool ValidCount(long value) => value is >= 0 and <= MaxSafeCount;
    public static bool Valid(PhysicalResult result) => result.Taxonomy is { Version: Version, Coverage: "COMPLETE", Counts: not null } taxonomy
        && result.BusinessObjectCount is not null && ValidCount(result.BusinessObjectCount.Value)
        && result.TechnicalObjectCount is not null && ValidCount(result.TechnicalObjectCount.Value)
        && taxonomy.Counts.Count == Categories.Count && Categories.All(taxonomy.Counts.ContainsKey)
        && taxonomy.Counts.Values.All(ValidCount)
        && taxonomy.Counts.Values.Aggregate(0m, (sum, value) => sum + value) == result.TechnicalObjectCount;

    public static async Task<PhysicalResult> ReadAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken) || reader.FieldCount != 2 + Categories.Count
            || Enumerable.Range(0, reader.FieldCount).Any(reader.IsDBNull) || reader.GetInt32(0) != 1)
            return new(PhysicalStatus.Partial, Diagnostic: new("PHYSICAL", "TAXONOMY_COVERAGE_INSUFFICIENT"));
        var business = reader.GetInt64(1);
        var complementary = Enumerable.Range(2, Categories.Count).Select(reader.GetInt64).ToArray();
        if (await reader.ReadAsync(cancellationToken) || await reader.NextResultAsync(cancellationToken))
            return new(PhysicalStatus.Partial, Diagnostic: new("PHYSICAL", "TAXONOMY_RESULT_INVALID"));
        return Complete(business, complementary);
    }

    // Historical sys.objects domain, with NULL-safe history parent exclusion.
    // Complementary categories never repeat an object_id counted by that set.
    public static string Sql { get; } = $$"""
        WITH businessObjects AS (
            SELECT o.object_id
            FROM sys.objects AS o
            WHERE o.is_ms_shipped = 0
              AND NOT (SCHEMA_NAME(o.schema_id) = N'dbo' AND OBJECT_NAME(o.object_id) = N'__EFMigrationsHistory')
              AND (OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
                   OR o.parent_object_id <> OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U'))
        )
        SELECT
            CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION') = 1 THEN 1 ELSE 0 END,
            (SELECT COUNT_BIG(*) FROM businessObjects),
            (SELECT COUNT_BIG(*) FROM sys.schemas AS s
             WHERE s.name COLLATE Latin1_General_100_BIN2 NOT IN ({{string.Join(", ", StandardSchemas.Select(name => $"N'{name}'"))}})),
            (SELECT COUNT_BIG(*) FROM sys.types AS ty
             LEFT JOIN sys.table_types AS tt ON tt.user_type_id = ty.user_type_id
             WHERE ty.is_user_defined = 1
               AND NOT EXISTS (SELECT 1 FROM businessObjects AS b WHERE b.object_id = tt.type_table_object_id)),
            (SELECT COUNT_BIG(*) FROM sys.triggers AS tr
             WHERE tr.parent_class = 0 AND tr.is_ms_shipped = 0
               AND NOT EXISTS (SELECT 1 FROM businessObjects AS b WHERE b.object_id = tr.object_id)),
            (SELECT COUNT_BIG(*) FROM sys.partition_functions),
            (SELECT COUNT_BIG(*) FROM sys.partition_schemes),
            (SELECT COUNT_BIG(*) FROM sys.assemblies AS a WHERE a.is_user_defined = 1),
            (SELECT COUNT_BIG(*) FROM sys.xml_schema_collections AS x WHERE x.xml_collection_id > 0),
            (SELECT COUNT_BIG(*) FROM sys.fulltext_catalogs);
        """;
}
