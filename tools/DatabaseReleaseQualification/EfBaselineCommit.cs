namespace DatabaseReleaseQualification;

public sealed record EfBaselineCommitContextV1(int Version, EfEvidenceScopeV1 Scope, string OperationId,
    string PlanHash, string CertificationId, ExpectedCertificationPointer? ExpectedPredecessor,
    EvidenceReference TransitionAuthorization, RegistryProvenance Registry, EvidenceReference Lineage,
    DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresAtUtc, CommitAuthorizationContext Authorization);
public sealed record EfCertifiedLineageV1(EfEvidenceScopeV1 Scope, string OperationId, string PlanHash,
    string[] MigrationIds, string PostSchemaHash);
public sealed record EfBaselineCommitResultV1(string Status, CertificationCommitResult? Commit, string[] Reasons);

public sealed partial class EfEvidencePreparationV1
{
    // No store ships in the operational host. A separately authorized host may
    // register the existing commit store; tests use its existing in-memory model.
    // This never modifies Registry or changes a target's governed lifecycle.
    public async Task<EfBaselineCommitResultV1> CommitBaselineAsync(EfReleasePlanV1 plan, string trustedHash,
        EfEvidenceScopeV1 scope, EvidenceReference execution, EvidenceReference actualPost,
        EvidenceReference transitionAuthorization, EvidenceReference commitAuthorization,
        DateTimeOffset now, CancellationToken token = default)
    {
        try
        {
            Require(provider is not null && certificationStore is not null, "CERTIFICATION_STORE_NOT_ACCREDITED");
            scope = Clone(scope);
            var candidate = await ProposeBaselineTransitionAsync(plan, trustedHash, scope, execution, actualPost,
                transitionAuthorization, now, token);
            Require(candidate.Status == "BASELINE_TRANSITION_CANDIDATE_NOT_COMMITTED" && candidate.Decision is not null,
                candidate.Reasons.FirstOrDefault() ?? "BASELINE_TRANSITION_BLOCKED");
            var used = new EvidenceLog();
            var (p, receipt, post) = await Post(plan, trustedHash, scope, execution, actualPost, now, used, token);
            var c = await Document<EfBaselineCommitContextV1>(scope, commitAuthorization, "CERTIFICATION_COMMIT_AUTHORIZATION", used, token);
            Require(c.Version == 1 && c.Scope == scope && c.OperationId == p.OperationId && c.PlanHash == trustedHash
                && c.TransitionAuthorization == transitionAuthorization && c.NotBeforeUtc <= now && now < c.ExpiresAtUtc,
                "CERTIFICATION_COMMIT_BINDING_MISMATCH");
            var lineage = await Document<EfCertifiedLineageV1>(scope, c.Lineage, "CERTIFIED_EF_LINEAGE", used, token);
            Require(lineage.Scope == scope && lineage.OperationId == p.OperationId && lineage.PlanHash == trustedHash
                && lineage.MigrationIds.SequenceEqual(post.AppliedMigrationIds)
                && lineage.PostSchemaHash == candidate.ProposedSchemaHash, "CERTIFICATION_LINEAGE_MISMATCH");
            var identity = new DatabaseIdentity(p.Authority.Target.ApplicationId, p.Authority.Target.Environment, p.Authority.Target.DatabaseName);
            CertifiedStateRecord? previous = null;
            if (c.ExpectedPredecessor is { } predecessor)
            {
                previous = certificationStore!.GetCertifiedById(predecessor.CertificationId);
                Require(previous is not null && previous.DatabaseIdentity == identity
                    && previous.CertificationEvidenceHash == predecessor.CertificationEvidenceHash
                    && previous.CanonicalSchemaHash == p.Authority.CertifiedBaseline?.SchemaHash
                    && p.Authority.CertifiedBaseline.Provenance.Document.EvidenceSha256
                        == Hashing.Sha256(System.Text.Json.JsonSerializer.Serialize(previous, JsonDefaults.Compact))
                    && p.Authority.Scenario == "EXISTING_EF", "CERTIFICATION_PREDECESSOR_MISMATCH");
            }
            else Require(p.Authority.Scenario == "NEW_EF" && p.Authority.CertifiedBaseline is null,
                "CERTIFICATION_PREDECESSOR_REQUIRED");
            var d = candidate.Decision!;
            var certification = new CertificationResult { Decision = d.Decision, DecisionReason = d.DecisionReason,
                Origin = d.Origin, NextCertifiedSchemaHash = d.NextCertifiedSchemaHash, Evidence = d };
            var record = CertifiedStateRecordBuilder.Build(new() {
                CertificationId = c.CertificationId, PreviousCertification = previous, DatabaseIdentity = identity,
                CanonicalSchema = SchemaCanonicalizer.Canonicalize(post.Schema), Certification = certification,
                RegistryProvenance = c.Registry, LineageStatus = LineageOnboardingState.ConsistentEf,
                LineageEvidenceHash = c.Lineage.EvidenceSha256, CreatedAtUtc = receipt.CompletedAtUtc,
                QualifiedRelease = QualifiedReleaseCertificationReference.FromPayload(p.Payload, null,
                    p.Acquisition!.QualificationEvidence.EvidenceSha256, null, transitionAuthorization.EvidenceSha256,
                    null, execution.EvidenceSha256, candidate.ProposedSchemaHash!) });
            used.Add(transitionAuthorization);
            await provider!.VerifyCurrentAsync(scope, used.Select(Clone).ToArray(), token);
            token.ThrowIfCancellationRequested();
            var committed = new CertificationCommitProtocol(certificationStore!).Commit(new() {
                OperationId = p.OperationId, DatabaseIdentity = identity, ExpectedPredecessor = c.ExpectedPredecessor,
                Candidate = record, EvidenceReferences = CertificationCommitProtocol.CollectEvidence(record),
                ReleaseOrCorrelationId = p.Payload.ReleaseId, Authorization = c.Authorization,
                Certification = certification, RequestedAtUtc = now });
            return new(committed.PointerAdvance == CommitOutcomeState.ConfirmedYes ? "BASELINE_COMMITTED_GOVERNANCE_TRANSITION_REQUIRED"
                : "BASELINE_NOT_COMMITTED", committed, committed.Reasons.ToArray());
        }
        catch (Block e) { return new("BLOCKED", null, [e.Message]); }
        catch { return new("BLOCKED", null, ["CERTIFICATION_EVIDENCE_NOT_VERIFIABLE"]); }
    }
}
