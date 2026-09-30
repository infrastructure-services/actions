using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace DatabaseReleaseQualification;

// Bounded, impact-scoped exact row-multiset equality. No raw data is exported.
// Unsupported types, metadata, computed columns, row limits and instability block.
public sealed class SqlLegacyDataEvidenceReaderV1(string inspectionConnection,
    LegacyBindingV1 binding) : ILegacyDataEvidenceReaderV1
{
    public async Task<LegacyDataPhaseEvidenceV1> CaptureAsync(LegacyDataContractDefinitionV1 definition,
        RecoveryPhase phase, CancellationToken token)
    {
        if (definition.ProviderKind != "SCOPED_ROWSET_SHA256_V1" || definition.Scope is null
            || definition.Scope.Targets.Count is < 1 or > 32
            || definition.MaximumRowsPerTable is < 1 or > 100000
            || LegacyRuntimeEvidenceHash.Hash(definition.Scope) != definition.ScopeHash)
            throw new LegacyContractException("DATA_PROVIDER_CONTRACT_INVALID");
        var first = await CaptureHash(definition, token);
        var second = await CaptureHash(definition, token);
        if (first != second) throw new LegacyContractException("DATA_CAPTURE_UNSTABLE");
        return new(1, definition.Selector, definition.Version, definition.TargetId,
            definition.ScopeHash, phase, DateTimeOffset.UtcNow, true, first);
    }

    private async Task<string> CaptureHash(LegacyDataContractDefinitionV1 definition, CancellationToken token)
    {
        var target = new SecurityTargetBindingV1(binding.DatabaseName, binding.ServerMatchPolicy, binding.AllowedServerInstances);
        await using var connection = new SqlConnection(SqlClientSecurityCatalogTransport.BuildConnectionOptions(
            inspectionConnection, target).ConnectionString);
        await connection.OpenAsync(token);
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')), DB_NAME(), HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION')";
            identity.CommandTimeout = 15;
            await using var reader = await identity.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.IsDBNull(0) || reader.IsDBNull(1)
                || reader.IsDBNull(2) || reader.GetInt32(2) != 1 || reader.GetString(1) != binding.DatabaseName
                || binding.ServerMatchPolicy == "ALLOW_LIST"
                    && !binding.AllowedServerInstances.Contains(reader.GetString(0), StringComparer.OrdinalIgnoreCase))
                throw new LegacyContractException("DATA_METADATA_INCOMPLETE");
        }
        var tableHashes = new List<string>();
        long totalBytes = 0;
        foreach (var table in definition.Scope!.Targets)
        {
            var name = Identifier(table.Schema) + "." + Identifier(table.Object);
            // Verify a local ordinary table before any read of application values.
            await using (var metadata = connection.CreateCommand())
            {
                metadata.CommandText = "SELECT t.is_memory_optimized,t.temporal_type,c.is_computed,c.is_hidden,c.encryption_type,c.system_type_id "
                    + "FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id "
                    + "WHERE s.name=" + SecuritySqlLiteral.Sysname(table.Schema) + " AND t.name=" + SecuritySqlLiteral.Sysname(table.Object);
                metadata.CommandTimeout = 30;
                await using var rows = await metadata.ExecuteReaderAsync(token);
                var columns = 0;
                while (await rows.ReadAsync(token))
                {
                    columns++;
                    // SQL scalar built-in types with lossless CLR representations.
                    if (rows.GetBoolean(0) || rows.GetByte(1) != 0 || rows.GetBoolean(2) || rows.GetBoolean(3)
                        || !rows.IsDBNull(4) || Convert.ToInt32(rows.GetValue(5)) is not
                            (36 or 48 or 52 or 56 or 59 or 62 or 104 or 106 or 108 or 127 or 165 or 167 or 173 or 175 or 231 or 239 or 40 or 41 or 42 or 43 or 58 or 61 or 60 or 122))
                        throw new LegacyContractException("DATA_TYPE_UNSUPPORTED");
                }
                if (columns == 0) throw new LegacyContractException("DATA_METADATA_INCOMPLETE");
            }
            var selected = table.Columns is null ? "*" : string.Join(",", table.Columns.Select(Identifier));
            if (selected.Length == 0) throw new LegacyContractException("DATA_PROVIDER_CONTRACT_INVALID");
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 60;
            command.CommandText = "SELECT TOP (" + (definition.MaximumRowsPerTable + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture) + ") " + selected + " FROM " + name;
            await using var result = await command.ExecuteReaderAsync(token);
            var rowHashes = new List<string>();
            var shape = Enumerable.Range(0, result.FieldCount)
                .Select(i => new { name = result.GetName(i), type = result.GetDataTypeName(i) }).ToArray();
            while (await result.ReadAsync(token))
            {
                if (rowHashes.Count >= definition.MaximumRowsPerTable)
                    throw new LegacyContractException("DATA_CAPTURE_LIMIT");
                var values = new object?[result.FieldCount];
                for (var i = 0; i < values.Length; i++) values[i] = result.IsDBNull(i) ? null : result.GetValue(i);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(values, JsonDefaults.Compact);
                totalBytes += bytes.Length;
                if (totalBytes > 16 * 1024 * 1024) throw new LegacyContractException("DATA_CAPTURE_LIMIT");
                rowHashes.Add(Hashing.Sha256(bytes));
            }
            rowHashes.Sort(StringComparer.Ordinal);
            tableHashes.Add(LegacyRuntimeEvidenceHash.Hash(new { table.Schema, table.Object, shape, rowHashes }));
        }
        return LegacyRuntimeEvidenceHash.Hash(tableHashes);
    }

    private static string Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new LegacyContractException("DATA_PROVIDER_CONTRACT_INVALID");
        return "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }
}
