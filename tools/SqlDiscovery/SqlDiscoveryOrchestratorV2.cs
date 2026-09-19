using System.Collections.ObjectModel;

namespace SqlDiscovery.V2;

public sealed class SqlDiscoveryOrchestratorV2(ISqlDiscoveryTransport transport)
{
    public async Task<SqlDiscoveryResult> DiscoverAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (cancellationToken.IsCancellationRequested)
        {
            return Result(new ConnectionResult(ConnectionStatus.Cancelled, Diagnostic("SERVER_CONNECTION", "CANCELLED")));
        }

        var server = await RunConnectionAsync("SERVER_CONNECTION", () => transport.ConnectServerAsync(target, cancellationToken), cancellationToken);
        if (server.Status != ConnectionStatus.Succeeded)
        {
            return Result(server);
        }

        var lookup = await RunLookupAsync(() => transport.LookupDatabaseAsync(target, cancellationToken), cancellationToken);
        if (lookup.Status != DatabaseLookupStatus.Found)
        {
            return Result(server, lookup);
        }

        var targetConnection = await RunConnectionAsync("TARGET_CONNECTION", () => transport.ConnectTargetAsync(target, cancellationToken), cancellationToken);
        if (targetConnection.Status != ConnectionStatus.Succeeded)
        {
            return Result(server, lookup, targetConnection);
        }

        var metadata = await RunMetadataAsync(() => transport.InspectMetadataAsync(target, cancellationToken), cancellationToken);
        if (metadata.Status != MetadataStatus.Sufficient)
        {
            return Result(server, lookup, targetConnection, metadata);
        }

        var physical = await RunPhysicalAsync(() => transport.ObservePhysicalAsync(target, cancellationToken), cancellationToken);
        var history = await RunHistoryAsync(() => transport.ObserveHistoryAsync(target, cancellationToken), cancellationToken);
        return Result(server, lookup, targetConnection, metadata, physical, history);
    }

    public SourceProjection ProjectSources(SqlDiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var gaps = new List<string>();

        string connection = result.ServerConnection.Status switch
        {
            ConnectionStatus.Succeeded => "SUCCEEDED",
            ConnectionStatus.AuthenticationFailed or ConnectionStatus.TransportFailed => "FAILED",
            ConnectionStatus.TimedOut => "TIMEOUT",
            ConnectionStatus.NotAttempted => "NOT_ATTEMPTED",
            ConnectionStatus.Cancelled => Gap("CONNECTION_CANCELLED_UNREPRESENTABLE"),
            _ => Gap("CONNECTION_STATE_UNREPRESENTABLE")
        };

        if (result.TargetConnection.Status is not (ConnectionStatus.Succeeded or ConnectionStatus.NotAttempted))
        {
            gaps.Add("TARGET_CONNECTION_STATE_UNREPRESENTABLE");
        }

        string lookup = result.DatabaseLookup.Status switch
        {
            DatabaseLookupStatus.Found => "FOUND",
            DatabaseLookupStatus.NotFoundConfirmed => "NOT_FOUND",
            DatabaseLookupStatus.VisibilityInsufficient => "UNKNOWN",
            DatabaseLookupStatus.TechnicalError => "ERROR",
            DatabaseLookupStatus.NotAttempted => "NOT_ATTEMPTED",
            DatabaseLookupStatus.TimedOut => Gap("DATABASE_LOOKUP_TIMEOUT_UNREPRESENTABLE"),
            DatabaseLookupStatus.Cancelled => Gap("DATABASE_LOOKUP_CANCELLED_UNREPRESENTABLE"),
            _ => Gap("DATABASE_LOOKUP_STATE_UNREPRESENTABLE")
        };

        string metadata = result.Metadata.Status switch
        {
            MetadataStatus.Sufficient => "SUFFICIENT",
            MetadataStatus.Insufficient => "INSUFFICIENT",
            MetadataStatus.TechnicalError => "ERROR",
            MetadataStatus.NotAttempted => "NOT_ATTEMPTED",
            MetadataStatus.TimedOut => Gap("METADATA_TIMEOUT_UNREPRESENTABLE"),
            MetadataStatus.Cancelled => Gap("METADATA_CANCELLED_UNREPRESENTABLE"),
            _ => Gap("METADATA_STATE_UNREPRESENTABLE")
        };

        var physical = new Dictionary<string, object?> { ["status"] = result.Physical.Status switch
        {
            PhysicalStatus.Complete => "OBSERVED",
            PhysicalStatus.TechnicalError => "ERROR",
            PhysicalStatus.NotAttempted => "NOT_ATTEMPTED",
            PhysicalStatus.Partial => Gap("PHYSICAL_PARTIAL_UNREPRESENTABLE"),
            PhysicalStatus.TimedOut => Gap("PHYSICAL_TIMEOUT_UNREPRESENTABLE"),
            PhysicalStatus.Cancelled => Gap("PHYSICAL_CANCELLED_UNREPRESENTABLE"),
            _ => Gap("PHYSICAL_STATE_UNREPRESENTABLE")
        }};
        if (result.Physical.Status == PhysicalStatus.Complete)
        {
            if (result.Physical.BusinessObjectCount is null or < 0)
            {
                gaps.Add("PHYSICAL_COMPLETE_COUNT_REQUIRED");
            }
            else
            {
                physical["businessObjectCount"] = result.Physical.BusinessObjectCount.Value;
            }
        }

        var history = new Dictionary<string, object?> { ["status"] = result.History.Status switch
        {
            HistoryStatus.Absent => "ABSENT",
            HistoryStatus.Empty or HistoryStatus.Present => "PRESENT",
            HistoryStatus.Unreadable => "UNREADABLE",
            HistoryStatus.InvalidStructure => "INVALID_STRUCTURE",
            HistoryStatus.TechnicalError => "ERROR",
            HistoryStatus.NotAttempted => "NOT_ATTEMPTED",
            HistoryStatus.TimedOut => Gap("HISTORY_TIMEOUT_UNREPRESENTABLE"),
            HistoryStatus.Cancelled => Gap("HISTORY_CANCELLED_UNREPRESENTABLE"),
            _ => Gap("HISTORY_STATE_UNREPRESENTABLE")
        }};
        if (result.History.Status is HistoryStatus.Empty or HistoryStatus.Present)
        {
            history["migrationCount"] = result.History.MigrationIds.Count;
            history["migrationIds"] = Array.AsReadOnly(result.History.MigrationIds.ToArray());
        }

        if (gaps.Count > 0)
        {
            return SourceProjection.Blocked(gaps.Distinct(StringComparer.Ordinal).ToArray());
        }

        var sources = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>
        {
            ["connectionSource"] = ReadOnlySection(new Dictionary<string, object?> { ["status"] = connection }),
            ["databaseLookupSource"] = ReadOnlySection(new Dictionary<string, object?> { ["status"] = lookup }),
            ["metadataSource"] = ReadOnlySection(new Dictionary<string, object?> { ["status"] = metadata }),
            ["physicalSource"] = ReadOnlySection(physical),
            ["historySource"] = ReadOnlySection(history)
        });
        return new SourceProjection(true, sources, Array.Empty<string>());

        string Gap(string code)
        {
            gaps.Add(code);
            return "UNREPRESENTABLE";
        }

        static IReadOnlyDictionary<string, object?> ReadOnlySection(Dictionary<string, object?> section) =>
            new ReadOnlyDictionary<string, object?>(section);
    }

    private static async Task<ConnectionResult> RunConnectionAsync(string stage, Func<Task> operation, CancellationToken token)
    {
        try
        {
            await operation();
            return new(ConnectionStatus.Succeeded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(ConnectionStatus.Cancelled, Diagnostic(stage, "CANCELLED"));
        }
        catch (TimeoutException)
        {
            return new(ConnectionStatus.TimedOut, Diagnostic(stage, "TIMEOUT"));
        }
        catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number == -2)
        {
            return new(ConnectionStatus.TimedOut, Diagnostic(stage, "TIMEOUT"));
        }
        catch (SqlDiscoveryAuthenticationException)
        {
            return new(ConnectionStatus.AuthenticationFailed, Diagnostic(stage, "AUTHENTICATION_FAILED"));
        }
        catch
        {
            return new(ConnectionStatus.TransportFailed, Diagnostic(stage, "TRANSPORT_FAILED"));
        }
    }

    private static async Task<DatabaseLookupResult> RunLookupAsync(Func<Task<DatabaseLookupResult>> operation, CancellationToken token)
    {
        try { return await operation(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(DatabaseLookupStatus.Cancelled, Diagnostic("DATABASE_LOOKUP", "CANCELLED")); }
        catch (TimeoutException) { return new(DatabaseLookupStatus.TimedOut, Diagnostic("DATABASE_LOOKUP", "TIMEOUT")); }
        catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number == -2) { return new(DatabaseLookupStatus.TimedOut, Diagnostic("DATABASE_LOOKUP", "TIMEOUT")); }
        catch { return new(DatabaseLookupStatus.TechnicalError, Diagnostic("DATABASE_LOOKUP", "TECHNICAL_ERROR")); }
    }

    private static async Task<MetadataResult> RunMetadataAsync(Func<Task<MetadataResult>> operation, CancellationToken token)
    {
        try { return await operation(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(MetadataStatus.Cancelled, Diagnostic("METADATA", "CANCELLED")); }
        catch (TimeoutException) { return new(MetadataStatus.TimedOut, Diagnostic("METADATA", "TIMEOUT")); }
        catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number == -2) { return new(MetadataStatus.TimedOut, Diagnostic("METADATA", "TIMEOUT")); }
        catch { return new(MetadataStatus.TechnicalError, Diagnostic("METADATA", "TECHNICAL_ERROR")); }
    }

    private static async Task<PhysicalResult> RunPhysicalAsync(Func<Task<PhysicalResult>> operation, CancellationToken token)
    {
        try { return await operation(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(PhysicalStatus.Cancelled, Diagnostic: Diagnostic("PHYSICAL", "CANCELLED")); }
        catch (TimeoutException) { return new(PhysicalStatus.TimedOut, Diagnostic: Diagnostic("PHYSICAL", "TIMEOUT")); }
        catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number == -2) { return new(PhysicalStatus.TimedOut, Diagnostic: Diagnostic("PHYSICAL", "TIMEOUT")); }
        catch { return new(PhysicalStatus.TechnicalError, Diagnostic: Diagnostic("PHYSICAL", "TECHNICAL_ERROR")); }
    }

    private static async Task<HistoryResult> RunHistoryAsync(Func<Task<HistoryResult>> operation, CancellationToken token)
    {
        try { return await operation(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(HistoryStatus.Cancelled, diagnostic: Diagnostic("HISTORY", "CANCELLED")); }
        catch (TimeoutException) { return new(HistoryStatus.TimedOut, diagnostic: Diagnostic("HISTORY", "TIMEOUT")); }
        catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number == -2) { return new(HistoryStatus.TimedOut, diagnostic: Diagnostic("HISTORY", "TIMEOUT")); }
        catch { return new(HistoryStatus.TechnicalError, diagnostic: Diagnostic("HISTORY", "TECHNICAL_ERROR")); }
    }

    private static StageDiagnostic Diagnostic(string stage, string code) => new(stage, code);

    private static SqlDiscoveryResult Result(
        ConnectionResult? server = null,
        DatabaseLookupResult? lookup = null,
        ConnectionResult? target = null,
        MetadataResult? metadata = null,
        PhysicalResult? physical = null,
        HistoryResult? history = null) => new(
            server ?? new(ConnectionStatus.NotAttempted),
            lookup ?? new(DatabaseLookupStatus.NotAttempted),
            target ?? new(ConnectionStatus.NotAttempted),
            metadata ?? new(MetadataStatus.NotAttempted),
            physical ?? new(PhysicalStatus.NotAttempted),
            history ?? new(HistoryStatus.NotAttempted));
}

public sealed class SqlDiscoveryAuthenticationException : Exception
{
    public SqlDiscoveryAuthenticationException() : base("Authentication failed.") { }
}
