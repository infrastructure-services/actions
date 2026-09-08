using System.Text.Json.Serialization;

namespace DatabaseReleaseQualification;

public enum ReconciliationClassification
{
    Expected,
    ApprovedOutOfBand,
    Unexplained
}

public enum ReconciliationChangeType
{
    Added,
    Removed,
    Modified
}

public enum ReconciliationStatus
{
    ReadyForCertification,
    Blocked,
    NoDifferences
}

public sealed class StructuralDifference
{
    public required string DifferenceId { get; init; }
    public required string ObjectIdentity { get; init; }
    public required string ObjectType { get; init; }
    public ReconciliationChangeType ChangeType { get; init; }
    public string? BeforeFingerprint { get; init; }
    public string? AfterFingerprint { get; init; }
}

public sealed class ReconciliationDisposition
{
    public required string DifferenceId { get; init; }
    public ReconciliationClassification Classification { get; init; } = ReconciliationClassification.Unexplained;
    public string? ChangeOrigin { get; init; }
    public string? Reference { get; init; }
    public string? Reason { get; init; }
}

public sealed class ReconciliationContext
{
    public required string ReconciliationId { get; init; }
    public required string PreviousCertificationId { get; init; }
    public required string ApplicationId { get; init; }
    public required string Environment { get; init; }
    public required string DatabaseName { get; init; }
    public required CertificationOrigin CertificationOrigin { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public SortedDictionary<string, string> RunMetadata { get; init; } = new(StringComparer.Ordinal);
}

public sealed class ReconciliationItem
{
    public required string DifferenceId { get; init; }
    public required string ObjectIdentity { get; init; }
    public required string ObjectType { get; init; }
    public ReconciliationChangeType ChangeType { get; init; }
    public string? BeforeFingerprint { get; init; }
    public string? AfterFingerprint { get; init; }
    public ReconciliationClassification Classification { get; init; }
    public string? ChangeOrigin { get; init; }
    public string? Reference { get; init; }
    public string? Reason { get; init; }
    public bool DispositionValid { get; init; }
}

public sealed class ReconciliationEvidence
{
    public int FormatVersion { get; init; } = 1;
    public required string ReconciliationId { get; init; }
    public required string PreviousCertificationId { get; init; }
    public required string ApplicationId { get; init; }
    public required string Environment { get; init; }
    public required string DatabaseName { get; init; }
    public required string CertifiedPreSchemaHash { get; init; }
    public required string ObservedSchemaHash { get; init; }
    public ReconciliationStatus Status { get; init; }
    public CertificationOrigin CertificationOrigin { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public SortedDictionary<string, string> RunMetadata { get; init; } = new(StringComparer.Ordinal);
    public List<ReconciliationItem> Items { get; init; } = [];
    public List<string> BlockingReasons { get; init; } = [];
    public int ApprovedDifferenceCount { get; init; }
    public int UnexplainedDifferenceCount { get; init; }
    public string? ReconciledCanonicalStateCandidateHash { get; init; }
}

public sealed class ReconciliationResult
{
    public ReconciliationStatus ReconciliationStatus { get; init; }
    public List<string> BlockingReasons { get; init; } = [];
    public List<ReconciliationItem> Items { get; init; } = [];
    public int ApprovedDifferenceCount { get; init; }
    public int UnexplainedDifferenceCount { get; init; }
    public required ReconciliationEvidence Evidence { get; init; }

    [JsonIgnore]
    public CanonicalSchema? ReconciledCanonicalStateCandidate { get; init; }
}

public static class StructuralDifferenceBuilder
{
    public static IReadOnlyList<StructuralDifference> Build(CanonicalSchema certified, CanonicalSchema observed)
    {
        ArgumentNullException.ThrowIfNull(certified);
        ArgumentNullException.ThrowIfNull(observed);

        var before = ByIdentity(certified);
        var after = ByIdentity(observed);
        var identities = before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var differences = new List<StructuralDifference>();

        foreach (var identity in identities)
        {
            before.TryGetValue(identity, out var beforeObject);
            after.TryGetValue(identity, out var afterObject);
            if (beforeObject is not null && afterObject is not null
                && string.Equals(beforeObject.Fingerprint, afterObject.Fingerprint, StringComparison.Ordinal))
                continue;

            var changeType = beforeObject is null
                ? ReconciliationChangeType.Added
                : afterObject is null
                    ? ReconciliationChangeType.Removed
                    : ReconciliationChangeType.Modified;
            var objectType = afterObject?.ObjectType ?? beforeObject!.ObjectType;
            var beforeFingerprint = beforeObject?.Fingerprint;
            var afterFingerprint = afterObject?.Fingerprint;
            var differenceIdentity = string.Join("\n", identity, changeType, beforeFingerprint ?? "", afterFingerprint ?? "");
            differences.Add(new StructuralDifference
            {
                DifferenceId = Hashing.Sha256(differenceIdentity),
                ObjectIdentity = identity,
                ObjectType = objectType,
                ChangeType = changeType,
                BeforeFingerprint = beforeFingerprint,
                AfterFingerprint = afterFingerprint
            });
        }

        return differences;
    }

    private static Dictionary<string, ObjectFingerprint> ByIdentity(CanonicalSchema schema) => schema.Document.Objects
        .GroupBy(item => item.Identity, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => new ObjectFingerprint(
                group.First().Kind,
                Hashing.Sha256(string.Join("\n", group
                    .Select(SchemaCanonicalizer.SerializeObject)
                    .Order(StringComparer.Ordinal)))),
            StringComparer.Ordinal);

    private sealed record ObjectFingerprint(string ObjectType, string Fingerprint);
}

public sealed class ReconciliationEvaluator
{
    public ReconciliationResult Evaluate(
        ReconciliationContext context,
        CanonicalSchema certified,
        CanonicalSchema observed,
        IReadOnlyList<ReconciliationDisposition>? dispositions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(certified);
        ArgumentNullException.ThrowIfNull(observed);
        ValidateContext(context);

        var differences = StructuralDifferenceBuilder.Build(certified, observed);
        var supplied = dispositions ?? [];
        var reasons = new List<string>();
        var byDifference = supplied.GroupBy(item => item.DifferenceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var knownIds = differences.Select(item => item.DifferenceId).ToHashSet(StringComparer.Ordinal);

        foreach (var duplicate in byDifference.Where(group => group.Value.Count != 1).Select(group => group.Key))
            reasons.Add($"DUPLICATE_RECONCILIATION_ITEM:{duplicate}");
        foreach (var unknown in byDifference.Keys.Where(item => !knownIds.Contains(item)))
            reasons.Add($"UNKNOWN_RECONCILIATION_ITEM:{unknown}");

        var items = differences.Select(difference => EvaluateItem(
            difference,
            byDifference.TryGetValue(difference.DifferenceId, out var matches) && matches.Count == 1
                ? matches[0]
                : null,
            reasons)).ToList();

        var status = differences.Count == 0 && reasons.Count == 0
            ? ReconciliationStatus.NoDifferences
            : reasons.Count == 0
                ? ReconciliationStatus.ReadyForCertification
                : ReconciliationStatus.Blocked;
        var approvedCount = items.Count(item => item.DispositionValid
            && item.Classification is ReconciliationClassification.Expected
                or ReconciliationClassification.ApprovedOutOfBand);
        var unexplainedCount = items.Count(item => !item.DispositionValid
            || item.Classification == ReconciliationClassification.Unexplained);
        var candidate = status == ReconciliationStatus.ReadyForCertification ? observed : null;
        var blockingReasons = reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var evidence = new ReconciliationEvidence
        {
            ReconciliationId = context.ReconciliationId,
            PreviousCertificationId = context.PreviousCertificationId,
            ApplicationId = context.ApplicationId,
            Environment = context.Environment,
            DatabaseName = context.DatabaseName,
            CertifiedPreSchemaHash = certified.Sha256,
            ObservedSchemaHash = observed.Sha256,
            Status = status,
            CertificationOrigin = context.CertificationOrigin,
            CreatedAtUtc = context.CreatedAtUtc,
            RunMetadata = new SortedDictionary<string, string>(context.RunMetadata, StringComparer.Ordinal),
            Items = items,
            BlockingReasons = blockingReasons,
            ApprovedDifferenceCount = approvedCount,
            UnexplainedDifferenceCount = unexplainedCount,
            ReconciledCanonicalStateCandidateHash = candidate?.Sha256
        };

        return new ReconciliationResult
        {
            ReconciliationStatus = status,
            BlockingReasons = blockingReasons,
            Items = items,
            ApprovedDifferenceCount = approvedCount,
            UnexplainedDifferenceCount = unexplainedCount,
            Evidence = evidence,
            ReconciledCanonicalStateCandidate = candidate
        };
    }

    private static ReconciliationItem EvaluateItem(
        StructuralDifference difference,
        ReconciliationDisposition? disposition,
        ICollection<string> reasons)
    {
        var classification = disposition?.Classification ?? ReconciliationClassification.Unexplained;
        var valid = classification switch
        {
            ReconciliationClassification.Expected => ValidExpected(disposition),
            ReconciliationClassification.ApprovedOutOfBand => ValidApprovedOutOfBand(disposition),
            _ => false
        };

        if (!valid)
        {
            var reason = classification switch
            {
                ReconciliationClassification.Expected => "EXPECTED_METADATA_REQUIRED",
                ReconciliationClassification.ApprovedOutOfBand => "APPROVED_OUT_OF_BAND_METADATA_REQUIRED",
                _ => "UNEXPLAINED_DIFFERENCE"
            };
            reasons.Add($"{reason}:{difference.DifferenceId}");
        }

        return new ReconciliationItem
        {
            DifferenceId = difference.DifferenceId,
            ObjectIdentity = difference.ObjectIdentity,
            ObjectType = difference.ObjectType,
            ChangeType = difference.ChangeType,
            BeforeFingerprint = difference.BeforeFingerprint,
            AfterFingerprint = difference.AfterFingerprint,
            Classification = classification,
            ChangeOrigin = disposition?.ChangeOrigin,
            Reference = disposition?.Reference,
            Reason = disposition?.Reason,
            DispositionValid = valid
        };
    }

    private static bool ValidExpected(ReconciliationDisposition? disposition) => disposition is not null
        && disposition.ChangeOrigin is DatabaseChangeOrigins.Application or DatabaseChangeOrigins.Dba
        && !string.IsNullOrWhiteSpace(disposition.Reference)
        && !string.IsNullOrWhiteSpace(disposition.Reason);

    private static bool ValidApprovedOutOfBand(ReconciliationDisposition? disposition) => disposition is not null
        && disposition.ChangeOrigin == DatabaseChangeOrigins.Dba
        && !string.IsNullOrWhiteSpace(disposition.Reference)
        && !string.IsNullOrWhiteSpace(disposition.Reason);

    private static void ValidateContext(ReconciliationContext context)
    {
        if (context.CertificationOrigin is not (CertificationOrigin.DriftReconciliation
            or CertificationOrigin.BreakGlassReconciliation))
            throw new InvalidOperationException("RECONCILIATION_CERTIFICATION_ORIGIN_INVALID");
        if (new[]
            {
                context.ReconciliationId,
                context.PreviousCertificationId,
                context.ApplicationId,
                context.Environment,
                context.DatabaseName
            }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("RECONCILIATION_CONTEXT_REQUIRED");
    }
}
