using System.Text.Json;

namespace SqlDiscovery.V2;

public static class SqlDiscoveryPublicCli
{
    public static async Task<int> RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("SQL_SERVER_CONNECTION");
        var database = Environment.GetEnvironmentVariable("SQL_DATABASE_NAME");
        var environment = Environment.GetEnvironmentVariable("ENVIRONMENT_NAME");
        var tlsModeInput = Environment.GetEnvironmentVariable("SQL_TLS_MODE") ?? "STRICT";
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(environment)
            || tlsModeInput is not ("STRICT" or "TEST_UNTRUSTED_CERTIFICATE"))
        {
            Console.Error.WriteLine("SQL_DISCOVERY_INPUT_REQUIRED");
            return 64;
        }

        SqlClientDiscoveryTransportV2? transport = null;
        SqlDiscoveryResult? result = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            transport = new SqlClientDiscoveryTransportV2(
                environmentName: environment,
                tlsMode: SqlTlsPolicy.Parse(tlsModeInput));
            var orchestrator = new SqlDiscoveryOrchestratorV2(transport);
            result = await orchestrator.DiscoverAsync(new SqlDiscoveryTarget(connection, database), timeout.Token);
            var projection = orchestrator.ProjectSources(result);
            if (!projection.IsRepresentable || projection.Sources is null)
            {
                Console.WriteLine(JsonSerializer.Serialize(BuildFailureEnvelope(
                    75, "SQL_DISCOVERY_PROJECTION_BLOCKED", transport.TlsEvidence, result, projection.Gaps)));
                Console.Error.WriteLine("SQL_DISCOVERY_PROJECTION_BLOCKED");
                return 75;
            }

            var tlsEvidence = new Dictionary<string, object?>
            {
                ["tlsRequestedMode"] = transport.TlsEvidence.TlsRequestedMode,
                ["tlsInitialMode"] = transport.TlsEvidence.TlsInitialMode,
                ["tlsInitialResult"] = transport.TlsEvidence.TlsInitialResult,
                ["tlsFallbackAllowed"] = transport.TlsEvidence.TlsFallbackAllowed,
                ["tlsFallbackAttempted"] = transport.TlsEvidence.TlsFallbackAttempted,
                ["tlsEffectiveMode"] = transport.TlsEvidence.TlsEffectiveMode,
                ["tlsCertificateValidated"] = transport.TlsEvidence.TlsCertificateValidated,
                ["transportEncrypted"] = transport.TlsEvidence.TransportEncrypted,
                ["tlsPolicySource"] = transport.TlsEvidence.TlsPolicySource
            };
            if (transport.TlsEvidence.DiagnosticFingerprint is not null)
                tlsEvidence["diagnosticFingerprint"] = transport.TlsEvidence.DiagnosticFingerprint;

            var publicEvidence = new Dictionary<string, object?>(projection.Sources)
            {
                ["tls"] = tlsEvidence,
                ["serverConnectionStatus"] = result.ServerConnection.Status switch
                {
                    ConnectionStatus.Succeeded => "SUCCEEDED",
                    ConnectionStatus.AuthenticationFailed => "AUTHENTICATION_FAILED",
                    ConnectionStatus.TransportFailed => "TRANSPORT_FAILED",
                    ConnectionStatus.TimedOut => "TIMEOUT",
                    ConnectionStatus.Cancelled => "CANCELLED",
                    ConnectionStatus.NotAttempted => "NOT_ATTEMPTED",
                    _ => throw new InvalidOperationException("SERVER_CONNECTION_STATE_INVALID")
                }
            };
            if (result.TargetConnection.Status == ConnectionStatus.Succeeded)
            {
                publicEvidence["observedDatabaseIdentity"] = result.ObservedIdentity.Identity
                    ?? throw new InvalidOperationException("TARGET_IDENTITY_REQUIRED");
            }
            Console.WriteLine(JsonSerializer.Serialize(publicEvidence));
            return 0;
        }
        catch (Exception)
        {
            if (transport is not null)
            {
                var tlsEvidence = transport.TlsEvidence;
                Console.WriteLine(JsonSerializer.Serialize(BuildFailureEnvelope(
                    70, "SQL_DISCOVERY_INTERNAL_ERROR", tlsEvidence, result)));
            }
            Console.Error.WriteLine("SQL_DISCOVERY_INTERNAL_ERROR");
            return 70;
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQL_SERVER_CONNECTION", null);
        }
    }

    public static IReadOnlyDictionary<string, object?> BuildFailureEnvelope(
        int exitCode,
        string reasonCode,
        TlsDiscoveryEvidence tlsEvidence,
        SqlDiscoveryResult? result = null,
        IReadOnlyList<string>? projectionGaps = null)
    {
        if ((exitCode, reasonCode) is not ((70, "SQL_DISCOVERY_INTERNAL_ERROR") or (75, "SQL_DISCOVERY_PROJECTION_BLOCKED")))
            throw new ArgumentException("FAILURE_TERMINAL_INVALID");
        ArgumentNullException.ThrowIfNull(tlsEvidence);
        var envelope = new Dictionary<string, object?>
        {
            ["failureContractVersion"] = 1,
            ["executionExitCode"] = exitCode,
            ["executionReasonCode"] = reasonCode,
            ["tls"] = TlsSection(tlsEvidence),
            ["projectionRepresentable"] = exitCode == 75 ? false : null,
            ["projectionGaps"] = Array.AsReadOnly((projectionGaps ?? []).Distinct(StringComparer.Ordinal).ToArray())
        };
        if (result is not null)
        {
            envelope["serverConnectionStatus"] = ConnectionName(result.ServerConnection.Status);
            envelope["databaseLookupStatus"] = LookupName(result.DatabaseLookup.Status);
            envelope["targetConnectionStatus"] = ConnectionName(result.TargetConnection.Status);
            envelope["metadataStatus"] = MetadataName(result.Metadata.Status);
            envelope["physicalStatus"] = PhysicalName(result.Physical.Status);
            envelope["historyStatus"] = HistoryName(result.History.Status);
        }
        return envelope;
    }

    private static IReadOnlyDictionary<string, object?> TlsSection(TlsDiscoveryEvidence evidence)
    {
        var section = new Dictionary<string, object?>
        {
            ["tlsRequestedMode"] = evidence.TlsRequestedMode,
            ["tlsInitialMode"] = evidence.TlsInitialMode,
            ["tlsInitialResult"] = evidence.TlsInitialResult,
            ["tlsFallbackAllowed"] = evidence.TlsFallbackAllowed,
            ["tlsFallbackAttempted"] = evidence.TlsFallbackAttempted,
            ["tlsEffectiveMode"] = evidence.TlsEffectiveMode,
            ["tlsCertificateValidated"] = evidence.TlsCertificateValidated,
            ["transportEncrypted"] = evidence.TransportEncrypted,
            ["tlsPolicySource"] = evidence.TlsPolicySource
        };
        if (evidence.DiagnosticFingerprint is not null) section["diagnosticFingerprint"] = evidence.DiagnosticFingerprint;
        return section;
    }

    private static string ConnectionName(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Succeeded => "SUCCEEDED", ConnectionStatus.AuthenticationFailed => "AUTHENTICATION_FAILED",
        ConnectionStatus.TransportFailed => "TRANSPORT_FAILED", ConnectionStatus.TimedOut => "TIMEOUT",
        ConnectionStatus.Cancelled => "CANCELLED", ConnectionStatus.NotAttempted => "NOT_ATTEMPTED",
        _ => throw new InvalidOperationException("CONNECTION_STATE_INVALID")
    };
    private static string LookupName(DatabaseLookupStatus status) => status switch
    {
        DatabaseLookupStatus.Found => "FOUND", DatabaseLookupStatus.NotFoundConfirmed => "NOT_FOUND",
        DatabaseLookupStatus.VisibilityInsufficient => "UNKNOWN", DatabaseLookupStatus.TechnicalError => "ERROR",
        DatabaseLookupStatus.TimedOut => "TIMEOUT", DatabaseLookupStatus.Cancelled => "CANCELLED",
        DatabaseLookupStatus.NotAttempted => "NOT_ATTEMPTED", _ => throw new InvalidOperationException("LOOKUP_STATE_INVALID")
    };
    private static string MetadataName(MetadataStatus status) => status switch
    {
        MetadataStatus.Sufficient => "SUFFICIENT", MetadataStatus.Insufficient => "INSUFFICIENT",
        MetadataStatus.TechnicalError => "ERROR", MetadataStatus.TimedOut => "TIMEOUT",
        MetadataStatus.Cancelled => "CANCELLED", MetadataStatus.NotAttempted => "NOT_ATTEMPTED",
        _ => throw new InvalidOperationException("METADATA_STATE_INVALID")
    };
    private static string PhysicalName(PhysicalStatus status) => status switch
    {
        PhysicalStatus.Complete => "OBSERVED", PhysicalStatus.Partial => "PARTIAL", PhysicalStatus.TechnicalError => "ERROR",
        PhysicalStatus.TimedOut => "TIMEOUT", PhysicalStatus.Cancelled => "CANCELLED",
        PhysicalStatus.NotAttempted => "NOT_ATTEMPTED", _ => throw new InvalidOperationException("PHYSICAL_STATE_INVALID")
    };
    private static string HistoryName(HistoryStatus status) => status switch
    {
        HistoryStatus.Absent => "ABSENT", HistoryStatus.Empty or HistoryStatus.Present => "PRESENT",
        HistoryStatus.Unreadable => "UNREADABLE", HistoryStatus.InvalidStructure => "INVALID_STRUCTURE",
        HistoryStatus.TechnicalError => "ERROR", HistoryStatus.TimedOut => "TIMEOUT",
        HistoryStatus.Cancelled => "CANCELLED", HistoryStatus.NotAttempted => "NOT_ATTEMPTED",
        _ => throw new InvalidOperationException("HISTORY_STATE_INVALID")
    };
}
