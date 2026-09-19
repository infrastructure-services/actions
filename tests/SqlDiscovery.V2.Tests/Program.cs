using System.Diagnostics;
using System.Text.Json;
using SqlDiscovery.V2;

var tests = new List<(string Name, Func<Task> Run)>
{
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
    ("target fallido no contamina conexión servidor", TargetFailure),
    ("metadata suficiente", MetadataSufficient),
    ("metadata insuficiente", MetadataInsufficient),
    ("fallo de metadata", MetadataFailure),
    ("observación física completa y count válido", PhysicalComplete),
    ("observación física parcial no publica counts", PhysicalPartial),
    ("history ausente", HistoryAbsent),
    ("history presente vacía", HistoryEmpty),
    ("history presente conserva duplicados y orden", HistoryRows),
    ("history ilegible", HistoryUnreadable),
    ("history con estructura inválida", HistoryInvalid),
    ("history con error técnico", HistoryError),
    ("prerrequisitos dejan etapas no intentadas", Prerequisites),
    ("excepción sensible no se expone", SensitiveException),
    ("transporte declara cero retries, TLS estricto y sólo lectura", StaticTransportGuards),
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

static RecordingTransport SuccessfulTransport() => new();

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
    False(projection.IsRepresentable);
    Contains("CONNECTION_CANCELLED_UNREPRESENTABLE", projection.Gaps);
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
    False(new SqlDiscoveryOrchestratorV2(transport).ProjectSources(result).IsRepresentable);
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
    False(projection.IsRepresentable);
    Contains("DATABASE_LOOKUP_TIMEOUT_UNREPRESENTABLE", projection.Gaps);
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
    False(projection.IsRepresentable);
    Contains("TARGET_CONNECTION_STATE_UNREPRESENTABLE", projection.Gaps);
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
    Equal(HistoryStatus.Present, result.History.Status);
    True(transport.Calls.Contains("history"));
}

static Task StaticTransportGuards()
{
    var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "SqlDiscovery", "SqlClientDiscoveryTransportV2.cs"));
    foreach (var forbidden in new[] { "INSERT ", "UPDATE ", "DELETE ", "MERGE ", "EXEC ", "CREATE ", "ALTER ", "DROP ", "TRUNCATE " })
        False(source.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    True(source.Contains("@databaseName", StringComparison.Ordinal));
    True(source.Contains("connectionTimeoutSeconds = 15", StringComparison.Ordinal));
    True(source.Contains("commandTimeoutSeconds = 30", StringComparison.Ordinal));
    True(source.Contains("ConnectRetryCount = 0", StringComparison.Ordinal));
    True(source.Contains("SqlConnectionEncryptOption.Strict", StringComparison.Ordinal));
    True(source.Contains("TrustServerCertificate = false", StringComparison.Ordinal));
    True(source.Contains("ORDER BY MigrationId ASC", StringComparison.Ordinal));
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
    False(projection.IsRepresentable);
    Contains("TARGET_CONNECTION_STATE_UNREPRESENTABLE", projection.Gaps);
    var invocations = 0;
    False(InvokePipelineWhenRepresentable(projection, () => invocations++));
    Equal(0, invocations);
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
    public Func<CancellationToken, Task> ConnectTarget { get; set; } = _ => Task.CompletedTask;
    public Func<CancellationToken, Task<MetadataResult>> Metadata { get; set; } = _ => Task.FromResult(new MetadataResult(MetadataStatus.Sufficient));
    public Func<CancellationToken, Task<PhysicalResult>> Physical { get; set; } = _ => Task.FromResult(new PhysicalResult(PhysicalStatus.Complete, 5));
    public Func<CancellationToken, Task<HistoryResult>> History { get; set; } = _ => Task.FromResult(new HistoryResult(HistoryStatus.Present, ["20260101000000_A"]));

    public Task ConnectServerAsync(SqlDiscoveryTarget target, CancellationToken token) => Invoke("server", token, ConnectServer);
    public Task<DatabaseLookupResult> LookupDatabaseAsync(SqlDiscoveryTarget target, CancellationToken token) => Invoke("lookup", token, Lookup);
    public Task ConnectTargetAsync(SqlDiscoveryTarget target, CancellationToken token) => Invoke("target", token, ConnectTarget);
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
