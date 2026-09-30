using System.Text.Json;

namespace DatabaseReleaseQualification;

// Qualification-local V1 contract. These states do not alter discovery or Registry enums.
public enum RecoveryEvidenceCoverage { NotRequired, Complete, Partial, Insufficient, Error }
public enum RecoveryComparison { NotEvaluated, Match, Mismatch }
public enum RecoveryPhase { Pre, Post1, Pre2, Post2 }

public sealed record RecoveryDimensionEvidence(
    bool Required,
    RecoveryEvidenceCoverage Coverage,
    RecoveryComparison Recovery = RecoveryComparison.NotEvaluated,
    RecoveryComparison Reapply = RecoveryComparison.NotEvaluated)
{
    public bool Sufficient => Required
        ? Coverage == RecoveryEvidenceCoverage.Complete && Recovery == RecoveryComparison.Match && Reapply == RecoveryComparison.Match
        : Coverage == RecoveryEvidenceCoverage.NotRequired;
}

public sealed record RecoveryCoverageEvidence(
    string ForwardHash,
    string RollbackHash,
    bool ImpactComplete,
    RecoveryDimensionEvidence Structure,
    RecoveryDimensionEvidence Data,
    RecoveryDimensionEvidence Security,
    IReadOnlyDictionary<RecoveryPhase, string> SecurityHashes,
    string? SecurityScopeHash,
    IReadOnlyDictionary<RecoveryPhase, CanonicalRecoverySecurity> SecurityEvidence)
{
    public int ContractVersion => 1;
    public bool Complete => ImpactComplete && Structure.Required && Structure.Sufficient && Data.Sufficient && Security.Sufficient
        && Hash(ForwardHash) && Hash(RollbackHash)
        && (!Security.Required || Hash(SecurityScopeHash) && SecurityHashes.Count == 4 && SecurityEvidence.Count == 4
            && Enum.GetValues<RecoveryPhase>().All(p => SecurityHashes.TryGetValue(p, out var hash)
                && SecurityEvidence.TryGetValue(p, out var evidence) && Hash(hash)
                && evidence.Sha256 == hash && Hashing.Sha256(evidence.Json) == hash)
            && SecurityHashes[RecoveryPhase.Pre] == SecurityHashes[RecoveryPhase.Pre2]
            && SecurityHashes[RecoveryPhase.Post1] == SecurityHashes[RecoveryPhase.Post2]);
    private static bool Hash(string? value) => value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[0-9a-f]{64}\z");
}

// The original DATA rollback interface retains its meaning. This optional companion
// supplies the distinct POST1/POST2 evidence required for complete V1 qualification.
public interface IDataReapplyValidationContract
{
    Task CapturePostDataAsync(CancellationToken cancellationToken = default);
    Task<DataRollbackValidity> ValidateReapplyDataAsync(CancellationToken cancellationToken = default);
}

public sealed record RecoverySecuritySecurable(string Kind, string Schema = "", string Name = "");
public sealed record RecoverySecurityScope(
    IReadOnlyList<RecoverySecuritySecurable> Securables,
    IReadOnlyList<string> RequiredPrincipals)
{
    public string Sha256 => Hashing.Sha256(JsonSerializer.Serialize(new {
        version = 1,
        securables = Securables.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Schema, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal),
        principals = RequiredPrincipals.Order(StringComparer.Ordinal)
    }, JsonDefaults.Compact));
}

public sealed record RecoveryImpact(bool Complete, bool DataRequired, RecoverySecurityScope SecurityScope)
{
    public bool SecurityRequired => SecurityScope.Securables.Count != 0;

    public static bool RequiresData(ScriptAnalysis forward, ScriptAnalysis rollback) =>
        new[] { forward, rollback }.Any(analysis =>
            analysis.Operations.Any(operation => operation.IsDataMutation || operation.HasPotentialDataLoss));

    public static RecoveryImpact Derive(ScriptAnalysis forward, ScriptAnalysis rollback)
    {
        var analyses = new[] { forward, rollback };
        var operations = analyses.SelectMany(x => x.Operations).ToArray();
        var complete = analyses.All(x => x.Confidence == AnalysisConfidence.Complete && x.ParseErrors.Count == 0 && x.UnknownStatementTypes.Count == 0);
        var scopes = new HashSet<RecoverySecuritySecurable>();
        foreach (var operation in operations)
        {
            if (!operation.TargetResolved) complete = false;
            if (operation.SecuritySecurable is not null) scopes.Add(operation.SecuritySecurable);
            else if (operation.IsSchemaMutation)
            {
                scopes.Add(new("OBJECT", operation.Schema, operation.Object));
                // Trigger creation/alter can affect another securable. Until the analyzer
                // supplies an unambiguous trigger identity, scope is not proven.
                if (operation.Operation.Contains("TRIGGER", StringComparison.Ordinal)) complete = false;
            }
        }
        return new(complete,
            RequiresData(forward, rollback),
            new(Array.AsReadOnly(scopes.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Schema, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray()),
                Array.AsReadOnly(operations.SelectMany(x => x.SecurityPrincipals).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())));
    }
}

internal sealed class RecoveryCoverageSession(IRecoverySecurityEvidenceProvider? securityProvider, IDataRollbackValidationContract? dataProvider)
{
    private RecoveryImpact? impact;
    private string forwardHash = "", rollbackHash = "";
    private RecoveryDimensionEvidence structure = new(true, RecoveryEvidenceCoverage.Insufficient);
    private RecoveryDimensionEvidence data = new(true, RecoveryEvidenceCoverage.Insufficient);
    private RecoveryDimensionEvidence security = new(true, RecoveryEvidenceCoverage.Insufficient);
    private readonly Dictionary<RecoveryPhase, string> hashes = [];
    private readonly Dictionary<RecoveryPhase, CanonicalRecoverySecurity> securityEvidence = [];
    private string? databaseIdentity;
    private bool postDataCaptured;

    public async Task<string?> BeginAsync(ReleaseScript forward, ReleaseScript rollback, ScriptAnalysis forwardAnalysis,
        ScriptAnalysis rollbackAnalysis, SchemaSnapshot pre, CancellationToken token)
    {
        forwardHash = forward.Sha256; rollbackHash = rollback.Sha256;
        impact = RecoveryImpact.Derive(forwardAnalysis, rollbackAnalysis);
        structure = new(true, pre.SchemaCoverage == SchemaCoverage.Complete ? RecoveryEvidenceCoverage.Complete : RecoveryEvidenceCoverage.Partial);
        data = new(impact.DataRequired, impact.DataRequired ? RecoveryEvidenceCoverage.Insufficient : RecoveryEvidenceCoverage.NotRequired);
        security = new(impact.SecurityRequired, impact.SecurityRequired ? RecoveryEvidenceCoverage.Insufficient : RecoveryEvidenceCoverage.NotRequired);
        if (!impact.Complete) return "BLOCKED_RECOVERY_IMPACT_INCOMPLETE";
        if (structure.Coverage != RecoveryEvidenceCoverage.Complete) return "BLOCKED_STRUCTURE_COVERAGE";
        return await CaptureAsync(RecoveryPhase.Pre, token);
    }

    public string? CheckPostImpact(ScriptAnalysis forward, ScriptAnalysis rollback)
    {
        var post = RecoveryImpact.Derive(forward, rollback);
        if (impact is null || !post.Complete || post.DataRequired != impact.DataRequired || post.SecurityScope.Sha256 != impact.SecurityScope.Sha256)
            return "BLOCKED_RECOVERY_IMPACT_CHANGED";
        return null;
    }

    public async Task<string?> ObserveAsync(RecoveryPhase phase, SchemaSnapshot snapshot, CancellationToken token)
    {
        if (snapshot.SchemaCoverage != SchemaCoverage.Complete)
        {
            structure = structure with { Coverage = RecoveryEvidenceCoverage.Partial };
            return "BLOCKED_STRUCTURE_COVERAGE";
        }
        var error = await CaptureAsync(phase, token);
        if (error is not null) return error;
        if (phase == RecoveryPhase.Post1 && impact?.DataRequired == true && dataProvider is IDataReapplyValidationContract reapply)
        {
            try { await reapply.CapturePostDataAsync(token); postDataCaptured = true; }
            catch { data = data with { Coverage = RecoveryEvidenceCoverage.Error }; return "BLOCKED_DATA_COVERAGE_ERROR"; }
        }
        return null;
    }

    private async Task<string?> CaptureAsync(RecoveryPhase phase, CancellationToken token)
    {
        if (impact?.SecurityRequired != true) return null;
        if (securityProvider is null) return "BLOCKED_SECURITY_EVIDENCE_MISSING";
        try
        {
            var evidence = await securityProvider.CaptureSecurityAsync(impact.SecurityScope, phase, token);
            var canonical = RecoverySecurityCanonicalizer.Canonicalize(impact.SecurityScope, phase, evidence);
            security = security with { Coverage = evidence.Coverage };
            if (canonical is null)
            {
                if (security.Coverage == RecoveryEvidenceCoverage.Complete) security = security with { Coverage = RecoveryEvidenceCoverage.Insufficient };
                return "BLOCKED_SECURITY_COVERAGE";
            }
            if (databaseIdentity is not null && databaseIdentity != canonical.DatabaseIdentity)
            { security = security with { Coverage = RecoveryEvidenceCoverage.Error }; return "BLOCKED_SECURITY_IDENTITY_MISMATCH"; }
            databaseIdentity = canonical.DatabaseIdentity;
            hashes.Add(phase, canonical.Sha256);
            securityEvidence.Add(phase, canonical);
            if (phase == RecoveryPhase.Pre2)
                security = security with { Recovery = hashes[RecoveryPhase.Pre] == canonical.Sha256 ? RecoveryComparison.Match : RecoveryComparison.Mismatch };
            if (phase == RecoveryPhase.Post2)
                security = security with { Reapply = hashes[RecoveryPhase.Post1] == canonical.Sha256 ? RecoveryComparison.Match : RecoveryComparison.Mismatch };
            if (security.Recovery == RecoveryComparison.Mismatch || security.Reapply == RecoveryComparison.Mismatch) return "BLOCKED_SECURITY_MISMATCH";
            return null;
        }
        catch
        { security = security with { Coverage = RecoveryEvidenceCoverage.Error }; return "BLOCKED_SECURITY_COVERAGE_ERROR"; }
    }

    public void StructureRecovery(bool match) => structure = structure with { Recovery = match ? RecoveryComparison.Match : RecoveryComparison.Mismatch };
    public void StructureReapply(bool match) => structure = structure with { Reapply = match ? RecoveryComparison.Match : RecoveryComparison.Mismatch };
    public void DataError() => data = data with { Coverage = RecoveryEvidenceCoverage.Error };
    public string? DataReapplyPreflight() => impact?.DataRequired == true && (!postDataCaptured || dataProvider is not IDataReapplyValidationContract)
        ? "BLOCKED_DATA_REAPPLY_EVIDENCE_MISSING" : null;
    public void DataRecovery(DataRollbackValidity validity)
    {
        if (impact?.DataRequired != true) return;
        data = data with {
            Coverage = validity == DataRollbackValidity.Valid ? RecoveryEvidenceCoverage.Complete : RecoveryEvidenceCoverage.Insufficient,
            Recovery = validity == DataRollbackValidity.Valid ? RecoveryComparison.Match : validity == DataRollbackValidity.Invalid ? RecoveryComparison.Mismatch : RecoveryComparison.NotEvaluated
        };
    }
    public async Task<string?> DataReapplyAsync(CancellationToken token)
    {
        if (impact?.DataRequired != true) return null;
        if (!postDataCaptured || dataProvider is not IDataReapplyValidationContract provider) return "BLOCKED_DATA_REAPPLY_EVIDENCE_MISSING";
        try
        {
            var validity = await provider.ValidateReapplyDataAsync(token);
            data = data with { Reapply = validity == DataRollbackValidity.Valid ? RecoveryComparison.Match : validity == DataRollbackValidity.Invalid ? RecoveryComparison.Mismatch : RecoveryComparison.NotEvaluated };
            if (validity != DataRollbackValidity.Valid)
            { data = data with { Coverage = RecoveryEvidenceCoverage.Insufficient }; return "BLOCKED_DATA_REAPPLY_UNVERIFIED"; }
            return null;
        }
        catch { data = data with { Coverage = RecoveryEvidenceCoverage.Error }; return "BLOCKED_DATA_COVERAGE_ERROR"; }
    }

    public RehearsalResult Attach(RehearsalResult result) => new()
    {
        QualificationStatus = result.QualificationStatus,
        SchemaRollbackValidity = result.SchemaRollbackValidity, DataRollbackValidity = result.DataRollbackValidity,
        RollbackCapability = result.RollbackCapability, ForwardCertified = result.ForwardCertified,
        RollbackCertified = result.RollbackCertified && (!security.Required || security.Coverage == RecoveryEvidenceCoverage.Complete && security.Recovery == RecoveryComparison.Match),
        ReapplyCertified = result.ReapplyCertified,
        Pre = result.Pre, Post1 = result.Post1, Pre2 = result.Pre2, Post2 = result.Post2,
        RollbackDiff = result.RollbackDiff, ReapplyDiff = result.ReapplyDiff, AnalysisEvidence = result.AnalysisEvidence, ExecutionAudit = result.ExecutionAudit,
        RecoveryCoverage = new(forwardHash, rollbackHash, impact?.Complete == true, structure, data, security,
            new Dictionary<RecoveryPhase, string>(hashes), impact?.SecurityScope.Sha256,
            new Dictionary<RecoveryPhase, CanonicalRecoverySecurity>(securityEvidence))
    };
}
