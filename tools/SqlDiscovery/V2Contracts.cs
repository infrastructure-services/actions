using System.Collections.ObjectModel;

namespace SqlDiscovery.V2;

public enum ConnectionStatus { Succeeded, AuthenticationFailed, TransportFailed, TimedOut, Cancelled, NotAttempted }
public enum DatabaseLookupStatus { Found, NotFoundConfirmed, VisibilityInsufficient, TechnicalError, TimedOut, Cancelled, NotAttempted }
public enum MetadataStatus { Sufficient, Insufficient, TechnicalError, TimedOut, Cancelled, NotAttempted }
public enum PhysicalStatus { Complete, Partial, TechnicalError, TimedOut, Cancelled, NotAttempted }
public enum HistoryStatus { Absent, Empty, Present, Unreadable, InvalidStructure, TechnicalError, TimedOut, Cancelled, NotAttempted }

public sealed record SqlDiscoveryTarget(string ServerConnectionString, string DatabaseName);

public sealed record StageDiagnostic(string Stage, string Code);

public sealed record ConnectionResult(ConnectionStatus Status, StageDiagnostic? Diagnostic = null);
public sealed record DatabaseLookupResult(DatabaseLookupStatus Status, StageDiagnostic? Diagnostic = null);
public sealed record MetadataResult(MetadataStatus Status, StageDiagnostic? Diagnostic = null);
public sealed record PhysicalResult(PhysicalStatus Status, long? BusinessObjectCount = null, StageDiagnostic? Diagnostic = null);

public sealed record HistoryResult
{
    public HistoryStatus Status { get; }
    public IReadOnlyList<string> MigrationIds { get; }
    public StageDiagnostic? Diagnostic { get; }

    public HistoryResult(HistoryStatus status, IEnumerable<string>? migrationIds = null, StageDiagnostic? diagnostic = null)
    {
        Status = status;
        MigrationIds = new ReadOnlyCollection<string>((migrationIds ?? []).ToArray());
        Diagnostic = diagnostic;
    }
}

public sealed record SqlDiscoveryResult(
    ConnectionResult ServerConnection,
    DatabaseLookupResult DatabaseLookup,
    ConnectionResult TargetConnection,
    MetadataResult Metadata,
    PhysicalResult Physical,
    HistoryResult History)
{
    public IReadOnlyList<StageDiagnostic> Diagnostics => new ReadOnlyCollection<StageDiagnostic>(
        new StageDiagnostic?[]
        {
            ServerConnection.Diagnostic, DatabaseLookup.Diagnostic, TargetConnection.Diagnostic,
            Metadata.Diagnostic, Physical.Diagnostic, History.Diagnostic
        }.Where(value => value is not null).Cast<StageDiagnostic>().ToArray());
}

public interface ISqlDiscoveryTransport
{
    Task ConnectServerAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken);
    Task<DatabaseLookupResult> LookupDatabaseAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken);
    Task ConnectTargetAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken);
    Task<MetadataResult> InspectMetadataAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken);
    Task<PhysicalResult> ObservePhysicalAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken);
    Task<HistoryResult> ObserveHistoryAsync(SqlDiscoveryTarget target, CancellationToken cancellationToken);
}

public sealed record SourceProjection(bool IsRepresentable, IReadOnlyDictionary<string, object?>? Sources, IReadOnlyList<string> Gaps)
{
    public static SourceProjection Blocked(params string[] gaps) => new(false, null, Array.AsReadOnly(gaps.ToArray()));
}
