using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DatabaseReleaseQualification;

internal static class LegacyModuleAnalysis
{
    internal static bool TryAnalyze(TSqlStatement statement, ScriptAnalysis result)
    {
        var name = statement.GetType().Name;
        var operation = name switch
        {
            "CreateProcedureStatement" => "CREATE_PROCEDURE",
            "AlterProcedureStatement" => "ALTER_PROCEDURE",
            "CreateOrAlterProcedureStatement" => "CREATE_OR_ALTER_PROCEDURE",
            "DropProcedureStatement" => "DROP_PROCEDURE",
            "CreateFunctionStatement" => "CREATE_FUNCTION",
            "AlterFunctionStatement" => "ALTER_FUNCTION",
            "CreateOrAlterFunctionStatement" => "CREATE_OR_ALTER_FUNCTION",
            "DropFunctionStatement" => "DROP_FUNCTION",
            _ => null
        };
        if (operation is null) return false;
        IEnumerable<SchemaObjectName> targets = statement switch
        {
            ProcedureStatementBody procedure => [procedure.ProcedureReference.Name],
            FunctionStatementBody function => [function.Name],
            DropProcedureStatement drop => drop.Objects,
            DropFunctionStatement drop => drop.Objects,
            _ => []
        };
        foreach (var target in targets)
        {
            var resolved = target.Identifiers.Count == 2;
            result.Operations.Add(new ScriptOperation
            {
                Operation = operation,
                Schema = target.SchemaIdentifier?.Value ?? "",
                Object = target.BaseIdentifier?.Value ?? "",
                AstNodeType = name,
                TargetResolved = resolved,
                IsSchemaMutation = true,
                IsSensitive = true,
                IsDestructive = operation.StartsWith("DROP_", StringComparison.Ordinal)
            });
            // Changing a definition does not execute its body and is not DATA.
            // Keep a conservative risk floor through the existing finding model.
            result.Findings.Add(new DependencyFinding
            {
                Severity = FindingSeverity.Warning,
                Object = target.ToString() ?? "module",
                Operation = operation,
                DependentObject = "module-definition",
                DependencyType = "legacy-module",
                Source = "LEGACY_MODULES_V1",
                Reason = "Module definition and dependencies require full structural and security recovery."
            });
            if (!resolved)
            {
                result.Confidence = AnalysisConfidence.Insufficient;
                result.ConfidenceReasons.Add("LEGACY_MODULE_TARGET_UNRESOLVED");
            }
        }
        return true;
    }
}
