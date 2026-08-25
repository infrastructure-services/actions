using System.Globalization;
using Microsoft.Data.SqlClient;

namespace DatabaseReleaseQualification;

public sealed class SqlServerSchemaReader
{
    private const int CommandTimeoutSeconds = 60;

    public async Task<SchemaSnapshot> CaptureAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A SQL Server connection string is required.", nameof(connectionString));
        }

        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "cicd-database-release-qualification-v1"
        };

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureMetadataVisibilityAsync(connection, cancellationToken);
        var snapshot = new SchemaSnapshot();

        await ReadAsync(connection, SchemasSql, reader => snapshot.Objects.Add(new SchemaObject
        {
            Kind = "schema",
            Schema = Text(reader, 0),
            Name = Text(reader, 0),
            Properties = Properties(("owner", Text(reader, 1)))
        }), cancellationToken);

        await ReadAsync(connection, TablesSql, reader => snapshot.Objects.Add(new SchemaObject
        {
            Kind = "table",
            Schema = Text(reader, 0),
            Name = Text(reader, 1),
            Properties = Properties(
                ("temporalType", Text(reader, 2)),
                ("historySchema", Text(reader, 3)),
                ("historyTable", Text(reader, 4)),
                ("durability", Text(reader, 5)),
                ("memoryOptimized", Boolean(reader, 6)))
        }), cancellationToken);

        await ReadAsync(connection, ColumnsSql, reader => snapshot.Objects.Add(new SchemaObject
        {
            Kind = "column",
            Schema = Text(reader, 0),
            Parent = Text(reader, 1),
            Name = Text(reader, 2),
            Properties = Properties(
                ("ordinal", Number(reader, 3)),
                ("typeSchema", Text(reader, 4)),
                ("dataType", Text(reader, 5)),
                ("maxLength", Number(reader, 6)),
                ("precision", Number(reader, 7)),
                ("scale", Number(reader, 8)),
                ("nullable", Boolean(reader, 9)),
                ("identity", Boolean(reader, 10)),
                ("identitySeed", Text(reader, 11)),
                ("identityIncrement", Text(reader, 12)),
                ("computed", Boolean(reader, 13)),
                ("computedPersisted", Boolean(reader, 14)),
                ("computedDefinitionSha256", DefinitionHash(Text(reader, 15))),
                ("collation", Text(reader, 16)),
                ("sparse", Boolean(reader, 17)),
                ("rowGuid", Boolean(reader, 18)))
        }), cancellationToken);

        await ReadAsync(connection, DefaultsSql, reader => snapshot.Objects.Add(new SchemaObject
        {
            Kind = "default-constraint",
            Schema = Text(reader, 0),
            Parent = Text(reader, 1),
            Name = Text(reader, 2),
            Properties = Properties(("column", Text(reader, 3)), ("definitionSha256", DefinitionHash(Text(reader, 4))))
        }), cancellationToken);

        await ReadAsync(connection, KeyConstraintsSql, reader => snapshot.Objects.Add(new SchemaObject
        {
            Kind = "key-constraint-column",
            Schema = Text(reader, 0),
            Parent = Text(reader, 1),
            Name = $"{Text(reader, 2)}:{Number(reader, 5).PadLeft(4, '0')}",
            Properties = Properties(
                ("constraint", Text(reader, 2)),
                ("constraintType", Text(reader, 3)),
                ("column", Text(reader, 4)),
                ("ordinal", Number(reader, 5)),
                ("descending", Boolean(reader, 6)))
        }), cancellationToken);

        await ReadAsync(connection, ChecksSql, reader => snapshot.Objects.Add(new SchemaObject
        {
            Kind = "check-constraint",
            Schema = Text(reader, 0),
            Parent = Text(reader, 1),
            Name = Text(reader, 2),
            Properties = Properties(
                ("column", Text(reader, 3)),
                ("definitionSha256", DefinitionHash(Text(reader, 4))),
                ("disabled", Boolean(reader, 5)),
                ("notTrusted", Boolean(reader, 6)),
                ("notForReplication", Boolean(reader, 7)))
        }), cancellationToken);

        await ReadAsync(connection, ForeignKeysSql, reader => snapshot.Objects.Add(new SchemaObject
        {
            Kind = "foreign-key-column",
            Schema = Text(reader, 0),
            Parent = Text(reader, 1),
            Name = $"{Text(reader, 2)}:{Number(reader, 9).PadLeft(4, '0')}",
            Properties = Properties(
                ("foreignKey", Text(reader, 2)),
                ("column", Text(reader, 3)),
                ("referencedSchema", Text(reader, 4)),
                ("referencedTable", Text(reader, 5)),
                ("referencedColumn", Text(reader, 6)),
                ("deleteAction", Text(reader, 7)),
                ("updateAction", Text(reader, 8)),
                ("ordinal", Number(reader, 9)),
                ("disabled", Boolean(reader, 10)),
                ("notTrusted", Boolean(reader, 11)))
        }), cancellationToken);

        await ReadAsync(connection, IndexesSql, reader => snapshot.Objects.Add(new SchemaObject
        {
            Kind = "index-column",
            Schema = Text(reader, 0),
            Parent = Text(reader, 1),
            Name = $"{Text(reader, 2)}:{Number(reader, 11).PadLeft(4, '0')}",
            Properties = Properties(
                ("index", Text(reader, 2)),
                ("indexType", Text(reader, 3)),
                ("unique", Boolean(reader, 4)),
                ("primaryKey", Boolean(reader, 5)),
                ("uniqueConstraint", Boolean(reader, 6)),
                ("filter", Text(reader, 7)),
                ("disabled", Boolean(reader, 8)),
                ("column", Text(reader, 9)),
                ("role", Text(reader, 10)),
                ("ordinal", Number(reader, 11)),
                ("descending", Boolean(reader, 12)),
                ("fillFactor", Number(reader, 13)),
                ("padded", Boolean(reader, 14)),
                ("ignoreDuplicateKey", Boolean(reader, 15)),
                ("allowRowLocks", Boolean(reader, 16)),
                ("allowPageLocks", Boolean(reader, 17)),
                ("dataSpace", Text(reader, 18)),
                ("dataSpaceType", Text(reader, 19)))
        }), cancellationToken);

        await ReadAsync(connection, TriggersSql, reader =>
        {
            var definition = Text(reader, 6);
            if (string.IsNullOrWhiteSpace(definition))
            {
                snapshot.UnsupportedSchemaFeatures.Add("trigger-definition-unavailable");
            }
            snapshot.Objects.Add(new SchemaObject
            {
                Kind = "trigger",
                Schema = Text(reader, 0),
                Parent = Text(reader, 1),
                Name = Text(reader, 2),
                Properties = Properties(
                    ("disabled", Boolean(reader, 3)),
                    ("insteadOf", Boolean(reader, 4)),
                    ("notForReplication", Boolean(reader, 5)),
                    ("definitionSha256", DefinitionHash(definition)))
            });
        }, cancellationToken);

        await ReadAsync(connection, ViewsSql, reader =>
        {
            var definition = Text(reader, 3);
            if (string.IsNullOrWhiteSpace(definition))
            {
                snapshot.UnsupportedSchemaFeatures.Add("view-definition-unavailable");
            }
            snapshot.Objects.Add(new SchemaObject
            {
                Kind = "view",
                Schema = Text(reader, 0),
                Name = Text(reader, 1),
                Properties = Properties(("schemaBound", Boolean(reader, 2)), ("definitionSha256", DefinitionHash(definition)))
            });
        }, cancellationToken);

        await ReadAsync(connection, DependenciesSql, reader => snapshot.Objects.Add(new SchemaObject
        {
            Kind = "schema-dependency",
            Schema = Text(reader, 0),
            Parent = Text(reader, 1),
            Name = $"{Text(reader, 2)}:{Text(reader, 5)}:{Text(reader, 6)}:{Text(reader, 7)}",
            Properties = Properties(
                ("referencingType", Text(reader, 2)),
                ("referencingColumn", Text(reader, 3)),
                ("referencedServer", Text(reader, 4)),
                ("referencedSchema", Text(reader, 5)),
                ("referencedEntity", Text(reader, 6)),
                ("referencedColumn", Text(reader, 7)),
                ("schemaBound", Boolean(reader, 8)),
                ("callerDependent", Boolean(reader, 9)))
        }), cancellationToken);

        await ReadAsync(connection, UnsupportedSql, reader =>
        {
            var category = Text(reader, 2);
            snapshot.UnsupportedSchemaFeatures.Add(category);
            snapshot.Objects.Add(new SchemaObject
            {
                Kind = "unsupported-object",
                Schema = Text(reader, 0),
                Name = Text(reader, 1),
                Properties = Properties(("type", category))
            });
        }, cancellationToken);

        await ReadAsync(connection, UnsupportedSchemaFeaturesSql, reader =>
        {
            var feature = Text(reader, 2);
            snapshot.UnsupportedSchemaFeatures.Add(feature);
            snapshot.Objects.Add(new SchemaObject
            {
                Kind = "unsupported-schema-feature",
                Schema = Text(reader, 0),
                Name = Text(reader, 1),
                Properties = Properties(("feature", feature))
            });
        }, cancellationToken);

        try
        {
            await ReadAsync(connection, ImpactSql, reader => snapshot.ImpactMetrics.Add(new TableImpactMetric
            {
                Schema = Text(reader, 0),
                Table = Text(reader, 1),
                RowCount = Int64(reader, 2),
                ReservedMb = Decimal(reader, 3),
                IndexMb = Decimal(reader, 4),
                LobMb = Decimal(reader, 5),
                PartitionCount = Int32(reader, 6),
                IndexCount = Int32(reader, 7),
                ForeignKeyCount = Int32(reader, 8),
                TriggerCount = Int32(reader, 9),
                DependencyCount = Int32(reader, 10)
            }), cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 229)
        {
            snapshot.UnsupportedSchemaFeatures.Add("impact-metrics:permission-denied");
        }

        var unsupportedFeatures = snapshot.UnsupportedSchemaFeatures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        snapshot.UnsupportedSchemaFeatures.Clear();
        snapshot.UnsupportedSchemaFeatures.AddRange(unsupportedFeatures);
        return snapshot;
    }

    private static async Task EnsureMetadataVisibilityAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION');";
        command.CommandTimeout = CommandTimeoutSeconds;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull || Convert.ToInt32(value, CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException("FAIL_METADATA_VISIBILITY");
        }
    }

    private static async Task ReadAsync(SqlConnection connection, string sql, Action<SqlDataReader> consume, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            consume(reader);
        }
    }

    private static SortedDictionary<string, string> Properties(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string Text(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
    private static string Number(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "0" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "0";
    private static string Boolean(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "false" : Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ? "true" : "false";
    private static long Int64(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    private static int Int32(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    private static decimal Decimal(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    private static string DefinitionHash(string definition) => string.IsNullOrEmpty(definition) ? "" : Hashing.Sha256(definition);

    private const string UserSchemaFilter = "s.name NOT IN (N'sys', N'INFORMATION_SCHEMA', N'cicd') AND NOT EXISTS (SELECT 1 FROM sys.database_principals AS dp WHERE dp.principal_id = s.principal_id AND dp.type = N'R' AND dp.is_fixed_role = 1)";

    private static readonly string SchemasSql = $"""
        SELECT s.name, USER_NAME(s.principal_id)
        FROM sys.schemas AS s
        WHERE {UserSchemaFilter}
        ORDER BY s.name;
        """;

    private static readonly string TablesSql = $"""
        SELECT s.name, t.name, t.temporal_type_desc,
               hs.name, ht.name, t.durability_desc, t.is_memory_optimized
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN sys.tables AS ht ON ht.object_id = t.history_table_id
        LEFT JOIN sys.schemas AS hs ON hs.schema_id = ht.schema_id
        WHERE t.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, t.name;
        """;

    private static readonly string ColumnsSql = $"""
        SELECT s.name, t.name, c.name, c.column_id, ts.name, ty.name,
               c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
               CONVERT(nvarchar(100), ic.seed_value), CONVERT(nvarchar(100), ic.increment_value),
               c.is_computed, ISNULL(cc.is_persisted, 0), cc.definition,
               c.collation_name, c.is_sparse, c.is_rowguidcol
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        INNER JOIN sys.columns AS c ON c.object_id = t.object_id
        INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
        INNER JOIN sys.schemas AS ts ON ts.schema_id = ty.schema_id
        LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        WHERE t.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, t.name, c.column_id;
        """;

    private static readonly string DefaultsSql = $"""
        SELECT s.name, t.name, dc.name, c.name, dc.definition
        FROM sys.default_constraints AS dc
        INNER JOIN sys.tables AS t ON t.object_id = dc.parent_object_id
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        INNER JOIN sys.columns AS c ON c.object_id = t.object_id AND c.column_id = dc.parent_column_id
        WHERE t.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, t.name, dc.name;
        """;

    private static readonly string KeyConstraintsSql = $"""
        SELECT s.name, t.name, kc.name, kc.type_desc, c.name, ic.key_ordinal, ic.is_descending_key
        FROM sys.key_constraints AS kc
        INNER JOIN sys.tables AS t ON t.object_id = kc.parent_object_id
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        INNER JOIN sys.index_columns AS ic ON ic.object_id = t.object_id AND ic.index_id = kc.unique_index_id AND ic.key_ordinal > 0
        INNER JOIN sys.columns AS c ON c.object_id = t.object_id AND c.column_id = ic.column_id
        WHERE t.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, t.name, kc.name, ic.key_ordinal;
        """;

    private static readonly string ChecksSql = $"""
        SELECT s.name, t.name, cc.name, c.name, cc.definition,
               cc.is_disabled, cc.is_not_trusted, cc.is_not_for_replication
        FROM sys.check_constraints AS cc
        INNER JOIN sys.tables AS t ON t.object_id = cc.parent_object_id
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN sys.columns AS c ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
        WHERE t.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, t.name, cc.name;
        """;

    private static readonly string ForeignKeysSql = $"""
        SELECT s.name, t.name, fk.name, c.name, rs.name, rt.name, rc.name,
               fk.delete_referential_action_desc, fk.update_referential_action_desc,
               fkc.constraint_column_id, fk.is_disabled, fk.is_not_trusted
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        INNER JOIN sys.tables AS t ON t.object_id = fk.parent_object_id
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        INNER JOIN sys.columns AS c ON c.object_id = t.object_id AND c.column_id = fkc.parent_column_id
        INNER JOIN sys.tables AS rt ON rt.object_id = fk.referenced_object_id
        INNER JOIN sys.schemas AS rs ON rs.schema_id = rt.schema_id
        INNER JOIN sys.columns AS rc ON rc.object_id = rt.object_id AND rc.column_id = fkc.referenced_column_id
        WHERE t.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, t.name, fk.name, fkc.constraint_column_id;
        """;

    private static readonly string IndexesSql = $"""
        SELECT s.name, t.name, i.name, i.type_desc, i.is_unique, i.is_primary_key,
               i.is_unique_constraint, i.filter_definition, i.is_disabled, c.name,
               CASE WHEN ic.is_included_column = 1 THEN N'INCLUDE' ELSE N'KEY' END,
               CASE WHEN ic.is_included_column = 1 THEN ic.index_column_id ELSE ic.key_ordinal END,
               ic.is_descending_key, i.fill_factor, i.is_padded, i.ignore_dup_key,
               i.allow_row_locks, i.allow_page_locks, ds.name, ds.type_desc
        FROM sys.indexes AS i
        INNER JOIN sys.tables AS t ON t.object_id = i.object_id
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        LEFT JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
        WHERE i.index_id > 0 AND i.is_hypothetical = 0 AND t.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, t.name, i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
        """;

    private static readonly string TriggersSql = $"""
        SELECT s.name, t.name, tr.name, tr.is_disabled, tr.is_instead_of_trigger,
               tr.is_not_for_replication, sm.definition
        FROM sys.triggers AS tr
        INNER JOIN sys.tables AS t ON t.object_id = tr.parent_id
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN sys.sql_modules AS sm ON sm.object_id = tr.object_id
        WHERE tr.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, t.name, tr.name;
        """;

    private static readonly string ViewsSql = $"""
        SELECT s.name, v.name, OBJECTPROPERTY(v.object_id, 'IsSchemaBound'), sm.definition
        FROM sys.views AS v
        INNER JOIN sys.schemas AS s ON s.schema_id = v.schema_id
        LEFT JOIN sys.sql_modules AS sm ON sm.object_id = v.object_id
        WHERE v.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, v.name;
        """;

    private static readonly string DependenciesSql = $"""
        SELECT s.name, o.name, o.type_desc, c.name, d.referenced_server_name,
               d.referenced_schema_name, d.referenced_entity_name, d.referenced_minor_name,
               d.is_schema_bound_reference, d.is_caller_dependent
        FROM sys.sql_expression_dependencies AS d
        INNER JOIN sys.objects AS o ON o.object_id = d.referencing_id
        INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        LEFT JOIN sys.columns AS c ON c.object_id = o.object_id AND c.column_id = d.referencing_minor_id
        WHERE o.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, o.name, d.referenced_schema_name, d.referenced_entity_name, d.referenced_minor_name;
        """;

    private static readonly string UnsupportedSql = $"""
        SELECT s.name, o.name, o.type_desc
        FROM sys.objects AS o
        INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0 AND {UserSchemaFilter}
          AND o.type NOT IN (N'U', N'V', N'PK', N'UQ', N'C', N'F', N'D', N'TR')
        ORDER BY s.name, o.type_desc, o.name;
        """;

    private static readonly string ImpactSql = $"""
        WITH physical AS (
            SELECT p.object_id,
                   SUM(CASE WHEN p.index_id IN (0, 1) THEN p.row_count ELSE 0 END) AS row_count,
                   SUM(p.reserved_page_count) * 8.0 / 1024.0 AS reserved_mb,
                   SUM(CASE WHEN p.index_id > 1 THEN p.reserved_page_count ELSE 0 END) * 8.0 / 1024.0 AS index_mb,
                   SUM(p.lob_reserved_page_count) * 8.0 / 1024.0 AS lob_mb,
                   COUNT(DISTINCT p.partition_number) AS partition_count
            FROM sys.dm_db_partition_stats AS p
            GROUP BY p.object_id
        )
        SELECT s.name, t.name,
               ISNULL(p.row_count, 0), ISNULL(p.reserved_mb, 0), ISNULL(p.index_mb, 0), ISNULL(p.lob_mb, 0),
               ISNULL(p.partition_count, 0),
               (SELECT COUNT(*) FROM sys.indexes AS i WHERE i.object_id = t.object_id AND i.index_id > 0 AND i.is_hypothetical = 0),
               (SELECT COUNT(*) FROM sys.foreign_keys AS fk WHERE fk.parent_object_id = t.object_id OR fk.referenced_object_id = t.object_id),
               (SELECT COUNT(*) FROM sys.triggers AS tr WHERE tr.parent_id = t.object_id AND tr.is_ms_shipped = 0),
               (SELECT COUNT(*) FROM sys.sql_expression_dependencies AS d WHERE d.referencing_id = t.object_id OR d.referenced_id = t.object_id)
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN physical AS p ON p.object_id = t.object_id
        WHERE t.is_ms_shipped = 0 AND {UserSchemaFilter}
        ORDER BY s.name, t.name;
        """;

    private static readonly string UnsupportedSchemaFeaturesSql = $"""
        SELECT feature_schema, feature_object, feature_name
        FROM (
            SELECT s.name AS feature_schema, seq.name AS feature_object, N'sequence-definition' AS feature_name
            FROM sys.sequences AS seq
            INNER JOIN sys.schemas AS s ON s.schema_id = seq.schema_id
            WHERE {UserSchemaFilter}
            UNION ALL
            SELECT s.name, sn.name, N'synonym-target'
            FROM sys.synonyms AS sn
            INNER JOIN sys.schemas AS s ON s.schema_id = sn.schema_id
            WHERE {UserSchemaFilter}
            UNION ALL
            SELECT s.name, ty.name, N'user-defined-type-definition'
            FROM sys.types AS ty
            INNER JOIN sys.schemas AS s ON s.schema_id = ty.schema_id
            WHERE ty.is_user_defined = 1 AND {UserSchemaFilter}
            UNION ALL
            SELECT N'', pf.name, N'partition-function-definition'
            FROM sys.partition_functions AS pf
            UNION ALL
            SELECT N'', ps.name, N'partition-scheme-mapping'
            FROM sys.partition_schemes AS ps
            UNION ALL
            SELECT DISTINCT s.name, t.name, N'data-compression'
            FROM sys.partitions AS p
            INNER JOIN sys.tables AS t ON t.object_id = p.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE p.data_compression > 0 AND t.is_ms_shipped = 0 AND {UserSchemaFilter}
            UNION ALL
            SELECT s.name, t.name, N'temporal-table-extended-metadata'
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE t.temporal_type > 0 AND t.is_ms_shipped = 0 AND {UserSchemaFilter}
            UNION ALL
            SELECT s.name, t.name + N'.' + i.name,
                   CASE i.type WHEN 3 THEN N'xml-index-options'
                               WHEN 4 THEN N'spatial-index-options'
                               WHEN 5 THEN N'columnstore-index-options'
                               WHEN 6 THEN N'columnstore-index-options'
                               ELSE N'special-index-options' END
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.object_id = i.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE i.type NOT IN (0, 1, 2) AND t.is_ms_shipped = 0 AND {UserSchemaFilter}
            UNION ALL
            SELECT s.name, v.name, N'indexed-view-index-options'
            FROM sys.views AS v
            INNER JOIN sys.schemas AS s ON s.schema_id = v.schema_id
            WHERE EXISTS (SELECT 1 FROM sys.indexes AS i WHERE i.object_id = v.object_id AND i.index_id > 0)
              AND v.is_ms_shipped = 0 AND {UserSchemaFilter}
            UNION ALL
            SELECT s.name, t.name, N'full-text-index-definition'
            FROM sys.fulltext_indexes AS fi
            INNER JOIN sys.tables AS t ON t.object_id = fi.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0 AND {UserSchemaFilter}
            UNION ALL
            SELECT s.name, o.name + N'.' + st.name, N'manually-created-statistics'
            FROM sys.stats AS st
            INNER JOIN sys.objects AS o ON o.object_id = st.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE st.user_created = 1 AND o.is_ms_shipped = 0 AND {UserSchemaFilter}
        ) AS features
        ORDER BY feature_schema, feature_object, feature_name;
        """;
}
