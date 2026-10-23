using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record EfContinuationSelectionV1(EvidenceReference Plan, EvidenceReference? FreshPre = null,
    EvidenceReference? CurrentSource = null, EvidenceReference? Execution = null, EvidenceReference? ActualPost = null);
public sealed record EfOperationalRequestV1(int ContractVersion, string Operation, string ApplicationId,
    string Environment, string GovernanceRevision, string ActionsRevision, EfPreparationSelectionV1 Selection,
    EfContinuationSelectionV1? Continuation = null);
public sealed record EfVerifiedGitSourceV1(string Repository, string Revision, string Path, string Sha256);
public sealed record EfOperationalSessionV1(IEfAuthorizedEvidenceProviderV1? Provider,
    EfVerifiedGitSourceV1[] VerifiedGitSources, string[] BlockingReasons);

// Host registration only. Neither JSON nor the CLI can select an implementation,
// supply an assembly, enable a test provider or declare producer authority.
public interface IEfOperationalSessionFactoryV1
{
    Task<EfOperationalSessionV1> OpenAsync(EfOperationalRequestV1 request, CancellationToken token);
}
public sealed record EfOperationalResultV1(int ContractVersion, string Operation, string Status,
    string[] Reasons, string? PlanHash, string? EvidenceSetHash, EfVerifiedGitSourceV1[] VerifiedGitSources)
{
    public bool ExecutionAuthorized => false;
    public bool Certified => false;
}
public sealed record EfPlanReviewV1(int ContractVersion, string PlanHash, string PayloadHash,
    string ForwardHash, string RollbackHash, int AppliedCount, int PendingCount,
    string Scenario, RiskLevel? FinalRisk, EfPlanRecoveryReviewV1 Recovery)
{
    public bool ExecutionAuthorized => false;
}
public sealed record EfPlanRecoveryReviewV1(RollbackCapability Strategy, bool ScriptAvailable,
    bool PotentialDataLoss, bool ExerciseVerified);
public sealed record EfOperationalOutcomeV1(EfOperationalResultV1 Result, EfPlanReviewV1? Review,
    [property: JsonIgnore] EfReleasePlanV1? PreparedPlan = null);
internal sealed class EfHostException(string code) : Exception(code);

internal static class EfHostContract
{
    internal static readonly JsonSerializerOptions Json = new(JsonDefaults.Compact) {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    internal static void Require(bool condition, string code) { if (!condition) throw new EfHostException(code); }
    internal static bool Sha(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{40}\z");
    internal static bool Hash(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z");
    internal static bool Token(string? value) => value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z");
    internal static bool Repository(string? value) => value is not null
        && Regex.IsMatch(value, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*\z") && value.Length <= 200;
    internal static T Parse<T>(byte[] bytes, int maximum = 65536)
    {
        Require(bytes.Length is > 0 && bytes.Length <= maximum, "INPUT_SIZE_INVALID");
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        Unique(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new EfHostException("INPUT_INVALID");
    }
    internal static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { Require(keys.Add(property.Name), "DUPLICATE_FIELD"); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    internal static void Reference(EvidenceReference? reference)
        => Require(reference is not null && reference.EvidenceId is not null
            && Regex.IsMatch(reference.EvidenceId, @"\A[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}\z") && Hash(reference.EvidenceSha256)
            && reference.StorageLocator is null, "EVIDENCE_REFERENCE_INVALID");
    internal static void Validate(EfOperationalRequestV1 r)
    {
        Require(r.ContractVersion == 1 && r.Operation is "PLAN" or "REVALIDATE" or "VERIFY_POST", "REQUEST_VERSION_OR_OPERATION_INVALID");
        Require(r.Environment == "TEST", "ENVIRONMENT_NOT_ALLOWED");
        Require(Token(r.ApplicationId) && Sha(r.GovernanceRevision) && Sha(r.ActionsRevision), "CONTEXT_INVALID");
        Require(r.Selection is not null && r.Selection.Scope is not null, "SELECTION_INVALID");
        var s = r.Selection!; var scope = s.Scope!;
        Require(Token(scope.TargetId) && Token(scope.SessionId) && Repository(scope.SourceRepository)
            && Sha(scope.SourceRevision) && Token(s.ReleaseId) && Guid.TryParseExact(s.OperationId, "D", out _), "SELECTION_INVALID");
        Reference(s.Governance); Reference(s.Source); Reference(s.Pre);
        if (s.Qualification is not null) Reference(s.Qualification);
        if (s.PreSources is { } sources)
        {
            Reference(sources.Discovery); Reference(sources.Schema);
            Reference(sources.Data); Reference(sources.Security);
        }
        var c = r.Continuation;
        if (r.Operation == "PLAN") Require(c is null, "UNEXPECTED_CONTINUATION");
        else
        {
            Require(c is not null, "CONTINUATION_REQUIRED"); Reference(c!.Plan);
            if (r.Operation == "REVALIDATE")
            { Reference(c.FreshPre); Reference(c.CurrentSource); Require(c.Execution is null && c.ActualPost is null, "UNEXPECTED_CONTINUATION"); }
            else
            { Reference(c.Execution); Reference(c.ActualPost); Require(c.FreshPre is null && c.CurrentSource is null, "UNEXPECTED_CONTINUATION"); }
        }
    }
}

public static class EfOperationalHostV1
{
    public static async Task<EfOperationalOutcomeV1> RunAsync(EfOperationalRequestV1 request,
        IEfOperationalSessionFactoryV1 factory, DateTimeOffset now, CancellationToken token = default)
    {
        var operation = "UNKNOWN";
        EfVerifiedGitSourceV1[] sources = [];
        EfOperationalOutcomeV1 Outcome(string status, string[] reasons, EfReleasePlanV1? plan = null, string? evidenceHash = null)
        {
            var p = plan?.Read();
            var review = p is null ? null : new EfPlanReviewV1(1, plan!.Sha256, p.Payload.PayloadHash,
                p.Payload.ForwardHash, p.Payload.RollbackHash, p.Pre.AppliedMigrationIds.Length,
                p.PendingMigrationIds.Length, p.Authority.Scenario, p.Risk?.FinalRisk,
                new(p.Recovery.Strategy, p.Recovery.ScriptAvailable, p.Recovery.PotentialDataLoss, p.Recovery.ExerciseVerified));
            return new(new(1, operation, status, reasons, plan?.Sha256, evidenceHash, sources), review, plan);
        }
        try
        {
            // Detach mutable arrays/references before handing a request to a host.
            var r = EfHostContract.Parse<EfOperationalRequestV1>(JsonSerializer.SerializeToUtf8Bytes(request, EfHostContract.Json));
            EfHostContract.Validate(r); operation = r.Operation;
            token.ThrowIfCancellationRequested();
            var session = await factory.OpenAsync(r, token);
            sources = session.VerifiedGitSources;
            if (session.BlockingReasons.Length != 0) return Outcome("BLOCKED", SafeReasons(session.BlockingReasons));
            if (session.Provider is null) return Outcome("BLOCKED", ["AUTHORIZED_PROVIDER_REQUIRED"]);
            var provider = new ContextProvider(session.Provider, r);
            var service = new EfEvidencePreparationV1(provider);
            EfPlanResultV1 result;
            if (operation == "PLAN")
            {
                var prepared = await service.PrepareAsync(r.Selection, now, token);
                token.ThrowIfCancellationRequested(); // A2.1 deliberately catches cancellation; preserve host semantics.
                return Outcome(prepared.Status, SafeReasons(provider.Failure is null ? prepared.Reasons : [provider.Failure]), prepared.PlanResult?.Plan, prepared.EvidenceSetHash);
            }
            var c = r.Continuation!;
            // A caller's plan hash is only a selector. Load exact canonical bytes
            // through an independently authorized, purpose-bound plan producer.
            var content = await provider.ReadAuthorizedAsync(new(r.Selection.Scope, "EF_PREPARED_PLAN", c.Plan), token);
            EfHostContract.Require(EfHostContract.Repository(content.ProducerRepository) && EfHostContract.Sha(content.ProducerRevision)
                && Hashing.Sha256(content.Content) == c.Plan.EvidenceSha256, "PLAN_CONTENT_UNVERIFIED");
            var manifest = EfHostContract.Parse<EfPlanManifestV1>(content.Content, 4 * 1024 * 1024);
            var plan = new EfReleasePlanV1(manifest);
            EfHostContract.Require(plan.Sha256 == c.Plan.EvidenceSha256 && manifest.OperationId == r.Selection.OperationId
                && manifest.Payload.ReleaseId == r.Selection.ReleaseId, "PLAN_CONTEXT_MISMATCH");
            provider.Bind(manifest.Authority, manifest.Source);
            if (operation == "REVALIDATE")
                result = await service.RevalidateAsync(plan, plan.Sha256, r.Selection.Scope, c.FreshPre!, c.CurrentSource!, now, token);
            else result = await service.VerifyPostAsync(plan, plan.Sha256, r.Selection.Scope, c.Execution!, c.ActualPost!, now, token);
            token.ThrowIfCancellationRequested();
            await provider.VerifyCurrentAsync(r.Selection.Scope, [c.Plan], token);
            return Outcome(result.Status, SafeReasons(provider.Failure is null ? result.Reasons : [provider.Failure]), result.Plan);
        }
        catch (OperationCanceledException) { throw; }
        catch (EfHostException e) { return Outcome("BLOCKED", [e.Message]); }
        catch { return Outcome("BLOCKED", ["HOST_EVIDENCE_UNVERIFIABLE"]); }
    }

    private static string[] SafeReasons(string[] reasons) => reasons.Select(x =>
        Regex.IsMatch(x, @"\A[A-Z][A-Z0-9_]{0,95}\z") ? x : "HOST_EVIDENCE_UNVERIFIABLE").Distinct().Order(StringComparer.Ordinal).ToArray();

    private sealed class ContextProvider(IEfAuthorizedEvidenceProviderV1 inner, EfOperationalRequestV1 request) : IEfAuthorizedEvidenceProviderV1
    {
        internal string? Failure { get; private set; }
        internal void Bind(EfPlanGovernanceV1 authority, EfPlanSourceV1? source = null)
        {
            EfHostContract.Require(authority.Target.TargetId == request.Selection.Scope.TargetId
                && authority.Target.ApplicationId == request.ApplicationId && authority.Target.Environment == request.Environment
                && authority.Target.SourceRepository == request.Selection.Scope.SourceRepository
                && authority.Governance.Revision == request.GovernanceRevision, "GOVERNED_CONTEXT_MISMATCH");
            if (source is not null) EfHostContract.Require(source.Repository == request.Selection.Scope.SourceRepository
                && source.Revision == request.Selection.Scope.SourceRevision, "SOURCE_CONTEXT_MISMATCH");
        }
        public async Task<EfAuthorizedContentV1> ReadAuthorizedAsync(EfEvidenceReadV1 read, CancellationToken token)
        {
            try
            {
                EfHostContract.Require(read.Scope == request.Selection.Scope, "EVIDENCE_SCOPE_MISMATCH");
                var content = await inner.ReadAuthorizedAsync(read, token);
                EfHostContract.Require(content.Content is { Length: > 0 and <= 16 * 1024 * 1024 }
                    && Hashing.Sha256(content.Content) == read.Reference.EvidenceSha256, "EVIDENCE_HASH_MISMATCH");
                if (read.Purpose == "GOVERNANCE") Bind(EfHostContract.Parse<EfGovernedPreparationV1>(content.Content, 16 * 1024 * 1024).Authority);
                return content;
            }
            catch (EfHostException e) { Failure = e.Message; throw; }
        }
        public Task VerifyCurrentAsync(EfEvidenceScopeV1 scope, IReadOnlyList<EvidenceReference> evidence, CancellationToken token)
            => inner.VerifyCurrentAsync(scope, evidence, token);
    }
}
