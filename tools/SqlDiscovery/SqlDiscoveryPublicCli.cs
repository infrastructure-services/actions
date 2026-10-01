using System.Text.Json;

namespace SqlDiscovery.V2;

public static class SqlDiscoveryPublicCli
{
    public static async Task<int> RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("SQL_SERVER_CONNECTION");
        var database = Environment.GetEnvironmentVariable("SQL_DATABASE_NAME");
        var environment = Environment.GetEnvironmentVariable("ENVIRONMENT_NAME");
        var fallbackInput = Environment.GetEnvironmentVariable("ALLOW_TEST_UNTRUSTED_CERTIFICATE_FALLBACK") ?? "false";
        var allowFallback = fallbackInput == "true";
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(environment)
            || fallbackInput is not ("true" or "false"))
        {
            Console.Error.WriteLine("SQL_DISCOVERY_INPUT_REQUIRED");
            return 64;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var transport = new SqlClientDiscoveryTransportV2(
                environmentName: environment,
                allowTestUntrustedCertificateFallback: allowFallback);
            var orchestrator = new SqlDiscoveryOrchestratorV2(transport);
            var result = await orchestrator.DiscoverAsync(new SqlDiscoveryTarget(connection, database), timeout.Token);
            var projection = orchestrator.ProjectSources(result);
            if (!projection.IsRepresentable || projection.Sources is null)
            {
                Console.Error.WriteLine("SQL_DISCOVERY_PROJECTION_BLOCKED");
                return 75;
            }

            var tlsEvidence = new Dictionary<string, object?>
            {
                ["tlsInitialMode"] = transport.TlsEvidence.TlsInitialMode,
                ["tlsInitialResult"] = transport.TlsEvidence.TlsInitialResult,
                ["tlsFallbackAllowed"] = transport.TlsEvidence.TlsFallbackAllowed,
                ["tlsFallbackAttempted"] = transport.TlsEvidence.TlsFallbackAttempted,
                ["tlsEffectiveMode"] = transport.TlsEvidence.TlsEffectiveMode,
                ["tlsCertificateValidated"] = transport.TlsEvidence.TlsCertificateValidated,
                ["transportEncrypted"] = transport.TlsEvidence.TransportEncrypted
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
            Console.Error.WriteLine("SQL_DISCOVERY_INTERNAL_ERROR");
            return 70;
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQL_SERVER_CONNECTION", null);
        }
    }
}
