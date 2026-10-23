namespace DatabaseReleaseQualification;

public sealed partial class EfEvidencePreparationV1
{
    // Host-only persistence. No command, workflow step or SQL adapter is added.
    // The original qualified-release commit route remains unchanged.
    public async Task<EfAdoptionCommitResultV1> CommitAdoptionAsync(EfAdoptionCandidateV1 candidate,
        string trustedHash, EfEvidenceScopeV1 scope, EvidenceReference freshPre,
        EvidenceReference currentSource, EvidenceReference approval, DateTimeOffset now,
        CancellationToken token = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        DateTimeOffset Clock() => now + System.Diagnostics.Stopwatch.GetElapsedTime(started);
        try
        {
            Require(provider is not null && certificationStore is not null, "ADOPTION_CERTIFICATION_STORE_REQUIRED");
            scope = Clone(scope); freshPre = Clone(freshPre); currentSource = Clone(currentSource); approval = Clone(approval);
            Require(Hash(trustedHash) && candidate.Sha256 == trustedHash && Hashing.Sha256(candidate.Json) == trustedHash,
                "ADOPTION_CANDIDATE_HASH_MISMATCH");
            var p = candidate.Read(); var used = new EvidenceLog(); used.AddRange(p.Evidence);
            Require(p.Version == 1 && p.Scope == scope && now < p.ExpiresAtUtc && p.Review is not null
                && p.Governed.AdoptionPolicy is { Version: 1, RegistrationState: "REGISTERED", StructuralState: "BASELINE_REQUIRED" }
                && p.Governed.Authority.CertifiedBaseline is null && p.Governed.Authority.Lifecycle == "EXISTING"
                && p.Governed.Authority.Scenario == "EXISTING_EF" && p.Governed.Authority.Target.Environment == "TEST",
                "ADOPTION_CANDIDATE_NOT_ELIGIBLE");
            var currentGovernance = await Document<EfGovernedPreparationV1>(scope, p.GovernanceReference, "GOVERNANCE", used, token);
            Require(Equal(currentGovernance, p.Governed), "ADOPTION_GOVERNANCE_CHANGED");
            var source = await Document<EfSourcePreparationV1>(scope, currentSource, "SOURCE", used, token);
            Require(Equal(source.Source, p.Source) && source.Forward.Length == 0 && source.Rollback.Length == 0,
                "ADOPTION_SOURCE_CHANGED");
            var pre = await Document<EfObservationEvidenceV1>(scope, freshPre, "FRESH_PRE", used, token);
            await Observation(scope, pre, "FRESH_PRE", p.Governed.ContentHashContract, used, token);
            var oldState = EfReleasePlannerV1.Observe(p.Pre.Observation, p.Governed.Authority.Target, now);
            var newState = EfReleasePlannerV1.Observe(pre.Observation, p.Governed.Authority.Target, now);
            Require(pre.Observation.CapturedAtUtc >= p.Pre.Observation.CapturedAtUtc
                && (now - pre.Observation.CapturedAtUtc).TotalSeconds <= p.Governed.Policy.MaximumValiditySeconds
                && pre.Observation.Evidence.EvidenceId != p.Pre.Observation.Evidence.EvidenceId
                && Equal(oldState, newState with { Evidence = oldState.Evidence, CapturedAtUtc = oldState.CapturedAtUtc }),
                "ADOPTION_PHYSICAL_STATE_CHANGED");
            var consent = await AdoptionApproval(candidate, approval, now, used, token);
            var decision = new CertificationDecisionEngine().Evaluate(new() {
                Origin = CertificationOrigin.BootstrapApproved, DatabaseLifecycle = "EXISTING",
                ObservedPreSchemaHash = oldState.SchemaHash, LineageStatus = "CONSISTENT",
                CertificationApprovalGranted = consent.Granted, CertificationApprovalReference = approval.EvidenceSha256 });
            Require(decision.ProducesCertifiedState && decision.Decision == CertificationDecision.HumanApproved,
                "ADOPTION_INITIAL_CERTIFICATION_BLOCKED");
            var id = new Guid(Convert.FromHexString(candidate.Sha256[..32])).ToString("D");
            var target = p.Governed.Authority.Target;
            var identity = new DatabaseIdentity(target.ApplicationId, target.Environment, target.DatabaseName);
            var record = CertifiedStateRecordBuilder.Build(new() {
                CertificationId = id, DatabaseIdentity = identity, CanonicalSchema = SchemaCanonicalizer.Canonicalize(p.Pre.Observation.Schema),
                Certification = decision, RegistryProvenance = p.Governed.AdoptionPolicy!.Registry,
                LineageStatus = LineageOnboardingState.ConsistentEf, LineageEvidenceHash = candidate.Sha256,
                CreatedAtUtc = p.PreparedAtUtc });
            await provider!.VerifyCurrentAsync(scope, used.Select(Clone).ToArray(), token);
            token.ThrowIfCancellationRequested();
            var currentTime = Clock();
            Require(currentTime < consent.ExpiresAtUtc && currentTime < p.ExpiresAtUtc, "ADOPTION_APPROVAL_EXPIRED");
            var committed = new CertificationCommitProtocol(certificationStore!).Commit(new() {
                OperationId = p.OperationId, DatabaseIdentity = identity, ExpectedPredecessor = null,
                Candidate = record, EvidenceReferences = CertificationCommitProtocol.CollectEvidence(record),
                ReleaseOrCorrelationId = p.OperationId, Authorization = consent.Actor,
                Certification = decision, RequestedAtUtc = currentTime });
            var confirmed = committed.Status is CertificationCommitStatus.Committed or CertificationCommitStatus.IdempotentReplay
                or CertificationCommitStatus.RecoveredAndCommitted;
            return new(confirmed && committed.RecordPersistence == CommitOutcomeState.ConfirmedYes
                    && committed.PointerAdvance == CommitOutcomeState.ConfirmedYes && !committed.RecoveryRequired
                    ? "GOVERNANCE_TRANSITION_REQUIRED"
                    : committed.PointerAdvance == CommitOutcomeState.Unknown || committed.RecordPersistence == CommitOutcomeState.Unknown
                        || committed.RecoveryRequired ? "ADOPTION_COMMIT_UNCERTAIN" : "ADOPTION_NOT_COMMITTED",
                committed, committed.Reasons.ToArray());
        }
        catch (Block e) { return new("BLOCKED", null, [e.Message]); }
        catch { return new("BLOCKED", null, ["ADOPTION_COMMIT_EVIDENCE_UNVERIFIABLE"]); }
    }
}
