namespace DatabaseReleaseQualification;

// Authored through the existing workflow repository Git/PR governance. A selector
// is not consent: this document must explicitly authorize the exact package and
// the complete forward/rollback/reapply sequence for a short TEST window.
public sealed record LegacyRehearsalAuthorizationV1(int ContractVersion,
    string AuthorizationId, string TargetId, string PackageIdentity,
    string PreconditionIdentity, string EngineCommit, string Environment,
    string Actor, DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string> ApprovedPhases, bool ExecutionAuthorized,
    string ApprovalReference, string? DataContractIdentity = null);

public sealed record LegacyRehearsalAuthorizationEvidenceV1(
    LegacyRehearsalAuthorizationV1 Grant, LegacyGitDocumentV1 Source);

public sealed class GovernedLegacyRehearsalAuthorityV1(
    LegacyRehearsalAuthorizationV1 grant, LegacyGitDocumentV1 provenance,
    LegacyRepositoryV1 governanceRepository, string governanceCommit,
    string actor, bool explicitlyRequested, Func<CancellationToken, Task> verifyFreshness,
    string? dataContractIdentity = null)
    : ILegacyRehearsalAuthorityV1
{
    private readonly string frozen = LegacyRuntimeEvidenceHash.Hash(grant);
    public LegacyRehearsalAuthorizationEvidenceV1 Evidence => new(grant, provenance);
    // Bind content, not the commit containing the grant itself: including that
    // commit would create a self-referential authorization document.
    public static string PreconditionIdentity(LegacyHandoffV1 h) => LegacyReadinessHash.LengthPrefix(
        h.TargetId, h.Baseline.CertifiedSchemaHash, h.Baseline.RegistryProvenance.RegistryFileSha256,
        h.Governance.SourceProvenance.SourceSha256, h.Onboarding.SourceProvenance.SourceSha256);
    public async Task VerifyAsync(LegacyHandoffV1 handoff, string phase, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        if (!explicitlyRequested || !grant.ExecutionAuthorized || grant.ContractVersion != 1
            || grant.TargetId != handoff.TargetId || grant.PackageIdentity != handoff.PackageIdentity
            || grant.PreconditionIdentity != PreconditionIdentity(handoff)
            || grant.EngineCommit != handoff.EngineCommit || grant.Environment != "TEST"
            || grant.DataContractIdentity != dataContractIdentity
            || handoff.Recovery.Data == "REQUIRED" && dataContractIdentity is null
            || handoff.Governance.Environment != "TEST" || grant.Actor != actor
            || grant.NotBeforeUtc > now || grant.ExpiresAtUtc <= now
            || grant.ExpiresAtUtc - grant.NotBeforeUtc > TimeSpan.FromHours(1)
            || !grant.ApprovedPhases.SequenceEqual(new[] { "FORWARD1", "ROLLBACK", "FORWARD2" })
            || phase is not ("PRE" or "FORWARD1" or "ROLLBACK" or "FORWARD2")
            || string.IsNullOrWhiteSpace(grant.ApprovalReference)
            || provenance.Repository != governanceRepository || provenance.Revision != governanceCommit
            || provenance.RawSha256.Length != 64 || LegacyRuntimeEvidenceHash.Hash(grant) != frozen)
            throw new LegacyContractException("EXECUTION_AUTHORIZATION_INVALID");
        await verifyFreshness(token);
    }
}

public sealed record LegacyPromotionFreezeV1(int ContractVersion, string TargetId,
    string ReleaseId, LegacyArtifactValidV1 Artifacts, string QualificationIdentity,
    string EvidenceSetHash, string RehearsalReceiptHash, string RecoveryClass,
    RecoveryCoverageEvidence Coverage, string BaselineIdentity,
    string PreEvidenceHash, string FreezeHash);

public static class LegacyPromotionFreeze
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LegacyRehearsalReceiptV1, object> Produced = new();
    internal static void RegisterProducedReceipt(LegacyRehearsalReceiptV1 receipt) => Produced.Add(receipt, new object());
    // Only the in-process real harness can mint a freeze. Imported, rehashed,
    // deserialized and synthetic receipts have no producer capability.
    public static LegacyPromotionFreezeV1 CreateFromTrustedProducer(LegacyRehearsalReceiptV1 receipt)
    {
        var expected = new[] { "PRE", "FORWARD1", "POST1", "ROLLBACK", "PRE2", "FORWARD2", "POST2" };
        if (!Produced.TryGetValue(receipt, out _) || !LegacyRehearsalReceiptHash.VerifyIntegrity(receipt) || receipt.EvidenceKind != "REAL_TEST"
            || receipt.State != LegacyRehearsalStateV1.RehearsalEvaluated
            || receipt.Status != "REHEARSAL_COMPLETE" || receipt.RecoveryClass != "FULL_REVERSIBLE"
            || receipt.ReasonCodes.Count != 0 || receipt.Evaluation?.RecoveryCoverage is not { Complete: true } coverage
            || !receipt.Phases.Select(x => x.Phase).SequenceEqual(expected)
            || receipt.Phases.Any(x => x.Status is not ("APPLIED" or "CAPTURED"))
            || receipt.Observations.Count != 4
            || coverage.ForwardHash != receipt.Artifacts.Forward.Sha256
            || coverage.RollbackHash != receipt.Artifacts.Rollback.Sha256)
            throw new LegacyContractException("PROMOTION_FREEZE_BLOCKED");
        var freeze = new LegacyPromotionFreezeV1(1, receipt.TargetId, receipt.Artifacts.ReleaseId,
            receipt.Artifacts, receipt.QualificationIdentity, receipt.EvidenceSetHash,
            receipt.ReceiptHash, receipt.RecoveryClass, coverage, receipt.BaselineIdentity,
            LegacyRuntimeEvidenceHash.Hash(receipt.Observations[RecoveryPhase.Pre]), "");
        return freeze with { FreezeHash = LegacyRuntimeEvidenceHash.Hash(freeze) };
    }
}
