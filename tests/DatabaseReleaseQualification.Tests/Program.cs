using System.Text.Json;
using DatabaseReleaseQualification;

var tests = new (string Name, Func<Task> Run)[]
{
    ("fingerprint estable ante distinto orden", FingerprintIgnoresOrder),
    ("diferencia estructural cambia fingerprint", StructuralDifferenceChangesFingerprint),
    ("métricas no contaminan fingerprint", MetricsDoNotChangeFingerprint),
    ("AST reconoce formatos equivalentes", AstEquivalentFormatting),
    ("AST resuelve aliases UPDATE y DELETE", AstResolvesAliases),
    ("AST reconoce statements requeridos", AstRecognizesRequiredStatements),
    ("AST conserva múltiples statements y comentarios", AstMultipleStatementsAndComments),
    ("parse error nunca queda COMPLETE ni LOW", AstParseFailureNeverLow),
    ("SQL no parseable no llega al rehearsal", UnparseableSqlDoesNotExecute),
    ("dynamic SQL queda INSUFFICIENT y bloqueado", DynamicSqlIsBlocked),
    ("target implícito queda PARTIAL y mínimo MEDIUM", ImplicitTargetIsNotSilentLow),
    ("statement desconocido nunca queda LOW", UnknownStatementNeverLow),
    ("rollback estructural puro es FULL_REVERSIBLE", PureSchemaRollbackIsValid),
    ("DELETE con schema idéntico deja datos UNVERIFIED", DeleteDataRollbackIsUnverified),
    ("DROP COLUMN recreada no recupera datos", DropColumnIsNotFullReversible),
    ("data contract válido permite rollback completo", DataContractCanValidateRollback),
    ("rollback que omite índice es INVALID", RollbackMissingIndex),
    ("ALTER COLUMN detecta índice dependiente", IndexedColumnDependency),
    ("ALTER COLUMN detecta FK desde tabla referenciada", ReferencedForeignKeyDependency),
    ("rollback detecta índice creado por forward en POST1", Post1IndexDependencyDetected),
    ("análisis POST1 reemplaza screening insuficiente de PRE", Post1AnalysisIsAuthoritative),
    ("rollback detecta FK creada por forward en POST1", Post1ForeignKeyDependencyDetected),
    ("rollback detecta constraint y computed dependency de POST1", Post1ConstraintAndComputedDependencyDetected),
    ("riesgo POST1 eleva final y queda en attestation", Post1RiskRaisesFinalAndIsAttested),
    ("rollback POST1 sin dependencias conserva LOW", Post1LowDependencyStaysLow),
    ("confidence POST1 insuficiente bloquea antes del rollback", Post1InsufficientConfidenceBlocksRollback),
    ("forward LOW y rollback HIGH produce HIGH", HighRollbackWins),
    ("rollback de índice grande eleva rollbackRisk", LargeRollbackCostRaisesRollbackRisk),
    ("dependency HIGH produce final HIGH", HighDependencyWins),
    ("todas las dimensiones LOW producen LOW", AllLow),
    ("rollback INVALID no puede continuar", InvalidRollbackCannotProceed),
    ("reapply divergente bloquea", ReapplyMismatch),
    ("discovery bloqueado no ejecuta nada", BlockedDiscoveryDoesNotExecute),
    ("PROD no puede ejecutar rehearsal", ProdGuard),
    ("scripts inmutables clonan bytes", ReleaseScriptIsImmutable),
    ("payload no depende del ambiente", PayloadIdentityIgnoresEnvironment),
    ("un byte distinto cambia payload", PayloadChangesWithScriptByte),
    ("attestations varían sin cambiar payload", AttestationsAccumulateWithoutChangingPayload),
    ("package usa hashes de schema explícitos", PackageUsesSchemaHashNames),
    ("artefactos de schema no conservan definiciones sensibles", SchemaArtifactsDoNotExposeRawDefinitions),
    ("run metadata rechaza claves sensibles", RunMetadataRejectsSensitiveKeys),
    ("rollback INVALID no solicita aprobación DBA", InvalidRollbackDoesNotRequestApproval),
    ("rollback VALID y HIGH requiere aprobación DBA", ValidHighRollbackRequiresApproval),
    ("target TEST LOW más PROD HIGH produce HIGH", TargetHighWins),
    ("release HIGH más target LOW permanece HIGH", QualifiedHighWins),
    ("release LOW más target LOW permanece LOW", QualifiedAndTargetLowRemainLow),
    ("FK y triggers contribuyen al target risk", TargetRelationshipsRaiseRisk),
    ("coverage unsupported degrada confidence", UnsupportedCoverageDegradesConfidence),
    ("feature unsupported relevante bloquea", RelevantUnsupportedFeatureBlocks),
    ("CLI analyze-only genera payload y attestation", CliAnalyzeOnlyPackage),
    ("CLI bloquea discovery inconsistente", CliBlocksInconsistentDiscovery)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}");
    }
}
Console.WriteLine($"Tests ejecutados: {tests.Length}; fallos: {failed}");
return failed == 0 ? 0 : 1;

static Task FingerprintIgnoresOrder()
{
    var first = BaseSnapshot(includeIndex: true);
    var second = BaseSnapshot(includeIndex: true);
    second.Objects.Reverse();
    Equal(SchemaCanonicalizer.Canonicalize(first).Sha256, SchemaCanonicalizer.Canonicalize(second).Sha256);
    return Task.CompletedTask;
}

static Task StructuralDifferenceChangesFingerprint()
{
    NotEqual(
        SchemaCanonicalizer.Canonicalize(BaseSnapshot(includeIndex: true)).Sha256,
        SchemaCanonicalizer.Canonicalize(BaseSnapshot(includeIndex: true, nullable: true)).Sha256);
    return Task.CompletedTask;
}

static Task MetricsDoNotChangeFingerprint()
{
    Equal(
        SchemaCanonicalizer.Canonicalize(BaseSnapshot(includeIndex: true, rows: 10)).Sha256,
        SchemaCanonicalizer.Canonicalize(BaseSnapshot(includeIndex: true, rows: 99_000_000)).Sha256);
    return Task.CompletedTask;
}

static Task AstEquivalentFormatting()
{
    var analyzer = new SqlScriptAnalyzer();
    var snapshot = BaseSnapshot(includeIndex: true);
    var forms = new[]
    {
        "ALTER TABLE dbo.Orden ALTER COLUMN Fecha datetime2 NOT NULL;",
        "alter\n table [dbo].[Orden]\n alter column [Fecha] datetime2 not null;",
        "/* qualification */ AlTeR TABLE \"dbo\".\"Orden\" ALTER COLUMN \"Fecha\" datetime2 NOT NULL;"
    };
    var operations = forms.Select(sql => analyzer.Analyze("forward", sql, snapshot).Operations.Single()).ToArray();
    True(operations.All(operation => operation.Operation == "ALTER_COLUMN"));
    True(operations.All(operation => operation.Schema == "dbo" && operation.Object == "Orden" && operation.Column == "Fecha"));
    True(operations.All(operation => operation.TargetResolved));
    return Task.CompletedTask;
}

static Task AstResolvesAliases()
{
    var analyzer = new SqlScriptAnalyzer();
    var snapshot = BaseSnapshot(includeIndex: false);
    var update = analyzer.Analyze("forward",
        "UPDATE o SET Fecha = SYSUTCDATETIME() FROM [dbo].[Orden] AS o WHERE o.Fecha IS NULL;", snapshot);
    var delete = analyzer.Analyze("rollback",
        "DELETE o FROM [dbo].[Orden] AS o WHERE o.Fecha IS NULL;", snapshot);
    foreach (var operation in update.Operations.Concat(delete.Operations))
    {
        Equal("dbo", operation.Schema);
        Equal("Orden", operation.Object);
        True(operation.TargetResolved);
    }
    Equal(AnalysisConfidence.Complete, update.Confidence);
    Equal(AnalysisConfidence.Complete, delete.Confidence);
    return Task.CompletedTask;
}

static Task AstRecognizesRequiredStatements()
{
    const string sql = """
        CREATE TABLE [dbo].[AstT] ([Id] int NOT NULL, CONSTRAINT [PK_AstT] PRIMARY KEY ([Id]));
        ALTER TABLE dbo.AstT ADD [Name] nvarchar(50) NULL;
        ALTER TABLE dbo.AstT ALTER COLUMN [Name] nvarchar(100) NULL;
        ALTER TABLE dbo.AstT DROP CONSTRAINT PK_AstT;
        ALTER TABLE dbo.AstT DROP COLUMN [Name];
        CREATE INDEX IX_AstT_Id ON dbo.AstT(Id);
        ALTER INDEX IX_AstT_Id ON dbo.AstT REBUILD;
        DROP INDEX IX_AstT_Id ON dbo.AstT;
        GO
        CREATE VIEW dbo.V_AstT AS SELECT Id FROM dbo.AstT;
        GO
        ALTER VIEW dbo.V_AstT AS SELECT Id FROM dbo.AstT;
        GO
        DROP VIEW dbo.V_AstT;
        GO
        CREATE TRIGGER dbo.TR_AstT ON dbo.AstT AFTER INSERT AS SELECT 1;
        GO
        ALTER TRIGGER dbo.TR_AstT ON dbo.AstT AFTER INSERT AS SELECT 1;
        GO
        DROP TRIGGER dbo.TR_AstT;
        GO
        INSERT INTO dbo.AstT(Id) VALUES (1);
        UPDATE a SET Id = 2 FROM dbo.AstT AS a WHERE a.Id = 1;
        DELETE a FROM dbo.AstT AS a WHERE a.Id = 2;
        MERGE dbo.AstT AS target USING (SELECT 3 AS Id) AS source ON target.Id = source.Id
          WHEN NOT MATCHED THEN INSERT (Id) VALUES (source.Id);
        TRUNCATE TABLE dbo.AstT;
        EXEC dbo.usp_Ast;
        DROP TABLE dbo.AstT;
        """;
    var analysis = new SqlScriptAnalyzer().Analyze("forward", sql, BaseSnapshot(includeIndex: false));
    var operations = analysis.Operations.Select(operation => operation.Operation).ToHashSet(StringComparer.Ordinal);
    foreach (var expected in new[]
    {
        "CREATE_TABLE", "ADD_COLUMN", "ADD_CONSTRAINT", "ALTER_COLUMN", "DROP_CONSTRAINT", "DROP_COLUMN",
        "CREATE_INDEX", "REBUILD_INDEX", "DROP_INDEX", "CREATE_VIEW", "ALTER_VIEW", "DROP_VIEW",
        "CREATE_TRIGGER", "ALTER_TRIGGER", "DROP_TRIGGER", "INSERT_DATA", "UPDATE_DATA", "DELETE_DATA",
        "MERGE_DATA", "TRUNCATE_TABLE", "EXECUTE", "DROP_TABLE"
    }) True(operations.Contains(expected));
    Equal(AnalysisConfidence.Insufficient, analysis.Confidence);
    return Task.CompletedTask;
}

static Task AstMultipleStatementsAndComments()
{
    var analysis = new SqlScriptAnalyzer().Analyze("forward", """
        -- harmless comment containing DROP TABLE dbo.Secret
        ALTER TABLE [dbo].[Orden] ADD [A] int NULL;
        /* multiline UPDATE dbo.Secret SET x = 1 */
        ALTER TABLE [dbo].[Orden] ADD [B] int NULL;
        """, BaseSnapshot(includeIndex: false));
    Equal(2, analysis.Operations.Count(operation => operation.Operation == "ADD_COLUMN"));
    Equal(2, analysis.StatementCount);
    Equal(AnalysisConfidence.Complete, analysis.Confidence);
    return Task.CompletedTask;
}

static Task AstParseFailureNeverLow()
{
    var snapshot = BaseSnapshot(includeIndex: false);
    var analysis = new SqlScriptAnalyzer().Analyze("forward", "ALTER TABLE dbo.Orden ALTER COLUMN ;", snapshot);
    True(analysis.ParseErrors.Count > 0);
    Equal(AnalysisConfidence.Insufficient, analysis.Confidence);
    var risk = RiskFor(analysis, SelectAnalysis(snapshot));
    Equal(RiskLevel.High, risk.FinalRisk);
    True(risk.AutoPromotionBlocked);
    return Task.CompletedTask;
}

static async Task UnparseableSqlDoesNotExecute()
{
    var database = new FakeRehearsalDatabase(BaseSnapshot(includeIndex: false));
    var result = await new RehearsalEngine().QualifyAsync(TestRelease(), ConsistentDiscovery(),
        ReleaseScript.FromText("forward", "ALTER TABLE dbo.Orden ADD ["), Rollback(), database);
    Equal("BLOCKED_ANALYSIS_CONFIDENCE", result.QualificationStatus);
    Equal(1, database.CaptureCount);
    Equal(0, database.Executions.Count);
}

static Task DynamicSqlIsBlocked()
{
    var snapshot = BaseSnapshot(includeIndex: false);
    var analysis = new SqlScriptAnalyzer().Analyze("forward", "EXEC(N'DELETE FROM dbo.Orden');", snapshot);
    True(analysis.Operations.Any(operation => operation.Operation == "EXECUTE_DYNAMIC_SQL"));
    Equal(AnalysisConfidence.Insufficient, analysis.Confidence);
    var risk = RiskFor(analysis, SelectAnalysis(snapshot));
    Equal(RiskLevel.High, risk.FinalRisk);
    True(risk.AutoPromotionBlocked);
    return Task.CompletedTask;
}

static Task ImplicitTargetIsNotSilentLow()
{
    var snapshot = BaseSnapshot(includeIndex: false);
    var analysis = new SqlScriptAnalyzer().Analyze("forward", "ALTER TABLE Orden ADD X int NULL;", snapshot);
    Equal(AnalysisConfidence.Partial, analysis.Confidence);
    True(analysis.Operations.Any(operation => !operation.TargetResolved));
    Equal(RiskLevel.Medium, RiskFor(analysis, SelectAnalysis(snapshot)).FinalRisk);
    return Task.CompletedTask;
}

static Task UnknownStatementNeverLow()
{
    var snapshot = BaseSnapshot(includeIndex: false);
    var analysis = new SqlScriptAnalyzer().Analyze("forward", "BACKUP DATABASE Demo TO DISK = 'x.bak';", snapshot);
    True(analysis.UnknownStatementTypes.Count > 0);
    True(analysis.Operations.Any(operation => operation.Operation == "UNKNOWN_SQL"));
    Equal(AnalysisConfidence.Insufficient, analysis.Confidence);
    Equal(RiskLevel.High, RiskFor(analysis, SelectAnalysis(snapshot)).FinalRisk);
    return Task.CompletedTask;
}

static async Task PureSchemaRollbackIsValid()
{
    var pre = BaseSnapshot(includeIndex: true);
    var post = AddCommentSnapshot(includeIndex: true, "nvarchar(50)");
    var database = new FakeRehearsalDatabase(pre, post, pre, post);
    var result = await new RehearsalEngine().QualifyAsync(
        TestRelease(), ConsistentDiscovery(), Forward(), Rollback(), database);
    Equal("QUALIFIED", result.QualificationStatus);
    Equal(SchemaRollbackValidity.Valid, result.SchemaRollbackValidity);
    Equal(DataRollbackValidity.NotApplicable, result.DataRollbackValidity);
    Equal(RollbackCapability.FullReversible, result.RollbackCapability);
    True(result.RollbackCertified && result.ReapplyCertified && result.CanProceed);
    Equal(3, database.Executions.Count);
}

static async Task DeleteDataRollbackIsUnverified()
{
    var schema = BaseSnapshot(includeIndex: false);
    var database = new FakeRehearsalDatabase(schema, schema, schema);
    var result = await new RehearsalEngine().QualifyAsync(
        TestRelease(), ConsistentDiscovery(),
        ReleaseScript.FromText("forward", "DELETE FROM dbo.Orden;"),
        ReleaseScript.FromText("rollback", "INSERT INTO dbo.Orden(Fecha) VALUES (SYSUTCDATETIME());"),
        database);
    Equal(SchemaRollbackValidity.Valid, result.SchemaRollbackValidity);
    Equal(DataRollbackValidity.Unverified, result.DataRollbackValidity);
    Equal(RollbackCapability.RestoreRequired, result.RollbackCapability);
    Equal("BLOCKED_DATA_ROLLBACK_UNVERIFIED", result.QualificationStatus);
    True(!result.RollbackCertified && !result.CanProceed);
    Equal(2, database.Executions.Count);
}

static async Task DropColumnIsNotFullReversible()
{
    var pre = BaseSnapshot(includeIndex: false);
    var post = SnapshotWithoutFecha();
    var database = new FakeRehearsalDatabase(pre, post, pre);
    var result = await new RehearsalEngine().QualifyAsync(
        TestRelease(), ConsistentDiscovery(),
        ReleaseScript.FromText("forward", "ALTER TABLE dbo.Orden DROP COLUMN Fecha;"),
        ReleaseScript.FromText("rollback", "ALTER TABLE dbo.Orden ADD Fecha datetime2 NOT NULL;"),
        database);
    Equal(SchemaRollbackValidity.Valid, result.SchemaRollbackValidity);
    Equal(DataRollbackValidity.Unverified, result.DataRollbackValidity);
    Equal(RollbackCapability.RestoreRequired, result.RollbackCapability);
    True(!result.RollbackCertified);
}

static async Task DataContractCanValidateRollback()
{
    var schema = BaseSnapshot(includeIndex: false);
    var database = new FakeRehearsalDatabase(schema, schema, schema, schema);
    var validator = new FakeDataRollbackContract(DataRollbackValidity.Valid);
    var result = await new RehearsalEngine().QualifyAsync(
        TestRelease(), ConsistentDiscovery(),
        ReleaseScript.FromText("forward", "UPDATE dbo.Orden SET Fecha = SYSUTCDATETIME();"),
        ReleaseScript.FromText("rollback", "UPDATE dbo.Orden SET Fecha = '2020-01-01';"),
        database, validator);
    True(validator.PreCaptured);
    Equal(DataRollbackValidity.Valid, result.DataRollbackValidity);
    Equal(RollbackCapability.FullReversible, result.RollbackCapability);
    True(result.RollbackCertified && result.CanProceed);
}

static async Task RollbackMissingIndex()
{
    var pre = BaseSnapshot(includeIndex: true);
    var post = AddCommentSnapshot(includeIndex: true, "nvarchar(50)");
    var incompletePre = BaseSnapshot(includeIndex: false);
    var database = new FakeRehearsalDatabase(pre, post, incompletePre);
    var result = await new RehearsalEngine().QualifyAsync(
        TestRelease(), ConsistentDiscovery(), Forward(), Rollback(), database);
    Equal("BLOCKED_SCHEMA_ROLLBACK_MISMATCH", result.QualificationStatus);
    Equal(SchemaRollbackValidity.Invalid, result.SchemaRollbackValidity);
    True(result.RollbackDiff is { IsEquivalent: false });
    Equal(2, database.Executions.Count);
}

static Task IndexedColumnDependency()
{
    var analysis = new SqlScriptAnalyzer().Analyze("rollback",
        "ALTER TABLE dbo.Orden ALTER COLUMN Fecha datetime2 NOT NULL;", BaseSnapshot(includeIndex: true));
    True(analysis.Findings.Any(finding => finding.DependencyType == "index-column"
        && finding.DependentObject == "IX_Orden_Fecha" && finding.Severity == FindingSeverity.Blocking));
    return Task.CompletedTask;
}

static Task ReferencedForeignKeyDependency()
{
    var snapshot = BaseSnapshot(includeIndex: false);
    snapshot.Objects.Add(Object("foreign-key-column", "dbo", "OrdenLinea", "FK_OrdenLinea_Orden:0001",
        ("foreignKey", "FK_OrdenLinea_Orden"), ("column", "OrdenFecha"),
        ("referencedSchema", "dbo"), ("referencedTable", "Orden"), ("referencedColumn", "Fecha")));
    var analysis = new SqlScriptAnalyzer().Analyze("forward",
        "ALTER TABLE dbo.Orden ALTER COLUMN Fecha datetime2 NOT NULL;", snapshot);
    True(analysis.Findings.Any(finding => finding.DependencyType == "foreign-key-column"
        && finding.DependentObject == "FK_OrdenLinea_Orden" && finding.Severity == FindingSeverity.Blocking));
    return Task.CompletedTask;
}

static async Task Post1IndexDependencyDetected()
{
    var result = await RunPost1IndexScenario();
    var evidence = result.AnalysisEvidence ?? throw new InvalidOperationException("Missing analysis evidence.");
    True(evidence.RollbackAgainstPost1!.Findings.Any(finding =>
        finding.DependencyType == "index-column" && finding.DependentObject == "IX_Orden_Fecha"));
    True(result.ExecutionAudit.Contains("ROLLBACK_ANALYSIS_BASIS:POST1"));
}

static async Task Post1AnalysisIsAuthoritative()
{
    var result = await RunPost1IndexScenario();
    var evidence = result.AnalysisEvidence ?? throw new InvalidOperationException("Missing analysis evidence.");
    True(!evidence.PreliminaryRollbackAgainstPre.Findings.Any(finding => finding.DependentObject == "IX_Orden_Fecha"));
    True(evidence.RollbackAgainstPost1!.Findings.Any(finding => finding.DependentObject == "IX_Orden_Fecha"));
    True(ReferenceEquals(evidence.EffectiveDependencyAnalysis.Rollback, evidence.RollbackAgainstPost1));
    Equal("POST1", evidence.RollbackAnalysisBasis);
}

static async Task Post1ForeignKeyDependencyDetected()
{
    var pre = BaseSnapshot(includeIndex: false);
    var post = BaseSnapshot(includeIndex: false);
    post.Objects.Add(Object("foreign-key-column", "dbo", "OrdenLinea", "FK_OrdenLinea_Orden:0001",
        ("foreignKey", "FK_OrdenLinea_Orden"), ("column", "OrdenFecha"),
        ("referencedSchema", "dbo"), ("referencedTable", "Orden"), ("referencedColumn", "Fecha")));
    var database = new FakeRehearsalDatabase(pre, post, post);
    var result = await new RehearsalEngine().QualifyAsync(TestRelease(), ConsistentDiscovery(),
        ReleaseScript.FromText("forward", "ALTER TABLE dbo.OrdenLinea ADD CONSTRAINT FK_OrdenLinea_Orden FOREIGN KEY (OrdenFecha) REFERENCES dbo.Orden(Fecha);"),
        ReleaseScript.FromText("rollback", "ALTER TABLE dbo.Orden ALTER COLUMN Fecha datetime2 NULL;"), database);
    var postAnalysis = result.AnalysisEvidence?.RollbackAgainstPost1
        ?? throw new InvalidOperationException("Missing POST1 rollback analysis.");
    True(postAnalysis.Findings.Any(finding => finding.DependencyType == "foreign-key-column"
        && finding.DependentObject == "FK_OrdenLinea_Orden"));
}

static async Task Post1ConstraintAndComputedDependencyDetected()
{
    var pre = BaseSnapshot(includeIndex: false);
    var post = BaseSnapshot(includeIndex: false);
    post.Objects.Add(Object("check-constraint", "dbo", "Orden", "CK_Orden_Fecha",
        ("column", "Fecha"), ("definitionSha256", Hashing.Sha256("Fecha IS NOT NULL"))));
    post.Objects.Add(Object("schema-dependency", "dbo", "OrdenCalculada", "COMPUTED:dbo:Orden:Fecha",
        ("referencingType", "COMPUTED_COLUMN"), ("referencedSchema", "dbo"),
        ("referencedEntity", "Orden"), ("referencedColumn", "Fecha"), ("schemaBound", "true")));
    var database = new FakeRehearsalDatabase(pre, post, post);
    var result = await new RehearsalEngine().QualifyAsync(TestRelease(), ConsistentDiscovery(),
        ReleaseScript.FromText("forward", "ALTER TABLE dbo.Orden ADD CONSTRAINT CK_Orden_Fecha CHECK (Fecha IS NOT NULL);"),
        ReleaseScript.FromText("rollback", "ALTER TABLE dbo.Orden ALTER COLUMN Fecha datetime2 NULL;"), database);
    var findings = result.AnalysisEvidence?.RollbackAgainstPost1?.Findings
        ?? throw new InvalidOperationException("Missing POST1 rollback findings.");
    True(findings.Any(finding => finding.DependencyType == "check-constraint"));
    True(findings.Any(finding => finding.DependencyType == "schema-dependency"));
}

static async Task Post1RiskRaisesFinalAndIsAttested()
{
    var pre = BaseSnapshot(includeIndex: false);
    var post = BaseSnapshot(includeIndex: false);
    post.Objects.Add(Object("view", "dbo", "", "V_Orden", ("schemaBound", "true"),
        ("definitionSha256", Hashing.Sha256("SELECT Fecha FROM dbo.Orden"))));
    post.Objects.Add(Object("schema-dependency", "dbo", "V_Orden", "VIEW:dbo:Orden:Fecha",
        ("referencingType", "VIEW"), ("referencedSchema", "dbo"),
        ("referencedEntity", "Orden"), ("referencedColumn", "Fecha"), ("schemaBound", "true")));
    var forward = ReleaseScript.FromText("forward", "CREATE VIEW dbo.V_Orden AS SELECT Fecha FROM dbo.Orden;");
    var rollback = ReleaseScript.FromText("rollback", "ALTER TABLE dbo.Orden ALTER COLUMN Fecha datetime2 NULL;");
    var database = new FakeRehearsalDatabase(pre, post, post);
    var result = await new RehearsalEngine().QualifyAsync(
        TestRelease(), ConsistentDiscovery(), forward, rollback, database);
    var evidence = result.AnalysisEvidence ?? throw new InvalidOperationException("Missing POST1 risk evidence.");
    Equal(RiskLevel.Low, evidence.PreliminaryRisk.ForwardRisk);
    Equal(RiskLevel.Low, evidence.PreliminaryRisk.RollbackDependencyRisk);
    Equal(RiskLevel.High, evidence.QualificationRisk!.RollbackDependencyRisk);
    Equal(RiskLevel.High, evidence.QualificationRisk.FinalRisk);
    True(evidence.QualificationRisk.RequiresDbaApproval);

    var root = TempDirectory("db-release-post1-attestation");
    try
    {
        var package = new ReleasePackageWriter().Write(root, "run-post1", TestRelease(), forward, rollback, pre,
            evidence.PreliminaryDependencyAnalysis, evidence.PreliminaryRisk, result);
        var attestation = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(package.AttestationDirectory, "qualification-attestation.json")));
        Equal("POST1", attestation.RootElement.GetProperty("rollbackAnalysisBasis").GetString());
        Equal("HIGH", attestation.RootElement.GetProperty("rollbackDependencyRisk").GetString());
        Equal("HIGH", attestation.RootElement.GetProperty("finalRisk").GetString());
        True(!attestation.RootElement.GetProperty("requiresDbaApproval").GetBoolean());
        Equal("INVALID", attestation.RootElement.GetProperty("schemaRollbackValidity").GetString());
        True(File.Exists(Path.Combine(package.AttestationDirectory, "post1-rollback-analysis.json")));
        var effectiveDependency = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(package.AttestationDirectory, "dependency-analysis.json")));
        True(effectiveDependency.RootElement.GetProperty("rollback").GetProperty("findings").GetArrayLength() > 0);
        SequenceEqual(forward.Bytes, File.ReadAllBytes(Path.Combine(package.PayloadDirectory, "forward.sql")));
        SequenceEqual(rollback.Bytes, File.ReadAllBytes(Path.Combine(package.PayloadDirectory, "rollback.sql")));
    }
    finally { DeleteTemp(root); }
}

static async Task Post1LowDependencyStaysLow()
{
    var schema = BaseSnapshot(includeIndex: false);
    var database = new FakeRehearsalDatabase(schema, schema, schema, schema);
    var result = await new RehearsalEngine().QualifyAsync(TestRelease(), ConsistentDiscovery(),
        ReleaseScript.FromText("forward", "SELECT 1;"), ReleaseScript.FromText("rollback", "SELECT 1;"), database);
    Equal("QUALIFIED", result.QualificationStatus);
    Equal(RiskLevel.Low, result.AnalysisEvidence!.QualificationRisk!.RollbackDependencyRisk);
    Equal(RiskLevel.Low, result.AnalysisEvidence.QualificationRisk.FinalRisk);
}

static async Task Post1InsufficientConfidenceBlocksRollback()
{
    var pre = BaseSnapshot(includeIndex: false);
    var post = BaseSnapshot(includeIndex: false);
    post.UnsupportedSchemaFeatures.Add("columnstore-index-options");
    post.Objects.Add(Object("unsupported-schema-feature", "dbo", "", "Orden.IX_Orden_CS",
        ("feature", "columnstore-index-options")));
    var database = new FakeRehearsalDatabase(pre, post);
    var result = await new RehearsalEngine().QualifyAsync(TestRelease(), ConsistentDiscovery(),
        ReleaseScript.FromText("forward", "CREATE COLUMNSTORE INDEX IX_Orden_CS ON dbo.Orden(Fecha);"),
        ReleaseScript.FromText("rollback", "ALTER TABLE dbo.Orden ALTER COLUMN Fecha datetime2 NULL;"), database);
    Equal("BLOCKED_POST1_ROLLBACK_ANALYSIS_CONFIDENCE", result.QualificationStatus);
    Equal(AnalysisConfidence.Insufficient, result.AnalysisEvidence!.RollbackAgainstPost1!.Confidence);
    Equal(1, database.Executions.Count);
    Equal("forward", database.Executions[0].Role);
}

static Task HighRollbackWins()
{
    var snapshot = BaseSnapshot(includeIndex: false);
    var risk = AnalyzeRisk(snapshot, "SELECT 1;", "DROP TABLE dbo.Orden;");
    Equal(RiskLevel.Low, risk.ForwardRisk);
    Equal(RiskLevel.High, risk.RollbackRisk);
    Equal(RiskLevel.High, risk.FinalRisk);
    return Task.CompletedTask;
}

static Task LargeRollbackCostRaisesRollbackRisk()
{
    var snapshot = BaseSnapshot(includeIndex: false, indexMb: 180_000m);
    var risk = AnalyzeRisk(snapshot, "SELECT 1;", "CREATE INDEX IX_BIG ON dbo.Orden(Fecha);");
    Equal(RiskLevel.High, risk.RollbackRisk);
    Equal(RiskLevel.High, risk.OperationalRisk);
    Equal(RiskLevel.High, risk.FinalRisk);
    return Task.CompletedTask;
}

static Task HighDependencyWins()
{
    var snapshot = BaseSnapshot(includeIndex: true);
    var risk = AnalyzeRisk(snapshot, "ALTER TABLE dbo.Orden ALTER COLUMN Fecha datetime2 NOT NULL;", "SELECT 1;");
    Equal(RiskLevel.High, risk.DependencyRisk);
    Equal(RiskLevel.High, risk.FinalRisk);
    return Task.CompletedTask;
}

static Task AllLow()
{
    var risk = AnalyzeRisk(BaseSnapshot(includeIndex: false),
        "ALTER TABLE dbo.Orden ADD Comentario nvarchar(50) NULL;", "SELECT 1;");
    Equal(RiskLevel.Low, risk.ForwardRisk);
    Equal(RiskLevel.Low, risk.RollbackRisk);
    Equal(RiskLevel.Low, risk.DependencyRisk);
    Equal(RiskLevel.Low, risk.DataRisk);
    Equal(RiskLevel.Low, risk.OperationalRisk);
    Equal(RiskLevel.Low, risk.FinalRisk);
    return Task.CompletedTask;
}

static Task InvalidRollbackCannotProceed()
{
    var result = new RehearsalResult
    {
        QualificationStatus = "QUALIFIED",
        SchemaRollbackValidity = SchemaRollbackValidity.Invalid,
        DataRollbackValidity = DataRollbackValidity.NotApplicable,
        RollbackCapability = RollbackCapability.FullReversible,
        ForwardCertified = true,
        RollbackCertified = true,
        ReapplyCertified = true
    };
    True(!result.CanProceed);
    return Task.CompletedTask;
}

static async Task ReapplyMismatch()
{
    var pre = BaseSnapshot(includeIndex: true);
    var post1 = AddCommentSnapshot(includeIndex: true, "nvarchar(50)");
    var post2 = AddCommentSnapshot(includeIndex: true, "nvarchar(60)");
    var database = new FakeRehearsalDatabase(pre, post1, pre, post2);
    var result = await new RehearsalEngine().QualifyAsync(
        TestRelease(), ConsistentDiscovery(), Forward(), Rollback(), database);
    Equal("BLOCKED_REAPPLY_MISMATCH", result.QualificationStatus);
    Equal(SchemaRollbackValidity.Valid, result.SchemaRollbackValidity);
    True(!result.ReapplyCertified);
}

static async Task BlockedDiscoveryDoesNotExecute()
{
    var database = new FakeRehearsalDatabase();
    var result = await new RehearsalEngine().QualifyAsync(
        TestRelease(),
        new DiscoveryGate { ConsistencyStatus = "BLOCKED", ConsistencyReason = "BLOCKED_HISTORY_WITHOUT_REPO" },
        Forward(), Rollback(), database);
    Equal("BLOCKED_DISCOVERY", result.QualificationStatus);
    Equal(0, database.CaptureCount);
    Equal(0, database.Executions.Count);
}

static async Task ProdGuard()
{
    var database = new FakeRehearsalDatabase();
    var result = await new RehearsalEngine().QualifyAsync(
        TestRelease("PROD"), ConsistentDiscovery(), Forward(), Rollback(), database);
    Equal("BLOCKED_PROD_REHEARSAL", result.QualificationStatus);
    Equal(0, database.CaptureCount);
    Equal(0, database.Executions.Count);
}

static Task ReleaseScriptIsImmutable()
{
    var source = new byte[] { 1, 2, 3 };
    var script = new ReleaseScript("forward", source);
    var expectedHash = script.Sha256;
    source[0] = 9;
    var exposed = script.Bytes;
    exposed[1] = 9;
    Equal(expectedHash, script.Sha256);
    SequenceEqual(new byte[] { 1, 2, 3 }, script.Bytes);
    return Task.CompletedTask;
}

static Task PayloadIdentityIgnoresEnvironment()
{
    var forward = Forward();
    var rollback = Rollback();
    var test = ReleasePayloadBuilder.Build(TestRelease("TEST"), forward, rollback);
    var qa = ReleasePayloadBuilder.Build(TestRelease("QA"), forward, rollback);
    var prod = ReleasePayloadBuilder.Build(TestRelease("PROD"), forward, rollback);
    Equal(test.PayloadHash, qa.PayloadHash);
    Equal(test.PayloadHash, prod.PayloadHash);
    Equal(test.ForwardHash, prod.ForwardHash);
    Equal(test.RollbackHash, prod.RollbackHash);
    return Task.CompletedTask;
}

static Task PayloadChangesWithScriptByte()
{
    var release = TestRelease();
    var first = ReleasePayloadBuilder.Build(release, new ReleaseScript("forward", [1, 2, 3]), Rollback());
    var second = ReleasePayloadBuilder.Build(release, new ReleaseScript("forward", [1, 2, 4]), Rollback());
    NotEqual(first.ForwardHash, second.ForwardHash);
    NotEqual(first.PayloadHash, second.PayloadHash);
    return Task.CompletedTask;
}

static Task AttestationsAccumulateWithoutChangingPayload()
{
    var root = TempDirectory("db-release-attestations");
    try
    {
        var snapshot = BaseSnapshot(includeIndex: false);
        var forward = Forward();
        var rollback = Rollback();
        var dependency = AnalyzePair(snapshot, forward.Text, rollback.Text);
        var lowRisk = new RiskEngine().Evaluate(dependency, snapshot);
        var rehearsal = AnalyzedResult(snapshot);
        var writer = new ReleasePackageWriter();
        var test = writer.Write(root, "run-test", TestRelease("TEST"), forward, rollback, snapshot,
            dependency, lowRisk, rehearsal, new Dictionary<string, string> { ["run"] = "test" });
        var qa = writer.Write(root, "run-qa", TestRelease("QA"), forward, rollback, snapshot,
            dependency, lowRisk, rehearsal, new Dictionary<string, string> { ["run"] = "qa" });
        Equal(test.PayloadHash, qa.PayloadHash);
        Equal(test.PayloadDirectory, qa.PayloadDirectory);
        NotEqual(test.AttestationDirectory, qa.AttestationDirectory);
        True(File.Exists(Path.Combine(test.AttestationDirectory, "qualification-attestation.json")));
        True(File.Exists(Path.Combine(qa.AttestationDirectory, "qualification-attestation.json")));
        SequenceEqual(forward.Bytes, File.ReadAllBytes(Path.Combine(test.PayloadDirectory, "forward.sql")));
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task PackageUsesSchemaHashNames()
{
    var root = TempDirectory("db-release-schema-hash");
    try
    {
        var snapshot = BaseSnapshot(includeIndex: false);
        var dependency = AnalyzePair(snapshot, Forward().Text, Rollback().Text);
        var result = new ReleasePackageWriter().Write(root, "run-1", TestRelease(), Forward(), Rollback(), snapshot,
            dependency, new RiskEngine().Evaluate(dependency, snapshot), AnalyzedResult(snapshot));
        True(File.Exists(Path.Combine(result.AttestationDirectory, "pre-schema.sha256")));
        True(!File.Exists(Path.Combine(result.AttestationDirectory, "pre-state.sha256")));
        True(!File.Exists(Path.Combine(result.PayloadDirectory, "metadata.json")));
        True(File.Exists(Path.Combine(result.PayloadDirectory, "payload.json")));
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task SchemaArtifactsDoNotExposeRawDefinitions()
{
    const string sensitiveDefinition = "SELECT 'Password=forbidden-artifact-value' AS Value;";
    var root = TempDirectory("db-release-safe-schema-artifact");
    try
    {
        var snapshot = BaseSnapshot(includeIndex: false);
        snapshot.Objects.Add(Object("view", "dbo", "", "SafeView", ("definition", sensitiveDefinition)));
        var canonical = SchemaCanonicalizer.Canonicalize(snapshot);
        True(!canonical.Json.Contains("forbidden-artifact-value", StringComparison.Ordinal));
        True(canonical.Json.Contains("definitionSha256", StringComparison.Ordinal));

        var dependency = AnalyzePair(snapshot, Forward().Text, Rollback().Text);
        var result = new ReleasePackageWriter().Write(root, "run-safe-artifact", TestRelease(), Forward(), Rollback(),
            snapshot, dependency, new RiskEngine().Evaluate(dependency, snapshot), AnalyzedResult(snapshot));
        foreach (var file in Directory.EnumerateFiles(result.AttestationDirectory, "*", SearchOption.AllDirectories))
            True(!File.ReadAllText(file).Contains("forbidden-artifact-value", StringComparison.Ordinal));
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task RunMetadataRejectsSensitiveKeys()
{
    var root = TempDirectory("db-release-sensitive-run-metadata");
    try
    {
        var snapshot = BaseSnapshot(includeIndex: false);
        var dependency = AnalyzePair(snapshot, Forward().Text, Rollback().Text);
        var rejected = false;
        try
        {
            _ = new ReleasePackageWriter().Write(root, "run-sensitive", TestRelease(), Forward(), Rollback(),
                snapshot, dependency, new RiskEngine().Evaluate(dependency, snapshot), AnalyzedResult(snapshot),
                new Dictionary<string, string> { ["connectionString"] = "TopSecret" });
        }
        catch (InvalidOperationException exception)
        {
            Equal("RUN_METADATA_SENSITIVE_KEY_REJECTED", exception.Message);
            rejected = true;
        }
        True(rejected);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            True(!File.ReadAllText(file).Contains("TopSecret", StringComparison.Ordinal));
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task InvalidRollbackDoesNotRequestApproval()
{
    var root = TempDirectory("db-release-invalid");
    try
    {
        var snapshot = BaseSnapshot(includeIndex: false);
        var dependency = AnalyzePair(snapshot, "SELECT 1;", "DROP TABLE dbo.Orden;");
        var risk = new RiskEngine().Evaluate(dependency, snapshot);
        True(risk.RequiresDbaApproval);
        var rehearsal = new RehearsalResult
        {
            QualificationStatus = "BLOCKED_SCHEMA_ROLLBACK_MISMATCH",
            SchemaRollbackValidity = SchemaRollbackValidity.Invalid,
            DataRollbackValidity = DataRollbackValidity.NotApplicable,
            RollbackCapability = RollbackCapability.Unknown,
            ForwardCertified = true,
            RollbackCertified = false,
            ReapplyCertified = false,
            Pre = SchemaCanonicalizer.Canonicalize(snapshot)
        };
        var package = new ReleasePackageWriter().Write(root, "run-invalid", TestRelease(),
            ReleaseScript.FromText("forward", "SELECT 1;"), ReleaseScript.FromText("rollback", "DROP TABLE dbo.Orden;"),
            snapshot, dependency, risk, rehearsal);
        var attestation = JsonDocument.Parse(File.ReadAllText(Path.Combine(package.AttestationDirectory, "qualification-attestation.json")));
        True(!attestation.RootElement.GetProperty("requiresDbaApproval").GetBoolean());
        Equal("INVALID", attestation.RootElement.GetProperty("schemaRollbackValidity").GetString());
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task ValidHighRollbackRequiresApproval()
{
    var root = TempDirectory("db-release-valid-high");
    try
    {
        var snapshot = BaseSnapshot(includeIndex: false, indexMb: 180_000m);
        var forward = ReleaseScript.FromText("forward", "SELECT 1;");
        var rollback = ReleaseScript.FromText("rollback", "CREATE INDEX IX_BIG ON dbo.Orden(Fecha);");
        var dependency = AnalyzePair(snapshot, forward.Text, rollback.Text);
        var risk = new RiskEngine().Evaluate(dependency, snapshot);
        Equal(RiskLevel.High, risk.FinalRisk);
        var rehearsal = new RehearsalResult
        {
            QualificationStatus = "QUALIFIED",
            SchemaRollbackValidity = SchemaRollbackValidity.Valid,
            DataRollbackValidity = DataRollbackValidity.NotApplicable,
            RollbackCapability = RollbackCapability.FullReversible,
            ForwardCertified = true,
            RollbackCertified = true,
            ReapplyCertified = true,
            Pre = SchemaCanonicalizer.Canonicalize(snapshot)
        };
        var package = new ReleasePackageWriter().Write(root, "run-valid-high", TestRelease(), forward, rollback,
            snapshot, dependency, risk, rehearsal);
        var attestation = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(package.AttestationDirectory, "qualification-attestation.json")));
        True(attestation.RootElement.GetProperty("requiresDbaApproval").GetBoolean());
        Equal("VALID", attestation.RootElement.GetProperty("schemaRollbackValidity").GetString());
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task TargetHighWins()
{
    var preflight = new TargetEnvironmentPreflight
    {
        Environment = "PROD",
        ImpactMetrics = [Metric(rows: 20_000_000, reservedMb: 100)]
    };
    var result = new TargetRiskEngine().Combine(RiskLevel.Low, preflight);
    Equal(RiskLevel.High, result.TargetPreflightRisk);
    Equal(RiskLevel.High, result.FinalTargetRisk);
    return Task.CompletedTask;
}

static Task QualifiedHighWins()
{
    var result = new TargetRiskEngine().Combine(RiskLevel.High,
        new TargetEnvironmentPreflight { Environment = "PROD", ImpactMetrics = [Metric(rows: 1, reservedMb: 1)] });
    Equal(RiskLevel.Low, result.TargetPreflightRisk);
    Equal(RiskLevel.High, result.FinalTargetRisk);
    return Task.CompletedTask;
}

static Task QualifiedAndTargetLowRemainLow()
{
    var result = new TargetRiskEngine().Combine(RiskLevel.Low,
        new TargetEnvironmentPreflight { Environment = "PROD", ImpactMetrics = [Metric(rows: 1, reservedMb: 1)] });
    Equal(RiskLevel.Low, result.TargetPreflightRisk);
    Equal(RiskLevel.Low, result.FinalTargetRisk);
    return Task.CompletedTask;
}

static Task TargetRelationshipsRaiseRisk()
{
    var result = new TargetRiskEngine().Combine(RiskLevel.Low,
        new TargetEnvironmentPreflight
        {
            Environment = "PROD",
            ImpactMetrics = [Metric(rows: 1, reservedMb: 1, foreignKeys: 60, triggers: 41)]
        });
    Equal(RiskLevel.High, result.TargetPreflightRisk);
    Equal(RiskLevel.High, result.FinalTargetRisk);
    return Task.CompletedTask;
}

static Task UnsupportedCoverageDegradesConfidence()
{
    var snapshot = BaseSnapshot(includeIndex: false);
    snapshot.UnsupportedSchemaFeatures.Add("partition-function-definition");
    var analysis = new SqlScriptAnalyzer().Analyze("forward", "ALTER TABLE dbo.Orden ADD X int NULL;", snapshot);
    Equal(SchemaCoverage.Partial, snapshot.SchemaCoverage);
    Equal(AnalysisConfidence.Partial, analysis.Confidence);
    Equal(RiskLevel.Medium, RiskFor(analysis, SelectAnalysis(snapshot)).FinalRisk);
    return Task.CompletedTask;
}

static Task RelevantUnsupportedFeatureBlocks()
{
    var snapshot = BaseSnapshot(includeIndex: false);
    snapshot.UnsupportedSchemaFeatures.Add("data-compression");
    snapshot.Objects.Add(Object("unsupported-schema-feature", "dbo", "", "Orden", ("feature", "data-compression")));
    var analysis = new SqlScriptAnalyzer().Analyze("forward",
        "ALTER TABLE dbo.Orden ALTER COLUMN Fecha datetime2 NOT NULL;", snapshot);
    Equal(AnalysisConfidence.Insufficient, analysis.Confidence);
    var risk = RiskFor(analysis, SelectAnalysis(snapshot));
    Equal(RiskLevel.High, risk.FinalRisk);
    True(risk.AutoPromotionBlocked);
    return Task.CompletedTask;
}

static async Task CliAnalyzeOnlyPackage()
{
    var root = TempDirectory("db-release-cli");
    try
    {
        var paths = WriteCliFixtures(root);
        var exit = await QualificationCli.RunAsync(CliArguments(paths, "CONSISTENT", "CONSISTENT_EXISTING_SQL"));
        Equal(0, exit);
        var result = JsonDocument.Parse(File.ReadAllText(paths.Result));
        Equal("ANALYZED_NOT_REHEARSED", result.RootElement.GetProperty("qualificationStatus").GetString());
        True(File.Exists(Path.Combine(result.RootElement.GetProperty("payloadDirectory").GetString()!, "forward.sql")));
        True(File.Exists(Path.Combine(result.RootElement.GetProperty("attestationDirectory").GetString()!, "qualification-attestation.json")));
    }
    finally { DeleteTemp(root); }
}

static async Task CliBlocksInconsistentDiscovery()
{
    var root = TempDirectory("db-release-cli-blocked");
    try
    {
        var paths = WriteCliFixtures(root);
        var exit = await QualificationCli.RunAsync(CliArguments(paths, "BLOCKED", "BLOCKED_HISTORY_WITHOUT_REPO"));
        Equal(4, exit);
        var result = JsonDocument.Parse(File.ReadAllText(paths.Result));
        Equal("BLOCKED_DISCOVERY", result.RootElement.GetProperty("qualificationStatus").GetString());
    }
    finally { DeleteTemp(root); }
}

static RiskAnalysisReport AnalyzeRisk(SchemaSnapshot snapshot, string forward, string rollback) =>
    new RiskEngine().Evaluate(AnalyzePair(snapshot, forward, rollback), snapshot);

static async Task<RehearsalResult> RunPost1IndexScenario()
{
    var pre = BaseSnapshot(includeIndex: false);
    var post = BaseSnapshot(includeIndex: true);
    var database = new FakeRehearsalDatabase(pre, post, post);
    return await new RehearsalEngine().QualifyAsync(TestRelease(), ConsistentDiscovery(),
        ReleaseScript.FromText("forward", "CREATE INDEX IX_Orden_Fecha ON dbo.Orden(Fecha);"),
        ReleaseScript.FromText("rollback", "ALTER TABLE dbo.Orden ALTER COLUMN Fecha datetime2 NULL;"), database);
}

static RiskAnalysisReport RiskFor(ScriptAnalysis forward, ScriptAnalysis rollback) =>
    new RiskEngine().Evaluate(new DependencyAnalysisReport { Forward = forward, Rollback = rollback }, BaseSnapshot(includeIndex: false));

static DependencyAnalysisReport AnalyzePair(SchemaSnapshot snapshot, string forward, string rollback)
{
    var analyzer = new SqlScriptAnalyzer();
    return new DependencyAnalysisReport
    {
        Forward = analyzer.Analyze("forward", forward, snapshot),
        Rollback = analyzer.Analyze("rollback", rollback, snapshot)
    };
}

static ScriptAnalysis SelectAnalysis(SchemaSnapshot snapshot) =>
    new SqlScriptAnalyzer().Analyze("rollback", "SELECT 1;", snapshot);

static RehearsalResult AnalyzedResult(SchemaSnapshot snapshot) => new()
{
    QualificationStatus = "ANALYZED_NOT_REHEARSED",
    SchemaRollbackValidity = SchemaRollbackValidity.NotTested,
    DataRollbackValidity = DataRollbackValidity.NotApplicable,
    RollbackCapability = RollbackCapability.Unknown,
    ForwardCertified = false,
    RollbackCertified = false,
    ReapplyCertified = false,
    Pre = SchemaCanonicalizer.Canonicalize(snapshot)
};

static ReleaseDescriptor TestRelease(string environment = "TEST") => new()
{
    ReleaseId = "release-001",
    Environment = environment,
    SourceKind = "SQL",
    Scenario = "EXISTING_SQL",
    DatabaseLifecycle = "EXISTING"
};

static DiscoveryGate ConsistentDiscovery() => new()
{
    ConsistencyStatus = "CONSISTENT",
    ConsistencyReason = "CONSISTENT_EXISTING_SQL"
};

static ReleaseScript Forward() => ReleaseScript.FromText("forward", "ALTER TABLE dbo.Orden ADD Comentario nvarchar(50) NULL;\n");
static ReleaseScript Rollback() => ReleaseScript.FromText("rollback", "ALTER TABLE dbo.Orden DROP COLUMN Comentario;\n");

static SchemaSnapshot BaseSnapshot(bool includeIndex, bool nullable = false, long rows = 10, decimal indexMb = 1)
{
    var objects = new List<SchemaObject>
    {
        Object("schema", "dbo", "", "dbo", ("owner", "dbo")),
        Object("table", "dbo", "", "Orden", ("temporalType", "0")),
        Object("column", "dbo", "Orden", "Fecha", ("type", "datetime2"),
            ("nullable", nullable ? "true" : "false"), ("identity", "false"), ("computed", "false"))
    };
    if (includeIndex)
    {
        objects.Add(Object("index", "dbo", "Orden", "IX_Orden_Fecha",
            ("type", "NONCLUSTERED"), ("unique", "false"), ("disabled", "false")));
        objects.Add(Object("index-column", "dbo", "Orden", "IX_Orden_Fecha:1",
            ("index", "IX_Orden_Fecha"), ("column", "Fecha"), ("role", "key"), ("descending", "false")));
    }
    return new SchemaSnapshot { Objects = objects, ImpactMetrics = [Metric(rows, 1, indexMb, includeIndex)] };
}

static SchemaSnapshot AddCommentSnapshot(bool includeIndex, string type)
{
    var snapshot = BaseSnapshot(includeIndex);
    snapshot.Objects.Add(Object("column", "dbo", "Orden", "Comentario", ("type", type), ("nullable", "true")));
    return snapshot;
}

static SchemaSnapshot SnapshotWithoutFecha() => new()
{
    Objects =
    [
        Object("schema", "dbo", "", "dbo", ("owner", "dbo")),
        Object("table", "dbo", "", "Orden", ("temporalType", "0"))
    ],
    ImpactMetrics = [Metric(10, 1)]
};

static TableImpactMetric Metric(
    long rows,
    decimal reservedMb,
    decimal indexMb = 1,
    bool includeIndex = false,
    int foreignKeys = 0,
    int triggers = 0) => new()
{
    Schema = "dbo", Table = "Orden", RowCount = rows, ReservedMb = reservedMb, IndexMb = indexMb,
    LobMb = 0, PartitionCount = 1, IndexCount = includeIndex ? 1 : 0,
    ForeignKeyCount = foreignKeys, TriggerCount = triggers, DependencyCount = includeIndex ? 1 : 0
};

static SchemaObject Object(string kind, string schema, string parent, string name, params (string Key, string Value)[] properties) => new()
{
    Kind = kind,
    Schema = schema,
    Parent = parent,
    Name = name,
    Properties = new SortedDictionary<string, string>(properties.ToDictionary(item => item.Key, item => item.Value), StringComparer.Ordinal)
};

static (string Forward, string Rollback, string Schema, string Output, string Result) WriteCliFixtures(string root)
{
    Directory.CreateDirectory(root);
    var paths = (
        Forward: Path.Combine(root, "forward.sql"),
        Rollback: Path.Combine(root, "rollback.sql"),
        Schema: Path.Combine(root, "schema.json"),
        Output: Path.Combine(root, "package-root"),
        Result: Path.Combine(root, "result.json"));
    File.WriteAllText(paths.Forward, Forward().Text);
    File.WriteAllText(paths.Rollback, Rollback().Text);
    File.WriteAllText(paths.Schema, JsonSerializer.Serialize(BaseSnapshot(includeIndex: false), JsonDefaults.Indented));
    return paths;
}

static string[] CliArguments(
    (string Forward, string Rollback, string Schema, string Output, string Result) paths,
    string discoveryStatus,
    string discoveryReason) =>
[
    "analyze", "--release-id", "release-cli-001", "--attestation-id", "run-cli-001",
    "--environment", "TEST", "--source-kind", "SQL", "--scenario", "EXISTING_SQL",
    "--database-lifecycle", "EXISTING", "--discovery-status", discoveryStatus,
    "--discovery-reason", discoveryReason, "--forward", paths.Forward, "--rollback", paths.Rollback,
    "--schema", paths.Schema, "--output", paths.Output, "--result", paths.Result
];

static string TempDirectory(string prefix) => Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
static void DeleteTemp(string path) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }

static void True(bool condition) { if (!condition) throw new InvalidOperationException("Expected condition to be true."); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
}
static void NotEqual<T>(T first, T second)
{
    if (EqualityComparer<T>.Default.Equals(first, second))
        throw new InvalidOperationException($"Expected values to differ, both were '{first}'.");
}
static void SequenceEqual(byte[] expected, byte[] actual)
{
    if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Byte sequences differ.");
}

internal sealed class FakeRehearsalDatabase(params SchemaSnapshot[] snapshots) : IRehearsalDatabase
{
    private readonly Queue<SchemaSnapshot> _snapshots = new(snapshots);
    public int CaptureCount { get; private set; }
    public List<(string Role, string Hash)> Executions { get; } = [];

    public Task<SchemaSnapshot> CaptureSchemaAsync(CancellationToken cancellationToken = default)
    {
        CaptureCount++;
        if (_snapshots.Count == 0) throw new InvalidOperationException("Unexpected schema capture.");
        return Task.FromResult(_snapshots.Dequeue());
    }

    public Task ExecuteSqlAsync(ReleaseScript script, string expectedSha256, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(expectedSha256, script.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Executed script hash differs from expected hash.");
        Executions.Add((script.Role, expectedSha256));
        return Task.CompletedTask;
    }
}

internal sealed class FakeDataRollbackContract(DataRollbackValidity result) : IDataRollbackValidationContract
{
    public bool PreCaptured { get; private set; }
    public Task CapturePreDataAsync(CancellationToken cancellationToken = default)
    {
        PreCaptured = true;
        return Task.CompletedTask;
    }
    public Task<DataRollbackValidity> ValidateRollbackDataAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
}
