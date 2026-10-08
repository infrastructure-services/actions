using System.Text;
using System.Security.Cryptography;
using DatabaseReleaseQualification;

internal static class EfIdentityInsertTests
{
    private const string Probe = "IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'OID', N'NRVERSION') AND [object_id] = OBJECT_ID(N'[SEBLOB]')) ";
    private const string On = "SET IDENTITY_INSERT [SEBLOB] ON;";
    private const string Off = "SET IDENTITY_INSERT [SEBLOB] OFF;";
    internal static (string Name, Func<Task> Run)[] Cases =>
        Positive.Select(sql => ("EF closed grammar accepts " + sql.Name, (Func<Task>)(() => Accept(sql.Sql))))
        .Concat(Negative.Select(sql => ("EF closed grammar rejects " + sql.Name, (Func<Task>)(() => Reject(sql.Sql)))))
        .Concat(new (string, Func<Task>)[] { ("EF public validator preserves strict UTF8 and exact fixtures", PublicFixtures) }).ToArray();

    private static readonly (string Name, string Sql)[] Positive = [
        ("ON", Probe + On), ("OFF", Probe + Off),
        ("schema-qualified target", (Probe + On).Replace("[SEBLOB]", "[dbo].[SEBLOB]")),
        ("formatting and comments", (Probe + On).Replace("IF EXISTS", "IF /* EF */\nEXISTS")),
        ("rollback", "BEGIN TRANSACTION; DROP TABLE [SEBLOB]; COMMIT;\nGO\n"),
        ("initial BOM", "\uFEFFBEGIN TRANSACTION; DROP TABLE [SEBLOB]; COMMIT;\nGO\n")
    ];
    private static readonly (string Name, string Sql)[] Negative = [
        ("generic IF", "IF 1=1 " + On),
        ("wrong catalog", (Probe + On).Replace("[sys].[identity_columns]", "[dbo].[identity_columns]")),
        ("ELSE", Probe + On + " ELSE " + Off),
        ("INSERT body", Probe + "INSERT INTO [SEBLOB] VALUES ('x');"),
        ("DELETE body", Probe + "DELETE FROM [SEBLOB];"),
        ("UPDATE body", Probe + "UPDATE [SEBLOB] SET OID='x';"),
        ("CREATE body", Probe + "CREATE TABLE X (Id int);"),
        ("DROP body", Probe + "DROP TABLE X;"),
        ("ALTER body", Probe + "ALTER TABLE X ADD Id int;"),
        ("EXEC", Probe + "EXEC p;"), ("dynamic SQL", Probe + "EXEC(N'DROP TABLE X');"),
        ("mismatched target", Probe + On.Replace("[SEBLOB]", "[OTHER]")),
        ("cross database", (Probe + On).Replace("[SEBLOB]", "[QA].[dbo].[SEBLOB]")),
        ("cross server", (Probe + On).Replace("[SEBLOB]", "[srv].[QA].[dbo].[SEBLOB]")),
        ("extra subquery", (Probe + On).Replace("SELECT *", "SELECT (SELECT 1)")),
        ("join", (Probe + On).Replace(" WHERE", " JOIN dbo.X ON 1=1 WHERE")),
        ("OR predicate", (Probe + On).Replace(" AND", " OR")),
        ("extra predicate", (Probe + On).Replace("'))", "') AND 1=1)")),
        ("wrong function", (Probe + On).Replace("OBJECT_ID", "OTHER_FUNCTION")),
        ("target literal injection", (Probe + On).Replace("N'[SEBLOB]'", "N'[SEBLOB] ON; DROP TABLE X;--'")),
        ("literal target case mismatch", Probe.Replace("N'[SEBLOB]'", "N'[seblob]'") + On),
        ("TOP query", (Probe + On).Replace("SELECT *", "SELECT TOP (1) *")),
        ("table hint", (Probe + On).Replace(" WHERE", " WITH (NOLOCK) WHERE")),
        ("wrong comparison", (Probe + On).Replace(" = OBJECT_ID", " <> OBJECT_ID")),
        ("nested IF", Probe + Probe + On),
        ("multiple body statements", Probe + "BEGIN " + On + Off + " END;"),
        ("standalone identity", On),
        ("ROLLBACK", "BEGIN TRANSACTION; ROLLBACK TRANSACTION;"),
        ("named transaction", "BEGIN TRANSACTION x; COMMIT TRANSACTION x;"),
        ("nested transaction", "BEGIN TRANSACTION; BEGIN TRANSACTION; COMMIT; COMMIT;"),
        ("unbalanced transaction", "BEGIN TRANSACTION; DROP TABLE X;"),
        ("embedded BOM", "DROP TABLE X;\uFEFFDROP TABLE Y;"),
        ("double BOM", "\uFEFF\uFEFFDROP TABLE X;"),
        ("BOM after whitespace", " \uFEFFDROP TABLE X;"),
        ("invalid SQL after BOM", "\uFEFFthis is invalid SQL"),
        ("BOM does not allow SELECT", "\uFEFFSELECT 1;")
    ];
    private static Task Accept(string sql)
    {
        var batches = EfTestMigrationCli.ExactBatches(sql);
        if (batches.Count == 0 || batches.Any(x => x.Contains('\uFEFF')))
            throw new Exception("Invalid normalized batches");
        return Task.CompletedTask;
    }
    private static Task Reject(string sql)
    {
        try { EfTestMigrationCli.ExactBatches(sql); }
        catch (LegacyContractException) { return Task.CompletedTask; }
        throw new Exception("Unsupported SQL accepted");
    }
    private static async Task PublicFixtures()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ef1009");
        foreach (var role in new[] { "forward", "rollback" })
        {
            var path = Path.Combine(root, role + ".sql");
            var bytes = await File.ReadAllBytesAsync(path);
            if (!bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }))
                throw new Exception("Fixture must preserve EF BOM");
            var expectedHash = role == "forward"
                ? "309581D85A1525F03F0125337EC39655F89E8E6A2E4A02D48C415A096024B47F"
                : "C05451E73BA4ECE0E69A21E6B4D8AA9DFF227009CD37B1ED7128EA452808F1F9";
            if (Convert.ToHexString(SHA256.HashData(bytes)) != expectedHash)
                throw new Exception("Exact EF fixture bytes changed");
            if (await EfTestMigrationCli.ValidateAsync(["--script", path]) != 0)
                throw new Exception("Real EF fixture rejected");
            var sql = new UTF8Encoding(false, true).GetString(bytes);
            await Accept(sql[1..]);
            if (!EfTestMigrationCli.ExactBatches(sql).SequenceEqual(EfTestMigrationCli.ExactBatches(sql[1..])))
                throw new Exception("BOM changed executable batches");
        }
        var temporary = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql");
        try
        {
            await File.WriteAllBytesAsync(temporary, [239, 187, 191, 0xFF]);
            if (await EfTestMigrationCli.ValidateAsync(["--script", temporary]) != 66)
                throw new Exception("Invalid UTF8 accepted");
        }
        finally { File.Delete(temporary); }
    }
}
