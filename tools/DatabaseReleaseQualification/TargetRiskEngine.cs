namespace DatabaseReleaseQualification;

public sealed class TargetRiskEngine(RiskPolicy? policy = null)
{
    private readonly RiskPolicy _policy = policy ?? new RiskPolicy();

    public TargetRiskReport Combine(RiskLevel qualifiedReleaseRisk, TargetEnvironmentPreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        var reasons = new List<string>();
        var targetRisk = EvaluateTargetMetrics(preflight, reasons);
        var finalRisk = RiskEngine.Max(qualifiedReleaseRisk, targetRisk);
        reasons.Add($"QUALIFIED_RELEASE_RISK:{qualifiedReleaseRisk.ToString().ToUpperInvariant()}");
        reasons.Add($"TARGET_PREFLIGHT_RISK:{targetRisk.ToString().ToUpperInvariant()}");
        return new TargetRiskReport
        {
            Environment = preflight.Environment,
            QualifiedReleaseRisk = qualifiedReleaseRisk,
            TargetPreflightRisk = targetRisk,
            FinalTargetRisk = finalRisk,
            Confidence = preflight.Confidence,
            Reasons = reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
        };
    }

    private RiskLevel EvaluateTargetMetrics(TargetEnvironmentPreflight preflight, ICollection<string> reasons)
    {
        var risk = preflight.Confidence switch
        {
            AnalysisConfidence.Insufficient => RiskLevel.High,
            AnalysisConfidence.Partial => RiskLevel.Medium,
            _ => RiskLevel.Low
        };
        if (preflight.Confidence != AnalysisConfidence.Complete)
            reasons.Add($"TARGET_PREFLIGHT_CONFIDENCE:{preflight.Confidence.ToString().ToUpperInvariant()}");

        foreach (var metric in preflight.ImpactMetrics)
        {
            var high = metric.RowCount >= _policy.HighRowThreshold
                || metric.ReservedMb >= _policy.HighSizeMbThreshold
                || metric.IndexMb >= _policy.HighIndexSizeMbThreshold
                || metric.LobMb >= _policy.HighSizeMbThreshold
                || metric.PartitionCount >= _policy.HighPartitionThreshold
                || metric.RelatedObjectCount >= _policy.HighDependencyThreshold;
            var medium = metric.RowCount >= _policy.MediumRowThreshold
                || metric.ReservedMb >= _policy.MediumSizeMbThreshold
                || metric.IndexMb >= _policy.MediumIndexSizeMbThreshold
                || metric.LobMb >= _policy.MediumSizeMbThreshold
                || metric.PartitionCount >= _policy.MediumPartitionThreshold
                || metric.RelatedObjectCount >= _policy.MediumDependencyThreshold;

            if (high)
            {
                risk = RiskEngine.Max(risk, RiskLevel.High);
                reasons.Add($"HIGH_TARGET_IMPACT:{metric.Schema}.{metric.Table}");
            }
            else if (medium)
            {
                risk = RiskEngine.Max(risk, RiskLevel.Medium);
                reasons.Add($"MEDIUM_TARGET_IMPACT:{metric.Schema}.{metric.Table}");
            }
        }
        return risk;
    }
}
