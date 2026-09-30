using System.Text.Json;

namespace DatabaseReleaseQualification;

// Selected by identifier; definitions and factories belong to the governed
// producer, never to the application package or an arbitrary assembly path.
public sealed record LegacyDataContractDefinitionV1(int ContractVersion,
    string Selector, int Version, string TargetId, string ScopeHash,
    string ProviderKind, string Equality, LegacyGitDocumentV1 Source,
    LegacyDataScopeV1? Scope = null, int MaximumRowsPerTable = 10000);

public sealed record LegacyDataPhaseEvidenceV1(int ContractVersion,
    string Selector, int Version, string TargetId, string ScopeHash,
    RecoveryPhase Phase, DateTimeOffset CapturedAtUtc, bool Complete,
    string ContentHash);

public interface ILegacyDataEvidenceReaderV1
{
    Task<LegacyDataPhaseEvidenceV1> CaptureAsync(LegacyDataContractDefinitionV1 definition,
        RecoveryPhase phase, CancellationToken token);
}

public sealed class LegacyDataContractResolverV1(
    IReadOnlyList<LegacyDataContractDefinitionV1> governedDefinitions,
    IReadOnlyDictionary<string, Func<LegacyDataContractDefinitionV1, ILegacyDataEvidenceReaderV1>> governedFactories)
{
    public LegacyDataValidationDescriptorV1 Resolve(string selector, string targetId,
        string scopeHash, LegacyRepositoryV1 producerRepository, string producerCommit)
    {
        var definitions = governedDefinitions.Where(x => x.Selector == selector).ToArray();
        if (definitions.Length != 1) throw new LegacyContractException("DATA_PROVIDER_UNKNOWN");
        var definition = definitions[0];
        if (definition.ContractVersion != 1 || definition.Version < 1
            || definition.TargetId != targetId || definition.ScopeHash != scopeHash
            || definition.Equality != "EXACT_CONTENT_HASH_V1"
            || definition.Source.Repository != producerRepository
            || definition.Source.Revision != producerCommit
            || !System.Text.RegularExpressions.Regex.IsMatch(definition.Source.RawSha256, @"\A[0-9a-f]{64}\z")
            || !governedFactories.TryGetValue(definition.ProviderKind, out var factory))
            throw new LegacyContractException("DATA_PROVIDER_CONTRACT_INVALID");
        LegacyPortablePath.Validate(definition.Source.Path);
        return new(selector, definition.Version, scopeHash,
            new LegacyDataValidationProviderV1(definition, factory(definition)));
    }
}

public sealed class LegacyDataValidationProviderV1(LegacyDataContractDefinitionV1 definition,
    ILegacyDataEvidenceReaderV1 reader) : IDataRollbackValidationContract, IDataReapplyValidationContract
{
    private readonly string frozenDefinition = LegacyRuntimeEvidenceHash.Hash(definition);
    public LegacyDataContractDefinitionV1 Definition => definition;
    public string ApprovalIdentity => LegacyReadinessHash.LengthPrefix(definition.Selector,
        definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),definition.TargetId,
        definition.ScopeHash,definition.Source.RawSha256);
    private readonly Dictionary<RecoveryPhase, LegacyDataPhaseEvidenceV1> evidence = [];
    public IReadOnlyDictionary<RecoveryPhase, LegacyDataPhaseEvidenceV1> Evidence =>
        new Dictionary<RecoveryPhase, LegacyDataPhaseEvidenceV1>(evidence);

    // Only the verified phase checkpoint path may seed prior read-only evidence.
    // A fresh capture is still required before the next mutation.
    internal void RestoreVerifiedEvidence(IReadOnlyDictionary<RecoveryPhase, LegacyDataPhaseEvidenceV1> previous)
    {
        VerifyDefinition();
        if (evidence.Count != 0 || previous.Count > 3)
            throw new LegacyContractException("DATA_EVIDENCE_INCOMPLETE");
        for (var i = 0; i < previous.Count; i++)
        {
            var phase = Enum.GetValues<RecoveryPhase>()[i];
            if (!previous.TryGetValue(phase, out var item)
                || item.ContractVersion != 1 || item.Phase != phase || !item.Complete
                || item.Selector != definition.Selector || item.Version != definition.Version
                || item.TargetId != definition.TargetId || item.ScopeHash != definition.ScopeHash
                || item.CapturedAtUtc > DateTimeOffset.UtcNow
                || !System.Text.RegularExpressions.Regex.IsMatch(item.ContentHash, @"\A[0-9a-f]{64}\z"))
                throw new LegacyContractException("DATA_EVIDENCE_INCOMPLETE");
            evidence.Add(phase, item);
        }
    }

    public async Task VerifyUnchangedAsync(RecoveryPhase phase, CancellationToken token)
    {
        VerifyDefinition();
        if (!evidence.TryGetValue(phase, out var previous))
            throw new LegacyContractException("DATA_EVIDENCE_INCOMPLETE");
        var start = DateTimeOffset.UtcNow;
        var current = await reader.CaptureAsync(definition, phase, token);
        VerifyDefinition();
        if (!current.Complete || current.TargetId != definition.TargetId
            || current.ScopeHash != definition.ScopeHash || current.Selector != definition.Selector
            || current.Version != definition.Version || current.Phase != phase
            || current.ContractVersion != 1 || current.CapturedAtUtc < start
            || current.CapturedAtUtc > DateTimeOffset.UtcNow || current.ContentHash != previous.ContentHash)
            throw new LegacyContractException("DATA_DRIFT_BEFORE_MUTATION");
    }

    public async Task CapturePreDataAsync(CancellationToken cancellationToken = default) =>
        await Capture(RecoveryPhase.Pre, cancellationToken);
    public async Task CapturePostDataAsync(CancellationToken cancellationToken = default) =>
        await Capture(RecoveryPhase.Post1, cancellationToken);
    public async Task<DataRollbackValidity> ValidateRollbackDataAsync(CancellationToken cancellationToken = default)
    {
        await Capture(RecoveryPhase.Pre2, cancellationToken);
        return evidence[RecoveryPhase.Pre].ContentHash == evidence[RecoveryPhase.Pre2].ContentHash
            ? DataRollbackValidity.Valid : DataRollbackValidity.Invalid;
    }
    public async Task<DataRollbackValidity> ValidateReapplyDataAsync(CancellationToken cancellationToken = default)
    {
        await Capture(RecoveryPhase.Post2, cancellationToken);
        return evidence[RecoveryPhase.Post1].ContentHash == evidence[RecoveryPhase.Post2].ContentHash
            ? DataRollbackValidity.Valid : DataRollbackValidity.Invalid;
    }

    private async Task Capture(RecoveryPhase phase, CancellationToken token)
    {
        VerifyDefinition();
        if ((int)phase != evidence.Count) throw new LegacyContractException("DATA_PHASE_ORDER_INVALID");
        var start = DateTimeOffset.UtcNow;
        var captured = await reader.CaptureAsync(definition, phase, token);
        VerifyDefinition();
        token.ThrowIfCancellationRequested();
        if (captured.ContractVersion != 1 || captured.Selector != definition.Selector
            || captured.Version != definition.Version || captured.TargetId != definition.TargetId
            || captured.ScopeHash != definition.ScopeHash || captured.Phase != phase
            || !captured.Complete || captured.CapturedAtUtc < start
            || captured.CapturedAtUtc > DateTimeOffset.UtcNow
            || !System.Text.RegularExpressions.Regex.IsMatch(captured.ContentHash, @"\A[0-9a-f]{64}\z"))
            throw new LegacyContractException("DATA_EVIDENCE_INCOMPLETE");
        evidence.Add(phase, captured);
    }
    private void VerifyDefinition()
    {
        if (LegacyRuntimeEvidenceHash.Hash(definition) != frozenDefinition)
            throw new LegacyContractException("DATA_CONTRACT_MUTATED");
    }
}
