using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DatabaseReleaseQualification;

public static class Hashing
{
    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
    public static string Sha256(string content) => Sha256(Encoding.UTF8.GetBytes(content));
}

public static class SchemaCanonicalizer
{
    public static CanonicalSchema Canonicalize(SchemaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var objects = snapshot.Objects
            .Select(NormalizeObject)
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Parent, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => SerializeObject(item), StringComparer.Ordinal)
            .ToArray();

        var document = new CanonicalSchemaDocument { FormatVersion = snapshot.FormatVersion, Objects = objects };
        var json = JsonSerializer.Serialize(document, JsonDefaults.Compact);
        return new CanonicalSchema(document, json, Hashing.Sha256(json));
    }

    private static SchemaObject NormalizeObject(SchemaObject item) => new()
    {
        Kind = item.Kind.Trim().ToLowerInvariant(),
        Schema = item.Schema.Trim(),
        Parent = item.Parent.Trim(),
        Name = item.Name.Trim(),
        Properties = NormalizeProperties(item.Properties)
    };

    private static SortedDictionary<string, string> NormalizeProperties(
        IReadOnlyDictionary<string, string> properties)
    {
        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in properties)
        {
            var key = pair.Key.Trim();
            var value = pair.Value?.Trim() ?? "";
            if (IsSensitivePropertyKey(key))
                throw new InvalidOperationException("SCHEMA_PROPERTY_REJECTED_SENSITIVE_KEY");

            if (IsDefinitionPropertyKey(key))
            {
                key = $"{key}Sha256";
                value = Hashing.Sha256(value);
            }

            if (!normalized.TryAdd(key, value))
                throw new InvalidOperationException("SCHEMA_PROPERTY_DUPLICATE_AFTER_NORMALIZATION");
        }
        return normalized;
    }

    private static bool IsDefinitionPropertyKey(string key) =>
        key.Equals("definition", StringComparison.OrdinalIgnoreCase)
        || key.Equals("computedDefinition", StringComparison.OrdinalIgnoreCase)
        || key.Equals("moduleDefinition", StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitivePropertyKey(string key) =>
        key.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase);

    internal static string SerializeObject(SchemaObject item) => JsonSerializer.Serialize(item, JsonDefaults.Compact);
}

public static class SchemaComparer
{
    public static SchemaDiff Compare(CanonicalSchema expected, CanonicalSchema actual)
    {
        var expectedByIdentity = ByIdentity(expected);
        var actualByIdentity = ByIdentity(actual);
        var diff = new SchemaDiff();
        diff.MissingObjects.AddRange(expectedByIdentity.Keys.Except(actualByIdentity.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        diff.ExtraObjects.AddRange(actualByIdentity.Keys.Except(expectedByIdentity.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        diff.ChangedObjects.AddRange(expectedByIdentity.Keys
            .Intersect(actualByIdentity.Keys, StringComparer.Ordinal)
            .Where(key => !string.Equals(expectedByIdentity[key], actualByIdentity[key], StringComparison.Ordinal))
            .Order(StringComparer.Ordinal));
        return diff;
    }

    private static Dictionary<string, string> ByIdentity(CanonicalSchema schema) => schema.Document.Objects
        .GroupBy(item => item.Identity, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => string.Join("\n", group.Select(SchemaCanonicalizer.SerializeObject).Order(StringComparer.Ordinal)),
            StringComparer.Ordinal);
}
