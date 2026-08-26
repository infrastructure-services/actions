using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public enum CertificationOrigin
{
    BootstrapApproved,
    QualifiedRelease,
    DriftReconciliation,
    BreakGlassReconciliation
}

public enum CertificationDecision
{
    Automatic,
    HumanApproved,
    ReadyForHumanApproval,
    Blocked
}

public enum CertificationApprovalRequirement
{
    None,
    Human,
    Dba
}

public enum DeploymentAuthorizationRequirement
{
    AutomaticPolicy,
    HumanApproval,
    DbaApproval
}

public enum DeploymentAuthorizationDecision
{
    Authorized,
    NotAuthorized,
    Blocked
}

public static class CertificationDecisionReasons
{
    public const string QualifiedReleaseTransition = "QUALIFIED_RELEASE_TRANSITION";
    public const string QualifiedInitialReleaseTransition = "QUALIFIED_INITIAL_RELEASE_TRANSITION";
    public const string InitialBaselineApproval = "INITIAL_BASELINE_APPROVAL";
    public const string DriftReconciliation = "DRIFT_RECONCILIATION";
    public const string BreakGlassReconciliation = "BREAK_GLASS_RECONCILIATION";
    public const string CertifiedPreRequired = "CERTIFIED_PRE_REQUIRED";
    public const string PreStateDriftDetected = "PRE_STATE_DRIFT_DETECTED";
    public const string QualifiedPreMismatch = "QUALIFIED_PRE_MISMATCH";
    public const string QualifiedReleaseRequired = "QUALIFIED_RELEASE_REQUIRED";
    public const string ExactQualifiedReleaseRequired = "EXACT_QUALIFIED_RELEASE_REQUIRED";
    public const string SuccessfulExecutionRequired = "SUCCESSFUL_EXECUTION_REQUIRED";
    public const string QualifiedPostMismatch = "QUALIFIED_POST_MISMATCH";
    public const string InvalidRollback = "INVALID_ROLLBACK";
    public const string InsufficientSensitiveAnalysisConfidence = "INSUFFICIENT_SENSITIVE_ANALYSIS_CONFIDENCE";
    public const string LineageNotEligible = "LINEAGE_NOT_ELIGIBLE";
    public const string DriftReconciliationRequired = "DRIFT_RECONCILIATION_REQUIRED";
    public const string BreakGlassReconciliationRequired = "BREAK_GLASS_RECONCILIATION_REQUIRED";
    public const string HumanApprovalEvidenceRequired = "HUMAN_APPROVAL_EVIDENCE_REQUIRED";
    public const string RequiredApproverNotSatisfied = "REQUIRED_APPROVER_NOT_SATISFIED";
    public const string BootstrapAlreadyCertified = "BOOTSTRAP_ALREADY_CERTIFIED";
    public const string InvalidCertificationEvidence = "INVALID_CERTIFICATION_EVIDENCE";
    public const string ReleaseQualificationGateNotPassed = "RELEASE_QUALIFICATION_GATE_NOT_PASSED";
    public const string DeploymentAuthorizationRequired = "DEPLOYMENT_AUTHORIZATION_REQUIRED";
    public const string DeploymentAuthorizationBlocked = "DEPLOYMENT_AUTHORIZATION_BLOCKED";
    public const string DeploymentAuthorizationReferenceRequired = "DEPLOYMENT_AUTHORIZATION_REFERENCE_REQUIRED";
    public const string ControlledInitialPreRequired = "CONTROLLED_INITIAL_PRE_REQUIRED";
}

public sealed class CertificationPolicy
{
    public string PolicyId { get; init; } = "CERTIFICATION_POLICY_V1";
    public CertificationApprovalRequirement BootstrapApproval { get; init; } = CertificationApprovalRequirement.Human;
    public CertificationApprovalRequirement DriftReconciliationApproval { get; init; } = CertificationApprovalRequirement.Dba;
    public CertificationApprovalRequirement BreakGlassApproval { get; init; } = CertificationApprovalRequirement.Dba;
}

public sealed class CertificationPolicyEvidence
{
    public required string PolicyId { get; init; }
    public CertificationApprovalRequirement BootstrapApproval { get; init; }
    public CertificationApprovalRequirement DriftReconciliationApproval { get; init; }
    public CertificationApprovalRequirement BreakGlassApproval { get; init; }
}

public sealed class DeploymentAuthorizationEvidence
{
    public required string PolicyId { get; init; }
    public RiskLevel Risk { get; init; }
    public DeploymentAuthorizationRequirement Requirement { get; init; }
    public DeploymentAuthorizationDecision Decision { get; init; }
    public string? AuthorizationReference { get; init; }
    public bool ReleaseQualificationGatePassed { get; init; }
    public AnalysisConfidence AnalysisConfidence { get; init; } = AnalysisConfidence.Complete;
    public bool SensitiveChange { get; init; }
    public SchemaRollbackValidity SchemaRollbackValidity { get; init; } = SchemaRollbackValidity.Valid;
    public DataRollbackValidity DataRollbackValidity { get; init; } = DataRollbackValidity.NotApplicable;
    public RollbackCapability RollbackCapability { get; init; } = RollbackCapability.FullReversible;
}

public sealed class CertificationRequest
{
    public required CertificationOrigin Origin { get; init; }
    public string DatabaseLifecycle { get; init; } = DatabaseLifecycles.Existing;
    public bool InitialPreStateValidated { get; init; }
    public string? CertifiedPreSchemaHash { get; init; }
    public required string ObservedPreSchemaHash { get; init; }
    public string? QualifiedPreSchemaHash { get; init; }
    public string? QualifiedPostSchemaHash { get; init; }
    public string? ObservedPostSchemaHash { get; init; }
    public string? ReleaseId { get; init; }
    public string? QualifiedPayloadHash { get; init; }
    public string? ExecutedPayloadHash { get; init; }
    public string? QualifiedForwardHash { get; init; }
    public string? ExecutedForwardHash { get; init; }
    public string? QualifiedRollbackHash { get; init; }
    public string? VerifiedRollbackHash { get; init; }
    public bool QualifiedRelease { get; init; }
    public bool ExecutionSucceeded { get; init; }
    public string DriftStatus { get; init; } = DatabaseDriftStatuses.Match;
    public string LineageStatus { get; init; } = "CONSISTENT";
    public bool OutOfBandChangeDetected { get; init; }
    public bool ReconciliationCompleted { get; init; }
    public DeploymentAuthorizationEvidence? DeploymentAuthorization { get; init; }
    public CertificationApprovalRequirement CertificationApprovalGranted { get; init; } = CertificationApprovalRequirement.None;
    public string? CertificationApprovalReference { get; init; }
}

public sealed class CertificationEvidence
{
    public int FormatVersion { get; init; } = 1;
    public required string PolicyId { get; init; }
    public CertificationPolicyEvidence? CertificationPolicy { get; init; }
    public required CertificationOrigin Origin { get; init; }
    public required string DatabaseLifecycle { get; init; }
    public bool InitialPreStateValidated { get; init; }
    public bool ControlledInitialCertification { get; init; }
    public required CertificationDecision Decision { get; init; }
    public required string DecisionReason { get; init; }
    public string? PreviousCertifiedSchemaHash { get; init; }
    public required string ObservedPreSchemaHash { get; init; }
    public string? QualifiedPreSchemaHash { get; init; }
    public string? QualifiedPostSchemaHash { get; init; }
    public string? ObservedPostSchemaHash { get; init; }
    public string? NextCertifiedSchemaHash { get; init; }
    public string? ReleaseId { get; init; }
    public string? QualifiedPayloadHash { get; init; }
    public string? ExecutedPayloadHash { get; init; }
    public string? QualifiedForwardHash { get; init; }
    public string? ExecutedForwardHash { get; init; }
    public string? QualifiedRollbackHash { get; init; }
    public string? VerifiedRollbackHash { get; init; }
    public bool CertifiedPreAvailable { get; init; }
    public bool PreMatchesCertified { get; init; }
    public bool QualifiedPreMatchesCertified { get; init; }
    public bool InitialPreMatchesQualified { get; init; }
    public bool QualifiedRelease { get; init; }
    public bool ExactQualifiedRelease { get; init; }
    public bool ExecutionSucceeded { get; init; }
    public bool PostMatchesQualified { get; init; }
    public required string DriftStatus { get; init; }
    public required string LineageStatus { get; init; }
    public bool OutOfBandChangeDetected { get; init; }
    public bool ReconciliationCompleted { get; init; }
    public DeploymentAuthorizationRequirement? DeploymentAuthorizationRequirement { get; init; }
    public DeploymentAuthorizationDecision? DeploymentAuthorizationDecision { get; init; }
    public string? AuthorizationReference { get; init; }
    public bool ReleaseQualificationGatePassed { get; init; }
    public RiskLevel? FinalRisk { get; init; }
    public AnalysisConfidence? AnalysisConfidence { get; init; }
    public bool? SensitiveChange { get; init; }
    public SchemaRollbackValidity? SchemaRollbackValidity { get; init; }
    public DataRollbackValidity? DataRollbackValidity { get; init; }
    public RollbackCapability? RollbackCapability { get; init; }
    public bool ChainOfTrustIntact { get; init; }
    public bool AutomaticEligible { get; init; }
    public CertificationApprovalRequirement CertificationApprovalRequired { get; init; }
    public CertificationApprovalRequirement CertificationApprovalGranted { get; init; }
    public string? CertificationApprovalReference { get; init; }
}

public sealed class CertificationResult
{
    public required CertificationDecision Decision { get; init; }
    public required string DecisionReason { get; init; }
    public required CertificationOrigin Origin { get; init; }
    public string? NextCertifiedSchemaHash { get; init; }
    public required CertificationEvidence Evidence { get; init; }
    public bool ProducesCertifiedState => Decision is CertificationDecision.Automatic or CertificationDecision.HumanApproved
        && NextCertifiedSchemaHash is not null;
}

public sealed class CertificationDecisionEngine(CertificationPolicy? policy = null)
{
    private static readonly Regex Sha256 = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);
    private readonly CertificationPolicy _policy = policy ?? new CertificationPolicy();

    public CertificationResult Evaluate(CertificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!HasValidCommonEvidence(request))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.InvalidCertificationEvidence);

        if (!string.Equals(request.LineageStatus, "CONSISTENT", StringComparison.Ordinal))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.LineageNotEligible);

        return request.Origin == CertificationOrigin.QualifiedRelease
            ? EvaluateDerived(request)
            : EvaluateHumanCertification(request);
    }

    private CertificationResult EvaluateDerived(CertificationRequest request)
    {
        var controlledInitial = IsControlledInitial(request);
        if (request.CertifiedPreSchemaHash is null && !controlledInitial)
        {
            var reason = request.DatabaseLifecycle == DatabaseLifecycles.New
                ? CertificationDecisionReasons.ControlledInitialPreRequired
                : CertificationDecisionReasons.CertifiedPreRequired;
            return Result(request, CertificationDecision.Blocked,
                reason);
        }

        if (request.OutOfBandChangeDetected
            || !string.Equals(request.DriftStatus, DatabaseDriftStatuses.Match, StringComparison.Ordinal))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.DriftReconciliationRequired);

        if (!controlledInitial
            && !SameHash(request.CertifiedPreSchemaHash, request.ObservedPreSchemaHash))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.PreStateDriftDetected);

        if (!request.QualifiedRelease)
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.QualifiedReleaseRequired);

        if (!(controlledInitial
                ? SameHash(request.ObservedPreSchemaHash, request.QualifiedPreSchemaHash)
                : SameHash(request.CertifiedPreSchemaHash, request.QualifiedPreSchemaHash)))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.QualifiedPreMismatch);

        var authorization = request.DeploymentAuthorization;
        if (authorization is null)
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.DeploymentAuthorizationRequired);

        if (!authorization.ReleaseQualificationGatePassed)
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.ReleaseQualificationGateNotPassed);

        if (!RollbackIsAcceptable(authorization))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.InvalidRollback);

        if (authorization.SensitiveChange
            && authorization.AnalysisConfidence == AnalysisConfidence.Insufficient)
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.InsufficientSensitiveAnalysisConfidence);

        if (!ExactQualifiedRelease(request))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.ExactQualifiedReleaseRequired);

        if (authorization.Decision == DeploymentAuthorizationDecision.Blocked)
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.DeploymentAuthorizationBlocked);

        if (authorization.Decision != DeploymentAuthorizationDecision.Authorized)
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.DeploymentAuthorizationRequired);

        if (authorization.Requirement != DeploymentAuthorizationRequirement.AutomaticPolicy
            && string.IsNullOrWhiteSpace(authorization.AuthorizationReference))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.DeploymentAuthorizationReferenceRequired);

        if (!request.ExecutionSucceeded)
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.SuccessfulExecutionRequired);

        if (!SameHash(request.QualifiedPostSchemaHash, request.ObservedPostSchemaHash))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.QualifiedPostMismatch);

        return Result(request, CertificationDecision.Automatic,
            controlledInitial
                ? CertificationDecisionReasons.QualifiedInitialReleaseTransition
                : CertificationDecisionReasons.QualifiedReleaseTransition,
            request.QualifiedPostSchemaHash);
    }

    private CertificationResult EvaluateHumanCertification(CertificationRequest request)
    {
        if (request.Origin == CertificationOrigin.BootstrapApproved)
        {
            if (request.CertifiedPreSchemaHash is not null)
                return Result(request, CertificationDecision.Blocked,
                    CertificationDecisionReasons.BootstrapAlreadyCertified);

            return HumanDecision(request, CertificationDecisionReasons.InitialBaselineApproval,
                request.ObservedPreSchemaHash, _policy.BootstrapApproval);
        }

        if (request.Origin == CertificationOrigin.DriftReconciliation)
        {
            if (!request.ReconciliationCompleted)
                return Result(request, CertificationDecision.Blocked,
                    CertificationDecisionReasons.DriftReconciliationRequired);

            return HumanDecision(request, CertificationDecisionReasons.DriftReconciliation,
                request.ObservedPostSchemaHash ?? request.ObservedPreSchemaHash,
                _policy.DriftReconciliationApproval);
        }

        if (!request.ReconciliationCompleted)
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.BreakGlassReconciliationRequired);

        return HumanDecision(request, CertificationDecisionReasons.BreakGlassReconciliation,
            request.ObservedPostSchemaHash ?? request.ObservedPreSchemaHash,
            _policy.BreakGlassApproval);
    }

    private CertificationResult HumanDecision(
        CertificationRequest request,
        string reason,
        string? nextCertifiedSchemaHash,
        CertificationApprovalRequirement requiredApproval)
    {
        if (requiredApproval == CertificationApprovalRequirement.None)
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.InvalidCertificationEvidence);

        if (request.CertificationApprovalGranted == CertificationApprovalRequirement.None)
            return Result(request, CertificationDecision.ReadyForHumanApproval, reason,
                requiredApproval: requiredApproval);

        if (!ApprovalSatisfies(requiredApproval, request.CertificationApprovalGranted))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.RequiredApproverNotSatisfied,
                requiredApproval: requiredApproval);

        if (string.IsNullOrWhiteSpace(request.CertificationApprovalReference))
            return Result(request, CertificationDecision.Blocked,
                CertificationDecisionReasons.HumanApprovalEvidenceRequired,
                requiredApproval: requiredApproval);

        return Result(request, CertificationDecision.HumanApproved, reason,
            nextCertifiedSchemaHash, requiredApproval);
    }

    private CertificationResult Result(
        CertificationRequest request,
        CertificationDecision decision,
        string reason,
        string? nextCertifiedSchemaHash = null,
        CertificationApprovalRequirement requiredApproval = CertificationApprovalRequirement.None)
    {
        var certifiedPreAvailable = request.CertifiedPreSchemaHash is not null;
        var controlledInitial = IsControlledInitial(request);
        var preMatchesCertified = SameHash(request.CertifiedPreSchemaHash, request.ObservedPreSchemaHash);
        var qualifiedPreMatchesCertified = SameHash(request.CertifiedPreSchemaHash, request.QualifiedPreSchemaHash);
        var initialPreMatchesQualified = controlledInitial
            && SameHash(request.ObservedPreSchemaHash, request.QualifiedPreSchemaHash);
        var exactQualifiedRelease = ExactQualifiedRelease(request);
        var postMatchesQualified = SameHash(request.QualifiedPostSchemaHash, request.ObservedPostSchemaHash);
        var authorization = request.DeploymentAuthorization;
        var deploymentAuthorized = authorization is not null
            && authorization.ReleaseQualificationGatePassed
            && authorization.Decision == DeploymentAuthorizationDecision.Authorized
            && (authorization.Requirement == DeploymentAuthorizationRequirement.AutomaticPolicy
                || !string.IsNullOrWhiteSpace(authorization.AuthorizationReference));
        var trustedPre = controlledInitial
            ? request.InitialPreStateValidated && initialPreMatchesQualified
            : certifiedPreAvailable && preMatchesCertified && qualifiedPreMatchesCertified;
        var chainIntact = request.Origin == CertificationOrigin.QualifiedRelease
            && trustedPre
            && request.QualifiedRelease
            && exactQualifiedRelease
            && request.ExecutionSucceeded
            && postMatchesQualified
            && string.Equals(request.DriftStatus, DatabaseDriftStatuses.Match, StringComparison.Ordinal)
            && string.Equals(request.LineageStatus, "CONSISTENT", StringComparison.Ordinal)
            && !request.OutOfBandChangeDetected
            && deploymentAuthorized
            && authorization is not null
            && RollbackIsAcceptable(authorization)
            && !(authorization.SensitiveChange
                && authorization.AnalysisConfidence == AnalysisConfidence.Insufficient);
        var automaticEligible = decision == CertificationDecision.Automatic && chainIntact;

        var evidence = new CertificationEvidence
        {
            PolicyId = authorization?.PolicyId ?? _policy.PolicyId,
            CertificationPolicy = request.Origin == CertificationOrigin.QualifiedRelease
                ? null
                : new CertificationPolicyEvidence
                {
                    PolicyId = _policy.PolicyId,
                    BootstrapApproval = _policy.BootstrapApproval,
                    DriftReconciliationApproval = _policy.DriftReconciliationApproval,
                    BreakGlassApproval = _policy.BreakGlassApproval
                },
            Origin = request.Origin,
            DatabaseLifecycle = request.DatabaseLifecycle,
            InitialPreStateValidated = request.InitialPreStateValidated,
            ControlledInitialCertification = controlledInitial,
            Decision = decision,
            DecisionReason = reason,
            PreviousCertifiedSchemaHash = request.CertifiedPreSchemaHash,
            ObservedPreSchemaHash = request.ObservedPreSchemaHash,
            QualifiedPreSchemaHash = request.QualifiedPreSchemaHash,
            QualifiedPostSchemaHash = request.QualifiedPostSchemaHash,
            ObservedPostSchemaHash = request.ObservedPostSchemaHash,
            NextCertifiedSchemaHash = nextCertifiedSchemaHash,
            ReleaseId = request.ReleaseId,
            QualifiedPayloadHash = request.QualifiedPayloadHash,
            ExecutedPayloadHash = request.ExecutedPayloadHash,
            QualifiedForwardHash = request.QualifiedForwardHash,
            ExecutedForwardHash = request.ExecutedForwardHash,
            QualifiedRollbackHash = request.QualifiedRollbackHash,
            VerifiedRollbackHash = request.VerifiedRollbackHash,
            CertifiedPreAvailable = certifiedPreAvailable,
            PreMatchesCertified = preMatchesCertified,
            QualifiedPreMatchesCertified = qualifiedPreMatchesCertified,
            InitialPreMatchesQualified = initialPreMatchesQualified,
            QualifiedRelease = request.QualifiedRelease,
            ExactQualifiedRelease = exactQualifiedRelease,
            ExecutionSucceeded = request.ExecutionSucceeded,
            PostMatchesQualified = postMatchesQualified,
            DriftStatus = request.DriftStatus,
            LineageStatus = request.LineageStatus,
            OutOfBandChangeDetected = request.OutOfBandChangeDetected,
            ReconciliationCompleted = request.ReconciliationCompleted,
            DeploymentAuthorizationRequirement = authorization?.Requirement,
            DeploymentAuthorizationDecision = authorization?.Decision,
            AuthorizationReference = authorization?.AuthorizationReference,
            ReleaseQualificationGatePassed = authorization?.ReleaseQualificationGatePassed ?? false,
            FinalRisk = authorization?.Risk,
            AnalysisConfidence = authorization?.AnalysisConfidence,
            SensitiveChange = authorization?.SensitiveChange,
            SchemaRollbackValidity = authorization?.SchemaRollbackValidity,
            DataRollbackValidity = authorization?.DataRollbackValidity,
            RollbackCapability = authorization?.RollbackCapability,
            ChainOfTrustIntact = chainIntact,
            AutomaticEligible = automaticEligible,
            CertificationApprovalRequired = requiredApproval,
            CertificationApprovalGranted = request.CertificationApprovalGranted,
            CertificationApprovalReference = request.CertificationApprovalReference
        };

        return new CertificationResult
        {
            Decision = decision,
            DecisionReason = reason,
            Origin = request.Origin,
            NextCertifiedSchemaHash = nextCertifiedSchemaHash,
            Evidence = evidence
        };
    }

    private static bool HasValidCommonEvidence(CertificationRequest request)
    {
        if (request.DatabaseLifecycle is not (DatabaseLifecycles.New or DatabaseLifecycles.Existing)) return false;
        if (request.InitialPreStateValidated && request.DatabaseLifecycle != DatabaseLifecycles.New) return false;
        if (!IsHash(request.ObservedPreSchemaHash)) return false;
        if (request.CertifiedPreSchemaHash is not null && !IsHash(request.CertifiedPreSchemaHash)) return false;
        if (request.ObservedPostSchemaHash is not null && !IsHash(request.ObservedPostSchemaHash)) return false;
        if (request.DeploymentAuthorization is not null
            && string.IsNullOrWhiteSpace(request.DeploymentAuthorization.PolicyId)) return false;

        if (request.Origin != CertificationOrigin.QualifiedRelease) return true;
        return IsHash(request.QualifiedPreSchemaHash)
            && IsHash(request.QualifiedPostSchemaHash)
            && IsHash(request.QualifiedPayloadHash)
            && IsHash(request.ExecutedPayloadHash)
            && IsHash(request.QualifiedForwardHash)
            && IsHash(request.ExecutedForwardHash)
            && IsHash(request.QualifiedRollbackHash)
            && IsHash(request.VerifiedRollbackHash)
            && !string.IsNullOrWhiteSpace(request.ReleaseId);
    }

    private static bool RollbackIsAcceptable(DeploymentAuthorizationEvidence authorization) =>
        authorization.SchemaRollbackValidity != SchemaRollbackValidity.Invalid
        && authorization.DataRollbackValidity != DataRollbackValidity.Invalid
        && authorization.RollbackCapability is RollbackCapability.FullReversible
            or RollbackCapability.SchemaOnly
            or RollbackCapability.ForwardFixOnly
            or RollbackCapability.RestoreRequired;

    private static bool ExactQualifiedRelease(CertificationRequest request) =>
        SameHash(request.QualifiedPayloadHash, request.ExecutedPayloadHash)
        && SameHash(request.QualifiedForwardHash, request.ExecutedForwardHash)
        && SameHash(request.QualifiedRollbackHash, request.VerifiedRollbackHash);

    private static bool SameHash(string? first, string? second) =>
        first is not null && second is not null
        && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static bool IsHash(string? value) => value is not null && Sha256.IsMatch(value);

    private static bool IsControlledInitial(CertificationRequest request) =>
        request.Origin == CertificationOrigin.QualifiedRelease
        && request.DatabaseLifecycle == DatabaseLifecycles.New
        && request.CertifiedPreSchemaHash is null
        && request.InitialPreStateValidated;

    private static bool ApprovalSatisfies(
        CertificationApprovalRequirement required,
        CertificationApprovalRequirement granted) => required switch
    {
        CertificationApprovalRequirement.Human => granted is CertificationApprovalRequirement.Human
            or CertificationApprovalRequirement.Dba,
        CertificationApprovalRequirement.Dba => granted == CertificationApprovalRequirement.Dba,
        _ => false
    };
}
