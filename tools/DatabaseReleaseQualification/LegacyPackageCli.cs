using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public static class LegacyPackageCli
{
    public static async Task<int> RunAsync(string[] args,
        LegacyPackageQualificationAdapter? trustedAdapter = null,
        CancellationToken token = default)
    {
        try
        {
            if (args.Length != 4 || args[0] != "--request" || args[2] != "--output")
                return 65;
            var path = Path.GetFullPath(args[1]);
            if (!File.Exists(path)) return 65;
            var request = ParseRequest(await File.ReadAllBytesAsync(path, token));
            if (trustedAdapter is null)
            {
                try { trustedAdapter = LegacyProductionCompositionRoot.Create(); }
                catch (LegacyContractException exception)
                {
                    Console.Error.WriteLine("LEGACY_QUALIFICATION_BLOCKED:" + exception.Code);
                    return 66;
                }
            }
            var outcome = await trustedAdapter.EvaluateAsync(request, token);
            var output = Path.GetFullPath(args[3]);
            Directory.CreateDirectory(output);
            if (outcome.Rejected is not null)
            {
                WriteNew(Path.Combine(output, "legacy-blocked.json"),
                    JsonSerializer.Serialize(new {
                        rejected = outcome.Rejected,
                        trustedRuntime = outcome.TrustedRuntime is null ? (object?)null
                            : JsonDocument.Parse(LegacyRuntimeEvidenceHash.Serialize(
                                outcome.TrustedRuntime)).RootElement
                    }, JsonDefaults.Compact));
                return outcome.Rejected.ReasonCodes.Contains("TECHNICAL_ERROR") ? 70 : 66;
            }
            if (outcome.Readiness is not { Status: "READY_FOR_TEST_REHEARSAL" }
                || outcome.Package is null || outcome.StaticSafety is null
                || outcome.CertifiedBaseline is null || outcome.ObservedSnapshot is null)
            {
                WriteNew(Path.Combine(output, "legacy-blocked.json"),
                    JsonSerializer.Serialize(new {
                        artifact = (object?)outcome.ArtifactBlocked ?? outcome.Package?.Evidence,
                        trustedRuntime = outcome.TrustedRuntime is null ? (object?)null
                            : JsonDocument.Parse(LegacyRuntimeEvidenceHash.Serialize(
                                outcome.TrustedRuntime)).RootElement,
                        readiness = outcome.Readiness is null ? (object?)null
                            : JsonDocument.Parse(LegacyReadinessHash.Serialize(outcome.Readiness)).RootElement
                    }, JsonDefaults.Compact));
                return 66;
            }
            var result = new ReleasePackageWriter().WriteLegacy(output,
                outcome.Readiness.EvidenceHash[..16], outcome);
            WriteNew(Path.Combine(result.AttestationDirectory, "legacy-readiness.json"),
                LegacyReadinessHash.Serialize(outcome.Readiness));
            WriteNew(Path.Combine(result.AttestationDirectory, "legacy-static-safety.json"),
                LegacyStaticSafety.Serialize(outcome.StaticSafety));
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (LegacyContractException) { return 65; }
        catch (JsonException) { return 65; }
        catch (IOException) { return 70; }
        catch { return 70; }
    }

    private static LegacyQualificationRequestV1 ParseRequest(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 16384) throw new LegacyContractException("TECHNICAL_ERROR");
        var content = bytes.AsSpan();
        if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
        try { _ = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { throw new LegacyContractException("TECHNICAL_ERROR"); }
        using var doc = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        var root = Properties(doc.RootElement, ["contractVersion", "artifactSelection", "targetId"]);
        if (root["contractVersion"].ValueKind != JsonValueKind.Number
            || !root["contractVersion"].TryGetInt32(out var version) || version != 1)
            throw new LegacyContractException("TECHNICAL_ERROR");
        var selector = Properties(root["artifactSelection"],
            ["contractVersion", "manifestPath", "expectedCommit", "expectedRepository"]);
        if (selector["contractVersion"].ValueKind != JsonValueKind.Number
            || !selector["contractVersion"].TryGetInt32(out var selectorVersion) || selectorVersion != 1)
            throw new LegacyContractException("TECHNICAL_ERROR");
        var repo = Properties(selector["expectedRepository"], ["host", "repositoryId", "fullName"]);
        var repository = new LegacyRepositoryV1(String(repo["host"]),
            String(repo["repositoryId"]), String(repo["fullName"]));
        repository.Validate();
        var manifestPath = String(selector["manifestPath"]);
        LegacyPortablePath.Validate(manifestPath);
        var selection = new LegacyArtifactSelectionV1(1, manifestPath,
            String(selector["expectedCommit"]), repository);
        var targetId = String(root["targetId"]);
        if (!Regex.IsMatch(targetId, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z"))
            throw new LegacyContractException("TECHNICAL_ERROR");
        return new(1, selection, targetId, null);
    }

    private static string String(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new LegacyContractException("TECHNICAL_ERROR");
        return value.GetString() ?? throw new LegacyContractException("TECHNICAL_ERROR");
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement element,
        string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new LegacyContractException("TECHNICAL_ERROR");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value) || !allowed.Contains(property.Name))
                throw new LegacyContractException("TECHNICAL_ERROR");
        }
        if (result.Count != allowed.Length || allowed.Any(x => !result.ContainsKey(x)))
            throw new LegacyContractException("TECHNICAL_ERROR");
        return result;
    }

    private static void WriteNew(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(value + "\n"));
    }
}
