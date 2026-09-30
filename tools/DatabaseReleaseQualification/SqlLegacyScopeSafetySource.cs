using System.Text.Json;

namespace DatabaseReleaseQualification;

// Captures only the objects referenced by the package. DDL trigger inventories
// are database/server-wide because those triggers can fire outside object scope.
public sealed class SqlLegacyScopeSafetySource(ISecurityCatalogTransport transport,
    SecurityTargetBindingV1 binding) : ILegacyScopeSafetySource
{
    public async Task<LegacyScopeSafetySnapshotV1> CaptureAsync(
        IReadOnlyList<RecoverySecuritySecurable> scope, CancellationToken token)
    {
        if (scope.Count > 1024) throw new SecurityCatalogException("RESOURCE_LIMIT", "SCOPE");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        await using var session = await transport.OpenAsync(deadline.Token);
        var identity = One(await session.QueryAsync(
            "SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')) AS serverName, DB_NAME() AS databaseName, CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) AS majorVersion, CONVERT(int, SERVERPROPERTY('EngineEdition')) AS engineEdition", deadline.Token));
        var server = String(identity, "serverName");
        var database = String(identity, "databaseName");
        if (database != binding.DatabaseName
            || binding.ServerMatchPolicy == "ALLOW_LIST"
                && !binding.AllowedServerInstances.Contains(server, StringComparer.OrdinalIgnoreCase))
            throw new SecurityCatalogException("TARGET_IDENTITY_MISMATCH", "IDENTITY");
        if (Integer(identity, "majorVersion") is not (16 or 17)
            || Integer(identity, "engineEdition") is not (2 or 3 or 4))
            throw new SecurityCatalogException("SECURITY_ENGINE_UNSUPPORTED", "IDENTITY");
        var proof = One(await session.QueryAsync(
            "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') AS databaseView, HAS_PERMS_BY_NAME(NULL, 'SERVER', 'VIEW ANY DEFINITION') AS serverView", deadline.Token));
        if (Integer(proof, "databaseView") != 1)
            throw new SecurityCatalogException("METADATA_PERMISSION_INSUFFICIENT", "VISIBILITY");
        var serverComplete = Integer(proof, "serverView") == 1;
        var dbTriggers = await session.QueryAsync(
            "SELECT object_id, parent_class, is_disabled FROM sys.triggers WHERE parent_class = 0", deadline.Token);
        var serverTriggers = serverComplete
            ? await session.QueryAsync(
                "SELECT object_id, parent_class, is_disabled FROM sys.server_triggers", deadline.Token)
            : [];
        var hasDdl = dbTriggers.Any(x => Integer(x, "is_disabled") == 0)
            || serverTriggers.Any(x => Integer(x, "is_disabled") == 0);

        var objects = new List<LegacySafetyObject>();
        foreach (var item in scope)
        {
            if (item.Kind != "OBJECT")
                throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "SCOPE");
            var schema = SecuritySqlLiteral.Sysname(item.Schema);
            var name = SecuritySqlLiteral.Sysname(item.Name);
            var schemaProof = One(await session.QueryAsync(
                "SELECT HAS_PERMS_BY_NAME(" + schema +
                ", 'SCHEMA', 'VIEW DEFINITION') AS permitted", deadline.Token));
            if (Integer(schemaProof, "permitted") != 1)
                throw new SecurityCatalogException("METADATA_PERMISSION_INSUFFICIENT", "VISIBILITY");
            var rows = await session.QueryAsync(
                "SELECT o.object_id, s.name AS schemaName, o.name, o.type, o.is_ms_shipped FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name = " +
                schema + " AND o.name = " + name, deadline.Token);
            if (rows.Count == 0)
            {
                // Database and schema VIEW DEFINITION were proved above. Under those
                // visibility guarantees a zero-row object lookup proves absence.
                objects.Add(new(item.Schema, item.Name, "ABSENT", false, false,
                    false, false, false, false, false, false, false));
                continue;
            }
            var row = One(rows);
            if (String(row, "schemaName") != item.Schema || String(row, "name") != item.Name)
                throw new SecurityCatalogException("SECURITY_NAME_ALIAS", "SCOPE");
            var qualifiedName = SecuritySqlLiteral.Sysname(item.Schema + "." + item.Name);
            var objectProof = One(await session.QueryAsync(
                "SELECT HAS_PERMS_BY_NAME(" + qualifiedName +
                ", 'OBJECT', 'VIEW DEFINITION') AS permitted", deadline.Token));
            if (Integer(objectProof, "permitted") != 1)
                throw new SecurityCatalogException("METADATA_PERMISSION_INSUFFICIENT", "VISIBILITY");
            var id = Integer(row, "object_id");
            var objectId = SecuritySqlLiteral.Integer(id);
            var tableRows = await session.QueryAsync(
                "SELECT temporal_type, is_memory_optimized, is_external FROM sys.tables WHERE object_id = " +
                objectId, deadline.Token);
            var indexes = await session.QueryAsync(
                "SELECT type, is_hypothetical FROM sys.indexes WHERE object_id = " +
                objectId, deadline.Token);
            var special = tableRows.Count > 1 || tableRows.Any(x =>
                Integer(x, "temporal_type") != 0 || Integer(x, "is_memory_optimized") != 0
                || Integer(x, "is_external") != 0)
                || indexes.Any(x => Integer(x, "type") is not (0 or 1 or 2)
                    || Integer(x, "is_hypothetical") != 0);
            var synonym = (await session.QueryAsync(
                "SELECT object_id, base_object_name FROM sys.synonyms WHERE object_id = " + objectId,
                deadline.Token)).Count > 0;
            var triggers = await session.QueryAsync(
                "SELECT object_id, parent_class, parent_id, type, is_disabled FROM sys.triggers WHERE parent_class = 1 AND parent_id = " +
                objectId, deadline.Token);
            var foreignKeys = await session.QueryAsync(
                "SELECT object_id, parent_object_id, referenced_object_id, is_disabled, delete_referential_action, update_referential_action FROM sys.foreign_keys WHERE referenced_object_id = " +
                objectId, deadline.Token);
            var defaults = await session.QueryAsync(
                "SELECT object_id, parent_object_id, parent_column_id, definition FROM sys.default_constraints WHERE parent_object_id = " +
                objectId, deadline.Token);
            var checks = await session.QueryAsync(
                "SELECT object_id, parent_object_id, parent_column_id, definition FROM sys.check_constraints WHERE parent_object_id = " +
                objectId, deadline.Token);
            var computed = await session.QueryAsync(
                "SELECT object_id, column_id, definition FROM sys.computed_columns WHERE object_id = " +
                objectId, deadline.Token);
            var dependencies = await session.QueryAsync(
                "SELECT referencing_id, referencing_minor_id, referenced_id, referenced_server_name, referenced_database_name, referenced_schema_name, referenced_entity_name, is_caller_dependent, is_ambiguous FROM sys.sql_expression_dependencies WHERE referencing_id = " +
                objectId, deadline.Token);
            var policies = await session.QueryAsync(
                "SELECT object_id, is_enabled FROM sys.security_policies", deadline.Token);
            var predicates = await session.QueryAsync(
                "SELECT object_id, target_object_id, predicate_definition FROM sys.security_predicates WHERE target_object_id = " +
                objectId, deadline.Token);
            var rls = predicates.Any(x => policies.Any(p =>
                Integer(p, "object_id") == Integer(x, "object_id")
                && Integer(p, "is_enabled") != 0));
            var indirect = dependencies.Any(x => x["referenced_id"] is null
                || x["referenced_server_name"] is not null
                || x["referenced_database_name"] is not null
                || Integer(x, "is_caller_dependent") != 0
                || Integer(x, "is_ambiguous") != 0);
            var expressions = defaults.Any(x => !LegacyStaticSafety.CatalogExpressionSafe(
                    x.TryGetValue("definition", out var value) ? value as string : null, false))
                || checks.Any(x => !LegacyStaticSafety.CatalogExpressionSafe(
                    x.TryGetValue("definition", out var value) ? value as string : null, true))
                || computed.Any(x => !LegacyStaticSafety.CatalogExpressionSafe(
                    x.TryGetValue("definition", out var value) ? value as string : null, false))
                || predicates.Any(x => x["predicate_definition"] is null);
            objects.Add(new(item.Schema, item.Name,
                String(row, "type") == "U" ? "TABLE" : String(row, "type") == "V" ? "VIEW" : "UNSUPPORTED",
                Integer(row, "is_ms_shipped") != 0, special, synonym,
                triggers.Any(x => Integer(x, "is_disabled") == 0), triggers.Count != 0,
                foreignKeys.Any(x => Integer(x, "delete_referential_action") != 0
                    || Integer(x, "update_referential_action") != 0),
                rls, indirect, expressions));
        }
        var finalIdentity = One(await session.QueryAsync(
            "SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')) AS serverName, DB_NAME() AS databaseName", deadline.Token));
        if (String(finalIdentity, "serverName") != server
            || String(finalIdentity, "databaseName") != database)
            throw new SecurityCatalogException("SECURITY_CAPTURE_CHANGED", "STABILITY");
        var ordered = objects.OrderBy(x => x.Schema, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var result = new LegacyScopeSafetySnapshotV1(1, true, server, database,
            true, serverComplete, hasDdl, ordered, "");
        var hash = Hashing.Sha256(JsonSerializer.Serialize(new {
            result.ContractVersion, result.Complete, result.ServerInstance,
            result.DatabaseName, result.DatabaseDdlTriggersComplete,
            result.ServerDdlTriggersComplete, result.HasEnabledDdlTrigger,
            result.Objects
        }, JsonDefaults.Compact));
        return result with { Sha256 = hash };
    }

    private static IReadOnlyDictionary<string, object?> One(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count != 1) throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "SCOPE");
        return rows[0];
    }

    private static string String(IReadOnlyDictionary<string, object?> row, string field) =>
        row.TryGetValue(field, out var value) && value is string text && text.Length > 0
            ? text : throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "SCOPE");

    private static int Integer(IReadOnlyDictionary<string, object?> row, string field)
    {
        if (!row.TryGetValue(field, out var value) || value is null)
            throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "SCOPE");
        try { return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch { throw new SecurityCatalogException("SECURITY_EVIDENCE_INCOMPLETE", "SCOPE"); }
    }
}
