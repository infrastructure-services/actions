using Microsoft.Data.SqlClient;

namespace DatabaseReleaseQualification;

// LEGACY-owned enrichment. The EF reader's view query and hash format stay
// unchanged; persisted session properties belong to the explicit LEGACY profile.
internal sealed class LegacySchemaCapture
{
    internal const string ViewPropertiesSql = """
        SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')), DB_NAME(),
               s.name,v.name,sm.uses_ansi_nulls,sm.uses_quoted_identifier
        FROM sys.views v JOIN sys.schemas s ON s.schema_id=v.schema_id
        LEFT JOIN sys.sql_modules sm ON sm.object_id=v.object_id
        WHERE v.is_ms_shipped=0 ORDER BY s.name,v.name;
        """;

    public async Task<SchemaCaptureSourceResult> CaptureWithMetadataAsync(string connectionString, CancellationToken token)
    {
        var result = await new SqlServerSchemaReader(LegacySqlProfile.ModulesV1).CaptureWithMetadataAsync(connectionString, token);
        var views = result.Snapshot.Objects.Where(x => x.Kind == "view").ToArray();
        if (views.Length == 0) return result;
        SqlServerSchemaReader.EnsureSelectOnlySql(ViewPropertiesSql);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = ViewPropertiesSql; command.CommandTimeout = 60;
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<LegacyViewPropertiesV1>();
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetString(0) != result.ServerInstance
                || reader.GetString(1) != result.DatabaseName)
                throw new LegacyContractException("TARGET_IDENTITY_MISMATCH");
            rows.Add(new(reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetBoolean(5)));
        }
        Enrich(result.Snapshot, rows);
        return result;
    }

    internal static void Enrich(SchemaSnapshot snapshot, IReadOnlyList<LegacyViewPropertiesV1> rows)
    {
        foreach (var view in snapshot.Objects.Where(x => x.Kind == "view"))
        {
            var matches = rows.Where(x => x.Schema == view.Schema && x.Name == view.Name).ToArray();
            if (matches.Length != 1 || matches[0].AnsiNulls is null || matches[0].QuotedIdentifier is null)
                throw new LegacyContractException("VIEW_PROPERTIES_UNVERIFIED");
            view.Properties.Add("legacy-profile", "LEGACY_MODULES_V1");
            view.Properties.Add("legacy-ansinulls", matches[0].AnsiNulls == true ? "true" : "false");
            view.Properties.Add("legacy-quotedidentifier", matches[0].QuotedIdentifier == true ? "true" : "false");
        }
    }
}
internal sealed record LegacyViewPropertiesV1(string Schema, string Name, bool? AnsiNulls, bool? QuotedIdentifier);
