using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DatabaseReleaseQualification;

public sealed class SqlScriptAnalyzer
{
    public ScriptAnalysis Analyze(string scriptRole, string sql, SchemaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(snapshot);

        var result = new ScriptAnalysis { ScriptRole = scriptRole };
        var parser = new TSql180Parser(initialQuotedIdentifiers: true, SqlEngineType.Standalone);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var parseErrors);
        foreach (var error in parseErrors.OrderBy(error => error.Line).ThenBy(error => error.Column).ThenBy(error => error.Number))
        {
            result.ParseErrors.Add($"SQL_PARSE_ERROR_LINE_{error.Line}_COLUMN_{error.Column}_NUMBER_{error.Number}");
        }

        if (parseErrors.Count > 0)
        {
            Degrade(result, AnalysisConfidence.Insufficient, "AST_PARSE_FAILED");
        }

        if (fragment is TSqlScript script)
        {
            foreach (var statement in script.Batches.SelectMany(batch => batch.Statements))
            {
                ProcessStatement(statement, result);
            }
        }
        else
        {
            Degrade(result, AnalysisConfidence.Insufficient, "AST_ROOT_NOT_TSQL_SCRIPT");
        }

        EvaluateCoverage(snapshot, result);
        AddDependencyFindings(result, snapshot);
        result.ConfidenceReasons.Sort(StringComparer.Ordinal);
        result.UnknownStatementTypes.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void ProcessStatement(TSqlStatement statement, ScriptAnalysis result)
    {
        result.StatementCount++;
        switch (statement)
        {
            case BeginEndAtomicBlockStatement atomic:
                ProcessStatements(atomic.StatementList?.Statements, result);
                return;
            case BeginEndBlockStatement block:
                ProcessStatements(block.StatementList?.Statements, result);
                return;
            case IfStatement conditional:
                ProcessStatement(conditional.ThenStatement, result);
                if (conditional.ElseStatement is not null) ProcessStatement(conditional.ElseStatement, result);
                return;
            case WhileStatement loop:
                ProcessStatement(loop.Statement, result);
                return;
            case TryCatchStatement tryCatch:
                ProcessStatements(tryCatch.TryStatements?.Statements, result);
                ProcessStatements(tryCatch.CatchStatements?.Statements, result);
                return;

            case CreateTableStatement createTable:
                AddTableCreation(createTable, result);
                return;
            case AlterTableAddTableElementStatement addTableElement:
                AddTableElements(addTableElement.SchemaObjectName, addTableElement.Definition, result, addTableElement.GetType().Name);
                return;
            case AlterTableAlterColumnStatement alterColumn:
                Add(result, "ALTER_COLUMN", alterColumn.SchemaObjectName, alterColumn.GetType().Name,
                    column: alterColumn.ColumnIdentifier?.Value, sensitive: true, schemaMutation: true, affectsData: true);
                return;
            case AlterTableDropTableElementStatement dropTableElement:
                AddDroppedTableElements(dropTableElement, result);
                return;
            case AlterTableConstraintModificationStatement constraintModification:
                foreach (var constraint in constraintModification.ConstraintNames)
                {
                    Add(result, "ALTER_CONSTRAINT", constraintModification.SchemaObjectName, constraintModification.GetType().Name,
                        relatedObject: constraint.Value, sensitive: true, schemaMutation: true);
                }
                return;
            case AlterTableAlterIndexStatement alterTableIndex:
                Add(result, "ALTER_INDEX", alterTableIndex.SchemaObjectName, alterTableIndex.GetType().Name,
                    sensitive: true, schemaMutation: true);
                return;
            case AlterTableStatement unsupportedAlterTable:
                AddUnknown(result, unsupportedAlterTable, "UNSUPPORTED_ALTER_TABLE_FORM");
                return;
            case DropTableStatement dropTable:
                foreach (var table in dropTable.Objects)
                {
                    Add(result, "DROP_TABLE", table, dropTable.GetType().Name,
                        destructive: true, sensitive: true, schemaMutation: true, affectsData: true);
                }
                return;

            case CreateIndexStatement createIndex:
                AddIndex(result, "CREATE_INDEX", createIndex.OnName, createIndex.Name, createIndex.GetType().Name);
                return;
            case CreateXmlIndexStatement createXmlIndex:
                AddIndex(result, "CREATE_INDEX", createXmlIndex.OnName, createXmlIndex.Name, createXmlIndex.GetType().Name);
                return;
            case CreateSelectiveXmlIndexStatement selectiveXmlIndex:
                AddIndex(result, "CREATE_INDEX", selectiveXmlIndex.OnName, selectiveXmlIndex.Name, selectiveXmlIndex.GetType().Name);
                return;
            case CreateJsonIndexStatement jsonIndex:
                AddIndex(result, "CREATE_INDEX", jsonIndex.OnName, jsonIndex.Name, jsonIndex.GetType().Name);
                return;
            case CreateVectorIndexStatement vectorIndex:
                AddIndex(result, "CREATE_INDEX", vectorIndex.OnName, vectorIndex.Name, vectorIndex.GetType().Name);
                return;
            case CreateColumnStoreIndexStatement columnStoreIndex:
                AddIndex(result, "CREATE_INDEX", columnStoreIndex.OnName, columnStoreIndex.Name, columnStoreIndex.GetType().Name);
                return;
            case CreateSpatialIndexStatement spatialIndex:
                AddIndex(result, "CREATE_INDEX", spatialIndex.Object, spatialIndex.Name, spatialIndex.GetType().Name);
                return;
            case DropIndexStatement dropIndex:
                foreach (var clause in dropIndex.DropIndexClauses)
                {
                    if (clause is DropIndexClause modernClause)
                    {
                        Add(result, "DROP_INDEX", modernClause.Object, dropIndex.GetType().Name,
                            relatedObject: modernClause.Index?.Value, sensitive: true, schemaMutation: true);
                    }
                    else
                    {
                        AddUnknown(result, dropIndex, "BACKWARDS_COMPATIBLE_DROP_INDEX_NOT_RESOLVABLE");
                    }
                }
                return;
            case AlterIndexStatement alterIndex:
                Add(result, alterIndex.AlterIndexType == AlterIndexType.Rebuild ? "REBUILD_INDEX" : "ALTER_INDEX",
                    alterIndex.OnName, alterIndex.GetType().Name, relatedObject: alterIndex.Name?.Value,
                    sensitive: true, schemaMutation: true);
                return;

            case CreateViewStatement createView:
                Add(result, "CREATE_VIEW", createView.SchemaObjectName, createView.GetType().Name, schemaMutation: true);
                return;
            case CreateOrAlterViewStatement createOrAlterView:
                Add(result, "CREATE_OR_ALTER_VIEW", createOrAlterView.SchemaObjectName, createOrAlterView.GetType().Name,
                    sensitive: true, schemaMutation: true);
                return;
            case AlterViewStatement alterView:
                Add(result, "ALTER_VIEW", alterView.SchemaObjectName, alterView.GetType().Name, sensitive: true, schemaMutation: true);
                return;
            case DropViewStatement dropView:
                foreach (var view in dropView.Objects)
                {
                    Add(result, "DROP_VIEW", view, dropView.GetType().Name, destructive: true, sensitive: true, schemaMutation: true);
                }
                return;

            case CreateTriggerStatement createTrigger:
                AddTrigger(result, "CREATE_TRIGGER", createTrigger.Name, createTrigger.TriggerObject, createTrigger.GetType().Name, false);
                return;
            case CreateOrAlterTriggerStatement createOrAlterTrigger:
                AddTrigger(result, "CREATE_OR_ALTER_TRIGGER", createOrAlterTrigger.Name, createOrAlterTrigger.TriggerObject,
                    createOrAlterTrigger.GetType().Name, true);
                return;
            case AlterTriggerStatement alterTrigger:
                AddTrigger(result, "ALTER_TRIGGER", alterTrigger.Name, alterTrigger.TriggerObject, alterTrigger.GetType().Name, true);
                return;
            case DropTriggerStatement dropTrigger:
                foreach (var trigger in dropTrigger.Objects)
                {
                    Add(result, "DROP_TRIGGER", trigger, dropTrigger.GetType().Name,
                        destructive: true, sensitive: true, schemaMutation: true);
                }
                return;

            case InsertStatement insert:
                AddDataOperation(result, "INSERT_DATA", insert.InsertSpecification.Target, null, insert.GetType().Name, false, false);
                return;
            case InsertBulkStatement bulkInsert:
                Add(result, "INSERT_DATA", bulkInsert.To, bulkInsert.GetType().Name,
                    sensitive: true, dataMutation: true, targetResolvedOverride: IsFullyQualified(bulkInsert.To));
                return;
            case UpdateStatement update:
                AddDataOperation(result, "UPDATE_DATA", update.UpdateSpecification.Target, update.UpdateSpecification.FromClause,
                    update.GetType().Name, false, true);
                return;
            case DeleteStatement delete:
                AddDataOperation(result, "DELETE_DATA", delete.DeleteSpecification.Target, delete.DeleteSpecification.FromClause,
                    delete.GetType().Name, true, true);
                return;
            case MergeStatement merge:
                AddDataOperation(result, "MERGE_DATA", merge.MergeSpecification.Target, null,
                    merge.GetType().Name, true, true);
                return;
            case TruncateTableStatement truncate:
                Add(result, "TRUNCATE_TABLE", truncate.TableName, truncate.GetType().Name,
                    destructive: true, sensitive: true, dataMutation: true, affectsData: true);
                return;
            case ExecuteStatement execute:
                AddExecute(result, execute);
                return;

            case SelectStatement select:
                AddSelectIntoIfPresent(select, result);
                return;
            case DeclareVariableStatement:
            case SetVariableStatement:
            case SetOnOffStatement:
            case PrintStatement:
            case BeginTransactionStatement:
            case CommitTransactionStatement:
            case RollbackTransactionStatement:
            case SaveTransactionStatement:
            case ThrowStatement:
            case RaiseErrorStatement:
            case ReturnStatement:
                return;
            default:
                AddUnknown(result, statement, "AST_STATEMENT_NOT_SUPPORTED");
                return;
        }
    }

    private static void ProcessStatements(IEnumerable<TSqlStatement>? statements, ScriptAnalysis result)
    {
        if (statements is null) return;
        foreach (var statement in statements) ProcessStatement(statement, result);
    }

    private static void AddTableCreation(CreateTableStatement statement, ScriptAnalysis result)
    {
        Add(result, "CREATE_TABLE", statement.SchemaObjectName, statement.GetType().Name, schemaMutation: true);
        AddTableElements(statement.SchemaObjectName, statement.Definition, result, statement.GetType().Name);
    }

    private static void AddTableElements(
        SchemaObjectName table,
        TableDefinition? definition,
        ScriptAnalysis result,
        string astNodeType)
    {
        if (definition is null)
        {
            Degrade(result, AnalysisConfidence.Insufficient, "TABLE_DEFINITION_NOT_AVAILABLE");
            return;
        }

        foreach (var column in definition.ColumnDefinitions)
        {
            Add(result, "ADD_COLUMN", table, astNodeType, column: column.ColumnIdentifier?.Value, schemaMutation: true);
            foreach (var constraint in column.Constraints.Where(constraint =>
                         constraint is UniqueConstraintDefinition or ForeignKeyConstraintDefinition or CheckConstraintDefinition))
            {
                Add(result, "ADD_CONSTRAINT", table, constraint.GetType().Name,
                    column: column.ColumnIdentifier?.Value,
                    relatedObject: constraint.ConstraintIdentifier?.Value ?? constraint.GetType().Name,
                    sensitive: true, schemaMutation: true);
            }
            if (column.DefaultConstraint is not null)
            {
                Add(result, "ADD_CONSTRAINT", table, column.DefaultConstraint.GetType().Name,
                    column: column.ColumnIdentifier?.Value,
                    relatedObject: column.DefaultConstraint.ConstraintIdentifier?.Value ?? "DEFAULT",
                    sensitive: true, schemaMutation: true);
            }
        }

        foreach (var constraint in definition.TableConstraints)
        {
            Add(result, "ADD_CONSTRAINT", table, constraint.GetType().Name,
                relatedObject: constraint.ConstraintIdentifier?.Value ?? constraint.GetType().Name,
                sensitive: true, schemaMutation: true);
        }

        foreach (var index in definition.Indexes)
        {
            Add(result, "CREATE_INDEX", table, index.GetType().Name,
                relatedObject: index.Name?.Value ?? "UNNAMED_INDEX", sensitive: true, schemaMutation: true);
        }
    }

    private static void AddDroppedTableElements(AlterTableDropTableElementStatement statement, ScriptAnalysis result)
    {
        foreach (var element in statement.AlterTableDropTableElements)
        {
            if (element.TableElementType == TableElementType.Column)
            {
                Add(result, "DROP_COLUMN", statement.SchemaObjectName, statement.GetType().Name,
                    column: element.Name?.Value, destructive: true, sensitive: true, schemaMutation: true, affectsData: true);
            }
            else if (element.TableElementType == TableElementType.Constraint)
            {
                Add(result, "DROP_CONSTRAINT", statement.SchemaObjectName, statement.GetType().Name,
                    relatedObject: element.Name?.Value, sensitive: true, schemaMutation: true);
            }
            else
            {
                AddUnknown(result, statement, $"UNSUPPORTED_DROP_TABLE_ELEMENT_{element.TableElementType}");
            }
        }
    }

    private static void AddIndex(
        ScriptAnalysis result,
        string operation,
        SchemaObjectName table,
        Identifier? index,
        string astNodeType) =>
        Add(result, operation, table, astNodeType, relatedObject: index?.Value,
            sensitive: true, schemaMutation: true);

    private static void AddTrigger(
        ScriptAnalysis result,
        string operation,
        SchemaObjectName triggerName,
        TriggerObject triggerObject,
        string astNodeType,
        bool sensitive)
    {
        var target = triggerObject?.Name;
        if (target is null)
        {
            Add(result, operation, triggerName, astNodeType, relatedObject: Name(triggerName),
                sensitive: true, schemaMutation: true, targetResolvedOverride: false);
            Degrade(result, AnalysisConfidence.Partial, "TRIGGER_TARGET_NOT_DATABASE_OBJECT");
            return;
        }

        Add(result, operation, target, astNodeType, relatedObject: Name(triggerName),
            sensitive: sensitive, schemaMutation: true);
    }

    private static void AddDataOperation(
        ScriptAnalysis result,
        string operation,
        TableReference target,
        FromClause? fromClause,
        string astNodeType,
        bool destructive,
        bool potentialDataLoss)
    {
        var resolved = ResolveTarget(target, fromClause);
        result.Operations.Add(new ScriptOperation
        {
            Operation = operation,
            AstNodeType = astNodeType,
            Schema = resolved.Schema,
            Object = resolved.Object,
            IsDestructive = destructive,
            IsSensitive = true,
            IsDataMutation = true,
            HasPotentialDataLoss = potentialDataLoss,
            TargetResolved = resolved.Resolved
        });
        if (!resolved.Resolved) Degrade(result, AnalysisConfidence.Partial, resolved.Reason);
    }

    private static void AddExecute(ScriptAnalysis result, ExecuteStatement statement)
    {
        var entity = statement.ExecuteSpecification?.ExecutableEntity;
        var dynamic = entity is ExecutableStringList
            || entity is ExecutableProcedureReference procedure
                && string.Equals(procedure.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value,
                    "sp_executesql", StringComparison.OrdinalIgnoreCase);
        var target = entity is ExecutableProcedureReference procedureReference
            ? Name(procedureReference.ProcedureReference?.ProcedureReference?.Name)
            : dynamic ? "DYNAMIC_SQL" : "UNRESOLVED_EXECUTABLE";

        result.Operations.Add(new ScriptOperation
        {
            Operation = dynamic ? "EXECUTE_DYNAMIC_SQL" : "EXECUTE",
            AstNodeType = statement.GetType().Name,
            Schema = "",
            Object = target,
            IsSensitive = true,
            IsDataMutation = true,
            HasPotentialDataLoss = true,
            TargetResolved = false
        });
        Degrade(result, AnalysisConfidence.Insufficient, dynamic ? "DYNAMIC_SQL_NOT_ANALYZABLE" : "EXECUTED_CODE_EFFECTS_NOT_ANALYZABLE");
    }

    private static void AddSelectIntoIfPresent(SelectStatement statement, ScriptAnalysis result)
    {
        if (statement.Into is not null)
        {
            Add(result, "SELECT_INTO", statement.Into, statement.GetType().Name,
                sensitive: true, schemaMutation: true, dataMutation: true);
        }
    }

    private static void AddUnknown(ScriptAnalysis result, TSqlStatement statement, string reason)
    {
        var type = statement.GetType().Name;
        result.UnknownStatementTypes.Add(type);
        result.Operations.Add(new ScriptOperation
        {
            Operation = "UNKNOWN_SQL",
            AstNodeType = type,
            Schema = "",
            Object = type,
            IsSensitive = true,
            IsDataMutation = true,
            HasPotentialDataLoss = true,
            TargetResolved = false
        });
        Degrade(result, AnalysisConfidence.Insufficient, reason);
    }

    private static void Add(
        ScriptAnalysis result,
        string operation,
        SchemaObjectName? target,
        string astNodeType,
        string? column = null,
        string? relatedObject = null,
        bool destructive = false,
        bool sensitive = false,
        bool schemaMutation = false,
        bool dataMutation = false,
        bool affectsData = false,
        bool? targetResolvedOverride = null)
    {
        var resolved = Resolve(target);
        var targetResolved = targetResolvedOverride ?? resolved.Resolved;
        result.Operations.Add(new ScriptOperation
        {
            Operation = operation,
            AstNodeType = astNodeType,
            Schema = resolved.Schema,
            Object = resolved.Object,
            Column = column,
            RelatedObject = relatedObject,
            IsDestructive = destructive,
            IsSensitive = sensitive,
            IsSchemaMutation = schemaMutation,
            IsDataMutation = dataMutation,
            HasPotentialDataLoss = affectsData,
            TargetResolved = targetResolved
        });
        if (!targetResolved) Degrade(result, AnalysisConfidence.Partial, resolved.Reason);
    }

    private static TargetResolution ResolveTarget(TableReference? target, FromClause? fromClause)
    {
        if (target is NamedTableReference named)
        {
            if (named.SchemaObject?.Identifiers.Count == 1 && fromClause is not null)
            {
                var alias = named.SchemaObject.BaseIdentifier?.Value;
                var collector = new NamedTableCollector();
                fromClause.Accept(collector);
                var match = collector.Tables.FirstOrDefault(table =>
                    string.Equals(table.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return Resolve(match.SchemaObject);
            }
            return Resolve(named.SchemaObject);
        }

        return new TargetResolution("", target?.GetType().Name ?? "UNKNOWN_TARGET", false, "DML_TARGET_NOT_RESOLVABLE");
    }

    private static TargetResolution Resolve(SchemaObjectName? name)
    {
        if (name?.BaseIdentifier is null)
        {
            return new TargetResolution("", "UNKNOWN_TARGET", false, "TARGET_NOT_RESOLVABLE");
        }

        var schema = name.SchemaIdentifier?.Value ?? "dbo";
        var crossDatabase = name.ServerIdentifier is not null || name.DatabaseIdentifier is not null;
        var schemaExplicit = name.SchemaIdentifier is not null;
        var resolved = schemaExplicit && !crossDatabase;
        var reason = crossDatabase ? "CROSS_DATABASE_TARGET_NOT_CERTIFIED" : "TARGET_SCHEMA_IMPLICIT";
        return new TargetResolution(schema, name.BaseIdentifier.Value, resolved, reason);
    }

    private static bool IsFullyQualified(SchemaObjectName? name) => Resolve(name).Resolved;

    private static string Name(SchemaObjectName? name)
    {
        if (name is null) return "UNKNOWN";
        return string.Join(".", name.Identifiers.Select(identifier => identifier.Value));
    }

    private static void EvaluateCoverage(SchemaSnapshot snapshot, ScriptAnalysis result)
    {
        if (snapshot.UnsupportedSchemaFeatures.Count == 0) return;

        var targetsUnsupportedObject = result.Operations.Any(operation => snapshot.Objects.Any(item =>
            item.Kind == "unsupported-schema-feature"
            && string.Equals(item.Schema, operation.Schema, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(item.Name, operation.Object, StringComparison.OrdinalIgnoreCase)
                || item.Name.StartsWith(operation.Object + ".", StringComparison.OrdinalIgnoreCase))));
        Degrade(result,
            targetsUnsupportedObject ? AnalysisConfidence.Insufficient : AnalysisConfidence.Partial,
            targetsUnsupportedObject
                ? "TARGET_USES_UNSUPPORTED_SCHEMA_FEATURE"
                : "CERTIFIED_SCHEMA_HAS_UNSUPPORTED_FEATURES");
    }

    private static void Degrade(ScriptAnalysis result, AnalysisConfidence confidence, string reason)
    {
        if (confidence > result.Confidence) result.Confidence = confidence;
        if (!result.ConfidenceReasons.Contains(reason, StringComparer.Ordinal)) result.ConfidenceReasons.Add(reason);
    }

    private static void AddDependencyFindings(ScriptAnalysis analysis, SchemaSnapshot snapshot)
    {
        foreach (var operation in analysis.Operations.Where(item => item.Column is not null || item.Operation is "DROP_TABLE"))
        {
            foreach (var dependency in DependenciesFor(operation, snapshot).DistinctBy(item => item.Identity))
            {
                var dependentName = DependencyName(dependency);
                var handled = analysis.Operations.Any(candidate =>
                    SameTable(candidate, operation)
                    && candidate.RelatedObject is not null
                    && string.Equals(candidate.RelatedObject, dependentName, StringComparison.OrdinalIgnoreCase)
                    && candidate.Operation is "DROP_INDEX" or "REBUILD_INDEX" or "DROP_CONSTRAINT");

                analysis.Findings.Add(new DependencyFinding
                {
                    Severity = handled ? FindingSeverity.Info : SeverityFor(operation, dependency),
                    Object = $"{operation.Schema}.{operation.Object}{(operation.Column is null ? "" : "." + operation.Column)}",
                    Operation = operation.Operation,
                    DependentObject = dependentName,
                    DependencyType = dependency.Kind,
                    Source = "certified-schema",
                    Reason = handled
                        ? "La release referencia explícitamente el objeto dependiente; su recreación debe validarse mediante fingerprint."
                        : "La operación toca un objeto utilizado por una dependencia física certificada que la release no maneja explícitamente."
                });
            }
        }
    }

    private static IEnumerable<SchemaObject> DependenciesFor(ScriptOperation operation, SchemaSnapshot snapshot)
    {
        foreach (var item in snapshot.Objects)
        {
            var sameParent = string.Equals(item.Schema, operation.Schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Parent, operation.Object, StringComparison.OrdinalIgnoreCase);

            if (operation.Operation == "DROP_TABLE" && sameParent && item.Kind != "column")
            {
                yield return item;
                continue;
            }

            if (!sameParent || operation.Column is null) continue;
            if (PropertyEquals(item, "column", operation.Column)
                || PropertyEquals(item, "referencingColumn", operation.Column)
                || PropertyEquals(item, "referencedColumn", operation.Column))
            {
                yield return item;
            }
        }

        foreach (var foreignKey in snapshot.Objects.Where(item => item.Kind == "foreign-key-column"))
        {
            if (PropertyEquals(foreignKey, "referencedSchema", operation.Schema)
                && PropertyEquals(foreignKey, "referencedTable", operation.Object)
                && (operation.Column is null || PropertyEquals(foreignKey, "referencedColumn", operation.Column)))
            {
                yield return foreignKey;
            }
        }

        foreach (var dependency in snapshot.Objects.Where(item => item.Kind == "schema-dependency"))
        {
            if (PropertyEquals(dependency, "referencedSchema", operation.Schema)
                && PropertyEquals(dependency, "referencedEntity", operation.Object)
                && (operation.Column is null || PropertyEquals(dependency, "referencedColumn", operation.Column)))
            {
                yield return dependency;
            }
        }
    }

    private static FindingSeverity SeverityFor(ScriptOperation operation, SchemaObject dependency) =>
        operation.IsDestructive || dependency.Kind is "index-column" or "key-constraint-column" or "foreign-key-column" or "schema-dependency"
            ? FindingSeverity.Blocking
            : FindingSeverity.Warning;

    private static string DependencyName(SchemaObject item)
    {
        foreach (var key in new[] { "index", "constraint", "foreignKey" })
        {
            if (item.Properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return $"{item.Schema}.{item.Parent}.{item.Name}";
    }

    private static bool SameTable(ScriptOperation left, ScriptOperation right) =>
        string.Equals(left.Schema, right.Schema, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Object, right.Object, StringComparison.OrdinalIgnoreCase);

    private static bool PropertyEquals(SchemaObject item, string key, string expected) =>
        item.Properties.TryGetValue(key, out var value) && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private sealed record TargetResolution(string Schema, string Object, bool Resolved, string Reason);

    private sealed class NamedTableCollector : TSqlFragmentVisitor
    {
        public List<NamedTableReference> Tables { get; } = [];
        public override void ExplicitVisit(NamedTableReference node) => Tables.Add(node);
    }

}
