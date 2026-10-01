using System.Diagnostics;
using System.Data;
using System.Security.Authentication;
using System.Text.Json;
using SqlDiscovery.V2;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("V1 exact standard schemas and SELECT-only coverage", TaxonomySqlContract),
    ("V1 reader zero positive and each complementary category", TaxonomyReader),
    ("V1 partial permissions malformed and overflowing reads fail closed", TaxonomyReaderFailures),
    ("V1 complete SQL projection reaches real NEW_EF", IntegrationNewEfComplete),
    ("V1 complementary objects block real NEW_EF", IntegrationNewEfComplementary),
    ("V1 forged aggregate and taxonomy fail projection", TaxonomyProjectionFailure),
    ("history spoof and revoked metadata never become ABSENT", HistoryStructureBoundary),
    ("conexión exitosa y orden completo", FullSuccess),
    ("fallo de autenticación sanitizado", AuthenticationFailure),
    ("fallo de transporte sanitizado", TransportFailure),
    ("timeout de conexión", ConnectionTimeout),
    ("cancelación y propagación del token", Cancellation),
    ("token precancelado produce estado interno", PreCancelled),
    ("base encontrada", DatabaseFound),
    ("ausencia confirmada con visibilidad", ConfirmedAbsent),
    ("lookup sin visibilidad queda desconocido", LookupVisibilityInsufficient),
    ("fallo técnico del lookup", LookupTechnicalFailure),
    ("timeout de lookup bloquea dependencias", LookupTimeout),
    ("cancelación de lookup bloquea dependencias", LookupCancellation),
    ("target fallido no contamina conexión servidor", TargetFailure),
    ("target conserva autenticación timeout y cancelación", TargetExactFailures),
    ("target exitoso publica identidad SQL exacta", TargetIdentitySuccess),
    ("identidad target ausente falla cerrado", TargetIdentityMissing),
    ("campos de identidad target vacíos fallan cerrado", TargetIdentityBlankFields),
    ("metadata suficiente", MetadataSufficient),
    ("metadata insuficiente", MetadataInsufficient),
    ("fallo de metadata", MetadataFailure),
    ("metadata conserva timeout y cancelación", MetadataTimeoutCancellation),
    ("observación física completa y count válido", PhysicalComplete),
    ("observación física parcial no publica counts", PhysicalPartial),
    ("física conserva timeout y cancelación", PhysicalTimeoutCancellation),
    ("history ausente", HistoryAbsent),
    ("history presente vacía", HistoryEmpty),
    ("history presente conserva duplicados y orden", HistoryRows),
    ("history ilegible", HistoryUnreadable),
    ("history con estructura inválida", HistoryInvalid),
    ("history con error técnico", HistoryError),
    ("history conserva timeout y cancelación", HistoryTimeoutCancellation),
    ("prerrequisitos dejan etapas no intentadas", Prerequisites),
    ("excepción sensible no se expone", SensitiveException),
    ("transporte declara cero retries, TLS estricto y sólo lectura", StaticTransportGuards),
    ("STRICT permanece como modo TLS default", TlsStrictDefault),
    ("modo TEST explícito abre directamente con cifrado obligatorio", TlsExplicitTestMode),
    ("modo TLS explícito no hace fallback ni retry", TlsExplicitModeNoFallback),
    ("QA y PROD bloquean modo TLS TEST", TlsNonTestBlocked),
    ("clasificación TLS excluye identidad y no filtra secretos", TlsClassifierAndSanitization),
    ("fingerprint TLS sanitiza categorías y excepción interna", TlsDiagnosticFingerprint),
    ("input conservado y resultados independientes", InputAndResultIsolation),
    ("integración real clasifica EXISTING_EF", IntegrationExistingEf),
    ("integración bloquea target fallido antes del productor", IntegrationTargetGap),
    ("integración bloqueada no invoca classifier real", IntegrationUnknownLookup),
    ("límite taxonómico bloquea elegibilidad NEW_EF", IntegrationNewEfTaxonomyGap)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL: {test.Name}");
    }
}

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine($"OK: {tests.Count} casos de SQL Discovery V2");
return 0;

static SqlDiscoveryTarget Target() => new("Server=opaque.invalid;Integrated Security=true", "opaque_database");

static ObservedIdentityResult AvailableIdentity(string serverInstance = "SQLNODE01\\INSTANCE", string databaseName = "ObservedDb") =>
    new(ObservedIdentityStatus.Available, new(serverInstance, databaseName));

static ObservedIdentityResult UnavailableIdentity() =>
    new(ObservedIdentityStatus.Unavailable, Diagnostic: new("OBSERVED_DATABASE_IDENTITY", "OBSERVED_DATABASE_IDENTITY_UNAVAILABLE"));

static RecordingTransport SuccessfulTransport() => new();

static Task TaxonomySqlContract()
{
    EqualSequence(new[] { "dbo", "guest", "sys", "INFORMATION_SCHEMA", "db_accessadmin", "db_backupoperator",
        "db_datareader", "db_datawriter", "db_ddladmin", "db_denydatareader", "db_denydatawriter", "db_owner", "db_securityadmin" }, EmptyForNewEfV1.StandardSchemas);
    False(EmptyForNewEfV1.StandardSchemas.Contains("cicd"));
    var sql = EmptyForNewEfV1.Sql;
    foreach (var catalog in new[] { "sys.objects", "sys.schemas", "sys.types", "sys.table_types", "sys.triggers", "sys.partition_functions", "sys.partition_schemes", "sys.assemblies", "sys.xml_schema_collections", "sys.fulltext_catalogs" }) True(sql.Contains(catalog));
    True(sql.Contains("Latin1_General_100_BIN2"));
    False(sql.Contains("is_fixed_role"));
    False(sql.Contains("N'cicd'"));
    True(sql.Contains("b.object_id = tt.type_table_object_id"));
    True(sql.Contains("b.object_id = tr.object_id"));
    True(sql.Contains("tr.parent_class = 0 AND tr.is_ms_shipped = 0"));
    True(sql.Contains("HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION')"));
    // With history absent OBJECT_ID is NULL. NOT(parent_id = NULL) would hide
    // every user object through SQL's UNKNOWN predicate and falsely prove empty.
    True(sql.Contains("OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL"));
    True(sql.Contains("OR o.parent_object_id <> OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U')"));
    foreach (var forbidden in new[] { "INSERT ", "UPDATE ", "DELETE ", "MERGE ", "EXEC ", "CREATE ", "ALTER ", "DROP ", "TRUNCATE ", "ExecuteNonQuery" }) False(sql.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    return Task.CompletedTask;
}

static DataTable TaxonomyTable()
{
    var table = new DataTable();
    table.Columns.Add("visibility", typeof(int)); table.Columns.Add("business", typeof(long));
    foreach (var category in EmptyForNewEfV1.Categories) table.Columns.Add(category, typeof(long));
    return table;
}
static object[] TaxonomyRow() => new object[] { 1, 0L }.Concat(EmptyForNewEfV1.Categories.Select(_ => (object)0L)).ToArray();

static async Task TaxonomyReader()
{
    for (var index = -1; index < EmptyForNewEfV1.Categories.Count; index++)
    {
        var table = TaxonomyTable(); var row = TaxonomyRow(); if (index >= 0) row[index + 2] = 1L; table.Rows.Add(row);
        using var reader = table.CreateDataReader(); var result = await EmptyForNewEfV1.ReadAsync(reader, default);
        Equal(PhysicalStatus.Complete, result.Status); Equal(0L, result.BusinessObjectCount!.Value);
        Equal(index < 0 ? 0L : 1L, result.TechnicalObjectCount!.Value); True(EmptyForNewEfV1.Valid(result));
    }
}

static async Task TaxonomyReaderFailures()
{
    for (var variant = 0; variant < 7; variant++)
    {
        var table = TaxonomyTable(); var row = TaxonomyRow();
        if (variant == 0) row[0] = 0;
        if (variant == 1) row[2] = DBNull.Value;
        if (variant == 2) row[2] = -1L;
        if (variant == 3) row[2] = EmptyForNewEfV1.MaxSafeCount + 1;
        if (variant == 4) { row[2] = EmptyForNewEfV1.MaxSafeCount; row[3] = 1L; }
        if (variant != 5) table.Rows.Add(row);
        if (variant == 6) table.Rows.Add(TaxonomyRow());
        using var reader = table.CreateDataReader(); var result = await EmptyForNewEfV1.ReadAsync(reader, default);
        Equal(PhysicalStatus.Partial, result.Status); True(result.TechnicalObjectCount is null); True(result.BusinessObjectCount is null);
    }
    var shortTable = TaxonomyTable(); shortTable.Columns.RemoveAt(9);
    using var shortReader = shortTable.CreateDataReader(); Equal(PhysicalStatus.Partial, (await EmptyForNewEfV1.ReadAsync(shortReader, default)).Status);
    var first = TaxonomyTable(); first.Rows.Add(TaxonomyRow()); var second = TaxonomyTable();
    using var extraReader = new DataTableReader(new[] { first, second });
    Equal(PhysicalStatus.Partial, (await EmptyForNewEfV1.ReadAsync(extraReader, default)).Status);
}

static async Task<(int ExitCode, JsonDocument Json)> NewPipeline(PhysicalResult physical)
{
    var transport = SuccessfulTransport(); transport.Physical = _ => Task.FromResult(physical);
    transport.History = _ => Task.FromResult(new HistoryResult(HistoryStatus.Absent));
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport));
    var envelope = Envelope(projection, "NEW", "EF_MIGRATIONS", new { status = "PRESENT_VALID", migrations = new { count = 1, ids = new[] { "20260101000000_First" } } },
        new { status = "NOT_EVALUATED" }, new { status = "NOT_EVALUATED" }, new { status = "NOT_REQUIRED" });
    return RunPipeline(envelope);
}
static async Task IntegrationNewEfComplete()
{
    var pipeline = await NewPipeline(EmptyForNewEfV1.Complete(0, new long[EmptyForNewEfV1.Categories.Count]));
    Equal(0, pipeline.ExitCode); var classification = pipeline.Json.RootElement.GetProperty("classificationResult");
    Equal("NEW_EF", classification.GetProperty("inferences").GetProperty("scenario").GetString());
    Equal("ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE", classification.GetProperty("decision").GetProperty("status").GetString());
}
static async Task IntegrationNewEfComplementary()
{
    for (var index = 0; index < EmptyForNewEfV1.Categories.Count; index++)
    {
        var counts = new long[EmptyForNewEfV1.Categories.Count]; counts[index] = 1;
        var pipeline = await NewPipeline(EmptyForNewEfV1.Complete(0, counts));
        Equal("BLOCKED_NEW_TECHNICAL_ONLY_UNDECIDED", pipeline.Json.RootElement.GetProperty("classificationResult").GetProperty("decision").GetProperty("primaryBlock").GetString());
    }
}
static async Task TaxonomyProjectionFailure()
{
    var transport = SuccessfulTransport(); var complete = EmptyForNewEfV1.Complete(0, new long[EmptyForNewEfV1.Categories.Count]);
    transport.Physical = _ => Task.FromResult(complete with { TechnicalObjectCount = 1 });
    False(new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport)).IsRepresentable);
    transport.Physical = _ => Task.FromResult(complete with { Taxonomy = complete.Taxonomy! with { Version = 2 } });
    False(new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport)).IsRepresentable);
}

static async Task HistoryStructureBoundary()
{
    foreach (var (flags, expected) in new[] {
        (new[] { 0, 0, 0, 1 }, (HistoryStatus?)HistoryStatus.Absent),
        (new[] { 0, 0, 0, 0 }, (HistoryStatus?)HistoryStatus.Unreadable),
        (new[] { 1, 1, 0, 1 }, (HistoryStatus?)HistoryStatus.InvalidStructure),
        (new[] { 1, 0, 0, 1 }, (HistoryStatus?)HistoryStatus.Unreadable),
        (new[] { 1, 1, 1, 1 }, (HistoryStatus?)null),
        (new[] { 0, 1, 1, 1 }, (HistoryStatus?)HistoryStatus.TechnicalError),
        (new[] { 2, 1, 1, 1 }, (HistoryStatus?)HistoryStatus.TechnicalError)
    })
    {
        var table = new DataTable(); for (var i = 0; i < 4; i++) table.Columns.Add($"flag{i}", typeof(int));
        table.Rows.Add(flags.Cast<object>().ToArray()); using var reader = table.CreateDataReader();
        Equal(expected, (await SqlClientDiscoveryTransportV2.ReadHistoryStructureAsync(reader, default))?.Status);
    }
}

static async Task<SqlDiscoveryResult> Discover(RecordingTransport transport, CancellationToken token = default) =>
    await new SqlDiscoveryOrchestratorV2(transport).DiscoverAsync(Target(), token);

static async Task FullSuccess()
{
    var transport = SuccessfulTransport();
    var result = await Discover(transport);
    Equal(ConnectionStatus.Succeeded, result.ServerConnection.Status);
    EqualSequence(new[] { "server", "lookup", "target", "metadata", "physical", "history" }, transport.Calls);
    True(transport.Calls.All(call => call is not "write"));
}

static async Task AuthenticationFailure()
{
    var transport = SuccessfulTransport();
    transport.ConnectServer = _ => throw new SqlDiscoveryAuthenticationException();
    var result = await Discover(transport);
    Equal(ConnectionStatus.AuthenticationFailed, result.ServerConnection.Status);
    Equal("AUTHENTICATION_FAILED", result.Diagnostics.Single().Code);
    EqualSequence(new[] { "server" }, transport.Calls);
}

static async Task TransportFailure()
{
    var transport = SuccessfulTransport();
    transport.ConnectServer = _ => throw new InvalidOperationException("Password=do-not-expose;Server=opaque.invalid");
    var result = await Discover(transport);
    Equal(ConnectionStatus.TransportFailed, result.ServerConnection.Status);
    Equal("TRANSPORT_FAILED", result.Diagnostics.Single().Code);
}

static async Task ConnectionTimeout()
{
    var transport = SuccessfulTransport();
    transport.ConnectServer = _ => throw new TimeoutException("opaque");
    var result = await Discover(transport);
    Equal(ConnectionStatus.TimedOut, result.ServerConnection.Status);
    Equal("TIMEOUT", result.Diagnostics.Single().Code);
    Equal(DatabaseLookupStatus.NotAttempted, result.DatabaseLookup.Status);
    EqualSequence(new[] { "server" }, transport.Calls);
}

static async Task Cancellation()
{
    var transport = SuccessfulTransport();
    using var source = new CancellationTokenSource();
    transport.ConnectServer = token => { source.Cancel(); return Task.FromCanceled(token); };
    var result = await Discover(transport, source.Token);
    Equal(ConnectionStatus.Cancelled, result.ServerConnection.Status);
    True(transport.SeenTokens.Single() == source.Token);
    Equal(DatabaseLookupStatus.NotAttempted, result.DatabaseLookup.Status);
    EqualSequence(new[] { "server" }, transport.Calls);
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(result);
    True(projection.IsRepresentable);
    Equal("CANCELLED", Status(projection, "connectionSource"));
}

static async Task PreCancelled()
{
    var transport = SuccessfulTransport();
    using var source = new CancellationTokenSource();
    source.Cancel();
    var result = await Discover(transport, source.Token);
    Equal(ConnectionStatus.Cancelled, result.ServerConnection.Status);
    Equal(DatabaseLookupStatus.NotAttempted, result.DatabaseLookup.Status);
    Equal(0, transport.Calls.Count);
    Equal("CANCELLED", Status(new SqlDiscoveryOrchestratorV2(transport).ProjectSources(result), "connectionSource"));
}

static async Task DatabaseFound() => Equal(DatabaseLookupStatus.Found, (await Discover(SuccessfulTransport())).DatabaseLookup.Status);

static async Task ConfirmedAbsent()
{
    var transport = SuccessfulTransport();
    transport.Lookup = _ => Task.FromResult(new DatabaseLookupResult(DatabaseLookupStatus.NotFoundConfirmed));
    var result = await Discover(transport);
    Equal(DatabaseLookupStatus.NotFoundConfirmed, result.DatabaseLookup.Status);
    Equal(ConnectionStatus.NotAttempted, result.TargetConnection.Status);
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(result);
    True(projection.IsRepresentable);
    Equal("NOT_FOUND", Status(projection, "databaseLookupSource"));
}

static async Task LookupVisibilityInsufficient()
{
    var transport = SuccessfulTransport();
    transport.Lookup = _ => Task.FromResult(new DatabaseLookupResult(DatabaseLookupStatus.VisibilityInsufficient));
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport));
    Equal("UNKNOWN", Status(projection, "databaseLookupSource"));
}

static async Task LookupTechnicalFailure()
{
    var transport = SuccessfulTransport();
    transport.Lookup = _ => throw new InvalidOperationException("sensitive");
    var result = await Discover(transport);
    Equal(DatabaseLookupStatus.TechnicalError, result.DatabaseLookup.Status);
    Equal("TECHNICAL_ERROR", result.Diagnostics.Single().Code);
}

static async Task LookupTimeout()
{
    var transport = SuccessfulTransport();
    transport.Lookup = _ => throw new TimeoutException("opaque");
    var result = await Discover(transport);
    Equal(DatabaseLookupStatus.TimedOut, result.DatabaseLookup.Status);
    Equal(ConnectionStatus.NotAttempted, result.TargetConnection.Status);
    Equal(MetadataStatus.NotAttempted, result.Metadata.Status);
    EqualSequence(new[] { "server", "lookup" }, transport.Calls);
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(result);
    True(projection.IsRepresentable);
    Equal("TIMEOUT", Status(projection, "databaseLookupSource"));
}

static async Task LookupCancellation()
{
    var transport = SuccessfulTransport();
    using var source = new CancellationTokenSource();
    transport.Lookup = token => { source.Cancel(); return Task.FromCanceled<DatabaseLookupResult>(token); };
    var result = await Discover(transport, source.Token);
    Equal(DatabaseLookupStatus.Cancelled, result.DatabaseLookup.Status);
    Equal(ConnectionStatus.NotAttempted, result.TargetConnection.Status);
    Equal("CANCELLED", Status(new SqlDiscoveryOrchestratorV2(transport).ProjectSources(result), "databaseLookupSource"));
}

static async Task TargetFailure()
{
    var transport = SuccessfulTransport();
    transport.ConnectTarget = _ => throw new InvalidOperationException("target failed");
    var result = await Discover(transport);
    Equal(ConnectionStatus.Succeeded, result.ServerConnection.Status);
    Equal(ConnectionStatus.TransportFailed, result.TargetConnection.Status);
    Equal(MetadataStatus.NotAttempted, result.Metadata.Status);
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(result);
    True(projection.IsRepresentable);
    Equal("TRANSPORT_FAILED", Status(projection, "targetConnectionSource"));
}

static async Task TargetExactFailures()
{
    foreach (var item in new (Func<CancellationToken, Task<ObservedIdentityResult>> Action, ConnectionStatus Expected, string Raw)[]
    {
        (_ => throw new SqlDiscoveryAuthenticationException(), ConnectionStatus.AuthenticationFailed, "AUTHENTICATION_FAILED"),
        (_ => throw new TimeoutException(), ConnectionStatus.TimedOut, "TIMEOUT")
    })
    {
        var transport = SuccessfulTransport(); transport.ConnectTarget = item.Action;
        var result = await Discover(transport);
        Equal(item.Expected, result.TargetConnection.Status);
        Equal(item.Raw, Status(new SqlDiscoveryOrchestratorV2(transport).ProjectSources(result), "targetConnectionSource"));
        Equal(MetadataStatus.NotAttempted, result.Metadata.Status);
    }
    using var source = new CancellationTokenSource();
    var cancelled = SuccessfulTransport();
    cancelled.ConnectTarget = token => { source.Cancel(); return Task.FromCanceled<ObservedIdentityResult>(token); };
    var cancelledResult = await Discover(cancelled, source.Token);
    Equal(ConnectionStatus.Cancelled, cancelledResult.TargetConnection.Status);
    Equal("CANCELLED", Status(new SqlDiscoveryOrchestratorV2(cancelled).ProjectSources(cancelledResult), "targetConnectionSource"));
}

static async Task TargetIdentitySuccess()
{
    var transport = SuccessfulTransport();
    transport.ConnectTarget = _ => Task.FromResult(AvailableIdentity("SQLNODE01\\INSTANCE", "ObservedDb"));
    var result = await Discover(transport);
    Equal(ConnectionStatus.Succeeded, result.TargetConnection.Status);
    Equal("SQLNODE01\\INSTANCE", result.ObservedIdentity.Identity?.ServerInstance);
    Equal("ObservedDb", result.ObservedIdentity.Identity?.DatabaseName);
    False(result.ObservedIdentity.Identity?.DatabaseName == Target().DatabaseName);
}

static async Task TargetIdentityMissing()
{
    var transport = SuccessfulTransport();
    transport.ConnectTarget = _ => Task.FromResult(UnavailableIdentity());
    var result = await Discover(transport);
    Equal(ConnectionStatus.Succeeded, result.TargetConnection.Status);
    Equal(ObservedIdentityStatus.Unavailable, result.ObservedIdentity.Status);
    True(result.ObservedIdentity.Identity is null);
    Equal(MetadataStatus.Sufficient, result.Metadata.Status);
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(result);
    False(projection.IsRepresentable);
    Contains("OBSERVED_DATABASE_IDENTITY_UNAVAILABLE", projection.Gaps);
}

static async Task TargetIdentityBlankFields()
{
    foreach (var identity in new[]
    {
        AvailableIdentity("", "ObservedDb"),
        AvailableIdentity("SQLNODE01\\INSTANCE", "")
    })
    {
        var transport = SuccessfulTransport();
        transport.ConnectTarget = _ => Task.FromResult(identity);
        var result = await Discover(transport);
        Equal(ConnectionStatus.Succeeded, result.TargetConnection.Status);
        False(new SqlDiscoveryOrchestratorV2(transport).ProjectSources(result).IsRepresentable);
    }
}

static async Task MetadataSufficient() => Equal(MetadataStatus.Sufficient, (await Discover(SuccessfulTransport())).Metadata.Status);

static async Task MetadataInsufficient()
{
    var transport = SuccessfulTransport();
    transport.Metadata = _ => Task.FromResult(new MetadataResult(MetadataStatus.Insufficient));
    var result = await Discover(transport);
    Equal(PhysicalStatus.NotAttempted, result.Physical.Status);
    Equal(HistoryStatus.NotAttempted, result.History.Status);
}

static async Task MetadataFailure()
{
    var transport = SuccessfulTransport();
    transport.Metadata = _ => throw new InvalidOperationException("provider details");
    var result = await Discover(transport);
    Equal(MetadataStatus.TechnicalError, result.Metadata.Status);
    Equal("TECHNICAL_ERROR", result.Diagnostics.Single().Code);
}

static async Task MetadataTimeoutCancellation()
{
    var timedOut = SuccessfulTransport(); timedOut.Metadata = _ => throw new TimeoutException();
    var timedOutResult = await Discover(timedOut);
    Equal("TIMEOUT", Status(new SqlDiscoveryOrchestratorV2(timedOut).ProjectSources(timedOutResult), "metadataSource"));
    Equal(PhysicalStatus.NotAttempted, timedOutResult.Physical.Status);
    using var source = new CancellationTokenSource();
    var cancelled = SuccessfulTransport(); cancelled.Metadata = token => { source.Cancel(); return Task.FromCanceled<MetadataResult>(token); };
    var cancelledResult = await Discover(cancelled, source.Token);
    Equal("CANCELLED", Status(new SqlDiscoveryOrchestratorV2(cancelled).ProjectSources(cancelledResult), "metadataSource"));
    Equal(PhysicalStatus.NotAttempted, cancelledResult.Physical.Status);
}

static async Task PhysicalComplete()
{
    var transport = SuccessfulTransport();
    transport.Physical = _ => Task.FromResult(new PhysicalResult(PhysicalStatus.Complete, 7));
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport));
    var physical = Section(projection, "physicalSource");
    Equal(7L, physical["businessObjectCount"]);
    False(physical.ContainsKey("technicalObjectCount"));
}

static async Task PhysicalPartial()
{
    var transport = SuccessfulTransport();
    transport.Physical = _ => Task.FromResult(new PhysicalResult(PhysicalStatus.Partial, 4));
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport));
    False(projection.IsRepresentable);
    Contains("PHYSICAL_PARTIAL_UNREPRESENTABLE", projection.Gaps);
    var invocations = 0;
    False(InvokePipelineWhenRepresentable(projection, () => invocations++));
    Equal(0, invocations);
}

static async Task PhysicalTimeoutCancellation()
{
    var timedOut = SuccessfulTransport(); timedOut.Physical = _ => throw new TimeoutException();
    var timedOutResult = await Discover(timedOut);
    Equal("TIMEOUT", Status(new SqlDiscoveryOrchestratorV2(timedOut).ProjectSources(timedOutResult), "physicalSource"));
    Equal(HistoryStatus.NotAttempted, timedOutResult.History.Status);
    using var source = new CancellationTokenSource();
    var cancelled = SuccessfulTransport(); cancelled.Physical = token => { source.Cancel(); return Task.FromCanceled<PhysicalResult>(token); };
    var cancelledResult = await Discover(cancelled, source.Token);
    Equal("CANCELLED", Status(new SqlDiscoveryOrchestratorV2(cancelled).ProjectSources(cancelledResult), "physicalSource"));
    Equal(HistoryStatus.NotAttempted, cancelledResult.History.Status);
}

static async Task HistoryAbsent()
{
    var transport = SuccessfulTransport();
    transport.History = _ => Task.FromResult(new HistoryResult(HistoryStatus.Absent));
    Equal("ABSENT", Status(new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport)), "historySource"));
}

static async Task HistoryEmpty()
{
    var transport = SuccessfulTransport();
    transport.History = _ => Task.FromResult(new HistoryResult(HistoryStatus.Empty));
    var section = Section(new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport)), "historySource");
    Equal("PRESENT", section["status"]);
    Equal(0, section["migrationCount"]);
}

static async Task HistoryRows()
{
    var ids = new[] { "20260101000000_A", "20260101000000_A", "20260201000000_B" };
    var transport = SuccessfulTransport();
    transport.History = _ => Task.FromResult(new HistoryResult(HistoryStatus.Present, ids));
    var result = await Discover(transport);
    EqualSequence(ids, result.History.MigrationIds);
    ids[0] = "mutated";
    Equal("20260101000000_A", result.History.MigrationIds[0]);
}

static async Task HistoryUnreadable() => await AssertHistoryStatus(HistoryStatus.Unreadable, "UNREADABLE");
static async Task HistoryInvalid() => await AssertHistoryStatus(HistoryStatus.InvalidStructure, "INVALID_STRUCTURE");
static async Task HistoryError() => await AssertHistoryStatus(HistoryStatus.TechnicalError, "ERROR");

static async Task HistoryTimeoutCancellation()
{
    var timedOut = SuccessfulTransport(); timedOut.History = _ => throw new TimeoutException();
    Equal("TIMEOUT", Status(new SqlDiscoveryOrchestratorV2(timedOut).ProjectSources(await Discover(timedOut)), "historySource"));
    using var source = new CancellationTokenSource();
    var cancelled = SuccessfulTransport(); cancelled.History = token => { source.Cancel(); return Task.FromCanceled<HistoryResult>(token); };
    Equal("CANCELLED", Status(new SqlDiscoveryOrchestratorV2(cancelled).ProjectSources(await Discover(cancelled, source.Token)), "historySource"));
}

static async Task AssertHistoryStatus(HistoryStatus status, string projected)
{
    var transport = SuccessfulTransport();
    transport.History = _ => Task.FromResult(new HistoryResult(status));
    Equal(projected, Status(new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport)), "historySource"));
}

static async Task Prerequisites()
{
    var transport = SuccessfulTransport();
    transport.Lookup = _ => Task.FromResult(new DatabaseLookupResult(DatabaseLookupStatus.NotFoundConfirmed));
    var result = await Discover(transport);
    Equal(ConnectionStatus.NotAttempted, result.TargetConnection.Status);
    Equal(MetadataStatus.NotAttempted, result.Metadata.Status);
    Equal(PhysicalStatus.NotAttempted, result.Physical.Status);
    Equal(HistoryStatus.NotAttempted, result.History.Status);
    EqualSequence(new[] { "server", "lookup" }, transport.Calls);
}

static async Task SensitiveException()
{
    var transport = SuccessfulTransport();
    transport.Physical = _ => throw new InvalidOperationException("Server=opaque.invalid;Password=do-not-expose;stack-path");
    var result = await Discover(transport);
    var serialized = JsonSerializer.Serialize(result);
    False(serialized.Contains("do-not-expose", StringComparison.OrdinalIgnoreCase));
    False(serialized.Contains("opaque.invalid", StringComparison.OrdinalIgnoreCase));
    Equal("TECHNICAL_ERROR", result.Physical.Diagnostic?.Code);
    Equal(HistoryStatus.NotAttempted, result.History.Status);
    False(transport.Calls.Contains("history"));
}

static Task StaticTransportGuards()
{
    var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "SqlDiscovery", "SqlClientDiscoveryTransportV2.cs"));
    var policySource = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "SqlDiscovery", "TestTlsFallbackPolicy.cs"));
    foreach (var forbidden in new[] { "INSERT ", "UPDATE ", "DELETE ", "MERGE ", "EXEC ", "CREATE ", "ALTER ", "DROP ", "TRUNCATE " })
        False(source.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    True(source.Contains("@databaseName", StringComparison.Ordinal));
    True(source.Contains("connectionTimeoutSeconds = 15", StringComparison.Ordinal));
    True(source.Contains("commandTimeoutSeconds = 30", StringComparison.Ordinal));
    True(source.Contains("ConnectRetryCount = 0", StringComparison.Ordinal));
    True(source.Contains("tlsPolicy.Apply(builder)", StringComparison.Ordinal));
    True(policySource.Contains("SqlConnectionEncryptOption.Strict", StringComparison.Ordinal));
    True(policySource.Contains("SqlConnectionEncryptOption.Mandatory", StringComparison.Ordinal));
    True(policySource.Contains("TrustServerCertificate = RequestedMode == SqlTlsMode.TestUntrustedCertificate", StringComparison.Ordinal));
    False((source + policySource).Contains("Encrypt = false", StringComparison.OrdinalIgnoreCase));
    True(source.Contains("ORDER BY MigrationId ASC", StringComparison.Ordinal));
    // The physical predicate excludes this name for compatibility. History must
    // also reject a view/synonym/procedure impersonating the excluded table.
    True(source.Contains("CASE WHEN OBJECT_ID(N'dbo.__EFMigrationsHistory') IS NULL THEN 0 ELSE 1 END"));
    var targetMethod = source[(source.IndexOf("ConnectTargetAsync", StringComparison.Ordinal))..source.IndexOf("InspectMetadataAsync", StringComparison.Ordinal)];
    Equal(1, targetMethod.Split("OpenConnectionAsync(", StringSplitOptions.None).Length - 1);
    True(targetMethod.Contains("CreateCommand(connection", StringComparison.Ordinal));
    True(targetMethod.Contains("SERVERPROPERTY(N'ServerName')", StringComparison.Ordinal));
    True(targetMethod.Contains("DB_NAME()", StringComparison.Ordinal));
    return Task.CompletedTask;
}

static async Task TlsStrictDefault()
{
    var policy = new SqlTlsPolicy("TEST");
    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder("Server=opaque;Encrypt=False;TrustServerCertificate=True");
    policy.Apply(builder);
    Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Strict, builder.Encrypt);
    False(builder.TrustServerCertificate);
    var attempts = new List<SqlTlsMode>();
    var result = await policy.ExecuteAsync((mode, _) => { attempts.Add(mode); return Task.FromResult(7); }, default);
    Equal(7, result);
    EqualSequence(new[] { SqlTlsMode.Strict }, attempts);
    Equal("STRICT", policy.Evidence.TlsRequestedMode);
    False(policy.Evidence.TlsFallbackAttempted);
    False(policy.Evidence.TlsFallbackAllowed);
    Equal("STRICT", policy.Evidence.TlsEffectiveMode);
    Equal("DEFAULT_STRICT", policy.Evidence.TlsPolicySource);
    Equal("SUCCEEDED", policy.Evidence.TlsInitialResult);
    True(policy.Evidence.TlsCertificateValidated);
    True(policy.Evidence.TransportEncrypted);
}

static async Task TlsExplicitTestMode()
{
    var policy = new SqlTlsPolicy("TEST", SqlTlsMode.TestUntrustedCertificate);
    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder("Server=opaque;Encrypt=Strict;TrustServerCertificate=False");
    policy.Apply(builder);
    Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
    True(builder.TrustServerCertificate);
    var attempts = new List<SqlTlsMode>();
    var result = await policy.ExecuteAsync((mode, _) => { attempts.Add(mode); return Task.FromResult("connected"); }, default);
    Equal("connected", result);
    EqualSequence(new[] { SqlTlsMode.TestUntrustedCertificate }, attempts);
    False(policy.Evidence.TlsFallbackAttempted);
    False(policy.Evidence.TlsFallbackAllowed);
    Equal("TEST_UNTRUSTED_CERTIFICATE", policy.Evidence.TlsRequestedMode);
    Equal("TEST_UNTRUSTED_CERTIFICATE", policy.Evidence.TlsEffectiveMode);
    Equal("EXPLICIT_TEST_CONFIGURATION", policy.Evidence.TlsPolicySource);
    False(policy.Evidence.TlsCertificateValidated);
    True(policy.Evidence.TransportEncrypted);
    Equal("SUCCEEDED", policy.Evidence.TlsInitialResult);
}

static async Task TlsExplicitModeNoFallback()
{
    var policy = new SqlTlsPolicy("TEST", SqlTlsMode.TestUntrustedCertificate);
    var attempts = new List<SqlTlsMode>();
    await ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync<int>((mode, _) =>
    {
        attempts.Add(mode);
        throw new InvalidOperationException("certificate chain was issued by an authority that is not trusted");
    }, default));
    EqualSequence(new[] { SqlTlsMode.TestUntrustedCertificate }, attempts);
    False(policy.Evidence.TlsFallbackAttempted);
    False(policy.Evidence.TlsFallbackAllowed);
    Equal("OTHER_FAILURE", policy.Evidence.TlsInitialResult);
    True(policy.Evidence.DiagnosticFingerprint is not null);
}

static Task TlsNonTestBlocked()
{
    foreach (var environment in new[] { "QA", "PROD" })
        Throws<InvalidOperationException>(() => new SqlTlsPolicy(environment, SqlTlsMode.TestUntrustedCertificate));
    _ = new SqlTlsPolicy("QA", SqlTlsMode.Strict);
    _ = new SqlTlsPolicy("PROD", SqlTlsMode.Strict);
    return Task.CompletedTask;
}

static Task TlsClassifierAndSanitization()
{
    True(TestTlsFallbackPolicy.HasCertificateTrustMessage("A valid TLS certificate is not configured to accept strict connections."));
    True(TestTlsFallbackPolicy.HasCertificateTrustMessage("certificate chain was issued by an authority that is not trusted"));
    False(TestTlsFallbackPolicy.HasCertificateTrustMessage("certificate hostname mismatch"));
    False(TestTlsFallbackPolicy.HasCertificateTrustMessage("target principal name is incorrect"));
    False(TestTlsFallbackPolicy.HasCertificateTrustMessage("login failed for Password=do-not-expose"));
    var serialized = JsonSerializer.Serialize(new SqlTlsPolicy("TEST", SqlTlsMode.TestUntrustedCertificate).Evidence);
    False(serialized.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    return Task.CompletedTask;
}

static Task TlsDiagnosticFingerprint()
{
    var generic = SanitizedExceptionFingerprint.Capture(new InvalidOperationException("Server=secret;Password=do-not-expose;token=hidden"));
    Equal("UNKNOWN", generic.TlsFailureCategory);
    var serialized = JsonSerializer.Serialize(generic);
    foreach (var forbidden in new[] { "do-not-expose", "Server=", "Password=", "token=", "StackTrace", "Message" })
        False(serialized.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    Equal("TIMEOUT", SanitizedExceptionFingerprint.Capture(new TimeoutException("private")).TlsFailureCategory);
    Equal("CANCELLED", SanitizedExceptionFingerprint.Capture(new OperationCanceledException("private")).TlsFailureCategory);
    Equal("AUTHENTICATION", SanitizedExceptionFingerprint.Capture(new SqlDiscoveryAuthenticationException()).TlsFailureCategory);
    Equal("HOSTNAME_OR_IDENTITY_MISMATCH", SanitizedExceptionFingerprint.Capture(
        new AuthenticationException("target principal name is incorrect")).TlsFailureCategory);
    Equal("KNOWN_CERTIFICATE_TRUST", SanitizedExceptionFingerprint.Capture(
        new AuthenticationException("certificate chain was issued by an authority that is not trusted")).TlsFailureCategory);

    var outer = new IOException("opaque", new AuthenticationException("opaque inner"));
    var nested = SanitizedExceptionFingerprint.Capture(outer);
    Equal("TRANSPORT_OTHER", nested.TlsFailureCategory);
    Equal(typeof(AuthenticationException).FullName, nested.InnerExceptionType);
    True(nested.InnerHResult?.StartsWith("0x", StringComparison.Ordinal) == true);
    return Task.CompletedTask;
}

static async Task InputAndResultIsolation()
{
    var target = Target();
    var before = JsonSerializer.Serialize(target);
    var transport = SuccessfulTransport();
    var first = await new SqlDiscoveryOrchestratorV2(transport).DiscoverAsync(target, default);
    transport.History = _ => Task.FromResult(new HistoryResult(HistoryStatus.Present, ["20260301000000_C"]));
    var second = await new SqlDiscoveryOrchestratorV2(transport).DiscoverAsync(target, default);
    Equal(before, JsonSerializer.Serialize(target));
    Equal("20260101000000_A", first.History.MigrationIds[0]);
    Equal("20260301000000_C", second.History.MigrationIds[0]);
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(first);
    var history = (IDictionary<string, object?>)projection.Sources!["historySource"]!;
    var mutationBlocked = false;
    try { history["status"] = "MUTATED"; } catch (NotSupportedException) { mutationBlocked = true; }
    True(mutationBlocked);
}

static async Task IntegrationExistingEf()
{
    var transport = SuccessfulTransport();
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport));
    var envelope = Envelope(projection, "EXISTING", "EF_MIGRATIONS",
        new { status = "PRESENT_VALID", migrations = new { count = 2, ids = new[] { "20260101000000_A", "20260201000000_B" } } },
        new { status = "CONSISTENT" }, new { status = "CERTIFIED" }, new { status = "MANAGED" });
    var pipeline = RunPipeline(envelope);
    Equal(0, pipeline.ExitCode);
    True(pipeline.Json.RootElement.GetProperty("classificationInvoked").GetBoolean());
    Equal("EXISTING_EF", pipeline.Json.RootElement.GetProperty("classificationResult").GetProperty("inferences").GetProperty("scenario").GetString());
}

static async Task IntegrationTargetGap()
{
    var transport = SuccessfulTransport();
    transport.ConnectTarget = _ => throw new InvalidOperationException("target");
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport));
    True(projection.IsRepresentable);
    var envelope = Envelope(projection, "EXISTING", "EF_MIGRATIONS",
        new { status = "PRESENT_VALID", migrations = new { count = 1, ids = new[] { "20260101000000_A" } } },
        new { status = "UNKNOWN" }, new { status = "UNKNOWN" }, new { status = "UNKNOWN" });
    var pipeline = RunPipeline(envelope);
    Equal(75, pipeline.ExitCode);
    False(pipeline.Json.RootElement.GetProperty("classificationInvoked").GetBoolean());
}

static async Task IntegrationUnknownLookup()
{
    var transport = SuccessfulTransport();
    transport.Lookup = _ => Task.FromResult(new DatabaseLookupResult(DatabaseLookupStatus.VisibilityInsufficient));
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport));
    var envelope = Envelope(projection, "EXISTING", "EF_MIGRATIONS",
        new { status = "NOT_ATTEMPTED" }, new { status = "INSUFFICIENT_EVIDENCE" },
        new { status = "UNKNOWN" }, new { status = "UNKNOWN" });
    var pipeline = RunPipeline(envelope);
    False(pipeline.Json.RootElement.GetProperty("classificationInvoked").GetBoolean());
    False(pipeline.Json.RootElement.GetProperty("adapterStatus").GetString() == "CLASSIFIED");
}

static async Task IntegrationNewEfTaxonomyGap()
{
    var transport = SuccessfulTransport();
    transport.Physical = _ => Task.FromResult(new PhysicalResult(PhysicalStatus.Complete, 0));
    transport.History = _ => Task.FromResult(new HistoryResult(HistoryStatus.Absent));
    var projection = new SqlDiscoveryOrchestratorV2(transport).ProjectSources(await Discover(transport));
    var envelope = Envelope(projection, "NEW", "EF_MIGRATIONS",
        new { status = "PRESENT_VALID", migrations = new { count = 1, ids = new[] { "20260101000000_A" } } },
        new { status = "NOT_EVALUATED" }, new { status = "NOT_EVALUATED" }, new { status = "NOT_REQUIRED" });
    var pipeline = RunPipeline(envelope);
    var root = pipeline.Json.RootElement;
    if (root.GetProperty("classificationInvoked").GetBoolean())
        True(root.GetProperty("classificationResult").GetProperty("decision").GetProperty("status").GetString() != "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE");
    else
        Equal("INSUFFICIENT_EVIDENCE", root.GetProperty("adapterStatus").GetString());
}

static object Envelope(SourceProjection projection, string lifecycle, string mode, object repository, object schema, object registry, object onboarding)
{
    True(projection.IsRepresentable);
    var source = projection.Sources!;
    return new Dictionary<string, object?>
    {
        ["producerContractVersion"] = 1,
        ["declarationsSource"] = new { databaseLifecycle = lifecycle, changeManagementMode = mode },
        ["connectionSource"] = source["connectionSource"],
        ["databaseLookupSource"] = source["databaseLookupSource"],
        ["targetConnectionSource"] = source["targetConnectionSource"],
        ["metadataSource"] = source["metadataSource"],
        ["physicalSource"] = source["physicalSource"],
        ["historySource"] = source["historySource"],
        ["repositorySource"] = repository,
        ["schemaSource"] = schema,
        ["registrySource"] = registry,
        ["onboardingSource"] = onboarding
    };
}

static (int ExitCode, JsonDocument Json) RunPipeline(object envelope)
{
    var root = RepositoryRoot();
    var producer = RunProcess("node", Path.Combine(root, "scripts", "compose-classification-evidence-v2.mjs"), JsonSerializer.Serialize(envelope));
    Equal(0, producer.ExitCode);
    var adapter = RunProcess("node", Path.Combine(root, "scripts", "adapt-classification-evidence-v2.mjs"), producer.Stdout);
    return (adapter.ExitCode, JsonDocument.Parse(adapter.Stdout));
}

static bool InvokePipelineWhenRepresentable(SourceProjection projection, Action invoke)
{
    if (!projection.IsRepresentable) return false;
    invoke();
    return true;
}

static (int ExitCode, string Stdout) RunProcess(string fileName, string argument, string stdin)
{
    var start = new ProcessStartInfo(fileName)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    var bashDirectory = Environment.GetEnvironmentVariable("ACTIONS_BASH_DIRECTORY");
    if (!string.IsNullOrWhiteSpace(bashDirectory))
        start.Environment["PATH"] = bashDirectory + Path.PathSeparator + (start.Environment["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
    start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("PROCESS_START_FAILED");
    process.StandardInput.Write(stdin);
    process.StandardInput.Close();
    var stdout = process.StandardOutput.ReadToEnd();
    _ = process.StandardError.ReadToEnd();
    if (!process.WaitForExit(10_000)) throw new TimeoutException("PROCESS_TIMEOUT");
    return (process.ExitCode, stdout);
}

static string RepositoryRoot() => Environment.GetEnvironmentVariable("ACTIONS_REPOSITORY_ROOT") ?? throw new InvalidOperationException("ACTIONS_REPOSITORY_ROOT_REQUIRED");

static IReadOnlyDictionary<string, object?> Section(SourceProjection projection, string name)
{
    True(projection.IsRepresentable);
    return (IReadOnlyDictionary<string, object?>)projection.Sources![name]!;
}

static string Status(SourceProjection projection, string name) => (string)Section(projection, name)["status"]!;

static void True(bool condition, string? message = null)
{
    if (!condition) throw new InvalidOperationException(message ?? "expected true");
}

static void False(bool condition, string? message = null) => True(!condition, message ?? "expected false");

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"expected {typeof(T).Name}");
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"expected {typeof(T).Name}");
}

static void Contains<T>(T expected, IEnumerable<T> actual)
{
    if (!actual.Contains(expected)) throw new InvalidOperationException($"missing {expected}");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"expected {expected}, actual {actual}");
}

static void EqualSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}]");
}

sealed class RecordingTransport : ISqlDiscoveryTransport
{
    public List<string> Calls { get; } = [];
    public List<CancellationToken> SeenTokens { get; } = [];
    public Func<CancellationToken, Task> ConnectServer { get; set; } = _ => Task.CompletedTask;
    public Func<CancellationToken, Task<DatabaseLookupResult>> Lookup { get; set; } = _ => Task.FromResult(new DatabaseLookupResult(DatabaseLookupStatus.Found));
    public Func<CancellationToken, Task<ObservedIdentityResult>> ConnectTarget { get; set; } = _ =>
        Task.FromResult(new ObservedIdentityResult(ObservedIdentityStatus.Available,
            new ObservedDatabaseIdentity("SQLNODE01\\INSTANCE", "ObservedDb")));
    public Func<CancellationToken, Task<MetadataResult>> Metadata { get; set; } = _ => Task.FromResult(new MetadataResult(MetadataStatus.Sufficient));
    public Func<CancellationToken, Task<PhysicalResult>> Physical { get; set; } = _ => Task.FromResult(new PhysicalResult(PhysicalStatus.Complete, 5));
    public Func<CancellationToken, Task<HistoryResult>> History { get; set; } = _ => Task.FromResult(new HistoryResult(HistoryStatus.Present, ["20260101000000_A"]));

    public Task ConnectServerAsync(SqlDiscoveryTarget target, CancellationToken token) => Invoke("server", token, ConnectServer);
    public Task<DatabaseLookupResult> LookupDatabaseAsync(SqlDiscoveryTarget target, CancellationToken token) => Invoke("lookup", token, Lookup);
    public Task<ObservedIdentityResult> ConnectTargetAsync(SqlDiscoveryTarget target, CancellationToken token) => Invoke("target", token, ConnectTarget);
    public Task<MetadataResult> InspectMetadataAsync(SqlDiscoveryTarget target, CancellationToken token) => Invoke("metadata", token, Metadata);
    public Task<PhysicalResult> ObservePhysicalAsync(SqlDiscoveryTarget target, CancellationToken token) => Invoke("physical", token, Physical);
    public Task<HistoryResult> ObserveHistoryAsync(SqlDiscoveryTarget target, CancellationToken token) => Invoke("history", token, History);

    private Task Invoke(string name, CancellationToken token, Func<CancellationToken, Task> action)
    {
        Calls.Add(name); SeenTokens.Add(token); return action(token);
    }

    private Task<T> Invoke<T>(string name, CancellationToken token, Func<CancellationToken, Task<T>> action)
    {
        Calls.Add(name); SeenTokens.Add(token); return action(token);
    }
}
