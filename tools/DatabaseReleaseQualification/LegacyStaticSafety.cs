using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DatabaseReleaseQualification;

public sealed record LegacySafetyObject(string Schema, string Name, string Kind,
    bool IsSystem, bool IsSpecialTable, bool IsSynonym, bool HasDmlTrigger,
    bool HasAnyTrigger, bool HasInboundCascade, bool HasRowLevelSecurity,
    bool HasUnresolvedDependency, bool HasUnsafeExpression);

public sealed record LegacyScopeSafetySnapshotV1(int ContractVersion, bool Complete,
    string ServerInstance, string DatabaseName, bool DatabaseDdlTriggersComplete,
    bool ServerDdlTriggersComplete, bool HasEnabledDdlTrigger,
    IReadOnlyList<LegacySafetyObject> Objects, string Sha256);

public interface ILegacyScopeSafetySource
{
    Task<LegacyScopeSafetySnapshotV1> CaptureAsync(IReadOnlyList<RecoverySecuritySecurable> scope,
        CancellationToken token);
}

public sealed record LegacyStaticSafetyEvidenceV1(
    int ContractVersion, string PolicyVersion, string PackageIdentity, string ForwardHash,
    string RollbackHash, string ParserVersion, string State, IReadOnlyList<string> ReasonCodes,
    ScriptAnalysis ForwardAnalysis, ScriptAnalysis RollbackAnalysis, RecoveryImpact Impact,
    string? ScopeSafetyHash, string EvidenceHash);

public sealed class LegacyStaticSafety
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions RequiredNulls = new(JsonDefaults.Compact) {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    public static string Serialize(LegacyStaticSafetyEvidenceV1 evidence) =>
        JsonSerializer.Serialize(evidence, RequiredNulls);

    public static string CalculateHash(LegacyStaticSafetyEvidenceV1 result) =>
        Hashing.Sha256(JsonSerializer.Serialize(new {
            result.ContractVersion, result.PolicyVersion, result.PackageIdentity,
            result.ForwardHash, result.RollbackHash, result.ParserVersion, result.State,
            result.ReasonCodes, result.ForwardAnalysis, result.RollbackAnalysis,
            result.Impact, result.ScopeSafetyHash
        }, RequiredNulls));

    internal static bool CatalogExpressionSafe(string? definition, bool predicate)
    {
        if (string.IsNullOrWhiteSpace(definition) || definition.Contains('$')
            || definition.Contains('\0')) return false;
        var sql = predicate ? "SELECT 1 WHERE " + definition : "SELECT " + definition;
        var parsed = SqlScriptAnalyzer.Parse(sql);
        if (parsed.Errors.Count != 0 || parsed.Fragment is not TSqlScript script
            || script.Batches.Count != 1 || script.Batches[0].Statements.Count != 1
            || script.Batches[0].Statements[0] is not SelectStatement) return false;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var references = new HashSet<RecoverySecuritySecurable>();
        InspectAst(parsed.Fragment, reasons, references,
            new HashSet<TSqlFragment>(ReferenceEqualityComparer.Instance));
        if (references.Count != 0 || reasons.Count != 0) return false;
        var visited = new HashSet<TSqlFragment>(ReferenceEqualityComparer.Instance);
        return !ContainsSubqueryOrExternal(parsed.Fragment, visited);
    }

    private static bool ContainsSubqueryOrExternal(TSqlFragment node, HashSet<TSqlFragment> visited)
    {
        if (!visited.Add(node)) return false;
        if (node.GetType().Name is "ScalarSubquery" or "QueryDerivedTable"
            or "ExecuteStatement" or "VariableReference" or "GlobalVariableExpression")
            return true;
        foreach (var property in node.GetType().GetProperties(System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.Name is "ScriptTokenStream" or "FirstTokenIndex" or "LastTokenIndex")
                continue;
            object? value;
            try { value = property.GetValue(node); }
            catch { continue; }
            if (value is TSqlFragment child && ContainsSubqueryOrExternal(child, visited)) return true;
            if (value is System.Collections.IEnumerable items && value is not string)
                foreach (var item in items)
                    if (item is TSqlFragment fragment && ContainsSubqueryOrExternal(fragment, visited))
                        return true;
        }
        return false;
    }
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase) {
        "ABS", "CEILING", "FLOOR", "ROUND", "LEN", "DATALENGTH", "UPPER", "LOWER",
        "LTRIM", "RTRIM", "TRIM", "SUBSTRING", "LEFT", "RIGHT", "REPLACE", "CONCAT",
        "COUNT", "COUNT_BIG", "SUM", "MIN", "MAX", "AVG"
    };

    public async Task<LegacyStaticSafetyEvidenceV1> EvaluateAsync(LegacyFrozenPackage package,
        SchemaSnapshot observedSnapshot, ILegacyScopeSafetySource source,
        CancellationToken token = default, LegacyBindingV1? expectedBinding = null)
    {
        package.Verify();
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var forward = Analyze("forward", package.ForwardBytes, observedSnapshot, reasons);
        var rollback = Analyze("rollback", package.RollbackBytes, observedSnapshot, reasons);
        var impact = RecoveryImpact.Derive(forward.Analysis, rollback.Analysis);
        if (!impact.Complete) reasons.Add("RECOVERY_IMPACT_INCOMPLETE");
        string? scopeHash = null;
        if (reasons.Count == 0)
        {
            var securables = forward.Analysis.Operations.Concat(rollback.Analysis.Operations)
                .Where(x => x.TargetResolved && x.Object.Length > 0)
                .Select(x => new RecoverySecuritySecurable("OBJECT", x.Schema, x.Object))
                .Concat(forward.References).Concat(rollback.References)
                .Distinct().OrderBy(x => x.Schema, StringComparer.Ordinal)
                .ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
            try
            {
                var metadata = await source.CaptureAsync(securables, token);
                if (expectedBinding is not null
                    && (metadata.DatabaseName != expectedBinding.DatabaseName
                        || expectedBinding.ServerMatchPolicy == "ALLOW_LIST"
                            && !expectedBinding.AllowedServerInstances.Contains(
                                metadata.ServerInstance, StringComparer.OrdinalIgnoreCase)))
                    throw new LegacyContractException("TARGET_IDENTITY_MISMATCH");
                VerifyMetadata(metadata, securables, forward.References.Concat(rollback.References),
                    forward.Analysis, rollback.Analysis);
                scopeHash = metadata.Sha256;
            }
            catch (OperationCanceledException) { throw; }
            catch (LegacyContractException exception) { reasons.Add(exception.Code); }
            catch { reasons.Add("SQL_INDIRECT_EFFECT_UNPROVEN"); }
        }
        var state = reasons.Count == 0 ? "PASS" : "BLOCKED";
        var artifact = package.Evidence;
        var result = new LegacyStaticSafetyEvidenceV1(1, "LEGACY_STATIC_SAFETY_V1",
            artifact.PackageIdentity, artifact.Forward.Sha256, artifact.Rollback.Sha256,
            "180.102.0", state, reasons.ToArray(), forward.Analysis, rollback.Analysis,
            impact, scopeHash, "");
        var hash = CalculateHash(result);
        return result with { EvidenceHash = hash };
    }

    internal static async Task VerifyPhaseAsync(ReleaseScript script, SchemaSnapshot snapshot,
        ILegacyScopeSafetySource source, LegacyBindingV1 binding, CancellationToken token)
    {
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var current = Analyze(script.Role, script.Bytes, snapshot, reasons);
        if (reasons.Count != 0) throw new LegacyContractException("UNSUPPORTED_OPERATION_BEFORE_MUTATION");
        var scope = current.Analysis.Operations.Where(x => x.TargetResolved && x.Object.Length > 0)
            .Select(x => new RecoverySecuritySecurable("OBJECT", x.Schema, x.Object))
            .Concat(current.References).Distinct().ToArray();
        var metadata = await source.CaptureAsync(scope, token);
        if (metadata.DatabaseName != binding.DatabaseName || binding.ServerMatchPolicy == "ALLOW_LIST"
            && !binding.AllowedServerInstances.Contains(metadata.ServerInstance, StringComparer.OrdinalIgnoreCase))
            throw new LegacyContractException("TARGET_IDENTITY_MISMATCH");
        VerifyMetadata(metadata, scope, current.References, current.Analysis, current.Analysis, phaseOnly: true);
    }

    private static (ScriptAnalysis Analysis, IReadOnlyList<RecoverySecuritySecurable> References) Analyze(string role, byte[] bytes,
        SchemaSnapshot snapshot, ISet<string> reasons)
    {
        var content = bytes.AsSpan();
        if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
        string sql;
        try { sql = Utf8.GetString(content); }
        catch (DecoderFallbackException)
        {
            reasons.Add("ARTIFACT_ENCODING_INVALID");
            sql = "";
        }
        if (sql.Contains('\0')) reasons.Add("ARTIFACT_ENCODING_INVALID");
        var parsed = SqlScriptAnalyzer.Parse(sql);
        var analysis = new SqlScriptAnalyzer().AnalyzeParsed(role, parsed.Fragment, parsed.Errors, snapshot);
        if (parsed.Errors.Count != 0) reasons.Add("SQL_UNSUPPORTED");
        if (parsed.Fragment is not TSqlScript script || !script.Batches
            .SelectMany(x => x.Statements).Any(Effective))
            reasons.Add("SQL_NO_STATEMENTS");
        foreach (var token in parsed.Fragment.ScriptTokenStream ?? [])
        {
            var type = token.TokenType.ToString();
            if (type == "Go")
            {
                var line = sql.Split('\n').ElementAtOrDefault(token.Line - 1)?.Trim(' ', '\t', '\r');
                if (!string.Equals(line, "GO", StringComparison.OrdinalIgnoreCase))
                    reasons.Add("SQL_GO_UNSUPPORTED");
            }
            else if (!type.Contains("Comment", StringComparison.OrdinalIgnoreCase)
                     && token.Text?.Contains("$(", StringComparison.Ordinal) == true)
                reasons.Add("SQL_EXTERNAL_EXPANSION");
        }
        if (parsed.Fragment is TSqlScript root)
            foreach (var statement in root.Batches.SelectMany(x => x.Statements))
                CheckStatement(statement, reasons);
        var references = new HashSet<RecoverySecuritySecurable>();
        if (parsed.Fragment is TSqlScript astRoot)
            foreach (var statement in astRoot.Batches.SelectMany(x => x.Statements))
            {
                var names = LocalCteNames(statement, reasons);
                InspectAst(statement, reasons, references,
                    new HashSet<TSqlFragment>(ReferenceEqualityComparer.Instance), names);
            }
        if (analysis.Confidence != AnalysisConfidence.Complete || analysis.UnknownStatementTypes.Count != 0)
            reasons.Add("SQL_UNSUPPORTED");
        return (analysis, references.ToArray());
    }

    private static bool Effective(TSqlStatement statement) => statement switch {
        BeginEndBlockStatement block => block.StatementList?.Statements.Any(Effective) == true,
        TryCatchStatement compound => compound.TryStatements?.Statements.Any(Effective) == true
            || compound.CatchStatements?.Statements.Any(Effective) == true,
        IfStatement conditional => Effective(conditional.ThenStatement)
            || conditional.ElseStatement is not null && Effective(conditional.ElseStatement),
        _ => true
    };

    private static void CheckStatement(TSqlStatement statement, ISet<string> reasons)
    {
        if (statement is BeginEndBlockStatement block)
        {
            foreach (var nested in block.StatementList?.Statements ?? []) CheckStatement(nested, reasons);
            return;
        }
        if (statement is IfStatement condition)
        {
            if (ContainsNewObjectCreation(condition.ThenStatement)
                || condition.ElseStatement is not null
                    && ContainsNewObjectCreation(condition.ElseStatement))
                reasons.Add("SQL_INDIRECT_EFFECT_UNPROVEN");
            CheckStatement(condition.ThenStatement, reasons);
            if (condition.ElseStatement is not null) CheckStatement(condition.ElseStatement, reasons);
            return;
        }
        if (statement is TryCatchStatement tryCatch)
        {
            if ((tryCatch.TryStatements?.Statements ?? []).Any(ContainsNewObjectCreation)
                || (tryCatch.CatchStatements?.Statements ?? []).Any(ContainsNewObjectCreation))
                reasons.Add("SQL_INDIRECT_EFFECT_UNPROVEN");
            foreach (var nested in tryCatch.TryStatements?.Statements ?? []) CheckStatement(nested, reasons);
            foreach (var nested in tryCatch.CatchStatements?.Statements ?? []) CheckStatement(nested, reasons);
            if (tryCatch.CatchStatements?.Statements.LastOrDefault() is not ThrowStatement)
                reasons.Add("SQL_UNSUPPORTED");
            return;
        }
        if (statement is SecurityStatement security)
        {
            if (RecoverySecurityAnalysis.Analyze(security) is null)
                reasons.Add("SQL_SECURITY_UNSUPPORTED");
            foreach (var permission in security.Permissions)
            {
                var name = string.Join(" ", permission.Identifiers.Select(x => x.Value.ToUpperInvariant()));
                if (permission.Columns.Count > 0
                    && name is not ("SELECT" or "UPDATE" or "REFERENCES"))
                    reasons.Add("SQL_SECURITY_UNSUPPORTED");
            }
            return;
        }
        if (statement is BeginTransactionStatement or CommitTransactionStatement
            or RollbackTransactionStatement or SaveTransactionStatement)
        { reasons.Add("SQL_TRANSACTION_OWNERSHIP"); return; }
        if (statement is UseStatement) { reasons.Add("SQL_TARGET_SWITCH"); return; }
        if (statement is ExecuteStatement) { reasons.Add("SQL_EXECUTION_INDIRECTION"); return; }
        if (statement is SetOnOffStatement setOnOff)
        {
            var text = string.Concat(setOnOff.ScriptTokenStream?
                .Skip(setOnOff.FirstTokenIndex).Take(setOnOff.LastTokenIndex - setOnOff.FirstTokenIndex + 1)
                .Select(x => x.Text) ?? []);
            if (!text.Equals("SETNOCOUNTON", StringComparison.OrdinalIgnoreCase)
                && !text.Equals("SETNOCOUNTOFF", StringComparison.OrdinalIgnoreCase))
                reasons.Add("SQL_TRANSACTION_OWNERSHIP");
            return;
        }
        if (statement is CreateTriggerStatement or CreateOrAlterTriggerStatement
            or AlterTriggerStatement or DropTriggerStatement)
        { reasons.Add("SQL_INDIRECT_EFFECT_UNPROVEN"); return; }
        if (statement is CreateXmlIndexStatement or CreateSelectiveXmlIndexStatement
            or CreateJsonIndexStatement or CreateVectorIndexStatement
            or CreateColumnStoreIndexStatement or CreateSpatialIndexStatement)
        { reasons.Add("SQL_UNSUPPORTED"); return; }
        if (statement is not (CreateTableStatement or AlterTableStatement or DropTableStatement
            or CreateIndexStatement or DropIndexStatement or AlterIndexStatement
            or CreateViewStatement or CreateOrAlterViewStatement or AlterViewStatement or DropViewStatement
            or InsertStatement or UpdateStatement or DeleteStatement or MergeStatement
            or TruncateTableStatement or SelectStatement or DeclareVariableStatement
            or SetVariableStatement or ThrowStatement or PrintStatement))
            reasons.Add("SQL_UNSUPPORTED");
    }

    private static bool ContainsNewObjectCreation(TSqlStatement statement) => statement switch
    {
        CreateTableStatement or CreateViewStatement => true,
        SelectStatement { Into: not null } => true,
        BeginEndBlockStatement block => block.StatementList?.Statements
            .Any(ContainsNewObjectCreation) == true,
        IfStatement conditional => ContainsNewObjectCreation(conditional.ThenStatement)
            || conditional.ElseStatement is not null
                && ContainsNewObjectCreation(conditional.ElseStatement),
        TryCatchStatement compound => compound.TryStatements?.Statements
                .Any(ContainsNewObjectCreation) == true
            || compound.CatchStatements?.Statements.Any(ContainsNewObjectCreation) == true,
        _ => false
    };

    private static HashSet<string> LocalCteNames(TSqlStatement statement, ISet<string> reasons)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var with = statement.GetType().GetProperty("WithCtesAndXmlNamespaces")?.GetValue(statement);
        var ctes = with?.GetType().GetProperty("CommonTableExpressions")?.GetValue(with)
            as System.Collections.IEnumerable;
        if (ctes is null) return result;
        foreach (var cte in ctes)
        {
            var name = cte?.GetType().GetProperty("ExpressionName")?.GetValue(cte)
                as Identifier;
            if (name is null || !result.Add(name.Value)) reasons.Add("SQL_UNSUPPORTED");
            else if (cte is TSqlFragment fragment
                && ReferencesOnePartName(fragment, name.Value,
                    new HashSet<TSqlFragment>(ReferenceEqualityComparer.Instance)))
                reasons.Add("SQL_EXECUTION_INDIRECTION");
        }
        return result;
    }

    private static bool ReferencesOnePartName(TSqlFragment node, string name,
        HashSet<TSqlFragment> visited)
    {
        if (!visited.Add(node)) return false;
        if (node is NamedTableReference table
            && table.SchemaObject.Identifiers.Count == 1
            && table.SchemaObject.Identifiers[0].Value == name) return true;
        foreach (var property in node.GetType().GetProperties(System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0
                || property.Name is "ScriptTokenStream" or "FirstTokenIndex" or "LastTokenIndex") continue;
            object? value;
            try { value = property.GetValue(node); }
            catch { continue; }
            if (value is TSqlFragment child && ReferencesOnePartName(child, name, visited)) return true;
            if (value is System.Collections.IEnumerable items && value is not string)
                foreach (var item in items)
                    if (item is TSqlFragment fragment
                        && ReferencesOnePartName(fragment, name, visited)) return true;
        }
        return false;
    }

    private static void InspectAst(TSqlFragment node, ISet<string> reasons,
        ISet<RecoverySecuritySecurable> references, HashSet<TSqlFragment> visited,
        IReadOnlySet<string>? localCtes = null)
    {
        if (!visited.Add(node)) return;
        if (node is SchemaObjectName name && (name.Identifiers.Count > 2
            || name.Identifiers.Any(x => x.Value.StartsWith('#'))))
            reasons.Add("SQL_CROSS_DATABASE");
        if (node is NamedTableReference table)
        {
            if (table.SchemaObject.Identifiers.Count == 1
                && localCtes?.Contains(table.SchemaObject.Identifiers[0].Value) == true)
            { /* A CTE name is a scoped query symbol, never a physical object. */ }
            else if (table.SchemaObject.Identifiers.Count != 2)
                reasons.Add("SQL_CROSS_DATABASE");
            else
                references.Add(new("OBJECT", table.SchemaObject.Identifiers[0].Value,
                    table.SchemaObject.Identifiers[1].Value));
        }
        else if (node is TableReference reference && reference.GetType().Name is
            "OpenQueryTableReference" or "OpenRowsetTableReference" or "AdHocTableReference"
                or "VariableTableReference" or "SchemaObjectFunctionTableReference")
            reasons.Add("SQL_EXECUTION_INDIRECTION");
        if (node is FunctionCall call
            && (call.CallTarget is not null || !Functions.Contains(call.FunctionName.Value)))
            reasons.Add("SQL_EXECUTION_INDIRECTION");
        if (node.GetType().Name is "NextValueForExpression" or "VariableMethodCallTableReference")
            reasons.Add("SQL_EXECUTION_INDIRECTION");
        if (node is ForeignKeyConstraintDefinition)
            reasons.Add("SQL_INDIRECT_EFFECT_UNPROVEN");
        foreach (var property in node.GetType().GetProperties(System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.Name is "ScriptTokenStream" or "FirstTokenIndex" or "LastTokenIndex")
                continue;
            object? value;
            try { value = property.GetValue(node); }
            catch { continue; }
            if (value is TSqlFragment child) InspectAst(child, reasons, references, visited, localCtes);
            else if (value is System.Collections.IEnumerable items && value is not string)
                foreach (var item in items)
                    if (item is TSqlFragment fragment) InspectAst(fragment, reasons, references, visited, localCtes);
        }
    }

    private static void VerifyMetadata(LegacyScopeSafetySnapshotV1 metadata,
        IReadOnlyList<RecoverySecuritySecurable> scope,
        IEnumerable<RecoverySecuritySecurable> references,
        ScriptAnalysis forward, ScriptAnalysis rollback, bool phaseOnly = false)
    {
        var expectedHash = Hashing.Sha256(JsonSerializer.Serialize(new {
            metadata.ContractVersion, metadata.Complete, metadata.ServerInstance,
            metadata.DatabaseName, metadata.DatabaseDdlTriggersComplete,
            metadata.ServerDdlTriggersComplete, metadata.HasEnabledDdlTrigger,
            metadata.Objects
        }, JsonDefaults.Compact));
        if (metadata.ContractVersion != 1 || !metadata.Complete
            || metadata.Sha256 != expectedHash
            || scope.Count != metadata.Objects.Count)
            throw new LegacyContractException("SQL_INDIRECT_EFFECT_UNPROVEN");
        var all = forward.Operations.Concat(rollback.Operations).ToArray();
        var readReferences = references.ToHashSet();
        foreach (var objectScope in scope)
        {
            var item = metadata.Objects.SingleOrDefault(x => x.Schema == objectScope.Schema
                && x.Name == objectScope.Name);
            if (item is null || item.Kind == "UNSUPPORTED"
                || item.IsSystem || item.IsSpecialTable || item.IsSynonym
                || item.HasRowLevelSecurity || item.HasUnresolvedDependency || item.HasUnsafeExpression)
                throw new LegacyContractException("SQL_INDIRECT_EFFECT_UNPROVEN");
            if (item.Kind == "ABSENT")
            {
                if (readReferences.Contains(objectScope)
                    || !(phaseOnly ? forward.Operations.Any(x => x.Schema == objectScope.Schema
                        && x.Object == objectScope.Name && x.Operation is "CREATE_TABLE" or "CREATE_VIEW" or "SELECT_INTO")
                        : CompatibleNewObject(objectScope, forward, rollback))
                    || item.Schema is "sys" or "INFORMATION_SCHEMA")
                    throw new LegacyContractException("SECURABLE_ABSENCE_UNPROVEN");
                continue;
            }
            if (readReferences.Contains(objectScope) && item.Kind != "TABLE")
                throw new LegacyContractException("SQL_INDIRECT_EFFECT_UNPROVEN");
            var operations = all.Where(x => x.Schema == item.Schema && x.Object == item.Name).ToArray();
            if (forward.Operations.Any(x => x.Schema == item.Schema && x.Object == item.Name
                && x.Operation is "CREATE_TABLE" or "CREATE_VIEW" or "SELECT_INTO"))
                throw new LegacyContractException("SECURABLE_COLLISION");
            if (operations.Any(x => x.Operation == "DATABASE_SECURITY"
                && x.SecuritySecurable?.Kind == "OBJECT"
                && (x.SecurityPermissions.Any(permission =>
                    !ObjectPermissionCompatible(item.Kind, permission))
                    || x.SecurityHasColumns && x.SecurityPermissions.Any(permission =>
                        permission is not ("SELECT" or "UPDATE" or "REFERENCES")))))
                throw new LegacyContractException("SQL_SECURITY_UNSUPPORTED");
            if (operations.Any(x => x.IsDataMutation) && item.HasDmlTrigger
                || operations.Any(x => x.IsSchemaMutation) && item.HasAnyTrigger
                || operations.Any(x => x.Operation is "UPDATE_DATA" or "DELETE_DATA" or "MERGE_DATA")
                    && item.HasInboundCascade)
                throw new LegacyContractException("SQL_INDIRECT_EFFECT_UNPROVEN");
        }
        if (all.Any(x => x.IsSchemaMutation || x.Operation == "DATABASE_SECURITY")
            && (!metadata.DatabaseDdlTriggersComplete || !metadata.ServerDdlTriggersComplete
                || metadata.HasEnabledDdlTrigger))
            throw new LegacyContractException("SQL_INDIRECT_EFFECT_UNPROVEN");
    }

    private static bool CompatibleNewObject(RecoverySecuritySecurable objectScope,
        ScriptAnalysis forward, ScriptAnalysis rollback)
    {
        var first = forward.Operations.Where(x => x.Schema == objectScope.Schema
            && x.Object == objectScope.Name).ToArray();
        var second = rollback.Operations.Where(x => x.Schema == objectScope.Schema
            && x.Object == objectScope.Name).ToArray();
        var creation = first.Where(x => x.Operation is "CREATE_TABLE" or "CREATE_VIEW"
            or "SELECT_INTO").ToArray();
        if (creation.Length != 1 || second.Length != 1) return false;
        var expectedDrop = creation[0].Operation == "CREATE_VIEW"
            ? "DROP_VIEW" : "DROP_TABLE";
        if (second[0].Operation != expectedDrop) return false;
        return first.All(x => ReferenceEquals(x, creation[0])
            || creation[0].Operation == "CREATE_TABLE"
                && x.AstNodeType == "CreateTableStatement"
                && x.Operation is "ADD_COLUMN" or "ADD_CONSTRAINT" or "CREATE_INDEX");
    }

    private static bool ObjectPermissionCompatible(string kind, string permission) => kind switch
    {
        "TABLE" => permission is "SELECT" or "INSERT" or "UPDATE" or "DELETE"
            or "REFERENCES" or "VIEW DEFINITION" or "ALTER" or "CONTROL"
            or "TAKE OWNERSHIP",
        "VIEW" => permission is "SELECT" or "INSERT" or "UPDATE" or "DELETE"
            or "VIEW DEFINITION" or "ALTER" or "CONTROL" or "TAKE OWNERSHIP",
        _ => false
    };
}
