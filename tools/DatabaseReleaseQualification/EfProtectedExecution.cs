namespace DatabaseReleaseQualification;

public sealed record EfApplyAuthorizationV1(int Version, EfEvidenceScopeV1 Scope, string OperationId,
    string PlanHash, string PayloadHash, string ForwardHash, string DatabaseIncarnation,
    DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresAtUtc, DeploymentAuthorizationEvidence Authorization);
public sealed record EfExecutionStartV1(EfEvidenceScopeV1 Scope, string OperationId, string PlanHash,
    string PayloadHash, string ForwardHash, EfPlanTargetV1 Target, EfPlanStateV1 ExpectedPre,
    EvidenceReference Authorization, DateTimeOffset StartedAtUtc);
public sealed record EfProtectedExecutionResultV1(string Status, string Outcome,
    EvidenceReference? Receipt, string[] Reasons);

// Host registration only; no CLI or production implementation is provided.
// Begin must durably claim OperationId exactly once before returning the existing
// exact-byte execution contract, bound to Target/ExpectedPre with separate write
// credentials. A duplicate, uncertain claim or stale physical PRE returns null.
// Record must persist the terminal receipt even after cancellation. An uncertain
// write is never retried and never converted into a successful receipt.
public interface IEfProtectedExecutionHostV1
{
    Task<IRehearsalDatabase?> BeginOnceAsync(EfExecutionStartV1 start, CancellationToken token);
    Task<EvidenceReference> RecordAsync(EfExecutionStartV1 start, string outcome, DateTimeOffset completedAtUtc);
}

public sealed partial class EfEvidencePreparationV1
{
    public async Task<EfProtectedExecutionResultV1> ApplyProtectedAsync(EfReleasePlanV1 plan,
        string trustedHash, EfEvidenceScopeV1 scope, EvidenceReference freshPre, EvidenceReference currentSource,
        EvidenceReference independentAuthorization, DateTimeOffset now, CancellationToken token = default)
    {
        EfExecutionStartV1? start = null;
        var claimAttempted = false;
        var clockStart = System.Diagnostics.Stopwatch.GetTimestamp();
        DateTimeOffset CurrentTime() => now + System.Diagnostics.Stopwatch.GetElapsedTime(clockStart);
        try
        {
            Require(provider is not null && executionHost is not null, "PROTECTED_EXECUTION_HOST_NOT_ACCREDITED");
            scope = Clone(scope); freshPre = Clone(freshPre); currentSource = Clone(currentSource);
            independentAuthorization = Clone(independentAuthorization);
            var p = BoundPlan(plan, trustedHash, scope);
            Require(p.Authority.Target.Environment == "TEST" && p.Authority.Scenario is "NEW_EF" or "EXISTING_EF",
                "EF_EXECUTION_SCOPE_INVALID");
            var check = await RevalidateAsync(plan, trustedHash, scope, freshPre, currentSource, now, token);
            Require(check.Status == "PRE_MATCH_NOT_AUTHORIZED", check.Reasons.FirstOrDefault() ?? "PRE_NOT_VERIFIED");
            var used = new EvidenceLog(); used.AddRange(p.Acquisition!.Evidence);
            var consent = await Document<EfApplyAuthorizationV1>(scope, independentAuthorization, "EF_APPLY_AUTHORIZATION", used, token);
            Require(consent.Version == 1 && consent.Scope == scope && consent.OperationId == p.OperationId
                && consent.PlanHash == trustedHash && consent.PayloadHash == p.Payload.PayloadHash
                && consent.ForwardHash == p.Payload.ForwardHash && consent.DatabaseIncarnation == p.Pre.DatabaseIncarnation
                && consent.NotBeforeUtc <= now && now < consent.ExpiresAtUtc && consent.ExpiresAtUtc <= p.ExpiresAtUtc,
                "EF_APPLY_AUTHORIZATION_MISMATCH");
            var a = consent.Authorization;
            Require(a.Decision == DeploymentAuthorizationDecision.Authorized && a.ReleaseQualificationGatePassed
                && !string.IsNullOrWhiteSpace(a.PolicyId) && !string.IsNullOrWhiteSpace(a.AuthorizationReference)
                && a.Risk == p.Risk!.FinalRisk && a.AnalysisConfidence == p.Risk.AnalysisConfidence
                && a.SchemaRollbackValidity == p.Recovery.SchemaValidity && a.DataRollbackValidity == p.Recovery.DataValidity
                && a.RollbackCapability == p.Recovery.Strategy && Enum.IsDefined(a.Requirement)
                && (!p.Risk.RequiresDbaApproval || a.Requirement == DeploymentAuthorizationRequirement.DbaApproval),
                "EF_APPLY_AUTHORIZATION_DOWNGRADE");
            var source = await Document<EfSourcePreparationV1>(scope, currentSource, "SOURCE", used, token);
            var up = new ReleaseScript("forward", source.Forward); var down = new ReleaseScript("rollback", source.Rollback);
            Require(Equal(source.Source, p.Source) && up.Sha256 == p.Payload.ForwardHash && down.Sha256 == p.Payload.RollbackHash,
                "PAYLOAD_CHANGED");
            used.Add(freshPre);
            await provider!.VerifyCurrentAsync(scope, used.Select(Clone).ToArray(), token);
            token.ThrowIfCancellationRequested();
            var executionTime = CurrentTime();
            Require(executionTime < consent.ExpiresAtUtc && executionTime < p.ExpiresAtUtc, "EF_APPLY_AUTHORIZATION_EXPIRED");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(consent.ExpiresAtUtc - executionTime);
            start = new(scope, p.OperationId, trustedHash, p.Payload.PayloadHash, up.Sha256,
                p.Authority.Target, p.Pre, independentAuthorization, executionTime);
            claimAttempted = true;
            var database = await executionHost!.BeginOnceAsync(Clone(start), deadline.Token);
            if (database is null) return new("BLOCKED", "UNKNOWN", null, ["EXECUTION_CLAIM_UNAVAILABLE"]);
            // Reuse the existing exact-byte boundary. There is one invocation,
            // no retry loop, and no automatic DOWN on failure.
            deadline.Token.ThrowIfCancellationRequested();
            await database.ExecuteSqlAsync(up, p.Payload.ForwardHash, deadline.Token);
            var receipt = await executionHost.RecordAsync(Clone(start), "SUCCEEDED", CurrentTime());
            Require(receipt.StorageLocator is null && Hash(receipt.EvidenceSha256)
                && !string.IsNullOrWhiteSpace(receipt.EvidenceId), "EXECUTION_RECEIPT_INVALID");
            return new("EXECUTED_POSTCHECK_REQUIRED", "SUCCEEDED", receipt, []);
        }
        catch (Exception error)
        {
            if (claimAttempted)
            {
                EvidenceReference? receipt = null;
                if (start is not null)
                {
                    try { receipt = await executionHost!.RecordAsync(Clone(start), "UNKNOWN", CurrentTime()); }
                    catch { /* Durable claim remains terminal/uncertain; never retry writes. */ }
                }
                return new("EXECUTION_UNCERTAIN", "UNKNOWN", receipt, ["EXECUTION_RECONCILIATION_REQUIRED"]);
            }
            return new("BLOCKED", "NOT_EXECUTED", null,
                [error is Block ? error.Message : "EF_APPLY_EVIDENCE_NOT_VERIFIABLE"]);
        }
    }
}
