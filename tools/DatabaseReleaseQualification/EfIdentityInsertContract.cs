using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DatabaseReleaseQualification;

// Closed grammar for EF's identity-column probe. Comparing generated ASTs to
// a parsed template rejects every unmodelled clause, hint, expression or body.
internal static class EfIdentityInsertContract
{
    internal static bool Accepts(IfStatement statement)
    {
        if (statement.ElseStatement is not null
            || statement.ThenStatement is not SetIdentityInsertStatement identity
            || identity.Table.Identifiers.Count is < 1 or > 2
            || statement.Predicate is not ExistsPredicate exists
            || exists.Subquery.QueryExpression is not QuerySpecification query
            || query.WhereClause?.SearchCondition is not BooleanBinaryExpression filter
            || filter.BinaryExpressionType != BooleanBinaryExpressionType.And
            || filter.FirstExpression is not InPredicate names
            || names.Values.Count is < 1 or > 1024
            || names.Values.Any(value => value is not StringLiteral { IsNational: true, Value.Length: > 0 })
            || filter.SecondExpression is not BooleanComparisonExpression comparison
            || comparison.SecondExpression is not FunctionCall objectId
            || objectId.Parameters.Count != 1
            || objectId.Parameters[0] is not StringLiteral { IsNational: true } target)
            return false;

        // Parse OBJECT_ID's literal as an object name, never interpolate unchecked SQL.
        var targetParsed = SqlScriptAnalyzer.Parse("SET IDENTITY_INSERT " + target.Value + " ON;");
        if (targetParsed.Errors.Count != 0 || targetParsed.Fragment is not TSqlScript targetScript
            || targetScript.Batches.Count != 1 || targetScript.Batches[0].Statements.Count != 1
            || targetScript.Batches[0].Statements[0] is not SetIdentityInsertStatement targetSet
            || targetSet.Table.Identifiers.Count != identity.Table.Identifiers.Count
            || !targetSet.Table.Identifiers.Select(x => x.Value)
                .SequenceEqual(identity.Table.Identifiers.Select(x => x.Value), StringComparer.Ordinal))
            return false;

        // Also require the entire literal to be exactly the canonical local name:
        // no comments, extra statements, empty identifiers or database/server parts.
        var canonicalTarget = string.Join(".", identity.Table.Identifiers.Select(x =>
            "[" + x.Value.Replace("]", "]]") + "]"));
        if (target.Value != canonicalTarget) return false;
        var columnList = string.Join(", ", names.Values.Cast<StringLiteral>().Select(x =>
            "N'" + x.Value.Replace("'", "''") + "'"));
        var template = SqlScriptAnalyzer.Parse(
            $"IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN ({columnList}) " +
            $"AND [object_id] = OBJECT_ID(N'{canonicalTarget.Replace("'", "''")}')) " +
            $"SET IDENTITY_INSERT {canonicalTarget} {(identity.IsOn ? "ON" : "OFF")};");
        if (template.Errors.Count != 0 || template.Fragment is not TSqlScript expected) return false;
        var generator = new Sql180ScriptGenerator();
        generator.GenerateScript(statement, out var actualSql);
        generator.GenerateScript(expected.Batches[0].Statements[0], out var expectedSql);
        return string.Equals(actualSql, expectedSql, StringComparison.Ordinal);
    }
}
