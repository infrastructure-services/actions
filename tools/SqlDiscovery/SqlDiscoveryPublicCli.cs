using System.Text.Json;

namespace SqlDiscovery.V2;

public static class SqlDiscoveryPublicCli
{
    public static async Task<int> RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("SQL_SERVER_CONNECTION");
        var database = Environment.GetEnvironmentVariable("SQL_DATABASE_NAME");
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(database))
        {
            Console.Error.WriteLine("SQL_DISCOVERY_INPUT_REQUIRED");
            return 64;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var orchestrator = new SqlDiscoveryOrchestratorV2(new SqlClientDiscoveryTransportV2());
            var result = await orchestrator.DiscoverAsync(new SqlDiscoveryTarget(connection, database), timeout.Token);
            var projection = orchestrator.ProjectSources(result);
            if (!projection.IsRepresentable || projection.Sources is null)
            {
                Console.Error.WriteLine("SQL_DISCOVERY_PROJECTION_BLOCKED");
                return 75;
            }

            var publicEvidence = new Dictionary<string, object?>(projection.Sources)
            {
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
