using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DatabaseReleaseQualification;

// Resolution is supplied by an authenticated inspection/generation producer, not
// inferred from a table name or from the conventional SQL Server default schema.
public sealed record EfSqlResolutionV1(string DefaultSchema, EvidenceReference Evidence);
public sealed record EfSemanticResultV1(string Status, string ScriptHash, ScriptAnalysis Analysis, string[] Reasons);

public static class EfSqlSemanticAnalysisV1
{
    public static EfSemanticResultV1 Analyze(ReleaseScript sql, SchemaSnapshot snapshot, EfSqlResolutionV1? resolution)
    {
        var blocked = new ScriptAnalysis { ScriptRole = sql.Role, Confidence = AnalysisConfidence.Insufficient };
        try
        {
            if (sql.Length is 0 or > 8 * 1024 * 1024) throw new InvalidOperationException();
            var text = new UTF8Encoding(false, true).GetString(sql.Bytes);
            // Reuse the executor's validator only. The original bytes are never rewritten.
            _ = EfTestMigrationCli.ExactBatches(text);
            if (text.StartsWith('\uFEFF')) text = text[1..];
            var parsed = SqlScriptAnalyzer.Parse(text);
            var script = (TSqlScript)parsed.Fragment;
            var toggles = new List<ScriptOperation>();
            (string Table, string Predicate)? openIdentity = null;
            foreach (var batch in script.Batches)
            foreach (var statement in batch.Statements.ToArray())
            {
                if (statement is not IfStatement conditional)
                {
                    if (openIdentity is { } active)
                    {
                        // The identity probe must remain stable until OFF; only
                        // direct inserts into the same table may intervene.
                        if (statement is not InsertStatement insert
                            || insert.InsertSpecification.Target is not NamedTableReference table)
                            throw new InvalidOperationException();
                        table.SchemaObject.Accept(new ResolvedNames(resolution));
                        if (string.Join(".", table.SchemaObject.Identifiers.Select(x => x.Value)) != active.Table)
                            throw new InvalidOperationException();
                    }
                    continue;
                }
                if (!EfIdentityInsertContract.Accepts(conditional)) throw new InvalidOperationException();
                var identity = (SetIdentityInsertStatement)conditional.ThenStatement;
                new Sql180ScriptGenerator().GenerateScript(conditional.Predicate, out var predicate);
                var names = new ResolvedNames(resolution);
                identity.Table.Accept(names);
                var key = string.Join(".", identity.Table.Identifiers.Select(x => x.Value));
                if (identity.IsOn ? openIdentity is not null : openIdentity != (key, predicate))
                    throw new InvalidOperationException();
                openIdentity = identity.IsOn ? (key, predicate) : null;
                toggles.Add(new() { Operation = identity.IsOn ? "IDENTITY_INSERT_ON" : "IDENTITY_INSERT_OFF",
                    AstNodeType = nameof(SetIdentityInsertStatement), Schema = identity.Table.SchemaIdentifier.Value,
                    Object = identity.Table.BaseIdentifier.Value, TargetResolved = true, IsSensitive = true });
                // Only a recognized technical wrapper is omitted from persisted-state
                // analysis. Its session effect is retained as an explicit operation.
                batch.Statements.Remove(statement);
            }
            if (openIdentity is not null) throw new InvalidOperationException();
            script.Accept(new ResolvedNames(resolution));
            script.Accept(new Effects());
            var analysis = new SqlScriptAnalyzer().AnalyzeParsed(sql.Role, script, parsed.Errors, snapshot);
            analysis.Operations.AddRange(toggles);
            var tables = snapshot.Objects.Where(x => x.Kind == "table").Select(x => (x.Schema, x.Name)).ToHashSet();
            foreach (var operation in analysis.Operations)
            {
                if (operation.Operation == "CREATE_TABLE") tables.Add((operation.Schema!, operation.Object!));
                if (operation.Operation == "DROP_TABLE") tables.Remove((operation.Schema!, operation.Object!));
                if (!operation.IsDataMutation) continue;
                if (!tables.Contains((operation.Schema!, operation.Object!))) throw new InvalidOperationException();
                if (snapshot.Objects.Any(x => (x.Kind is "trigger" or "foreign-key-column" or "foreign-key" or "synonym"
                        or "schema-dependency" or "default-constraint" or "check-constraint"
                        || x.Kind == "column" && x.Properties.GetValueOrDefault("computed") == "true")
                    && (x.Schema == operation.Schema && (x.Parent == operation.Object || x.Name == operation.Object)
                        || x.Properties.GetValueOrDefault("referencedSchema") == operation.Schema
                            && (x.Properties.GetValueOrDefault("referencedEntity") == operation.Object
                                || x.Properties.GetValueOrDefault("referencedTable") == operation.Object))))
                    throw new InvalidOperationException();
            }
            var complete = analysis.Confidence == AnalysisConfidence.Complete && analysis.ParseErrors.Count == 0
                && analysis.UnknownStatementTypes.Count == 0 && analysis.Operations.Any(x => x.IsDataMutation || x.IsSchemaMutation);
            return new(complete ? "SEMANTIC_ANALYSIS_COMPLETE" : "SEMANTIC_ANALYSIS_BLOCKED", sql.Sha256,
                analysis, complete ? [] : ["EF_SEMANTIC_COVERAGE_INCOMPLETE"]);
        }
        catch
        {
            blocked.UnknownStatementTypes.Add("UNPROVEN_EF_SQL");
            blocked.ConfidenceReasons.Add("EF_SQL_SEMANTICS_UNPROVEN");
            return new("SEMANTIC_ANALYSIS_BLOCKED", sql.Sha256, blocked, ["EF_SQL_SEMANTICS_UNPROVEN"]);
        }
    }

    private sealed class ResolvedNames(EfSqlResolutionV1? resolution) : TSqlFragmentVisitor
    {
        // Built-in type names are not database object references (e.g. int/varchar).
        public override void ExplicitVisit(SqlDataTypeReference node) { }
        public override void Visit(SchemaObjectName name)
        {
            if (name.Identifiers.Count is < 1 or > 2 || name.Identifiers.Any(x => string.IsNullOrEmpty(x.Value) || x.Value.StartsWith('#')))
                throw new InvalidOperationException();
            if (name.Identifiers.Count == 1)
            {
                if (resolution is null || string.IsNullOrWhiteSpace(resolution.DefaultSchema)
                    || resolution.DefaultSchema.Length > 128 || resolution.DefaultSchema.Any(char.IsControl)
                    || resolution.Evidence is null) throw new InvalidOperationException();
                name.Identifiers.Insert(0, new Identifier { Value = resolution.DefaultSchema, QuoteType = QuoteType.SquareBracket });
            }
        }
    }

    // Deliberately narrower than the executor grammar: direct local effects only.
    // Function calls, subqueries and FK indirect effects remain outside this increment.
    private sealed class Effects : TSqlFragmentVisitor
    {
        public override void Visit(InsertStatement statement)
        {
            if (statement.WithCtesAndXmlNamespaces is not null
                || statement.InsertSpecification.InsertSource is not ValuesInsertSource
                || statement.InsertSpecification.OutputIntoClause is not null) throw new InvalidOperationException();
        }
        public override void Visit(UpdateStatement statement)
        {
            if (statement.WithCtesAndXmlNamespaces is not null || statement.UpdateSpecification.FromClause is not null
                || statement.UpdateSpecification.OutputIntoClause is not null) throw new InvalidOperationException();
        }
        public override void Visit(DeleteStatement statement)
        {
            if (statement.WithCtesAndXmlNamespaces is not null || statement.DeleteSpecification.FromClause is not null
                || statement.DeleteSpecification.OutputIntoClause is not null) throw new InvalidOperationException();
        }
        public override void Visit(TableReference table)
        {
            if (table is not NamedTableReference) throw new InvalidOperationException();
        }
        public override void Visit(FunctionCall node) => throw new InvalidOperationException();
        public override void Visit(ScalarSubquery node) => throw new InvalidOperationException();
        public override void Visit(VariableReference node) => throw new InvalidOperationException();
        public override void Visit(GlobalVariableExpression node) => throw new InvalidOperationException();
        public override void Visit(NextValueForExpression node) => throw new InvalidOperationException();
        public override void Visit(UserDataTypeReference node) => throw new InvalidOperationException();
        public override void Visit(XmlDataTypeReference node) => throw new InvalidOperationException();
        public override void Visit(ForeignKeyConstraintDefinition node)
        {
            // FK indirect effects require catalog/scope acquisition beyond A2.1.
            throw new InvalidOperationException();
        }
    }
}
