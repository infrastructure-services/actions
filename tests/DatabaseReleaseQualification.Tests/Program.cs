using System.Text.Json;
using DatabaseReleaseQualification;

var tests = new (string Name, Func<Task> Run)[]
{
    ("fingerprint estable ante distinto orden", FingerprintIgnoresOrder),
    ("diferencia estructural cambia fingerprint", StructuralDifferenceChangesFingerprint),
    ("métricas no contaminan fingerprint", MetricsDoNotChangeFingerprint),
    ("dos captures equivalentes son determinísticos", EquivalentCapturesAreDeterministic),
    ("captures estructuralmente distintas se bloquean", DifferentCapturesAreNondeterministic),
    ("metadata de capture separa métricas no disponibles", UnavailableMetricsDoNotFailCapture),
    ("discovery bloqueado permite capture pero no rehearsal", BlockedDiscoveryAllowsCaptureOnly),
    ("falta de metadata se clasifica de forma estable", MetadataVisibilityFailureIsClassified),
    ("fallo de conexión se clasifica de forma estable", ConnectionFailureIsClassified),
    ("guard de schema capture admite solo SELECT", SchemaCaptureSqlGuard),
    ("queries degradan por versión SQL Server", SqlServerVersionQueriesDegradeSafely),
    ("CLI capture sin conexión falla antes de SQL", CliSchemaCaptureRequiresEnvironmentSecret),
    ("CLI compare devuelve estado no determinístico", CliSchemaComparisonBlocksMismatch),
    ("artifacts no conservan valores de identidad", SchemaCaptureArtifactsExcludeIdentityValue),
    ("registry BASELINE_REQUIRED bloquea y pide candidate", RegistryBaselineRequired),
    ("registryFormatVersion 1 es válido", RegistryFormatVersionOneIsValid),
    ("registry sin versión es inválido", RegistryMissingFormatVersionIsInvalid),
    ("registry con versión desconocida es inválido", RegistryUnknownFormatVersionIsInvalid),
    ("registry CERTIFIED con hash igual produce MATCH", RegistryCertifiedMatch),
    ("registry CERTIFIED con hash distinto detecta drift", RegistryCertifiedMismatch),
    ("target no registrado queda bloqueado", RegistryTargetNotRegistered),
    ("registry rechaza targets duplicados", RegistryRejectsDuplicateTargets),
    ("registry rechaza CERTIFIED sin hash", RegistryRejectsCertifiedWithoutHash),
    ("registry rechaza SHA256 inválido", RegistryRejectsInvalidHash),
    ("registry rechaza environment inválido", RegistryRejectsInvalidEnvironment),
    ("registry rechaza certificationStatus inválido", RegistryRejectsInvalidCertificationStatus),
    ("registry rechaza lifecycle inválido", RegistryRejectsInvalidLifecycle),
    ("registry rechaza campos obligatorios vacíos", RegistryRejectsEmptyRequiredFields),
    ("registry rechaza baseline contradictorio", RegistryRejectsContradictoryBaseline),
    ("baseline candidate nunca queda certificado", BaselineCandidateIsNeverCertified),
    ("evaluación baseline no modifica registry", RegistryEvaluationDoesNotModifyRegistry),
    ("observed hash nunca sustituye certified hash", ObservedHashNeverReplacesCertifiedHash),
    ("evidencia de mismatch no inventa diff estructural", DriftEvidenceIsHashOnly),
    ("registry commit aparece en evidencia", RegistryCommitAppearsInEvidence),
    ("registry file SHA256 corresponde a bytes reales", RegistryFileShaMatchesBytes),
    ("registry file SHA256 declarado incorrecto falla cerrado", RegistryFileShaMismatchFailsClosed),
    ("cambiar targets cambia registry file SHA256", RegistryContentChangesFileSha),
    ("cambiar observed no cambia registry file SHA256", ObservedHashDoesNotChangeRegistryFileSha),
    ("baseline candidate conserva provenance", BaselineCandidatePreservesProvenance),
    ("drift evidence conserva provenance", DriftEvidencePreservesProvenance),
    ("MATCH sólo habilita gate de schema drift", MatchIsEligibleForSchemaDrift),
    ("CLI baseline genera artifacts sin certificar", CliDatabaseStateBaselineProducesEvidence),
    ("CLI registry inválido falla cerrado con evidencia", CliDatabaseStateInvalidRegistryFailsClosed),
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
    ("CLI bloquea discovery inconsistente", CliBlocksInconsistentDiscovery),
    ("sin Certified PRE no existe certificación automática", DerivedCertificationRequiresCertifiedPre),
    ("cadena íntegra produce certificación derivada automática", DerivedCertificationIsAutomatic),
    ("PRE con drift prohíbe certificación automática", PreDriftBlocksDerivedCertification),
    ("POST distinto del qualified POST bloquea certificación", PostMismatchBlocksDerivedCertification),
    ("LOW autorizado por policy certifica automáticamente", LowRiskExactDeploymentCertifiesAutomatically),
    ("HIGH sin autorización DBA no certifica", HighRiskMissingDeploymentAuthorizationBlocks),
    ("rollback INVALID bloquea incluso con autorización", InvalidRollbackCannotBeOverriddenByAuthorization),
    ("cambio out-of-band requiere reconciliación", OutOfBandRequiresReconciliation),
    ("bootstrap queda listo para aprobación humana", BootstrapIsReadyForHumanApproval),
    ("transición automática genera evidencia completa", AutomaticCertificationEvidenceIsComplete),
    ("payload ejecutado distinto bloquea transición derivada", ExactQualifiedReleaseIsRequired),
    ("HIGH con autorización DBA certifica sin segunda aprobación", HighRiskAuthorizedDeploymentCertifiesAutomatically),
    ("CICDV3 continúa bloqueada por lineage", Cicdv3BootstrapRemainsBlockedByLineage),
    ("RESTORE_REQUIRED autorizado puede certificar", RestoreRequiredAuthorizedCertifiesAutomatically),
    ("RESTORE_REQUIRED sin autorización queda bloqueado", RestoreRequiredWithoutAuthorizationBlocks),
    ("qualification gate no aprobado bloquea certificación", QualificationGateFailureBlocks)
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

static Task EquivalentCapturesAreDeterministic()
{
    var root = TempDirectory("schema-capture-equivalent");
    try
    {
        var writer = new SchemaCaptureArtifactWriter();
        var first = writer.WriteCapture(Path.Combine(root, "capture-1"), "capture-1",
            CaptureSource(BaseSnapshot(includeIndex: true)));
        var reordered = BaseSnapshot(includeIndex: true);
        reordered.Objects.Reverse();
        var second = writer.WriteCapture(Path.Combine(root, "capture-2"), "capture-2",
            CaptureSource(reordered));
        var comparison = writer.CompareAndWrite(first, second, Path.Combine(root, "comparison"));
        True(comparison.Deterministic);
        Equal(SchemaCaptureStatuses.Success, comparison.Status);
        Equal(first.SchemaHash, second.SchemaHash);
        True(File.Exists(Path.Combine(root, "capture-1", "canonical-schema.json")));
        True(File.Exists(Path.Combine(root, "capture-2", "metadata.json")));
        True(File.Exists(Path.Combine(root, "capture-2", "impact-metrics.json")));
        True(File.Exists(Path.Combine(root, "comparison", "determinism.json")));
        True(File.Exists(Path.Combine(root, "comparison", "schema-diff.json")));
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task DifferentCapturesAreNondeterministic()
{
    var root = TempDirectory("schema-capture-different");
    try
    {
        var writer = new SchemaCaptureArtifactWriter();
        var first = writer.WriteCapture(Path.Combine(root, "capture-1"), "capture-1",
            CaptureSource(BaseSnapshot(includeIndex: false)));
        var second = writer.WriteCapture(Path.Combine(root, "capture-2"), "capture-2",
            CaptureSource(BaseSnapshot(includeIndex: true)));
        var comparison = writer.CompareAndWrite(first, second, Path.Combine(root, "comparison"));
        True(!comparison.Deterministic);
        Equal(SchemaCaptureStatuses.Nondeterministic, comparison.Status);
        Equal("CONCURRENT_DDL_OR_NONDETERMINISTIC_CAPTURE", comparison.DiagnosticCode);
        True(!comparison.SchemaDiff.IsEquivalent);
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task UnavailableMetricsDoNotFailCapture()
{
    var root = TempDirectory("schema-capture-no-metrics");
    try
    {
        var snapshot = BaseSnapshot(includeIndex: true, rows: 100);
        snapshot.ImpactMetrics.Clear();
        var artifact = new SchemaCaptureArtifactWriter().WriteCapture(root, "capture-1",
            CaptureSource(snapshot, MetricsAvailability.Unavailable, "SQL_229"));
        Equal(SchemaCaptureStatuses.Success, artifact.Metadata.Status);
        Equal(MetricsAvailability.Unavailable, artifact.Metadata.MetricsAvailability);
        Equal("SQL_229", artifact.Metadata.MetricsDiagnosticCode);
        Equal(SchemaCanonicalizer.Canonicalize(BaseSnapshot(includeIndex: true, rows: 999)).Sha256, artifact.SchemaHash);
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static async Task BlockedDiscoveryAllowsCaptureOnly()
{
    True(SchemaCapturePolicy.AllowsReadOnlyCapture("BLOCKED_HISTORY_WITHOUT_REPO"));
    var database = new FakeRehearsalDatabase();
    var result = await new RehearsalEngine().QualifyAsync(
        TestRelease(),
        new DiscoveryGate { ConsistencyStatus = "BLOCKED", ConsistencyReason = "BLOCKED_HISTORY_WITHOUT_REPO" },
        Forward(), Rollback(), database);
    Equal("BLOCKED_DISCOVERY", result.QualificationStatus);
    Equal(0, database.CaptureCount);
    Equal(0, database.Executions.Count);
}

static Task MetadataVisibilityFailureIsClassified()
{
    var failure = SchemaCaptureErrorClassifier.Classify(
        SchemaCapturePhase.MetadataVisibility, new InvalidOperationException("not emitted"));
    Equal(SchemaCaptureStatuses.MetadataVisibility, failure.Status);
    Equal("InvalidOperationException", failure.DiagnosticCode);
    Equal(5, failure.ExitCode);
    return Task.CompletedTask;
}

static Task ConnectionFailureIsClassified()
{
    var failure = SchemaCaptureErrorClassifier.Classify(
        SchemaCapturePhase.OpenConnection, new InvalidOperationException("not emitted"));
    Equal(SchemaCaptureStatuses.DatabaseUnreachable, failure.Status);
    Equal("InvalidOperationException", failure.DiagnosticCode);
    Equal(4, failure.ExitCode);
    return Task.CompletedTask;
}

static Task SchemaCaptureSqlGuard()
{
    SqlServerSchemaReader.EnsureSelectOnlySql("SELECT DB_NAME();");
    SqlServerSchemaReader.EnsureSelectOnlySql("WITH objects AS (SELECT 1 AS id) SELECT id FROM objects;");
    foreach (var sql in new[]
    {
        "INSERT INTO dbo.T VALUES (1);", "UPDATE dbo.T SET A = 1;", "DELETE FROM dbo.T;",
        "MERGE dbo.T AS target USING dbo.S AS source ON 1 = 0 WHEN NOT MATCHED THEN INSERT (A) VALUES (1);",
        "CREATE TABLE dbo.T(A int);", "ALTER TABLE dbo.T ADD B int;", "DROP TABLE dbo.T;",
        "TRUNCATE TABLE dbo.T;", "EXEC dbo.p;", "EXECUTE dbo.p;"
    })
    {
        var blocked = false;
        try { SqlServerSchemaReader.EnsureSelectOnlySql(sql); }
        catch (InvalidOperationException) { blocked = true; }
        True(blocked);
    }
    return Task.CompletedTask;
}

static Task SqlServerVersionQueriesDegradeSafely()
{
    var type = typeof(SqlServerSchemaReader);
    var tablesMethod = type.GetMethod("TablesSql", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("TablesSql not found.");
    var featuresMethod = type.GetMethod("UnsupportedSchemaFeaturesSql", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("UnsupportedSchemaFeaturesSql not found.");
    string Tables(int version) => (string)(tablesMethod.Invoke(null, [version]) ?? "");
    string Features(int version) => (string)(featuresMethod.Invoke(null, [version]) ?? "");

    var version10Tables = Tables(10);
    var version10Features = Features(10);
    True(!version10Tables.Contains("temporal_type", StringComparison.Ordinal));
    True(!version10Tables.Contains("is_memory_optimized", StringComparison.Ordinal));
    True(!version10Features.Contains("sys.sequences", StringComparison.Ordinal));
    True(!version10Features.Contains("temporal_type", StringComparison.Ordinal));

    True(Tables(12).Contains("is_memory_optimized", StringComparison.Ordinal));
    True(!Tables(12).Contains("temporal_type_desc", StringComparison.Ordinal));
    True(Features(12).Contains("sys.sequences", StringComparison.Ordinal));
    True(Features(13).Contains("temporal_type", StringComparison.Ordinal));
    True(Features(14).Contains("is_node", StringComparison.Ordinal));
    True(Features(16).Contains("ledger_type", StringComparison.Ordinal));

    foreach (var sql in new[] { version10Tables, version10Features, Tables(12), Features(12), Tables(13), Features(13), Features(14), Features(16) })
    {
        SqlServerSchemaReader.EnsureSelectOnlySql(sql);
    }
    foreach (var field in type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        .Where(field => field.FieldType == typeof(string) && field.Name.EndsWith("Sql", StringComparison.Ordinal)))
    {
        SqlServerSchemaReader.EnsureSelectOnlySql((string)(field.GetValue(null) ?? ""));
    }
    return Task.CompletedTask;
}

static async Task CliSchemaCaptureRequiresEnvironmentSecret()
{
    var root = TempDirectory("schema-capture-cli-missing-connection");
    var previous = Environment.GetEnvironmentVariable("DB_CONNECTION");
    try
    {
        Environment.SetEnvironmentVariable("DB_CONNECTION", null);
        var resultPath = Path.Combine(root, "result.json");
        var exit = await QualificationCli.RunAsync([
            "capture-schema", "--environment", "TEST", "--capture-id", "capture-1",
            "--output", Path.Combine(root, "capture-1"), "--result", resultPath
        ]);
        Equal(4, exit);
        var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        Equal(SchemaCaptureStatuses.DatabaseUnreachable, result.RootElement.GetProperty("status").GetString());
        Equal("DB_CONNECTION_REQUIRED", result.RootElement.GetProperty("diagnosticCode").GetString());
        True(!Directory.Exists(Path.Combine(root, "capture-1")));
    }
    finally
    {
        Environment.SetEnvironmentVariable("DB_CONNECTION", previous);
        DeleteTemp(root);
    }
}

static async Task CliSchemaComparisonBlocksMismatch()
{
    var root = TempDirectory("schema-capture-cli-compare");
    try
    {
        var writer = new SchemaCaptureArtifactWriter();
        writer.WriteCapture(Path.Combine(root, "capture-1"), "capture-1", CaptureSource(BaseSnapshot(includeIndex: false)));
        writer.WriteCapture(Path.Combine(root, "capture-2"), "capture-2", CaptureSource(BaseSnapshot(includeIndex: true)));
        var resultPath = Path.Combine(root, "comparison-result.json");
        var exit = await QualificationCli.RunAsync([
            "compare-schema-captures", "--environment", "TEST",
            "--capture-1", Path.Combine(root, "capture-1"), "--capture-2", Path.Combine(root, "capture-2"),
            "--output", Path.Combine(root, "comparison"), "--result", resultPath
        ]);
        Equal(7, exit);
        var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        Equal(SchemaCaptureStatuses.Nondeterministic, result.RootElement.GetProperty("status").GetString());
        True(!result.RootElement.GetProperty("deterministic").GetBoolean());
    }
    finally { DeleteTemp(root); }
}

static Task SchemaCaptureArtifactsExcludeIdentityValue()
{
    var root = TempDirectory("schema-capture-no-identity-value");
    const string sentinel = "Server=hidden;User Id=hidden;Password=never-persist-this";
    var previous = Environment.GetEnvironmentVariable("DB_CONNECTION");
    try
    {
        Environment.SetEnvironmentVariable("DB_CONNECTION", sentinel);
        var artifact = new SchemaCaptureArtifactWriter().WriteCapture(
            root, "capture-1", CaptureSource(BaseSnapshot(includeIndex: true)));
        Equal("INSPECTION", artifact.Metadata.IdentityPurpose);
        var evidence = string.Join("\n", Directory.GetFiles(root).Select(File.ReadAllText));
        True(!evidence.Contains(sentinel, StringComparison.Ordinal));
        True(!evidence.Contains("never-persist-this", StringComparison.Ordinal));
    }
    finally
    {
        Environment.SetEnvironmentVariable("DB_CONNECTION", previous);
        DeleteTemp(root);
    }
    return Task.CompletedTask;
}

static Task RegistryBaselineRequired()
{
    var evaluation = EvaluateRegistry(RegistryTarget(DatabaseCertificationStatuses.BaselineRequired));
    Equal(DatabaseCertificationStatuses.BaselineRequired, evaluation.RegistryStatus);
    Equal(DatabaseDriftStatuses.BaselineRequired, evaluation.DriftStatus);
    Equal(DatabaseGateStatuses.Blocked, evaluation.GateStatus);
    Equal(DatabaseStateReasons.OnboardingBaselineRequired, evaluation.Reason);
    True(evaluation.BaselineCandidate);
    True(evaluation.CertifiedSchemaHash is null);
    return Task.CompletedTask;
}

static Task RegistryFormatVersionOneIsValid()
{
    var registry = DatabaseRegistryLoader.Validate(RegistryDocument(), RegistryProvenance());
    True(registry.IsValid);
    Equal(1, registry.Registry!.RegistryFormatVersion);
    return Task.CompletedTask;
}

static Task RegistryMissingFormatVersionIsInvalid()
{
    var registry = DatabaseRegistryLoader.Validate(
        new DatabaseRegistryDocument { Targets = [] }, RegistryProvenance());
    True(!registry.IsValid);
    True(registry.Errors.Contains("REGISTRY_FORMAT_VERSION_INVALID", StringComparer.Ordinal));
    Equal(DatabaseDriftStatuses.InvalidRegistry,
        new DatabaseStateEvaluator().Evaluate(registry, RegistryObservation()).DriftStatus);
    return Task.CompletedTask;
}

static Task RegistryUnknownFormatVersionIsInvalid()
{
    var registry = DatabaseRegistryLoader.Validate(new DatabaseRegistryDocument
    {
        RegistryFormatVersion = 2,
        Targets = []
    }, RegistryProvenance());
    True(!registry.IsValid);
    True(registry.Errors.Contains("REGISTRY_FORMAT_VERSION_INVALID", StringComparer.Ordinal));
    Equal(DatabaseGateStatuses.Blocked,
        new DatabaseStateEvaluator().Evaluate(registry, RegistryObservation()).GateStatus);
    return Task.CompletedTask;
}

static Task RegistryCertifiedMatch()
{
    var evaluation = EvaluateRegistry(RegistryTarget(DatabaseCertificationStatuses.Certified, ObservedSchemaHash()));
    Equal(DatabaseDriftStatuses.Match, evaluation.DriftStatus);
    Equal(DatabaseGateStatuses.Eligible, evaluation.GateStatus);
    Equal(DatabaseStateReasons.SchemaHashMatch, evaluation.Reason);
    True(!evaluation.DriftDetected);
    return Task.CompletedTask;
}

static Task RegistryCertifiedMismatch()
{
    var evaluation = EvaluateRegistry(RegistryTarget(DatabaseCertificationStatuses.Certified, new string('b', 64)));
    Equal(DatabaseDriftStatuses.DriftDetected, evaluation.DriftStatus);
    Equal(DatabaseGateStatuses.Blocked, evaluation.GateStatus);
    Equal(DatabaseStateReasons.CertifiedSchemaHashMismatch, evaluation.Reason);
    True(evaluation.DriftDetected);
    Equal("HASH_MISMATCH", evaluation.DriftEvidenceKind);
    True(!evaluation.StructuralDiffAvailable);
    return Task.CompletedTask;
}

static Task RegistryTargetNotRegistered()
{
    var registry = DatabaseRegistryLoader.Validate(RegistryDocument(), RegistryProvenance());
    var evaluation = new DatabaseStateEvaluator().Evaluate(registry, RegistryObservation());
    Equal(DatabaseDriftStatuses.TargetNotRegistered, evaluation.RegistryStatus);
    Equal(DatabaseDriftStatuses.TargetNotRegistered, evaluation.DriftStatus);
    Equal(DatabaseGateStatuses.Blocked, evaluation.GateStatus);
    Equal(DatabaseStateReasons.TargetNotRegistered, evaluation.Reason);
    return Task.CompletedTask;
}

static Task RegistryRejectsDuplicateTargets()
{
    var registry = DatabaseRegistryLoader.Validate(new DatabaseRegistryDocument
    {
        RegistryFormatVersion = 1,
        Targets =
        [
            RegistryTarget(DatabaseCertificationStatuses.BaselineRequired),
            RegistryTarget(DatabaseCertificationStatuses.BaselineRequired)
        ]
    }, RegistryProvenance());
    True(!registry.IsValid);
    True(registry.Errors.Any(item => item.EndsWith("_DUPLICATE_TARGET", StringComparison.Ordinal)));
    Equal(DatabaseDriftStatuses.InvalidRegistry,
        new DatabaseStateEvaluator().Evaluate(registry, RegistryObservation()).DriftStatus);
    return Task.CompletedTask;
}

static Task RegistryRejectsCertifiedWithoutHash()
{
    var registry = ValidateTarget(RegistryTarget(DatabaseCertificationStatuses.Certified));
    True(!registry.IsValid);
    True(registry.Errors.Any(item => item.EndsWith("_CERTIFIED_SCHEMA_HASH_REQUIRED", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static Task RegistryRejectsInvalidHash()
{
    var registry = ValidateTarget(RegistryTarget(DatabaseCertificationStatuses.Certified, "not-a-sha256"));
    True(!registry.IsValid);
    True(registry.Errors.Any(item => item.EndsWith("_CERTIFIED_SCHEMA_HASH_INVALID", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static Task RegistryRejectsInvalidEnvironment()
{
    var registry = ValidateTarget(RegistryTarget(DatabaseCertificationStatuses.BaselineRequired, environment: "DEV"));
    True(!registry.IsValid);
    True(registry.Errors.Any(item => item.EndsWith("_ENVIRONMENT_INVALID", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static Task RegistryRejectsInvalidCertificationStatus()
{
    var registry = ValidateTarget(RegistryTarget("AUTO_CERTIFIED"));
    True(!registry.IsValid);
    True(registry.Errors.Any(item => item.EndsWith("_CERTIFICATION_STATUS_INVALID", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static Task RegistryRejectsInvalidLifecycle()
{
    var registry = ValidateTarget(new DatabaseTarget
    {
        ApplicationId = "3602",
        Environment = "TEST",
        DatabaseName = "CICDV3",
        Lifecycle = "LEGACY",
        CertificationStatus = DatabaseCertificationStatuses.BaselineRequired
    });
    True(!registry.IsValid);
    True(registry.Errors.Any(item => item.EndsWith("_LIFECYCLE_INVALID", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static Task RegistryRejectsEmptyRequiredFields()
{
    var registry = ValidateTarget(new DatabaseTarget
    {
        ApplicationId = "",
        Environment = "TEST",
        DatabaseName = "",
        Lifecycle = "EXISTING",
        CertificationStatus = DatabaseCertificationStatuses.BaselineRequired
    });
    True(!registry.IsValid);
    True(registry.Errors.Any(item => item.EndsWith("_APPLICATION_ID_REQUIRED", StringComparison.Ordinal)));
    True(registry.Errors.Any(item => item.EndsWith("_DATABASE_NAME_REQUIRED", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static Task RegistryRejectsContradictoryBaseline()
{
    var registry = ValidateTarget(RegistryTarget(
        DatabaseCertificationStatuses.BaselineRequired, ""));
    True(!registry.IsValid);
    True(registry.Errors.Any(item => item.EndsWith("_BASELINE_CERTIFIED_HASH_CONTRADICTORY", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static Task BaselineCandidateIsNeverCertified()
{
    var root = TempDirectory("baseline-candidate");
    try
    {
        var observation = RegistryObservation();
        var evaluation = EvaluateRegistry(RegistryTarget(DatabaseCertificationStatuses.BaselineRequired), observation);
        var artifact = new DatabaseStateArtifactWriter().Write(root, observation, evaluation);
        True(artifact.BaselineCandidatePath is not null);
        var candidate = JsonDocument.Parse(File.ReadAllText(artifact.BaselineCandidatePath!));
        Equal("NOT_CERTIFIED", candidate.RootElement.GetProperty("candidateStatus").GetString());
        Equal(ObservedSchemaHash(), candidate.RootElement.GetProperty("observedSchemaHash").GetString());
        True(!candidate.RootElement.TryGetProperty("certifiedSchemaHash", out _));
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task RegistryEvaluationDoesNotModifyRegistry()
{
    var root = TempDirectory("registry-immutable");
    try
    {
        Directory.CreateDirectory(root);
        var registryPath = Path.Combine(root, "targets.json");
        File.WriteAllText(registryPath, JsonSerializer.Serialize(new DatabaseRegistryDocument
        {
            RegistryFormatVersion = 1,
            Targets = [RegistryTarget(DatabaseCertificationStatuses.BaselineRequired)]
        }, DatabaseStateJson.Indented));
        var before = File.ReadAllBytes(registryPath);
        var validation = DatabaseRegistryLoader.Load(registryPath, RegistryProvenance(
            registryFileSha256: DatabaseRegistryLoader.ComputeFileSha256(registryPath)));
        var observation = RegistryObservation();
        var evaluation = new DatabaseStateEvaluator().Evaluate(validation, observation);
        new DatabaseStateArtifactWriter().Write(Path.Combine(root, "artifact"), observation, evaluation);
        SequenceEqual(before, File.ReadAllBytes(registryPath));
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task ObservedHashNeverReplacesCertifiedHash()
{
    var certified = new string('b', 64);
    var target = RegistryTarget(DatabaseCertificationStatuses.Certified, certified);
    var evaluation = EvaluateRegistry(target);
    Equal(certified, target.CertifiedSchemaHash);
    Equal(certified, evaluation.CertifiedSchemaHash);
    Equal(ObservedSchemaHash(), evaluation.ObservedSchemaHash);
    NotEqual(evaluation.CertifiedSchemaHash, evaluation.ObservedSchemaHash);
    return Task.CompletedTask;
}

static Task DriftEvidenceIsHashOnly()
{
    var root = TempDirectory("drift-hash-only");
    try
    {
        var observation = RegistryObservation();
        var evaluation = EvaluateRegistry(
            RegistryTarget(DatabaseCertificationStatuses.Certified, new string('b', 64)), observation);
        var artifact = new DatabaseStateArtifactWriter().Write(root, observation, evaluation);
        True(artifact.DriftAnalysisPath is not null);
        var drift = JsonDocument.Parse(File.ReadAllText(artifact.DriftAnalysisPath!)).RootElement;
        Equal("HASH_MISMATCH", drift.GetProperty("evidenceKind").GetString());
        True(!drift.GetProperty("structuralDiffAvailable").GetBoolean());
        True(!drift.TryGetProperty("changedObjects", out _));
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task RegistryCommitAppearsInEvidence()
{
    var root = TempDirectory("registry-commit-evidence");
    try
    {
        var commit = new string('e', 40);
        var observation = RegistryObservation();
        var validation = DatabaseRegistryLoader.Validate(
            RegistryDocument(RegistryTarget(DatabaseCertificationStatuses.BaselineRequired)),
            RegistryProvenance(registryCommitSha: commit));
        var evaluation = new DatabaseStateEvaluator().Evaluate(validation, observation);
        var artifact = new DatabaseStateArtifactWriter().Write(root, observation, evaluation);
        var evidence = JsonDocument.Parse(File.ReadAllText(artifact.RegistryEvaluationPath)).RootElement;
        Equal(commit, evidence.GetProperty("registryProvenance").GetProperty("registryCommitSha").GetString());
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task RegistryFileShaMatchesBytes()
{
    var root = TempDirectory("registry-file-sha");
    try
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "targets.json");
        File.WriteAllText(path, JsonSerializer.Serialize(
            RegistryDocument(RegistryTarget(DatabaseCertificationStatuses.BaselineRequired)),
            DatabaseStateJson.Indented));
        var expected = DatabaseRegistryLoader.ComputeFileSha256(path);
        var validation = DatabaseRegistryLoader.Load(path, RegistryProvenance(registryFileSha256: expected));
        True(validation.IsValid);
        Equal(expected, validation.RegistryProvenance!.RegistryFileSha256);
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task RegistryFileShaMismatchFailsClosed()
{
    var root = TempDirectory("registry-file-sha-mismatch");
    try
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "targets.json");
        File.WriteAllText(path, JsonSerializer.Serialize(RegistryDocument(), DatabaseStateJson.Indented));
        var validation = DatabaseRegistryLoader.Load(path, RegistryProvenance(registryFileSha256: new string('f', 64)));
        True(!validation.IsValid);
        True(validation.Errors.Contains("REGISTRY_FILE_SHA256_MISMATCH", StringComparer.Ordinal));
        Equal(DatabaseGateStatuses.Blocked,
            new DatabaseStateEvaluator().Evaluate(validation, RegistryObservation()).GateStatus);
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task RegistryContentChangesFileSha()
{
    var root = TempDirectory("registry-file-sha-change");
    try
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "targets.json");
        File.WriteAllText(path, JsonSerializer.Serialize(RegistryDocument(), DatabaseStateJson.Indented));
        var before = DatabaseRegistryLoader.ComputeFileSha256(path);
        File.WriteAllText(path, JsonSerializer.Serialize(
            RegistryDocument(RegistryTarget(DatabaseCertificationStatuses.BaselineRequired)),
            DatabaseStateJson.Indented));
        var after = DatabaseRegistryLoader.ComputeFileSha256(path);
        NotEqual(before, after);
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task ObservedHashDoesNotChangeRegistryFileSha()
{
    var root = TempDirectory("observed-does-not-change-registry-sha");
    try
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "targets.json");
        File.WriteAllText(path, JsonSerializer.Serialize(
            RegistryDocument(RegistryTarget(DatabaseCertificationStatuses.BaselineRequired)),
            DatabaseStateJson.Indented));
        var fileSha = DatabaseRegistryLoader.ComputeFileSha256(path);
        var validation = DatabaseRegistryLoader.Load(path, RegistryProvenance(registryFileSha256: fileSha));
        var first = new DatabaseStateEvaluator().Evaluate(validation, RegistryObservation(new string('a', 64)));
        var second = new DatabaseStateEvaluator().Evaluate(validation, RegistryObservation(new string('b', 64)));
        NotEqual(first.ObservedSchemaHash, second.ObservedSchemaHash);
        Equal(fileSha, first.RegistryProvenance!.RegistryFileSha256);
        Equal(fileSha, second.RegistryProvenance!.RegistryFileSha256);
        Equal(fileSha, DatabaseRegistryLoader.ComputeFileSha256(path));
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task BaselineCandidatePreservesProvenance()
{
    var root = TempDirectory("baseline-provenance");
    try
    {
        var observation = RegistryObservation();
        var evaluation = EvaluateRegistry(RegistryTarget(DatabaseCertificationStatuses.BaselineRequired), observation);
        var artifact = new DatabaseStateArtifactWriter().Write(root, observation, evaluation);
        var candidate = JsonDocument.Parse(File.ReadAllText(artifact.BaselineCandidatePath!)).RootElement;
        Equal(1, candidate.GetProperty("registryFormatVersion").GetInt32());
        Equal(evaluation.RegistryProvenance!.RegistryCommitSha,
            candidate.GetProperty("registryProvenance").GetProperty("registryCommitSha").GetString());
        Equal(evaluation.RegistryProvenance.RegistryFileSha256,
            candidate.GetProperty("registryProvenance").GetProperty("registryFileSha256").GetString());
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task DriftEvidencePreservesProvenance()
{
    var root = TempDirectory("drift-provenance");
    try
    {
        var observation = RegistryObservation();
        var evaluation = EvaluateRegistry(
            RegistryTarget(DatabaseCertificationStatuses.Certified, new string('b', 64)), observation);
        var artifact = new DatabaseStateArtifactWriter().Write(root, observation, evaluation);
        var drift = JsonDocument.Parse(File.ReadAllText(artifact.DriftAnalysisPath!)).RootElement;
        Equal(1, drift.GetProperty("registryFormatVersion").GetInt32());
        Equal(evaluation.RegistryProvenance!.RegistryCommitSha,
            drift.GetProperty("registryProvenance").GetProperty("registryCommitSha").GetString());
        Equal(evaluation.RegistryProvenance.RegistryFileSha256,
            drift.GetProperty("registryProvenance").GetProperty("registryFileSha256").GetString());
    }
    finally { DeleteTemp(root); }
    return Task.CompletedTask;
}

static Task MatchIsEligibleForSchemaDrift()
{
    var evaluation = EvaluateRegistry(RegistryTarget(DatabaseCertificationStatuses.Certified, ObservedSchemaHash()));
    Equal(DatabaseGateStatuses.Eligible, evaluation.GateStatus);
    Equal(DatabaseDriftStatuses.Match, evaluation.DriftStatus);
    Equal("NONE", evaluation.DriftEvidenceKind);
    True(!evaluation.BaselineCandidate);
    return Task.CompletedTask;
}

static async Task CliDatabaseStateBaselineProducesEvidence()
{
    var root = TempDirectory("database-state-cli-baseline");
    try
    {
        Directory.CreateDirectory(root);
        var captureDirectory = Path.Combine(root, "capture-1");
        new SchemaCaptureArtifactWriter().WriteCapture(
            captureDirectory, "capture-1", CaptureSource(BaseSnapshot(includeIndex: true)));
        var registryPath = Path.Combine(root, "targets.json");
        File.WriteAllText(registryPath, JsonSerializer.Serialize(new DatabaseRegistryDocument
        {
            RegistryFormatVersion = 1,
            Targets = [RegistryTarget(DatabaseCertificationStatuses.BaselineRequired, databaseName: "DatabaseForTests")]
        }, DatabaseStateJson.Indented));
        var registryBefore = File.ReadAllBytes(registryPath);
        var resultPath = Path.Combine(root, "result.json");
        var output = Path.Combine(root, "artifact");

        var exit = await QualificationCli.RunAsync(DatabaseStateCliArguments(
            registryPath, captureDirectory, output, resultPath));

        Equal(0, exit);
        var result = JsonDocument.Parse(File.ReadAllText(resultPath)).RootElement;
        Equal("SUCCESS", result.GetProperty("status").GetString());
        Equal(DatabaseDriftStatuses.BaselineRequired, result.GetProperty("driftStatus").GetString());
        Equal(DatabaseGateStatuses.Blocked, result.GetProperty("gateStatus").GetString());
        True(File.Exists(Path.Combine(output, "registry", "target.json")));
        True(File.Exists(Path.Combine(output, "registry", "registry-evaluation.json")));
        True(File.Exists(Path.Combine(output, "baseline", "baseline-candidate.json")));
        SequenceEqual(registryBefore, File.ReadAllBytes(registryPath));
    }
    finally { DeleteTemp(root); }
}

static async Task CliDatabaseStateInvalidRegistryFailsClosed()
{
    var root = TempDirectory("database-state-cli-invalid");
    try
    {
        Directory.CreateDirectory(root);
        var captureDirectory = Path.Combine(root, "capture-1");
        new SchemaCaptureArtifactWriter().WriteCapture(
            captureDirectory, "capture-1", CaptureSource(BaseSnapshot(includeIndex: true)));
        var registryPath = Path.Combine(root, "targets.json");
        File.WriteAllText(registryPath, JsonSerializer.Serialize(new DatabaseRegistryDocument
        {
            RegistryFormatVersion = 1,
            Targets = [RegistryTarget(DatabaseCertificationStatuses.Certified, databaseName: "DatabaseForTests")]
        }, DatabaseStateJson.Indented));
        var resultPath = Path.Combine(root, "result.json");
        var output = Path.Combine(root, "artifact");

        var exit = await QualificationCli.RunAsync(DatabaseStateCliArguments(
            registryPath, captureDirectory, output, resultPath));

        Equal(8, exit);
        var result = JsonDocument.Parse(File.ReadAllText(resultPath)).RootElement;
        Equal("FAIL_INVALID_REGISTRY", result.GetProperty("status").GetString());
        Equal(DatabaseDriftStatuses.InvalidRegistry, result.GetProperty("driftStatus").GetString());
        Equal(DatabaseGateStatuses.Blocked, result.GetProperty("gateStatus").GetString());
        True(File.Exists(Path.Combine(output, "registry", "registry-evaluation.json")));
        True(!Directory.Exists(Path.Combine(output, "baseline")));
    }
    finally { DeleteTemp(root); }
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

static Task DerivedCertificationRequiresCertifiedPre()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(includeCertifiedPre: false));
    Equal(CertificationDecision.Blocked, result.Decision);
    Equal(CertificationDecisionReasons.CertifiedPreRequired, result.DecisionReason);
    True(!result.Evidence.AutomaticEligible);
    True(!result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task DerivedCertificationIsAutomatic()
{
    var result = new CertificationDecisionEngine().Evaluate(DerivedCertificationRequest());
    Equal(CertificationDecision.Automatic, result.Decision);
    Equal(CertificationOrigin.QualifiedRelease, result.Origin);
    Equal(CertificationDecisionReasons.QualifiedReleaseTransition, result.DecisionReason);
    Equal(CertificationPostHash(), result.NextCertifiedSchemaHash);
    True(result.Evidence.ChainOfTrustIntact);
    True(result.Evidence.AutomaticEligible);
    True(result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task PreDriftBlocksDerivedCertification()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(observedPreSchemaHash: new string('9', 64)));
    Equal(CertificationDecision.Blocked, result.Decision);
    Equal(CertificationDecisionReasons.PreStateDriftDetected, result.DecisionReason);
    True(!result.Evidence.PreMatchesCertified);
    True(!result.Evidence.AutomaticEligible);
    return Task.CompletedTask;
}

static Task PostMismatchBlocksDerivedCertification()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(observedPostSchemaHash: new string('8', 64)));
    Equal(CertificationDecision.Blocked, result.Decision);
    Equal(CertificationDecisionReasons.QualifiedPostMismatch, result.DecisionReason);
    True(!result.Evidence.PostMatchesQualified);
    True(!result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task LowRiskExactDeploymentCertifiesAutomatically()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(finalRisk: RiskLevel.Low));
    Equal(CertificationDecision.Automatic, result.Decision);
    True(result.Evidence.ExactQualifiedRelease);
    Equal(DeploymentAuthorizationRequirement.AutomaticPolicy,
        result.Evidence.DeploymentAuthorizationRequirement);
    Equal(DeploymentAuthorizationDecision.Authorized,
        result.Evidence.DeploymentAuthorizationDecision);
    Equal(CertificationApprovalRequirement.None, result.Evidence.CertificationApprovalRequired);
    return Task.CompletedTask;
}

static Task HighRiskMissingDeploymentAuthorizationBlocks()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(
            finalRisk: RiskLevel.High,
            authorizationRequirement: DeploymentAuthorizationRequirement.DbaApproval,
            authorizationDecision: DeploymentAuthorizationDecision.NotAuthorized,
            includeAuthorizationReference: false));
    Equal(CertificationDecision.Blocked, result.Decision);
    Equal(CertificationDecisionReasons.DeploymentAuthorizationRequired, result.DecisionReason);
    True(!result.Evidence.AutomaticEligible);
    True(!result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task InvalidRollbackCannotBeOverriddenByAuthorization()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(
            finalRisk: RiskLevel.High,
            schemaRollbackValidity: SchemaRollbackValidity.Invalid,
            authorizationRequirement: DeploymentAuthorizationRequirement.DbaApproval,
            authorizationDecision: DeploymentAuthorizationDecision.Authorized));
    Equal(CertificationDecision.Blocked, result.Decision);
    Equal(CertificationDecisionReasons.InvalidRollback, result.DecisionReason);
    True(!result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task OutOfBandRequiresReconciliation()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(outOfBandChangeDetected: true));
    Equal(CertificationDecision.Blocked, result.Decision);
    Equal(CertificationDecisionReasons.DriftReconciliationRequired, result.DecisionReason);
    Equal(CertificationOrigin.QualifiedRelease, result.Origin);
    True(!result.Evidence.ChainOfTrustIntact);
    return Task.CompletedTask;
}

static Task BootstrapIsReadyForHumanApproval()
{
    var result = new CertificationDecisionEngine().Evaluate(BootstrapCertificationRequest());
    Equal(CertificationDecision.ReadyForHumanApproval, result.Decision);
    Equal(CertificationOrigin.BootstrapApproved, result.Origin);
    Equal(CertificationDecisionReasons.InitialBaselineApproval, result.DecisionReason);
    Equal(CertificationApprovalRequirement.Human,
        result.Evidence.CertificationApprovalRequired);
    True(!result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task AutomaticCertificationEvidenceIsComplete()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(
            finalRisk: RiskLevel.High,
            authorizationRequirement: DeploymentAuthorizationRequirement.DbaApproval,
            authorizationDecision: DeploymentAuthorizationDecision.Authorized));
    var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Evidence, JsonDefaults.Compact)).RootElement;
    Equal(1, json.GetProperty("formatVersion").GetInt32());
    Equal("DEPLOYMENT_POLICY_V1", json.GetProperty("policyId").GetString());
    Equal("QUALIFIED_RELEASE", json.GetProperty("origin").GetString());
    Equal("AUTOMATIC", json.GetProperty("decision").GetString());
    Equal("QUALIFIED_RELEASE_TRANSITION", json.GetProperty("decisionReason").GetString());
    Equal(CertificationPreHash(), json.GetProperty("previousCertifiedSchemaHash").GetString());
    Equal(CertificationPreHash(), json.GetProperty("observedPreSchemaHash").GetString());
    Equal(CertificationPostHash(), json.GetProperty("qualifiedPostSchemaHash").GetString());
    Equal(CertificationPostHash(), json.GetProperty("observedPostSchemaHash").GetString());
    Equal(CertificationPostHash(), json.GetProperty("nextCertifiedSchemaHash").GetString());
    Equal(CertificationPayloadHash(), json.GetProperty("qualifiedPayloadHash").GetString());
    Equal(CertificationPayloadHash(), json.GetProperty("executedPayloadHash").GetString());
    Equal(CertificationForwardHash(), json.GetProperty("qualifiedForwardHash").GetString());
    Equal(CertificationForwardHash(), json.GetProperty("executedForwardHash").GetString());
    Equal(CertificationRollbackHash(), json.GetProperty("qualifiedRollbackHash").GetString());
    Equal(CertificationRollbackHash(), json.GetProperty("verifiedRollbackHash").GetString());
    True(json.GetProperty("exactQualifiedRelease").GetBoolean());
    True(json.GetProperty("executionSucceeded").GetBoolean());
    True(json.GetProperty("postMatchesQualified").GetBoolean());
    True(json.GetProperty("chainOfTrustIntact").GetBoolean());
    True(json.GetProperty("automaticEligible").GetBoolean());
    Equal("HIGH", json.GetProperty("finalRisk").GetString());
    Equal("DBA_APPROVAL", json.GetProperty("deploymentAuthorizationRequirement").GetString());
    Equal("AUTHORIZED", json.GetProperty("deploymentAuthorizationDecision").GetString());
    Equal("CHG-FIXTURE-001", json.GetProperty("authorizationReference").GetString());
    True(json.GetProperty("releaseQualificationGatePassed").GetBoolean());
    Equal("NONE", json.GetProperty("certificationApprovalRequired").GetString());
    Equal("VALID", json.GetProperty("schemaRollbackValidity").GetString());
    Equal("NOT_APPLICABLE", json.GetProperty("dataRollbackValidity").GetString());
    Equal("FULL_REVERSIBLE", json.GetProperty("rollbackCapability").GetString());
    return Task.CompletedTask;
}

static Task ExactQualifiedReleaseIsRequired()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(executedPayloadHash: new string('7', 64)));
    Equal(CertificationDecision.Blocked, result.Decision);
    Equal(CertificationDecisionReasons.ExactQualifiedReleaseRequired, result.DecisionReason);
    True(!result.Evidence.ExactQualifiedRelease);
    True(!result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task HighRiskAuthorizedDeploymentCertifiesAutomatically()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(
            finalRisk: RiskLevel.High,
            authorizationRequirement: DeploymentAuthorizationRequirement.DbaApproval,
            authorizationDecision: DeploymentAuthorizationDecision.Authorized));
    Equal(CertificationDecision.Automatic, result.Decision);
    Equal(CertificationDecisionReasons.QualifiedReleaseTransition, result.DecisionReason);
    Equal(DeploymentAuthorizationRequirement.DbaApproval,
        result.Evidence.DeploymentAuthorizationRequirement);
    Equal("CHG-FIXTURE-001", result.Evidence.AuthorizationReference);
    Equal(CertificationApprovalRequirement.None, result.Evidence.CertificationApprovalRequired);
    True(result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task Cicdv3BootstrapRemainsBlockedByLineage()
{
    var result = new CertificationDecisionEngine().Evaluate(
        BootstrapCertificationRequest(lineageStatus: "BLOCKED_HISTORY_WITHOUT_REPO"));
    Equal(CertificationDecision.Blocked, result.Decision);
    Equal(CertificationDecisionReasons.LineageNotEligible, result.DecisionReason);
    True(!result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task RestoreRequiredAuthorizedCertifiesAutomatically()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(
            finalRisk: RiskLevel.High,
            rollbackCapability: RollbackCapability.RestoreRequired,
            authorizationRequirement: DeploymentAuthorizationRequirement.DbaApproval,
            authorizationDecision: DeploymentAuthorizationDecision.Authorized));
    Equal(CertificationDecision.Automatic, result.Decision);
    Equal(CertificationOrigin.QualifiedRelease, result.Origin);
    Equal(RollbackCapability.RestoreRequired, result.Evidence.RollbackCapability);
    Equal(DeploymentAuthorizationDecision.Authorized,
        result.Evidence.DeploymentAuthorizationDecision);
    True(result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task RestoreRequiredWithoutAuthorizationBlocks()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(
            finalRisk: RiskLevel.High,
            rollbackCapability: RollbackCapability.RestoreRequired,
            authorizationRequirement: DeploymentAuthorizationRequirement.DbaApproval,
            authorizationDecision: DeploymentAuthorizationDecision.NotAuthorized,
            includeAuthorizationReference: false));
    Equal(CertificationDecision.Blocked, result.Decision);
    Equal(CertificationDecisionReasons.DeploymentAuthorizationRequired, result.DecisionReason);
    True(!result.ProducesCertifiedState);
    return Task.CompletedTask;
}

static Task QualificationGateFailureBlocks()
{
    var result = new CertificationDecisionEngine().Evaluate(
        DerivedCertificationRequest(releaseQualificationGatePassed: false));
    Equal(CertificationDecision.Blocked, result.Decision);
    Equal(CertificationDecisionReasons.ReleaseQualificationGateNotPassed, result.DecisionReason);
    True(!result.ProducesCertifiedState);
    return Task.CompletedTask;
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

static CertificationRequest DerivedCertificationRequest(
    bool includeCertifiedPre = true,
    string? observedPreSchemaHash = null,
    string? observedPostSchemaHash = null,
    string? executedPayloadHash = null,
    RiskLevel finalRisk = RiskLevel.Low,
    SchemaRollbackValidity schemaRollbackValidity = SchemaRollbackValidity.Valid,
    RollbackCapability rollbackCapability = RollbackCapability.FullReversible,
    bool outOfBandChangeDetected = false,
    DeploymentAuthorizationRequirement? authorizationRequirement = null,
    DeploymentAuthorizationDecision authorizationDecision = DeploymentAuthorizationDecision.Authorized,
    bool includeAuthorizationReference = true,
    bool releaseQualificationGatePassed = true)
{
    var requirement = authorizationRequirement
        ?? (finalRisk == RiskLevel.Low
            ? DeploymentAuthorizationRequirement.AutomaticPolicy
            : DeploymentAuthorizationRequirement.DbaApproval);
    return new CertificationRequest
    {
        Origin = CertificationOrigin.QualifiedRelease,
        CertifiedPreSchemaHash = includeCertifiedPre ? CertificationPreHash() : null,
        ObservedPreSchemaHash = observedPreSchemaHash ?? CertificationPreHash(),
        QualifiedPreSchemaHash = CertificationPreHash(),
        QualifiedPostSchemaHash = CertificationPostHash(),
        ObservedPostSchemaHash = observedPostSchemaHash ?? CertificationPostHash(),
        ReleaseId = "qualified-release-001",
        QualifiedPayloadHash = CertificationPayloadHash(),
        ExecutedPayloadHash = executedPayloadHash ?? CertificationPayloadHash(),
        QualifiedForwardHash = CertificationForwardHash(),
        ExecutedForwardHash = CertificationForwardHash(),
        QualifiedRollbackHash = CertificationRollbackHash(),
        VerifiedRollbackHash = CertificationRollbackHash(),
        QualifiedRelease = true,
        ExecutionSucceeded = true,
        DriftStatus = DatabaseDriftStatuses.Match,
        LineageStatus = "CONSISTENT",
        OutOfBandChangeDetected = outOfBandChangeDetected,
        DeploymentAuthorization = new DeploymentAuthorizationEvidence
        {
            PolicyId = "DEPLOYMENT_POLICY_V1",
            Risk = finalRisk,
            Requirement = requirement,
            Decision = authorizationDecision,
            AuthorizationReference = requirement == DeploymentAuthorizationRequirement.AutomaticPolicy
                || !includeAuthorizationReference
                ? null
                : "CHG-FIXTURE-001",
            ReleaseQualificationGatePassed = releaseQualificationGatePassed,
            AnalysisConfidence = AnalysisConfidence.Complete,
            SchemaRollbackValidity = schemaRollbackValidity,
            DataRollbackValidity = DataRollbackValidity.NotApplicable,
            RollbackCapability = rollbackCapability
        }
    };
}

static CertificationRequest BootstrapCertificationRequest(string lineageStatus = "CONSISTENT") => new()
{
    Origin = CertificationOrigin.BootstrapApproved,
    ObservedPreSchemaHash = CertificationPreHash(),
    DriftStatus = DatabaseDriftStatuses.BaselineRequired,
    LineageStatus = lineageStatus
};

static string CertificationPreHash() => new('1', 64);
static string CertificationPostHash() => new('2', 64);
static string CertificationPayloadHash() => new('3', 64);
static string CertificationForwardHash() => new('4', 64);
static string CertificationRollbackHash() => new('5', 64);

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

static SchemaCaptureSourceResult CaptureSource(
    SchemaSnapshot snapshot,
    MetricsAvailability metricsAvailability = MetricsAvailability.Complete,
    string? metricsDiagnosticCode = null) => new()
{
    Snapshot = snapshot,
    DatabaseName = "DatabaseForTests",
    ServerVersion = "16.0.1000.6",
    ServerMajorVersion = 16,
    MetricsAvailability = metricsAvailability,
    MetricsDiagnosticCode = metricsDiagnosticCode
};

static string ObservedSchemaHash() => new('a', 64);

static DatabaseTarget RegistryTarget(
    string certificationStatus,
    string? certifiedSchemaHash = null,
    string environment = "TEST",
    string databaseName = "CICDV3") => new()
{
    ApplicationId = "3602",
    Environment = environment,
    DatabaseName = databaseName,
    Lifecycle = "EXISTING",
    CertificationStatus = certificationStatus,
    CertifiedSchemaHash = certifiedSchemaHash
};

static DatabaseRegistryDocument RegistryDocument(params DatabaseTarget[] targets) => new()
{
    RegistryFormatVersion = 1,
    Targets = targets.ToList()
};

static RegistryProvenance RegistryProvenance(
    string? registryFileSha256 = null,
    string? registryCommitSha = null) => new()
{
    RegistryRepository = "infrastructure-services/workflow",
    RegistryRef = "feature/db-schema-capture-test",
    RegistryCommitSha = registryCommitSha ?? new string('d', 40),
    RegistryFilePath = "database-registry/targets.json",
    RegistryFileSha256 = registryFileSha256 ?? new string('c', 64)
};

static DatabaseStateObservation RegistryObservation(string? observedSchemaHash = null) => new()
{
    ApplicationId = "3602",
    Environment = "TEST",
    DatabaseName = "CICDV3",
    ObservedSchemaHash = observedSchemaHash ?? ObservedSchemaHash(),
    SchemaCoverage = "COMPLETE",
    UnsupportedSchemaFeatures = [],
    CaptureTimestampUtc = DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
    RunId = "123456",
    RunAttempt = "1"
};

static DatabaseRegistryValidation ValidateTarget(DatabaseTarget target) =>
    DatabaseRegistryLoader.Validate(RegistryDocument(target), RegistryProvenance());

static DatabaseStateEvaluation EvaluateRegistry(
    DatabaseTarget target,
    DatabaseStateObservation? observation = null) =>
    new DatabaseStateEvaluator().Evaluate(ValidateTarget(target), observation ?? RegistryObservation());

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

static string[] DatabaseStateCliArguments(
    string registry,
    string capture,
    string output,
    string result)
{
    var provenance = RegistryProvenance(
        registryFileSha256: DatabaseRegistryLoader.ComputeFileSha256(registry));
    return
    [
        "evaluate-database-state", "--environment", "TEST", "--application-id", "3602",
        "--registry", registry,
        "--registry-repository", provenance.RegistryRepository,
        "--registry-ref", provenance.RegistryRef,
        "--registry-commit-sha", provenance.RegistryCommitSha,
        "--registry-file-path", provenance.RegistryFilePath,
        "--registry-file-sha256", provenance.RegistryFileSha256,
        "--capture", capture, "--capture-timestamp-utc", "2026-08-25T12:00:00Z",
        "--run-id", "123456", "--run-attempt", "1", "--output", output, "--result", result
    ];
}

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
