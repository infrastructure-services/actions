namespace DatabaseReleaseQualification;

public interface IRehearsalDatabase
{
    Task<SchemaSnapshot> CaptureSchemaAsync(CancellationToken cancellationToken = default);
    Task ExecuteSqlAsync(ReleaseScript script, string expectedSha256, CancellationToken cancellationToken = default);
}

public interface IDataRollbackValidationContract
{
    Task CapturePreDataAsync(CancellationToken cancellationToken = default);
    Task<DataRollbackValidity> ValidateRollbackDataAsync(CancellationToken cancellationToken = default);
}

public sealed class RehearsalEngine
{
    private static readonly HashSet<string> AllowedEnvironments = new(StringComparer.OrdinalIgnoreCase) { "TEST" };

    public async Task<RehearsalResult> QualifyAsync(
        ReleaseDescriptor release,
        DiscoveryGate discovery,
        ReleaseScript forward,
        ReleaseScript rollback,
        IRehearsalDatabase database,
        IDataRollbackValidationContract? dataValidation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(database);

        if (!discovery.IsConsistent) return Blocked("BLOCKED_DISCOVERY", $"DISCOVERY:{discovery.ConsistencyReason}");
        if (string.Equals(release.Environment, "PROD", StringComparison.OrdinalIgnoreCase))
            return Blocked("BLOCKED_PROD_REHEARSAL", "ENVIRONMENT:PROD");
        if (!AllowedEnvironments.Contains(release.Environment))
            return Blocked("BLOCKED_ENVIRONMENT_NOT_ALLOWED", $"ENVIRONMENT:{release.Environment.ToUpperInvariant()}");
        if (forward.Length == 0 || rollback.Length == 0)
            return Blocked("BLOCKED_RELEASE_SCRIPT_MISSING", "SCRIPT:EMPTY");

        var audit = new List<string>
        {
            $"FORWARD_SHA256:{forward.Sha256}",
            $"ROLLBACK_SHA256:{rollback.Sha256}"
        };

        var preSnapshot = await database.CaptureSchemaAsync(cancellationToken);
        var pre = SchemaCanonicalizer.Canonicalize(preSnapshot);
        audit.Add($"PRE_SCHEMA_SHA256:{pre.Sha256}");

        var analyzer = new SqlScriptAnalyzer();
        var forwardAnalysis = analyzer.Analyze("forward", forward.Text, preSnapshot);
        var preliminaryRollbackAnalysis = analyzer.Analyze("rollback", rollback.Text, preSnapshot);
        var riskEngine = new RiskEngine();
        var preliminaryRisk = riskEngine.Evaluate(new DependencyAnalysisReport
        {
            Forward = forwardAnalysis,
            Rollback = preliminaryRollbackAnalysis
        }, preSnapshot);
        var preliminaryEvidence = Evidence(forwardAnalysis, preliminaryRollbackAnalysis, preliminaryRisk);
        audit.Add($"PRELIMINARY_FINAL_RISK:{preliminaryRisk.FinalRisk.ToString().ToUpperInvariant()}");

        if (BlocksExecution(forwardAnalysis) || BlocksExecution(preliminaryRollbackAnalysis))
        {
            audit.Add("ANALYSIS_CONFIDENCE:INSUFFICIENT");
            return new RehearsalResult
            {
                QualificationStatus = "BLOCKED_ANALYSIS_CONFIDENCE",
                SchemaRollbackValidity = SchemaRollbackValidity.NotTested,
                DataRollbackValidity = DataRollbackValidity.NotTested,
                RollbackCapability = RollbackCapability.Unknown,
                Pre = pre,
                AnalysisEvidence = preliminaryEvidence,
                ExecutionAudit = audit
            };
        }

        var preliminaryDataValidationRequired = RequiresDataValidation(forwardAnalysis, preliminaryRollbackAnalysis);
        var dataBaselineCaptured = preliminaryDataValidationRequired && dataValidation is not null;
        if (dataBaselineCaptured)
        {
            await dataValidation!.CapturePreDataAsync(cancellationToken);
            audit.Add("PRE_DATA_VALIDATION:CAPTURED");
        }

        try
        {
            await ExecuteExactAsync(database, forward, forward.Sha256, cancellationToken);
        }
        catch (Exception exception)
        {
            audit.Add($"FORWARD_EXECUTION_FAILED:{exception.GetType().Name}");
            return Result("BLOCKED_FORWARD_EXECUTION", SchemaRollbackValidity.NotTested,
                DataRollbackValidity.NotTested, RollbackCapability.Unknown, false, false, false, audit, pre,
                analysisEvidence: preliminaryEvidence);
        }
        audit.Add($"EXECUTED_FORWARD:{forward.Sha256}");

        var post1Snapshot = await database.CaptureSchemaAsync(cancellationToken);
        var post1 = SchemaCanonicalizer.Canonicalize(post1Snapshot);
        audit.Add($"POST1_SCHEMA_SHA256:{post1.Sha256}");

        var post1RollbackAnalysis = analyzer.Analyze("rollback", rollback.Text, post1Snapshot);
        var qualificationRisk = riskEngine.Evaluate(
            forwardAnalysis,
            preSnapshot,
            post1RollbackAnalysis,
            post1Snapshot);
        var qualificationEvidence = Evidence(
            forwardAnalysis,
            preliminaryRollbackAnalysis,
            preliminaryRisk,
            post1RollbackAnalysis,
            qualificationRisk,
            post1Snapshot.UnsupportedSchemaFeatures);
        audit.Add("ROLLBACK_ANALYSIS_BASIS:POST1");
        audit.Add($"POST1_ROLLBACK_DEPENDENCY_RISK:{qualificationRisk.RollbackDependencyRisk.ToString().ToUpperInvariant()}");
        audit.Add($"POST1_ROLLBACK_OPERATIONAL_RISK:{qualificationRisk.RollbackOperationalRisk.ToString().ToUpperInvariant()}");
        audit.Add($"QUALIFICATION_FINAL_RISK:{qualificationRisk.FinalRisk.ToString().ToUpperInvariant()}");

        if (BlocksExecution(post1RollbackAnalysis))
        {
            audit.Add("POST1_ROLLBACK_ANALYSIS_CONFIDENCE:INSUFFICIENT");
            return Result("BLOCKED_POST1_ROLLBACK_ANALYSIS_CONFIDENCE", SchemaRollbackValidity.NotTested,
                DataRollbackValidity.NotTested, RollbackCapability.Unknown, true, false, false,
                audit, pre, post1, analysisEvidence: qualificationEvidence);
        }

        var dataValidationRequired = RequiresDataValidation(forwardAnalysis, post1RollbackAnalysis);

        try
        {
            await ExecuteExactAsync(database, rollback, rollback.Sha256, cancellationToken);
        }
        catch (Exception exception)
        {
            audit.Add($"ROLLBACK_EXECUTION_FAILED:{exception.GetType().Name}");
            return Result("BLOCKED_ROLLBACK_EXECUTION", SchemaRollbackValidity.Invalid,
                dataValidationRequired ? DataRollbackValidity.NotTested : DataRollbackValidity.NotApplicable,
                RollbackCapability.Unknown, true, false, false, audit, pre, post1,
                analysisEvidence: qualificationEvidence);
        }
        audit.Add($"EXECUTED_ROLLBACK:{rollback.Sha256}");

        var pre2 = SchemaCanonicalizer.Canonicalize(await database.CaptureSchemaAsync(cancellationToken));
        audit.Add($"PRE2_SCHEMA_SHA256:{pre2.Sha256}");
        var rollbackDiff = SchemaComparer.Compare(pre, pre2);
        if (!rollbackDiff.IsEquivalent)
        {
            return Result("BLOCKED_SCHEMA_ROLLBACK_MISMATCH", SchemaRollbackValidity.Invalid,
                dataValidationRequired ? DataRollbackValidity.Unverified : DataRollbackValidity.NotApplicable,
                RollbackCapability.Unknown, true, false, false, audit, pre, post1, pre2,
                rollbackDiff: rollbackDiff, analysisEvidence: qualificationEvidence);
        }

        var dataValidity = DataRollbackValidity.NotApplicable;
        if (dataValidationRequired)
        {
            dataValidity = dataValidation is null || !dataBaselineCaptured
                ? DataRollbackValidity.Unverified
                : await dataValidation.ValidateRollbackDataAsync(cancellationToken);
            audit.Add($"DATA_ROLLBACK_VALIDITY:{dataValidity.ToString().ToUpperInvariant()}");

            if (dataValidity != DataRollbackValidity.Valid)
            {
                var capability = CapabilityForUnverifiedData(forwardAnalysis, dataValidity);
                var status = dataValidity == DataRollbackValidity.Invalid
                    ? "BLOCKED_DATA_ROLLBACK_MISMATCH"
                    : "BLOCKED_DATA_ROLLBACK_UNVERIFIED";
                return Result(status, SchemaRollbackValidity.Valid, dataValidity, capability,
                    true, false, false, audit, pre, post1, pre2, rollbackDiff: rollbackDiff,
                    analysisEvidence: qualificationEvidence);
            }
        }

        try
        {
            await ExecuteExactAsync(database, forward, forward.Sha256, cancellationToken);
        }
        catch (Exception exception)
        {
            audit.Add($"REAPPLY_EXECUTION_FAILED:{exception.GetType().Name}");
            return Result("BLOCKED_REAPPLY_EXECUTION", SchemaRollbackValidity.Valid,
                dataValidity, RollbackCapability.FullReversible, true, true, false,
                audit, pre, post1, pre2, rollbackDiff: rollbackDiff,
                analysisEvidence: qualificationEvidence);
        }
        audit.Add($"REEXECUTED_FORWARD:{forward.Sha256}");

        var post2 = SchemaCanonicalizer.Canonicalize(await database.CaptureSchemaAsync(cancellationToken));
        audit.Add($"POST2_SCHEMA_SHA256:{post2.Sha256}");
        var reapplyDiff = SchemaComparer.Compare(post1, post2);
        if (!reapplyDiff.IsEquivalent)
        {
            return Result("BLOCKED_REAPPLY_MISMATCH", SchemaRollbackValidity.Valid,
                dataValidity, RollbackCapability.FullReversible, true, true, false,
                audit, pre, post1, pre2, post2, rollbackDiff, reapplyDiff, qualificationEvidence);
        }

        return Result("QUALIFIED", SchemaRollbackValidity.Valid, dataValidity,
            RollbackCapability.FullReversible, true, true, true,
            audit, pre, post1, pre2, post2, rollbackDiff, reapplyDiff, qualificationEvidence);
    }

    private static bool BlocksExecution(ScriptAnalysis analysis) =>
        analysis.Confidence == AnalysisConfidence.Insufficient;

    private static bool RequiresDataValidation(ScriptAnalysis forward, ScriptAnalysis rollback) =>
        forward.Operations.Any(operation => operation.IsDataMutation || operation.HasPotentialDataLoss)
        || rollback.Operations.Any(operation => operation.IsDataMutation);

    private static RollbackCapability CapabilityForUnverifiedData(ScriptAnalysis forward, DataRollbackValidity validity)
    {
        if (validity == DataRollbackValidity.Invalid) return RollbackCapability.RestoreRequired;
        return forward.HasPotentialDataLoss ? RollbackCapability.RestoreRequired : RollbackCapability.SchemaOnly;
    }

    private static RehearsalAnalysisEvidence Evidence(
        ScriptAnalysis forward,
        ScriptAnalysis preliminaryRollback,
        RiskAnalysisReport preliminaryRisk,
        ScriptAnalysis? post1Rollback = null,
        RiskAnalysisReport? qualificationRisk = null,
        IEnumerable<string>? post1UnsupportedSchemaFeatures = null) => new()
    {
        ForwardAgainstPre = forward,
        PreliminaryRollbackAgainstPre = preliminaryRollback,
        RollbackAgainstPost1 = post1Rollback,
        PreliminaryRisk = preliminaryRisk,
        QualificationRisk = qualificationRisk,
        Post1UnsupportedSchemaFeatures = (post1UnsupportedSchemaFeatures ?? [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList()
    };

    private static async Task ExecuteExactAsync(
        IRehearsalDatabase database,
        ReleaseScript script,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(script.Sha256, expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"{script.Role.ToUpperInvariant()}_SHA256_MISMATCH");
        await database.ExecuteSqlAsync(script, expectedSha256, cancellationToken);
    }

    private static RehearsalResult Blocked(string status, string audit) => Result(
        status,
        SchemaRollbackValidity.NotTested,
        DataRollbackValidity.NotTested,
        RollbackCapability.Unknown,
        false,
        false,
        false,
        [audit]);

    private static RehearsalResult Result(
        string status,
        SchemaRollbackValidity schemaValidity,
        DataRollbackValidity dataValidity,
        RollbackCapability capability,
        bool forwardCertified,
        bool rollbackCertified,
        bool reapplyCertified,
        List<string> audit,
        CanonicalSchema? pre = null,
        CanonicalSchema? post1 = null,
        CanonicalSchema? pre2 = null,
        CanonicalSchema? post2 = null,
        SchemaDiff? rollbackDiff = null,
        SchemaDiff? reapplyDiff = null,
        RehearsalAnalysisEvidence? analysisEvidence = null) => new()
    {
        QualificationStatus = status,
        SchemaRollbackValidity = schemaValidity,
        DataRollbackValidity = dataValidity,
        RollbackCapability = capability,
        ForwardCertified = forwardCertified,
        RollbackCertified = rollbackCertified
            && schemaValidity == SchemaRollbackValidity.Valid
            && dataValidity is DataRollbackValidity.Valid or DataRollbackValidity.NotApplicable,
        ReapplyCertified = reapplyCertified,
        Pre = pre,
        Post1 = post1,
        Pre2 = pre2,
        Post2 = post2,
        RollbackDiff = rollbackDiff,
        ReapplyDiff = reapplyDiff,
        AnalysisEvidence = analysisEvidence,
        ExecutionAudit = audit
    };
}
