using System.Text.Json;
using System.Text.Json.Serialization;

namespace DatabaseReleaseQualification;

public enum RiskLevel { Low = 1, Medium = 2, High = 3 }
public enum FindingSeverity { Info = 1, Warning = 2, Blocking = 3 }
public enum AnalysisConfidence { Complete = 1, Partial = 2, Insufficient = 3 }
public enum SchemaCoverage { Complete = 1, Partial = 2, Insufficient = 3 }
public enum SchemaRollbackValidity { Valid, Invalid, NotTested }
public enum DataRollbackValidity { NotApplicable, Valid, Invalid, Unverified, NotTested }
public enum RollbackCapability { FullReversible, SchemaOnly, ForwardFixOnly, RestoreRequired, Unknown }

public static class JsonDefaults
{
    public static JsonSerializerOptions Compact { get; } = Create(false);
    public static JsonSerializerOptions Indented { get; } = Create(true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        return options;
    }
}

public sealed class SchemaObject
{
    public required string Kind { get; init; }
    public required string Schema { get; init; }
    public string Parent { get; init; } = "";
    public required string Name { get; init; }
    public SortedDictionary<string, string> Properties { get; init; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string Identity => string.Join("|", Kind, Schema, Parent, Name);
}

public sealed class TableImpactMetric
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public long RowCount { get; init; }
    public decimal ReservedMb { get; init; }
    public decimal IndexMb { get; init; }
    public decimal LobMb { get; init; }
    public int PartitionCount { get; init; }
    public int IndexCount { get; init; }
    public int ForeignKeyCount { get; init; }
    public int TriggerCount { get; init; }
    public int DependencyCount { get; init; }

    [JsonIgnore]
    public long RelatedObjectCount => (long)IndexCount + ForeignKeyCount + TriggerCount + DependencyCount;
}

public sealed class SchemaSnapshot
{
    public int FormatVersion { get; init; } = 1;
    public List<SchemaObject> Objects { get; init; } = [];
    public List<TableImpactMetric> ImpactMetrics { get; init; } = [];
    public List<string> UnsupportedSchemaFeatures { get; init; } = [];
    public SchemaCoverage SchemaCoverage => UnsupportedSchemaFeatures.Count == 0 ? SchemaCoverage.Complete : SchemaCoverage.Partial;
}

public sealed class CanonicalSchemaDocument
{
    public int FormatVersion { get; init; } = 1;
    public required IReadOnlyList<SchemaObject> Objects { get; init; }
}

public sealed record CanonicalSchema(CanonicalSchemaDocument Document, string Json, string Sha256);

public sealed class SchemaDiff
{
    public List<string> MissingObjects { get; init; } = [];
    public List<string> ExtraObjects { get; init; } = [];
    public List<string> ChangedObjects { get; init; } = [];
    public bool IsEquivalent => MissingObjects.Count == 0 && ExtraObjects.Count == 0 && ChangedObjects.Count == 0;
}

public sealed class ScriptOperation
{
    public required string Operation { get; init; }
    public required string AstNodeType { get; init; }
    public string Schema { get; init; } = "dbo";
    public required string Object { get; init; }
    public string? Column { get; init; }
    public string? RelatedObject { get; init; }
    public bool IsDestructive { get; init; }
    public bool IsSensitive { get; init; }
    public bool IsSchemaMutation { get; init; }
    public bool IsDataMutation { get; init; }
    public bool HasPotentialDataLoss { get; init; }
    public bool TargetResolved { get; init; } = true;
}

public sealed class DependencyFinding
{
    public required FindingSeverity Severity { get; init; }
    public required string Object { get; init; }
    public required string Operation { get; init; }
    public required string DependentObject { get; init; }
    public required string DependencyType { get; init; }
    public required string Source { get; init; }
    public required string Reason { get; init; }
}

public sealed class ScriptAnalysis
{
    public required string ScriptRole { get; init; }
    public string Parser { get; init; } = "Microsoft.SqlServer.TransactSql.ScriptDom.TSql180Parser";
    public int StatementCount { get; set; }
    public List<ScriptOperation> Operations { get; init; } = [];
    public List<DependencyFinding> Findings { get; init; } = [];
    public AnalysisConfidence Confidence { get; set; } = AnalysisConfidence.Complete;
    public List<string> ConfidenceReasons { get; init; } = [];
    public List<string> ParseErrors { get; init; } = [];
    public List<string> UnknownStatementTypes { get; init; } = [];
    public bool HasDataMutations => Operations.Any(operation => operation.IsDataMutation);
    public bool HasPotentialDataLoss => Operations.Any(operation => operation.HasPotentialDataLoss);
}

public sealed class DependencyAnalysisReport
{
    public required ScriptAnalysis Forward { get; init; }
    public required ScriptAnalysis Rollback { get; init; }
}

public sealed class RiskPolicy
{
    public long MediumRowThreshold { get; init; } = 1_000_000;
    public long HighRowThreshold { get; init; } = 10_000_000;
    public decimal MediumSizeMbThreshold { get; init; } = 10_240m;
    public decimal HighSizeMbThreshold { get; init; } = 51_200m;
    public decimal MediumIndexSizeMbThreshold { get; init; } = 10_240m;
    public decimal HighIndexSizeMbThreshold { get; init; } = 51_200m;
    public int MediumPartitionThreshold { get; init; } = 2;
    public int HighPartitionThreshold { get; init; } = 1_000;
    public int MediumDependencyThreshold { get; init; } = 25;
    public int HighDependencyThreshold { get; init; } = 100;
}

public sealed class RiskAnalysisReport
{
    public RiskLevel ForwardRisk { get; init; }
    public RiskLevel RollbackRisk { get; init; }
    public RiskLevel ForwardDependencyRisk { get; init; }
    public RiskLevel RollbackDependencyRisk { get; init; }
    public RiskLevel DependencyRisk { get; init; }
    public RiskLevel DataRisk { get; init; }
    public RiskLevel ForwardOperationalRisk { get; init; }
    public RiskLevel RollbackOperationalRisk { get; init; }
    public RiskLevel OperationalRisk { get; init; }
    public RiskLevel FinalRisk { get; init; }
    public AnalysisConfidence AnalysisConfidence { get; init; }
    public SchemaCoverage SchemaCoverage { get; init; }
    public bool RequiresDbaApproval { get; init; }
    public bool SensitiveOperationBlockedByConfidence { get; init; }
    public bool AutoPromotionBlocked { get; init; }
    public List<string> Reasons { get; init; } = [];
}

public sealed class TargetEnvironmentPreflight
{
    public required string Environment { get; init; }
    public List<TableImpactMetric> ImpactMetrics { get; init; } = [];
    public AnalysisConfidence Confidence { get; init; } = AnalysisConfidence.Complete;
}

public sealed class TargetRiskReport
{
    public required string Environment { get; init; }
    public RiskLevel QualifiedReleaseRisk { get; init; }
    public RiskLevel TargetPreflightRisk { get; init; }
    public RiskLevel FinalTargetRisk { get; init; }
    public AnalysisConfidence Confidence { get; init; }
    public List<string> Reasons { get; init; } = [];
}

public sealed class ReleaseScript
{
    private readonly byte[] _bytes;

    public ReleaseScript(string role, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(bytes);
        Role = role;
        _bytes = bytes.ToArray();
        Sha256 = Hashing.Sha256(_bytes);
    }

    public string Role { get; }
    public byte[] Bytes => _bytes.ToArray();
    public int Length => _bytes.Length;
    public string Text => System.Text.Encoding.UTF8.GetString(_bytes);
    public string Sha256 { get; }
    public static ReleaseScript FromFile(string role, string path) => new(role, File.ReadAllBytes(path));
    public static ReleaseScript FromText(string role, string text) => new(role, System.Text.Encoding.UTF8.GetBytes(text));
}

public sealed class DiscoveryGate
{
    public required string ConsistencyStatus { get; init; }
    public required string ConsistencyReason { get; init; }
    public bool IsConsistent => string.Equals(ConsistencyStatus, "CONSISTENT", StringComparison.Ordinal);
}

public sealed class ReleaseDescriptor
{
    public required string ReleaseId { get; init; }
    public required string Environment { get; init; }
    public required string SourceKind { get; init; }
    public required string Scenario { get; init; }
    public required string DatabaseLifecycle { get; init; }
}

public sealed class RehearsalResult
{
    public required string QualificationStatus { get; init; }
    public SchemaRollbackValidity SchemaRollbackValidity { get; init; } = SchemaRollbackValidity.NotTested;
    public DataRollbackValidity DataRollbackValidity { get; init; } = DataRollbackValidity.NotTested;
    public RollbackCapability RollbackCapability { get; init; } = RollbackCapability.Unknown;
    public bool ForwardCertified { get; init; }
    public bool RollbackCertified { get; init; }
    public bool ReapplyCertified { get; init; }
    public CanonicalSchema? Pre { get; init; }
    public CanonicalSchema? Post1 { get; init; }
    public CanonicalSchema? Pre2 { get; init; }
    public CanonicalSchema? Post2 { get; init; }
    public SchemaDiff? RollbackDiff { get; init; }
    public SchemaDiff? ReapplyDiff { get; init; }
    public RehearsalAnalysisEvidence? AnalysisEvidence { get; init; }
    public List<string> ExecutionAudit { get; init; } = [];
    public bool CanProceed => QualificationStatus == "QUALIFIED"
        && SchemaRollbackValidity == SchemaRollbackValidity.Valid
        && DataRollbackValidity is DataRollbackValidity.Valid or DataRollbackValidity.NotApplicable
        && RollbackCapability == RollbackCapability.FullReversible
        && RollbackCertified;
}

public sealed class RehearsalAnalysisEvidence
{
    public required ScriptAnalysis ForwardAgainstPre { get; init; }
    public required ScriptAnalysis PreliminaryRollbackAgainstPre { get; init; }
    public ScriptAnalysis? RollbackAgainstPost1 { get; init; }
    public required RiskAnalysisReport PreliminaryRisk { get; init; }
    public RiskAnalysisReport? QualificationRisk { get; init; }
    public List<string> Post1UnsupportedSchemaFeatures { get; init; } = [];

    [JsonIgnore]
    public string RollbackAnalysisBasis => RollbackAgainstPost1 is null ? "PRELIMINARY_PRE" : "POST1";

    [JsonIgnore]
    public RiskAnalysisReport EffectiveRisk => QualificationRisk ?? PreliminaryRisk;

    [JsonIgnore]
    public DependencyAnalysisReport EffectiveDependencyAnalysis => new()
    {
        Forward = ForwardAgainstPre,
        Rollback = RollbackAgainstPost1 ?? PreliminaryRollbackAgainstPre
    };

    [JsonIgnore]
    public DependencyAnalysisReport PreliminaryDependencyAnalysis => new()
    {
        Forward = ForwardAgainstPre,
        Rollback = PreliminaryRollbackAgainstPre
    };
}

public sealed class ReleasePayloadMetadata
{
    public int FormatVersion { get; init; } = 1;
    public required string ReleaseId { get; init; }
    public required string SourceKind { get; init; }
    public required string Scenario { get; init; }
    public required string DatabaseLifecycle { get; init; }
    public required string ForwardHash { get; init; }
    public required string RollbackHash { get; init; }
    public required string PayloadHash { get; init; }
}

public sealed class QualificationAttestation
{
    public int FormatVersion { get; init; } = 1;
    public required string AttestationId { get; init; }
    public required string ReleaseId { get; init; }
    public required string Environment { get; init; }
    public required string PayloadHash { get; init; }
    public required string ForwardHash { get; init; }
    public required string RollbackHash { get; init; }
    public string? PreSchemaHash { get; init; }
    public string? PostSchemaHash { get; init; }
    public SchemaRollbackValidity SchemaRollbackValidity { get; init; }
    public DataRollbackValidity DataRollbackValidity { get; init; }
    public RollbackCapability RollbackCapability { get; init; }
    public RiskLevel ForwardRisk { get; init; }
    public RiskLevel RollbackRisk { get; init; }
    public string RollbackAnalysisBasis { get; init; } = "PRELIMINARY_PRE";
    public RiskLevel RollbackDependencyRisk { get; init; }
    public RiskLevel RollbackOperationalRisk { get; init; }
    public RiskLevel PreliminaryFinalRisk { get; init; }
    public RiskLevel DependencyRisk { get; init; }
    public RiskLevel DataRisk { get; init; }
    public RiskLevel OperationalRisk { get; init; }
    public RiskLevel FinalRisk { get; init; }
    public AnalysisConfidence AnalysisConfidence { get; init; }
    public SchemaCoverage SchemaCoverage { get; init; }
    public List<string> UnsupportedSchemaFeatures { get; init; } = [];
    public bool RequiresDbaApproval { get; init; }
    public required string QualificationStatus { get; init; }
    public bool ForwardCertified { get; init; }
    public bool RollbackCertified { get; init; }
    public bool ReapplyCertified { get; init; }
    public SortedDictionary<string, string> RunMetadata { get; init; } = new(StringComparer.Ordinal);
}

public sealed record ReleasePackageResult(
    string ReleaseDirectory,
    string PayloadDirectory,
    string AttestationDirectory,
    string PayloadHash);
