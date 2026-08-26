namespace DatabaseReleaseQualification;

public static class DatabaseLifecycles
{
    public const string New = "NEW";
    public const string Existing = "EXISTING";
}

public enum StructuralOnboardingState
{
    BaselineRequired,
    Candidate,
    Certified
}

public enum LineageOnboardingState
{
    ConsistentEf,
    LegacySql,
    BlockedHistoryWithoutRepo,
    Divergent,
    Unknown
}

public enum ReconciliationOnboardingState
{
    NotRequired,
    Match,
    ReadyForCertification,
    Reconciled,
    Blocked
}

public enum CertificationOnboardingState
{
    NotCertified,
    Pending,
    Certified
}

public enum OverallOnboardingStatus
{
    Managed,
    Pending,
    Blocked
}

public static class DatabaseOnboardingReasons
{
    public const string BaselineRequired = "BASELINE_REQUIRED";
    public const string BaselineNotCertified = "BASELINE_NOT_CERTIFIED";
    public const string TargetNotRegistered = "TARGET_NOT_REGISTERED";
    public const string RegistryInvalid = "REGISTRY_INVALID";
    public const string LifecycleInvalid = "DATABASE_LIFECYCLE_INVALID";
    public const string LifecycleMismatch = "DATABASE_LIFECYCLE_MISMATCH";
    public const string LineageBlockedHistoryWithoutRepo = "LINEAGE_BLOCKED_HISTORY_WITHOUT_REPO";
    public const string LineageDivergent = "LINEAGE_DIVERGENT";
    public const string LineageUnknown = "LINEAGE_UNKNOWN";
    public const string UnexplainedDrift = "UNEXPLAINED_DRIFT";
    public const string ReconciliationBlocked = "RECONCILIATION_BLOCKED";
    public const string ReconciliationPending = "RECONCILIATION_PENDING";
    public const string CertificationPending = "CERTIFICATION_PENDING";
    public const string CertificationStateMismatch = "CERTIFICATION_STATE_MISMATCH";
    public const string ExistingBootstrapRequired = "EXISTING_BOOTSTRAP_REQUIRED";
    public const string QualifiedInitialReleaseRequired = "QUALIFIED_INITIAL_RELEASE_REQUIRED";
}

public sealed class DatabaseLineageAssessment
{
    public required DiscoveryGate Discovery { get; init; }
    public required string Scenario { get; init; }
    public required string SourceKind { get; init; }
}

public sealed class DatabaseOnboardingRequest
{
    public required string DatabaseLifecycle { get; init; }
    public required DatabaseLineageAssessment LineageAssessment { get; init; }
    public required DatabaseStateEvaluation DatabaseState { get; init; }
    public ReconciliationResult? Reconciliation { get; init; }
    public CertificationResult? Certification { get; init; }
}

public sealed class DatabaseOnboardingResult
{
    public required string DiscoveryConsistencyStatus { get; init; }
    public required string DiscoveryConsistencyReason { get; init; }
    public required string DiscoveryScenario { get; init; }
    public required string DiscoverySourceKind { get; init; }
    public required string DatabaseDriftStatus { get; init; }
    public CertificationDecision? CertificationDecision { get; init; }
    public string? CertificationDecisionReason { get; init; }
    public StructuralOnboardingState StructuralState { get; init; }
    public LineageOnboardingState LineageState { get; init; }
    public ReconciliationOnboardingState ReconciliationState { get; init; }
    public CertificationOnboardingState CertificationState { get; init; }
    public OverallOnboardingStatus OverallOnboardingStatus { get; init; }
    public bool DeploymentEligibility { get; init; }
    public bool RehearsalEligibility { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public bool IsManaged => OverallOnboardingStatus == OverallOnboardingStatus.Managed;
}

public sealed class DatabaseOnboardingEvaluator
{
    public DatabaseOnboardingResult Evaluate(DatabaseOnboardingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.LineageAssessment);
        ArgumentNullException.ThrowIfNull(request.LineageAssessment.Discovery);
        ArgumentNullException.ThrowIfNull(request.DatabaseState);

        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var lifecycleValid = request.DatabaseLifecycle is DatabaseLifecycles.New or DatabaseLifecycles.Existing;
        if (!lifecycleValid) reasons.Add(DatabaseOnboardingReasons.LifecycleInvalid);

        if (request.DatabaseState.Target is not null
            && !string.Equals(request.DatabaseState.Target.Lifecycle, request.DatabaseLifecycle, StringComparison.Ordinal))
            reasons.Add(DatabaseOnboardingReasons.LifecycleMismatch);

        var lineageState = MapLineage(request.LineageAssessment);
        AddLineageReason(lineageState, reasons);

        var certificationMatchesObserved = CertificationMatchesObserved(request);
        var certificationStateMismatch = request.Certification?.ProducesCertifiedState == true
            && !certificationMatchesObserved;
        if (certificationStateMismatch)
            reasons.Add(DatabaseOnboardingReasons.CertificationStateMismatch);

        var structuralState = StructuralState(request, certificationMatchesObserved);
        var certificationState = CertificationState(request, structuralState, certificationMatchesObserved);
        var reconciliationState = ReconciliationState(request, certificationMatchesObserved, reasons);

        var registryInvalid = request.DatabaseState.DriftStatus == DatabaseDriftStatuses.InvalidRegistry;
        if (registryInvalid) reasons.Add(DatabaseOnboardingReasons.RegistryInvalid);
        if (request.DatabaseState.DriftStatus == DatabaseDriftStatuses.TargetNotRegistered)
            reasons.Add(DatabaseOnboardingReasons.TargetNotRegistered);

        var blocking = !lifecycleValid
            || registryInvalid
            || certificationStateMismatch
            || lineageState is LineageOnboardingState.BlockedHistoryWithoutRepo
                or LineageOnboardingState.Divergent
                or LineageOnboardingState.Unknown
            || reconciliationState is ReconciliationOnboardingState.ReadyForCertification
                or ReconciliationOnboardingState.Blocked;

        var pending = false;
        if (structuralState == StructuralOnboardingState.BaselineRequired)
        {
            pending = true;
            reasons.Add(DatabaseOnboardingReasons.BaselineRequired);
        }
        else if (structuralState == StructuralOnboardingState.Candidate)
        {
            pending = true;
            reasons.Add(DatabaseOnboardingReasons.BaselineNotCertified);
            reasons.Add(request.DatabaseLifecycle == DatabaseLifecycles.New
                ? DatabaseOnboardingReasons.QualifiedInitialReleaseRequired
                : DatabaseOnboardingReasons.ExistingBootstrapRequired);
        }

        if (certificationState == CertificationOnboardingState.Pending)
        {
            pending = true;
            reasons.Add(DatabaseOnboardingReasons.CertificationPending);
        }

        var overall = blocking
            ? OverallOnboardingStatus.Blocked
            : pending
                ? OverallOnboardingStatus.Pending
                : OverallOnboardingStatus.Managed;
        var foundationallyEligible = overall == OverallOnboardingStatus.Managed
            && structuralState == StructuralOnboardingState.Certified;

        return new DatabaseOnboardingResult
        {
            DiscoveryConsistencyStatus = request.LineageAssessment.Discovery.ConsistencyStatus,
            DiscoveryConsistencyReason = request.LineageAssessment.Discovery.ConsistencyReason,
            DiscoveryScenario = request.LineageAssessment.Scenario,
            DiscoverySourceKind = request.LineageAssessment.SourceKind,
            DatabaseDriftStatus = request.DatabaseState.DriftStatus,
            CertificationDecision = request.Certification?.Decision,
            CertificationDecisionReason = request.Certification?.DecisionReason,
            StructuralState = structuralState,
            LineageState = lineageState,
            ReconciliationState = reconciliationState,
            CertificationState = certificationState,
            OverallOnboardingStatus = overall,
            DeploymentEligibility = foundationallyEligible,
            RehearsalEligibility = foundationallyEligible,
            Reasons = reasons.ToArray()
        };
    }

    private static StructuralOnboardingState StructuralState(
        DatabaseOnboardingRequest request,
        bool certificationMatchesObserved)
    {
        if (certificationMatchesObserved
            || request.DatabaseState.Target?.CertificationStatus == DatabaseCertificationStatuses.Certified
                && request.DatabaseState.CertifiedSchemaHash is not null)
            return StructuralOnboardingState.Certified;

        return request.DatabaseState.BaselineCandidate
            ? StructuralOnboardingState.Candidate
            : StructuralOnboardingState.BaselineRequired;
    }

    private static CertificationOnboardingState CertificationState(
        DatabaseOnboardingRequest request,
        StructuralOnboardingState structuralState,
        bool certificationMatchesObserved)
    {
        if (structuralState == StructuralOnboardingState.Certified && (certificationMatchesObserved
            || request.DatabaseState.Target?.CertificationStatus == DatabaseCertificationStatuses.Certified))
            return CertificationOnboardingState.Certified;
        return request.Certification?.Decision == CertificationDecision.ReadyForHumanApproval
            ? CertificationOnboardingState.Pending
            : CertificationOnboardingState.NotCertified;
    }

    private static ReconciliationOnboardingState ReconciliationState(
        DatabaseOnboardingRequest request,
        bool certificationMatchesObserved,
        ISet<string> reasons)
    {
        if (certificationMatchesObserved && request.Certification is not null)
        {
            return request.Certification.Origin is CertificationOrigin.DriftReconciliation
                or CertificationOrigin.BreakGlassReconciliation
                ? ReconciliationOnboardingState.Reconciled
                : ReconciliationOnboardingState.NotRequired;
        }

        if (request.Reconciliation is not null)
        {
            if (request.Reconciliation.ReconciliationStatus == ReconciliationStatus.ReadyForCertification)
            {
                reasons.Add(DatabaseOnboardingReasons.ReconciliationPending);
                return ReconciliationOnboardingState.ReadyForCertification;
            }

            if (request.Reconciliation.ReconciliationStatus == ReconciliationStatus.Blocked)
            {
                reasons.Add(DatabaseOnboardingReasons.ReconciliationBlocked);
                if (request.Reconciliation.UnexplainedDifferenceCount > 0)
                    reasons.Add(DatabaseOnboardingReasons.UnexplainedDrift);
                return ReconciliationOnboardingState.Blocked;
            }

            if (request.DatabaseState.DriftStatus == DatabaseDriftStatuses.DriftDetected)
            {
                reasons.Add(DatabaseOnboardingReasons.UnexplainedDrift);
                return ReconciliationOnboardingState.Blocked;
            }
        }

        return request.DatabaseState.DriftStatus switch
        {
            DatabaseDriftStatuses.Match => ReconciliationOnboardingState.Match,
            DatabaseDriftStatuses.BaselineRequired or DatabaseDriftStatuses.TargetNotRegistered =>
                ReconciliationOnboardingState.NotRequired,
            _ => BlockUnresolvedDrift(request.DatabaseState.DriftStatus, reasons)
        };
    }

    private static ReconciliationOnboardingState BlockUnresolvedDrift(string driftStatus, ISet<string> reasons)
    {
        if (driftStatus == DatabaseDriftStatuses.DriftDetected)
            reasons.Add(DatabaseOnboardingReasons.UnexplainedDrift);
        return ReconciliationOnboardingState.Blocked;
    }

    private static bool CertificationMatchesObserved(DatabaseOnboardingRequest request)
    {
        var certification = request.Certification;
        if (certification?.ProducesCertifiedState != true
            || !string.Equals(certification.Evidence.DatabaseLifecycle,
                request.DatabaseLifecycle, StringComparison.Ordinal)
            || !string.Equals(certification.NextCertifiedSchemaHash,
                request.DatabaseState.ObservedSchemaHash, StringComparison.OrdinalIgnoreCase))
            return false;

        if (certification.Origin is CertificationOrigin.DriftReconciliation
            or CertificationOrigin.BreakGlassReconciliation)
        {
            return request.Reconciliation?.ReconciliationStatus == ReconciliationStatus.ReadyForCertification
                && request.Reconciliation.UnexplainedDifferenceCount == 0
                && string.Equals(request.Reconciliation.Evidence.ReconciledCanonicalStateCandidateHash,
                    certification.NextCertifiedSchemaHash, StringComparison.OrdinalIgnoreCase);
        }

        if (request.Reconciliation is not null
            && request.Reconciliation.ReconciliationStatus != ReconciliationStatus.NoDifferences)
            return false;

        return true;
    }

    private static LineageOnboardingState MapLineage(DatabaseLineageAssessment assessment)
    {
        if (!string.Equals(assessment.Discovery.ConsistencyStatus, "CONSISTENT", StringComparison.Ordinal))
        {
            return assessment.Discovery.ConsistencyReason switch
            {
                "BLOCKED_HISTORY_WITHOUT_REPO" => LineageOnboardingState.BlockedHistoryWithoutRepo,
                "BLOCKED_EF_SEQUENCE_DIVERGED" or "BLOCKED_EF_REPOSITORY_INCONSISTENT"
                    or "BLOCKED_BASELINE_REQUIRED" => LineageOnboardingState.Divergent,
                _ => LineageOnboardingState.Unknown
            };
        }

        return assessment.Scenario switch
        {
            "NEW_EF" or "EXISTING_EF" when assessment.SourceKind == "EF" =>
                LineageOnboardingState.ConsistentEf,
            "EXISTING_SQL" when assessment.SourceKind == "SQL" => LineageOnboardingState.LegacySql,
            _ => LineageOnboardingState.Unknown
        };
    }

    private static void AddLineageReason(LineageOnboardingState state, ISet<string> reasons)
    {
        if (state == LineageOnboardingState.BlockedHistoryWithoutRepo)
            reasons.Add(DatabaseOnboardingReasons.LineageBlockedHistoryWithoutRepo);
        else if (state == LineageOnboardingState.Divergent)
            reasons.Add(DatabaseOnboardingReasons.LineageDivergent);
        else if (state == LineageOnboardingState.Unknown)
            reasons.Add(DatabaseOnboardingReasons.LineageUnknown);
    }
}
