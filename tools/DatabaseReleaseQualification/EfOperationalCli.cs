using System.Runtime.InteropServices;
using System.Text.Json;

namespace DatabaseReleaseQualification;

public static class EfOperationalCli
{
    public static async Task<int> RunAsync(string[] args, IEfOperationalSessionFactoryV1? host = null,
        CancellationToken cancellationToken = default, TimeProvider? clock = null)
    {
        string? output = null;
        var operation = "UNKNOWN";
        var seconds = 120;
        EfOperationalOutcomeV1 Failure(string status, string reason)
            => new(new(1, operation, status, [reason], null, null, []), null);
        EfOperationalOutcomeV1 outcome;
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; interrupted.Cancel(); };
        Console.CancelKeyPress += handler;
        using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            signal => { signal.Cancel = true; interrupted.Cancel(); });
        try
        {
            EfHostContract.Require(args.Length is 4 or 6 && args[0] == "--request" && args[2] == "--output"
                && (args.Length == 4 || args[4] == "--timeout-seconds" && int.TryParse(args[5], out seconds) && seconds is >= 1 and <= 900), "ARGUMENTS_INVALID");
            output = Path.GetFullPath(args[3]);
            EnsureOutput(output);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(interrupted.Token, deadline.Token);
            try
            {
                var bytes = await ReadBoundedAsync(args[1], 65536, linked.Token);
                var request = EfHostContract.Parse<EfOperationalRequestV1>(bytes);
                EfHostContract.Validate(request); operation = request.Operation;
                outcome = await EfOperationalHostV1.RunAsync(request, host ?? new EfGitHubSourceHostV1(),
                    (clock ?? TimeProvider.System).GetUtcNow(), linked.Token).WaitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            { outcome = interrupted.IsCancellationRequested ? Failure("CANCELLED", "HOST_CANCELLED") : Failure("TIMEOUT", "HOST_TIMEOUT"); }
            catch (JsonException) { outcome = Failure("INVALID", "REQUEST_JSON_INVALID"); }
            catch (EfHostException e) { outcome = Failure("INVALID", e.Message); }
            catch { outcome = Failure("ERROR", "HOST_TECHNICAL_ERROR"); }
        }
        catch (EfHostException e) { outcome = Failure("INVALID", e.Message); output = null; }
        catch { outcome = Failure("ERROR", "HOST_IO_ERROR"); output = null; }
        finally { Console.CancelKeyPress -= handler; }
        try
        {
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                if (outcome.Review is not null) WriteNew(Path.Combine(output, "ef-plan-review.json"), outcome.Review);
                // Result is the final completion marker. An interrupted/partial
                // publication cannot leave a successful result for another plan.
                WriteNew(Path.Combine(output, "ef-result.json"), outcome.Result);
            }
        }
        catch { outcome = Failure("ERROR", "ARTIFACT_PUBLICATION_FAILED"); }
        Console.WriteLine(JsonSerializer.Serialize(outcome.Result, JsonDefaults.Compact));
        return outcome.Result.Status switch {
            "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED" or "NO_CHANGES" or "PRE_MATCH_NOT_AUTHORIZED" or "POST_MATCH_NOT_CERTIFIED" => 0,
            "INVALID" => 65, "BLOCKED" => 66, "TIMEOUT" => 124, "CANCELLED" => 130, _ => 70
        };
    }
    private static void EnsureOutput(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            EfHostContract.Require(!current.Exists || !current.Attributes.HasFlag(FileAttributes.ReparsePoint), "OUTPUT_LINK_NOT_ALLOWED");
        EfHostContract.Require(!File.Exists(path) && (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any()), "OUTPUT_NOT_EMPTY");
    }
    private static void WriteNew<T>(string path, T value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, value, JsonDefaults.Compact); file.Flush(true);
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        EfHostContract.Require(file.Length is > 0 && file.Length <= maximum, "INPUT_SIZE_INVALID");
        var bytes = new byte[maximum + 1];
        var read = await file.ReadAtLeastAsync(bytes, maximum + 1, throwOnEndOfStream: false, token);
        EfHostContract.Require(read <= maximum, "INPUT_SIZE_INVALID");
        return bytes[..read];
    }
}
