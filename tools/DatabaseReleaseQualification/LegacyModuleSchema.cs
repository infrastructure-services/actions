namespace DatabaseReleaseQualification;

internal sealed record LegacyModuleDefinitionV1(string Schema, string Name, string Type,
    string Definition, string AnsiNulls, string QuotedIdentifier, string SchemaBound,
    string DatabaseCollation, string Recompiled, string NullOnNullInput, string ExecuteAs,
    string NativeOrSigned, string InlineType = "false", string Inlineable = "false");

internal static class LegacyModuleSchema
{
    internal static void Add(SchemaSnapshot snapshot, LegacyModuleDefinitionV1 module)
    {
        if (string.IsNullOrWhiteSpace(module.Definition) || module.NativeOrSigned != "false"
            || module.Type.Trim() is not ("P" or "FN" or "IF" or "TF")
            || module.ExecuteAs is "" or "UNRESOLVED"
            || new[] { module.AnsiNulls,module.QuotedIdentifier,module.SchemaBound,module.DatabaseCollation,
                module.Recompiled,module.NullOnNullInput,module.InlineType,module.Inlineable }
                .Any(x => x is not ("true" or "false")))
            snapshot.UnsupportedSchemaFeatures.Add("legacy-module-definition-unverifiable");
        snapshot.Objects.Add(new SchemaObject
        {
            Kind = module.Type.Trim() == "P" ? "procedure" : "function",
            Schema = module.Schema,
            Name = module.Name,
            Properties = new(StringComparer.Ordinal)
            {
                // Lowercase ASCII keys also preserve ordering when the generic
                // Certified State JSON clone reconstructs a sorted dictionary.
                ["profile"] = "LEGACY_MODULES_V1",
                ["moduletype"] = module.Type.Trim(),
                ["definitionsha256"] = Hashing.Sha256(module.Definition),
                ["ansinulls"] = module.AnsiNulls,
                ["quotedidentifier"] = module.QuotedIdentifier,
                ["schemabound"] = module.SchemaBound,
                ["databasecollation"] = module.DatabaseCollation,
                ["recompiled"] = module.Recompiled,
                ["nullonnullinput"] = module.NullOnNullInput,
                ["executeas"] = module.ExecuteAs,
                ["nativeorsigned"] = module.NativeOrSigned,
                ["inlinetype"] = module.InlineType,
                ["inlineable"] = module.Inlineable
            }
        });
    }
}
