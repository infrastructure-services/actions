namespace DatabaseReleaseQualification;

// This port is deliberately assembly-owned. Application JSON and arbitrary
// implementations supplied by a caller cannot register a corporate provider.
// Acquisition must authenticate review, current incarnation, independent target
// execution and source freshness; a rehearsal POST2 is not execution evidence.
internal interface ILegacyCertificationEvidenceProviderV1
{
    Task<LegacyCertificationEvidenceV1> AcquireAsync(string reference, CancellationToken token);
}

internal sealed record LegacyCertificationReviewV1(string CandidateHash, string Author,
    string Reviewer, string Policy, string Reference, DateTimeOffset ExpiresAtUtc,
    string Incarnation, string EndpointReference);

internal sealed record LegacyExecutedReleaseV1(string OperationId, string TargetId, string Incarnation,
    string PayloadHash, string ForwardHash, string RollbackHash, string PreHash, string PostHash,
    string ExecutionEvidenceHash, string AuthorizationEvidenceHash, bool Succeeded,
    string? PreDataHash = null, string? PostDataHash = null,
    string? PreSecurityHash = null, string? PostSecurityHash = null);

internal sealed record LegacyCertificationObservationV1(string TargetId, DatabaseIdentity Identity,
    string EndpointReference, string Incarnation, string IncarnationEvidenceHash,
    DateTimeOffset CapturedAtUtc, string SchemaHash, string EvidenceHash,
    string? DataScopeHash = null, string? DataHash = null,
    string? SecurityScopeHash = null, string? SecurityHash = null);

internal sealed record LegacyCertificationEvidenceV1(string OperationId, string CertificationId,
    LegacyAdoptionCandidateV1 Candidate, SchemaSnapshot Current,
    LegacyCertificationReviewV1 Review, ExpectedCertificationPointer? ExpectedPredecessor,
    LegacyRehearsalReceiptV1? Rehearsal = null, ReleasePayloadMetadata? Payload = null,
    LegacyExecutedReleaseV1? Execution = null, DatabaseStateEvaluation? GovernedState = null,
    LegacyOnboardingV1? GovernedOnboarding = null,
    LegacyCertificationObservationV1? Observation = null);

public sealed record LegacyCertificationContinuationResultV1(string Status,
    IReadOnlyList<string> Reasons, CertificationCommitResult? Commit,
    bool CorporateCertified = false, bool ExecutionAuthorized = false);

public sealed class LegacyCertificationContinuationV1
{
    private readonly ILegacyCertificationEvidenceProviderV1 provider;
    private readonly InMemoryCertificationCommitStore store;
    private readonly CertificationCommitProtocol protocol;

    // The offline composition cannot accept an external store. No synthetic
    // capability can leak into a corporate persistence adapter.
    internal LegacyCertificationContinuationV1(ILegacyCertificationEvidenceProviderV1 provider,
        InMemoryCertificationCommitStore store, ICertificationCommitFaultInjector? faults = null)
    { this.provider = provider; this.store = store; protocol = new(store, faults); }

    public static LegacyCertificationContinuationV1 CreateProduction() =>
        throw new LegacyContractException("LEGACY_CERTIFICATION_AUTHORITY_AND_STORE_UNBOUND");

    // Consumes an opaque source reference, never an approval or serialized
    // receipt from the CLI. Only the offline registration above exists today.
    public async Task<LegacyCertificationContinuationResultV1> ContinueAsync(string reference,
        CancellationToken token = default)
    {
        try
        {
            var e = await provider.AcquireAsync(reference, token);
            token.ThrowIfCancellationRequested();
            var c = e.Candidate; var r = e.Review;
            if (c.ContractVersion != 1 || c.CandidateHash != LegacyAdoptionPlanner.CalculateHash(c)
                || r.CandidateHash != c.CandidateHash || string.IsNullOrWhiteSpace(r.Author)
                || string.IsNullOrWhiteSpace(r.Reviewer) || r.Author == r.Reviewer
                || r.ExpiresAtUtc <= DateTimeOffset.UtcNow || r.ExpiresAtUtc > DateTimeOffset.UtcNow.AddHours(1)
                || r.Policy != (e.Rehearsal is null ? "LEGACY_STRUCTURE_ONLY_ADOPTION_V1" : "LEGACY_QUALIFIED_RELEASE_V1")
                || string.IsNullOrWhiteSpace(r.Reference)
                || string.IsNullOrWhiteSpace(r.Incarnation) || r.EndpointReference != c.EndpointReference
                || c.Structure != "COMPLETE" || c.Data != "NOT_VERIFIED" || c.Security != "NOT_VERIFIED"
                || c.Identity.Environment != "TEST" || e.Current.SchemaCoverage != SchemaCoverage.Complete
                || e.Current.UnsupportedSchemaFeatures.Count != 0)
                throw new LegacyContractException("LEGACY_CERTIFICATION_EVIDENCE_UNVERIFIED");
            var canonical = SchemaCanonicalizer.Canonicalize(e.Current);
            var observation = e.Observation;
            if (observation is null || observation.TargetId != c.TargetId || observation.Identity != c.Identity
                || observation.EndpointReference != c.EndpointReference || observation.Incarnation != r.Incarnation
                || observation.SchemaHash != canonical.Sha256
                || !CertifiedStateRecordValidator.IsHash(observation.IncarnationEvidenceHash)
                || !CertifiedStateRecordValidator.IsHash(observation.EvidenceHash)
                || observation.CapturedAtUtc > DateTimeOffset.UtcNow
                || DateTimeOffset.UtcNow - observation.CapturedAtUtc > TimeSpan.FromMinutes(15))
                throw new LegacyContractException("FRESH_IDENTITY_AND_INCARNATION_REQUIRED");
            // An already committed exact release is a no-write outcome only
            // with fresh scoped evidence, a current pointer and governed state.
            // Schema equality alone is insufficient for DATA-only changes.
            if (e.Payload is { } requested && store.GetReceipt(e.OperationId) is null)
            {
                var certified = store.ListCertifiedHistory(c.Identity)
                    .FirstOrDefault(s => s.QualifiedRelease?.ReleaseId == requested.ReleaseId);
                if (certified is not null)
                {
                    var pointer = store.GetCurrentPointer(c.Identity);
                    var exact = certified.QualifiedRelease!;
                    if (exact.PayloadHash != requested.PayloadHash || exact.ForwardHash != requested.ForwardHash
                        || exact.RollbackHash != requested.RollbackHash || pointer?.CertificationId != certified.CertificationId
                        || certified.CanonicalSchemaHash != canonical.Sha256
                        || !MatchesStoredScope(certified, observation)
                        || e.GovernedState is null || e.GovernedOnboarding is null
                        || !VerifyGovernedTransition(e.GovernedState, e.GovernedOnboarding, pointer, r.Incarnation))
                        throw new LegacyContractException("CERTIFIED_RELEASE_REPLAY_OR_DRIFT");
                    return new("NO_CHANGES_ALREADY_CERTIFIED", ["NO_SECOND_SQL_WRITE_AUTHORIZED"], null);
                }
            }
            CertifiedStateRecord? previous = null;
            if (e.ExpectedPredecessor is not null)
            {
                previous = store.GetCertifiedById(e.ExpectedPredecessor.CertificationId);
                if (previous is null || previous.CertificationEvidenceHash != e.ExpectedPredecessor.CertificationEvidenceHash
                    || previous.DatabaseIdentity != c.Identity || previous.CanonicalSchemaHash != c.SchemaHash
                    || previous.RunMetadata.GetValueOrDefault("legacy-incarnation") != r.Incarnation)
                    throw new LegacyContractException("LEGACY_PREDECESSOR_MISMATCH");
                if (store.GetReceipt(e.OperationId)?.Status != CertificationCommitReceiptStatus.Committed
                    && (e.GovernedState is null || e.GovernedOnboarding is null
                        || !VerifyGovernedTransition(e.GovernedState, e.GovernedOnboarding,
                            new(c.Identity, e.ExpectedPredecessor.CertificationId,
                                e.ExpectedPredecessor.CertificationEvidenceHash, e.ExpectedPredecessor.Version), r.Incarnation)))
                    throw new LegacyContractException("GOVERNED_TRANSITION_REQUIRED");
            }
            CertificationResult decision;
            QualifiedReleaseCertificationReference? release = null;
            if (e.Rehearsal is null)
            {
                if (e.Payload is not null || e.Execution is not null || e.ExpectedPredecessor is not null
                    || canonical.Sha256 != c.SchemaHash || observation.DataScopeHash is not null
                    || observation.DataHash is not null || observation.SecurityScopeHash is not null || observation.SecurityHash is not null)
                    throw new LegacyContractException("ADOPTION_STATE_CHANGED");
                decision = new CertificationDecisionEngine().Evaluate(new()
                {
                    Origin = CertificationOrigin.BootstrapApproved,
                    ObservedPreSchemaHash = c.SchemaHash,
                    CertificationApprovalGranted = CertificationApprovalRequirement.Human,
                    CertificationApprovalReference = r.Reference
                });
            }
            else
            {
                var q = e.Rehearsal; var p = e.Payload; var x = e.Execution;
                if (previous is null || p is null || x is null || !LegacyRehearsalReceiptHash.VerifyIntegrity(q)
                    || q.State != LegacyRehearsalStateV1.RehearsalEvaluated || q.Status != "REHEARSAL_COMPLETE"
                    || q.Evaluation?.RecoveryCoverage?.Complete != true || !x.Succeeded
                    || q.TargetId != c.TargetId || x.TargetId != c.TargetId || x.Incarnation != r.Incarnation
                    || p.SourceKind != "SQL" || p.Scenario != "EXISTING_LEGACY"
                    || p.ReleaseId != q.Artifacts.ReleaseId
                    || x.PayloadHash != p.PayloadHash || x.ForwardHash != p.ForwardHash || x.RollbackHash != p.RollbackHash
                    || p.ForwardHash != q.Artifacts.Forward.Sha256 || p.RollbackHash != q.Artifacts.Rollback.Sha256
                    || x.PreHash != c.SchemaHash || x.PostHash != canonical.Sha256
                    || q.Observations[RecoveryPhase.Pre].ObservedSchemaHash != x.PreHash
                    || q.Observations[RecoveryPhase.Post2].ObservedSchemaHash != x.PostHash
                    || !CertifiedStateRecordValidator.IsHash(x.ExecutionEvidenceHash)
                    || !CertifiedStateRecordValidator.IsHash(x.AuthorizationEvidenceHash)
                    || string.IsNullOrWhiteSpace(x.OperationId)
                    || x.ExecutionEvidenceHash == q.ReceiptHash || x.OperationId == q.Runtime.RunId)
                    throw new LegacyContractException("INDEPENDENT_TARGET_EXECUTION_REQUIRED");
                VerifyExecutionDimensions(q, x, observation);
                var authorization = new DeploymentAuthorizationEvidence
                {
                    PolicyId = r.Policy,
                    Decision = DeploymentAuthorizationDecision.Authorized,
                    Requirement = DeploymentAuthorizationRequirement.HumanApproval,
                    AuthorizationReference = r.Reference,
                    ReleaseQualificationGatePassed = true
                };
                decision = new CertificationDecisionEngine().Evaluate(new()
                {
                    Origin = CertificationOrigin.QualifiedRelease,
                    CertifiedPreSchemaHash = previous.CanonicalSchemaHash,
                    ObservedPreSchemaHash = x.PreHash,
                    QualifiedPreSchemaHash = x.PreHash,
                    QualifiedPostSchemaHash = q.Observations[RecoveryPhase.Post2].ObservedSchemaHash,
                    ObservedPostSchemaHash = x.PostHash,
                    ReleaseId = p.ReleaseId,
                    QualifiedPayloadHash = p.PayloadHash,
                    ExecutedPayloadHash = x.PayloadHash,
                    QualifiedForwardHash = p.ForwardHash,
                    ExecutedForwardHash = x.ForwardHash,
                    QualifiedRollbackHash = p.RollbackHash,
                    VerifiedRollbackHash = x.RollbackHash,
                    QualifiedRelease = true,
                    ExecutionSucceeded = true,
                    DeploymentAuthorization = authorization
                });
                release = QualifiedReleaseCertificationReference.FromPayload(p, null, q.ReceiptHash, null,
                    x.AuthorizationEvidenceHash, null, x.ExecutionEvidenceHash, x.PostHash);
            }
            if (!decision.ProducesCertifiedState) throw new LegacyContractException(decision.DecisionReason);
            var record = CertifiedStateRecordBuilder.Build(new()
            {
                CertificationId = e.CertificationId,
                PreviousCertification = previous,
                DatabaseIdentity = c.Identity,
                CanonicalSchema = canonical,
                Certification = decision,
                RegistryProvenance = c.RegistryProvenance,
                LineageStatus = LineageOnboardingState.LegacySql,
                LineageEvidenceHash = LegacyRuntimeEvidenceHash.Hash(new { c.LineageEvidenceHash, r.Incarnation, c.TargetId, c.EndpointReference }),
                QualifiedRelease = release,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                RunMetadata = new Dictionary<string, string>
                {
                    ["authority-domain"] = "OFFLINE_SIMULATION",
                    ["legacy-incarnation"] = r.Incarnation,
                    ["legacy-data-scope"] = observation.DataScopeHash ?? "NOT_REQUIRED",
                    ["legacy-data-post"] = observation.DataHash ?? "NOT_REQUIRED",
                    ["legacy-security-scope"] = observation.SecurityScopeHash ?? "NOT_REQUIRED",
                    ["legacy-security-post"] = observation.SecurityHash ?? "NOT_REQUIRED"
                }
            });
            token.ThrowIfCancellationRequested();
            var result = protocol.Commit(new()
            {
                OperationId = e.OperationId,
                DatabaseIdentity = c.Identity,
                ExpectedPredecessor = e.ExpectedPredecessor,
                Candidate = record,
                EvidenceReferences = CertificationCommitProtocol.CollectEvidence(record),
                ReleaseOrCorrelationId = e.Payload?.ReleaseId ?? c.CandidateHash,
                Authorization = new(r.Reviewer, "OFFLINE_SIMULATION", r.Policy, r.Reference),
                Certification = decision,
                RequestedAtUtc = DateTimeOffset.UtcNow
            });
            return new(result.RecoveryRequired ? "RECONCILIATION_REQUIRED"
                : result.PointerAdvance == CommitOutcomeState.ConfirmedYes ? "GOVERNED_TRANSITION_REQUIRED" : "BLOCKED",
                result.Reasons, result);
        }
        catch (OperationCanceledException) { return new("CANCELLED", ["CERTIFICATION_NOT_ATTEMPTED"], null); }
        catch (LegacyContractException e) { return new("BLOCKED", [e.Code], null); }
        catch (InvalidOperationException e) when (System.Text.RegularExpressions.Regex.IsMatch(e.Message, @"\A[A-Z0-9_;]{1,512}\z"))
        { return new("BLOCKED", e.Message.Split(';'), null); }
        catch (Exception e) when (e is not OutOfMemoryException) { return new("BLOCKED", ["CERTIFICATION_EVIDENCE_INVALID"], null); }
    }

    private static bool MatchesStoredScope(CertifiedStateRecord record, LegacyCertificationObservationV1 observation) =>
        record.RunMetadata.GetValueOrDefault("legacy-data-scope") == (observation.DataScopeHash ?? "NOT_REQUIRED")
        && record.RunMetadata.GetValueOrDefault("legacy-data-post") == (observation.DataHash ?? "NOT_REQUIRED")
        && record.RunMetadata.GetValueOrDefault("legacy-security-scope") == (observation.SecurityScopeHash ?? "NOT_REQUIRED")
        && record.RunMetadata.GetValueOrDefault("legacy-security-post") == (observation.SecurityHash ?? "NOT_REQUIRED");

    internal static void VerifyExecutionDimensions(LegacyRehearsalReceiptV1 rehearsal,
        LegacyExecutedReleaseV1 execution, LegacyCertificationObservationV1 current)
    {
        var coverage = rehearsal.Evaluation?.RecoveryCoverage;
        if (coverage?.Complete != true) throw new LegacyContractException("EXECUTION_RECOVERY_COVERAGE_REQUIRED");
        if (coverage.Data.Required)
        {
            if (rehearsal.DataContract is null || rehearsal.DataEvidence.Count != 4
                || !rehearsal.DataEvidence.TryGetValue(RecoveryPhase.Pre, out var pre)
                || !rehearsal.DataEvidence.TryGetValue(RecoveryPhase.Post2, out var post)
                || rehearsal.DataEvidence.Any(x => !x.Value.Complete
                    || x.Value.Phase != x.Key || x.Value.TargetId != rehearsal.TargetId
                    || x.Value.ScopeHash != rehearsal.DataContract.ScopeHash
                    || !CertifiedStateRecordValidator.IsHash(x.Value.ContentHash))
                || !Enum.GetValues<RecoveryPhase>().All(rehearsal.DataEvidence.ContainsKey)
                || rehearsal.DataEvidence[RecoveryPhase.Pre2].ContentHash != pre.ContentHash
                || rehearsal.DataEvidence[RecoveryPhase.Post1].ContentHash != post.ContentHash
                || execution.PreDataHash != pre.ContentHash || execution.PostDataHash != post.ContentHash
                || current.DataScopeHash != rehearsal.DataContract.ScopeHash || current.DataHash != post.ContentHash)
                throw new LegacyContractException("INDEPENDENT_DATA_EXECUTION_REQUIRED");
        }
        else if (execution.PreDataHash is not null || execution.PostDataHash is not null
            || current.DataScopeHash is not null || current.DataHash is not null)
            throw new LegacyContractException("UNEXPECTED_DATA_EXECUTION_EVIDENCE");
        if (coverage.Security.Required)
        {
            if (execution.PreSecurityHash != coverage.SecurityHashes[RecoveryPhase.Pre]
                || execution.PostSecurityHash != coverage.SecurityHashes[RecoveryPhase.Post2]
                || current.SecurityScopeHash != coverage.SecurityScopeHash
                || current.SecurityHash != coverage.SecurityHashes[RecoveryPhase.Post2])
                throw new LegacyContractException("INDEPENDENT_SECURITY_EXECUTION_REQUIRED");
        }
        else if (execution.PreSecurityHash is not null || execution.PostSecurityHash is not null
            || current.SecurityScopeHash is not null || current.SecurityHash is not null)
            throw new LegacyContractException("UNEXPECTED_SECURITY_EXECUTION_EVIDENCE");
    }

    internal bool VerifyGovernedTransition(DatabaseStateEvaluation registry,
        LegacyOnboardingV1 onboarding, CertificationPointer pointer, string incarnation)
    {
        var record = store.GetCertifiedById(pointer.CertificationId);
        var actual = store.GetCurrentPointer(pointer.DatabaseIdentity);
        return record is not null && actual == pointer && record.DatabaseIdentity == pointer.DatabaseIdentity
            && record.CertificationEvidenceHash == pointer.CertificationEvidenceHash
            && record.RunMetadata.GetValueOrDefault("legacy-incarnation") == incarnation
            && onboarding.Status == "MANAGED" && onboarding.AuthorityReference.Kind == "ONBOARDING_GIT_PR"
            && registry.Target?.CertificationStatus == DatabaseCertificationStatuses.Certified
            && registry.ApplicationId == record.ApplicationId && registry.Environment == record.Environment
            && registry.DatabaseName == record.DatabaseName && registry.CertifiedSchemaHash == record.CanonicalSchemaHash
            && registry.ObservedSchemaHash == record.CanonicalSchemaHash && registry.DriftStatus == DatabaseDriftStatuses.Match;
    }
}
