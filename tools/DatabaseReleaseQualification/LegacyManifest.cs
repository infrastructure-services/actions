using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed class LegacyContractException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record LegacyManifestV1(
    int ContractVersion, string ReleaseId, string TargetId, string ForwardPath,
    string RollbackPath, string ChangeOrigin, string? ChangeReference, string? ChangeReason);

public static class LegacyPortablePath
{
    private static readonly Regex Component = new(@"\A[A-Za-z0-9_.-]{1,128}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Device = new(@"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void Validate(string? path)
    {
        if (path is null || path.Length is < 1 or > 512 || path.StartsWith('/')
            || path.EndsWith('/') || path.Contains('%'))
            throw new LegacyContractException("ARTIFACT_PATH_INVALID");
        foreach (var segment in path.Split('/'))
            if (!Component.IsMatch(segment) || segment is "." or ".."
                || segment.EndsWith('.') || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || Device.IsMatch(segment))
                throw new LegacyContractException(segment is "." or ".."
                    ? "ARTIFACT_PATH_TRAVERSAL" : "ARTIFACT_PATH_INVALID");
    }
}

public static class LegacyManifest
{
    private static readonly Regex ReleasePattern = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex TargetPattern = new(@"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) {
        "contractVersion", "releaseId", "targetId", "forwardPath", "rollbackPath",
        "changeOrigin", "changeReference", "changeReason"
    };

    public static LegacyManifestV1 Parse(ReadOnlySpan<byte> originalBytes, string manifestPath, string expectedTargetId)
    {
        LegacyPortablePath.Validate(manifestPath);
        if (originalBytes.Length is < 1 or > 16384)
            throw new LegacyContractException("MANIFEST_MALFORMED");
        var bytes = originalBytes.ToArray();
        var content = bytes.AsSpan();
        if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
        try { _ = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { throw new LegacyContractException("MANIFEST_MALFORMED"); }
        JsonDocument document;
        try { document = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { throw new LegacyContractException("MANIFEST_MALFORMED"); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new LegacyContractException("MANIFEST_MALFORMED");
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                    throw new LegacyContractException("MANIFEST_DUPLICATE_PROPERTY");
                if (!Fields.Contains(property.Name))
                    throw new LegacyContractException("MANIFEST_UNKNOWN_PROPERTY");
            }
            if (!properties.TryGetValue("contractVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
                throw new LegacyContractException("MANIFEST_MALFORMED");
            if (number != 1) throw new LegacyContractException("MANIFEST_VERSION_UNSUPPORTED");
            var release = Required(properties, "releaseId");
            if (!ReleasePattern.IsMatch(release)) throw new LegacyContractException("RELEASE_ID_INVALID");
            var target = Required(properties, "targetId");
            if (!TargetPattern.IsMatch(target)) throw new LegacyContractException("MANIFEST_MALFORMED");
            if (!string.Equals(target, expectedTargetId, StringComparison.Ordinal))
                throw new LegacyContractException("TARGET_ID_MISMATCH");
            var forward = Required(properties, "forwardPath");
            var rollback = Required(properties, "rollbackPath");
            LegacyPortablePath.Validate(forward);
            LegacyPortablePath.Validate(rollback);
            if (new[] { manifestPath, forward, rollback }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
                throw new LegacyContractException("ARTIFACT_PATH_ALIAS");
            var origin = Required(properties, "changeOrigin");
            if (origin is not ("APPLICATION" or "DBA"))
                throw new LegacyContractException("MANIFEST_MALFORMED");
            var reference = Optional(properties, "changeReference", 256);
            var reason = Optional(properties, "changeReason", 1024);
            if (origin == "DBA" && (reference is null || reason is null))
                throw new LegacyContractException("MANIFEST_MALFORMED");
            return new(1, release, target, forward, rollback, origin, reference, reason);
        }
    }

    private static string Required(Dictionary<string, JsonElement> properties, string key)
    {
        if (!properties.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String)
            throw new LegacyContractException("MANIFEST_MALFORMED");
        var value = element.GetString();
        if (string.IsNullOrEmpty(value) || !ValidUnicode(value))
            throw new LegacyContractException("MANIFEST_MALFORMED");
        return value;
    }

    private static string? Optional(Dictionary<string, JsonElement> properties, string key, int maxLength)
    {
        if (!properties.ContainsKey(key)) return null;
        var value = Required(properties, key);
        if (value.Length > maxLength || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new LegacyContractException("MANIFEST_MALFORMED");
        return value;
    }

    private static bool ValidUnicode(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i == text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }
}
