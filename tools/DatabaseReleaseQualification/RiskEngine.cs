namespace DatabaseReleaseQualification;

public sealed class RiskEngine(RiskPolicy? policy = null)
{
    private readonly RiskPolicy _policy = policy ?? new RiskPolicy();

    public RiskAnalysisReport Evaluate(DependencyAnalysisReport analysis, SchemaSnapshot snapshot)
        => Evaluate(analysis.Forward, snapshot, analysis.Rollback, snapshot);

    public RiskAnalysisReport Evaluate(
        ScriptAnalysis forward,
        SchemaSnapshot forwardSnapshot,
        ScriptAnalysis rollback,
        SchemaSnapshot rollbackSnapshot)
    {
        var reasons = new List<string>();
        var forwardDataRisk = DataRisk(forward, forwardSnapshot, reasons, "FORWARD");
        var rollbackDataRisk = DataRisk(rollback, rollbackSnapshot, reasons, "ROLLBACK");
        var forwardOperationalRisk = OperationalRisk(forward, forwardSnapshot, reasons, "FORWARD");
        var rollbackOperationalRisk = OperationalRisk(rollback, rollbackSnapshot, reasons, "ROLLBACK");
        var forwardRisk = Max(
            ScriptRisk(forward, reasons, "FORWARD"),
            forwardDataRisk,
            forwardOperationalRisk);
        var rollbackRisk = Max(
            ScriptRisk(rollback, reasons, "ROLLBACK"),
            rollbackDataRisk,
            rollbackOperationalRisk);
        var forwardDependencyRisk = DependencyRisk(forward, reasons, "FORWARD");
        var rollbackDependencyRisk = DependencyRisk(rollback, reasons, "ROLLBACK");
        var dependencyRisk = Max(forwardDependencyRisk, rollbackDependencyRisk);
        var dataRisk = Max(forwardDataRisk, rollbackDataRisk);
        var operationalRisk = Max(forwardOperationalRisk, rollbackOperationalRisk);
        var confidence = MaxConfidence(forward.Confidence, rollback.Confidence);
        var sensitive = forward.Operations.Concat(rollback.Operations).Any(item => item.IsSensitive);
        var astUnresolved = forward.ParseErrors.Count > 0 || rollback.ParseErrors.Count > 0
            || forward.UnknownStatementTypes.Count > 0 || rollback.UnknownStatementTypes.Count > 0;
        var blockedByConfidence = confidence == AnalysisConfidence.Insufficient && (sensitive || astUnresolved);

        if (blockedByConfidence)
        {
            dependencyRisk = Max(dependencyRisk, RiskLevel.High);
            reasons.Add("ANALYSIS_CONFIDENCE_INSUFFICIENT_OR_UNRESOLVED_AST");
        }
        else if (confidence == AnalysisConfidence.Partial
                 && forward.Operations.Concat(rollback.Operations).Any())
        {
            dependencyRisk = Max(dependencyRisk, RiskLevel.Medium);
            reasons.Add("ANALYSIS_CONFIDENCE_PARTIAL_MINIMUM_MEDIUM");
        }

        var finalRisk = Max(forwardRisk, rollbackRisk, dependencyRisk, dataRisk, operationalRisk);
        return new RiskAnalysisReport
        {
            ForwardRisk = forwardRisk,
            RollbackRisk = rollbackRisk,
            ForwardDependencyRisk = forwardDependencyRisk,
            RollbackDependencyRisk = rollbackDependencyRisk,
            DependencyRisk = dependencyRisk,
            DataRisk = dataRisk,
            ForwardOperationalRisk = forwardOperationalRisk,
            RollbackOperationalRisk = rollbackOperationalRisk,
            OperationalRisk = operationalRisk,
            FinalRisk = finalRisk,
            AnalysisConfidence = confidence,
            SchemaCoverage = MaxCoverage(forwardSnapshot.SchemaCoverage, rollbackSnapshot.SchemaCoverage),
            RequiresDbaApproval = finalRisk is RiskLevel.Medium or RiskLevel.High,
            SensitiveOperationBlockedByConfidence = blockedByConfidence,
            AutoPromotionBlocked = blockedByConfidence,
            Reasons = reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
        };
    }

    private static RiskLevel ScriptRisk(ScriptAnalysis analysis, ICollection<string> reasons, string prefix)
    {
        var risk = RiskLevel.Low;
        foreach (var operation in analysis.Operations)
        {
            var operationRisk = operation.Operation switch
            {
                "DROP_TABLE" or "DROP_COLUMN" or "TRUNCATE_TABLE" or "DELETE_DATA" or "MERGE_DATA" or "UNKNOWN_SQL" or "EXECUTE" or "EXECUTE_DYNAMIC_SQL" => RiskLevel.High,
                "CREATE_TRIGGER" or "CREATE_OR_ALTER_TRIGGER" or "ALTER_TRIGGER" or "DROP_TRIGGER" => RiskLevel.High,
                "ALTER_COLUMN" or "CREATE_INDEX" or "DROP_INDEX" or "REBUILD_INDEX" or "ALTER_INDEX"
                    or "ADD_CONSTRAINT" or "DROP_CONSTRAINT" or "ALTER_CONSTRAINT"
                    or "UPDATE_DATA" or "INSERT_DATA" or "SELECT_INTO"
                    or "ALTER_VIEW" or "CREATE_OR_ALTER_VIEW" => RiskLevel.Medium,
                _ => RiskLevel.Low
            };
            risk = Max(risk, operationRisk);
            if (operationRisk != RiskLevel.Low)
            {
                reasons.Add($"{prefix}_{operation.Operation}_{operationRisk.ToString().ToUpperInvariant()}");
            }
        }
        return risk;
    }

    private static RiskLevel DependencyRisk(ScriptAnalysis analysis, ICollection<string> reasons, string prefix)
    {
        if (analysis.Findings.Any(item => item.Severity == FindingSeverity.Blocking))
        {
            reasons.Add($"{prefix}_BLOCKING_DEPENDENCY_DETECTED");
            return RiskLevel.High;
        }
        if (analysis.Findings.Any(item => item.Severity == FindingSeverity.Warning))
        {
            reasons.Add($"{prefix}_DEPENDENCY_WARNING_DETECTED");
            return RiskLevel.Medium;
        }
        return RiskLevel.Low;
    }

    private RiskLevel DataRisk(ScriptAnalysis analysis, SchemaSnapshot snapshot, ICollection<string> reasons, string prefix)
    {
        var risk = RiskLevel.Low;
        foreach (var operation in analysis.Operations)
        {
            if (operation.IsDestructive || operation.HasPotentialDataLoss)
            {
                risk = RiskLevel.High;
                reasons.Add($"{prefix}_DATA_LOSS_POTENTIAL_{operation.Operation}");
            }

            var metric = MetricFor(snapshot, operation);
            if (metric is null)
            {
                continue;
            }

            if (metric.RowCount >= _policy.HighRowThreshold || metric.ReservedMb >= _policy.HighSizeMbThreshold)
            {
                risk = Max(risk, RiskLevel.High);
                reasons.Add($"{prefix}_HIGH_DATA_IMPACT_{operation.Schema}.{operation.Object}");
            }
            else if (metric.RowCount >= _policy.MediumRowThreshold || metric.ReservedMb >= _policy.MediumSizeMbThreshold)
            {
                risk = Max(risk, RiskLevel.Medium);
                reasons.Add($"{prefix}_MEDIUM_DATA_IMPACT_{operation.Schema}.{operation.Object}");
            }
        }
        return risk;
    }

    private RiskLevel OperationalRisk(ScriptAnalysis analysis, SchemaSnapshot snapshot, ICollection<string> reasons, string prefix)
    {
        var risk = RiskLevel.Low;
        foreach (var operation in analysis.Operations
                     .Where(item => item.Operation.Contains("INDEX", StringComparison.Ordinal) || item.Operation == "ALTER_COLUMN"))
        {
            var metric = MetricFor(snapshot, operation);
            if (metric is null)
            {
                continue;
            }

            if (metric.IndexMb >= _policy.HighIndexSizeMbThreshold
                || metric.PartitionCount >= _policy.HighPartitionThreshold
                || metric.RelatedObjectCount >= _policy.HighDependencyThreshold)
            {
                risk = Max(risk, RiskLevel.High);
                reasons.Add($"{prefix}_HIGH_OPERATIONAL_COST_{operation.Schema}.{operation.Object}");
            }
            else if (metric.IndexMb >= _policy.MediumIndexSizeMbThreshold
                     || metric.PartitionCount >= _policy.MediumPartitionThreshold
                     || metric.RelatedObjectCount >= _policy.MediumDependencyThreshold)
            {
                risk = Max(risk, RiskLevel.Medium);
                reasons.Add($"{prefix}_MEDIUM_OPERATIONAL_COST_{operation.Schema}.{operation.Object}");
            }
        }
        return risk;
    }

    private static TableImpactMetric? MetricFor(SchemaSnapshot snapshot, ScriptOperation operation) =>
        snapshot.ImpactMetrics.FirstOrDefault(metric =>
            string.Equals(metric.Schema, operation.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(metric.Table, operation.Object, StringComparison.OrdinalIgnoreCase));

    public static RiskLevel Max(params RiskLevel[] values) => values.Max();
    private static AnalysisConfidence MaxConfidence(params AnalysisConfidence[] values) => values.Max();
    private static SchemaCoverage MaxCoverage(params SchemaCoverage[] values) => values.Max();
}
