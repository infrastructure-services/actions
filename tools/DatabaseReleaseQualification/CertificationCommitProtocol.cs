using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record CertificationPointer(
    DatabaseIdentity DatabaseIdentity,
    string CertificationId,
    string CertificationEvidenceHash,
    long Version);

public sealed record ExpectedCertificationPointer(
    string CertificationId,
    string CertificationEvidenceHash,
    long Version);

public sealed record CommitAuthorizationContext(
    string ActorId,
    string Mechanism,
    string PolicyId,
    string AuthorityReference);

public sealed class CertificationCommitRequest
{
    public required string OperationId { get; init; }
    public required DatabaseIdentity DatabaseIdentity { get; init; }
    public ExpectedCertificationPointer? ExpectedPredecessor { get; init; }
    public required CertifiedStateRecord Candidate { get; init; }
    public required IReadOnlyList<EvidenceReference> EvidenceReferences { get; init; }
    public required string ReleaseOrCorrelationId { get; init; }
    public required CommitAuthorizationContext Authorization { get; init; }
    public required CertificationResult Certification { get; init; }
    public required DateTimeOffset RequestedAtUtc { get; init; }
}

public enum CertificationCommitStatus
{
    Committed,
    IdempotentReplay,
    ValidationFailed,
    PredecessorMismatch,
    ConcurrencyConflict,
    IdempotencyConflict,
    EvidenceInconsistent,
    QualificationInvalid,
    ApprovalInsufficient,
    FailedBeforeRecord,
    RecordPersistedPointerPending,
    RecoveredAndCommitted,
    IntegrityViolation,
    IndeterminateFailure
}

public enum CommitOutcomeState { ConfirmedNo, ConfirmedYes, Unknown }

public static class CertificationCommitReasons
{
    public const string Committed = "CERTIFICATION_COMMIT_COMMITTED";
    public const string IdempotentReplay = "CERTIFICATION_COMMIT_IDEMPOTENT_REPLAY";
    public const string InvalidRequest = "CERTIFICATION_COMMIT_REQUEST_INVALID";
    public const string PredecessorMismatch = "CERTIFICATION_COMMIT_PREDECESSOR_MISMATCH";
    public const string ConcurrencyConflict = "CERTIFICATION_COMMIT_CONCURRENCY_CONFLICT";
    public const string IdempotencyConflict = "CERTIFICATION_COMMIT_IDEMPOTENCY_CONFLICT";
    public const string EvidenceInconsistent = "CERTIFICATION_COMMIT_EVIDENCE_INCONSISTENT";
    public const string QualificationInvalid = "CERTIFICATION_COMMIT_QUALIFICATION_INVALID";
    public const string ApprovalInsufficient = "CERTIFICATION_COMMIT_APPROVAL_INSUFFICIENT";
    public const string FailedBeforeRecord = "CERTIFICATION_COMMIT_FAILED_BEFORE_RECORD";
    public const string PointerPending = "CERTIFICATION_COMMIT_POINTER_PENDING";
    public const string Recovered = "CERTIFICATION_COMMIT_RECOVERED";
    public const string IntegrityViolation = "CERTIFICATION_COMMIT_INTEGRITY_VIOLATION";
    public const string Indeterminate = "CERTIFICATION_COMMIT_INDETERMINATE";
}

public sealed class CertificationCommitResult
{
    public required CertificationCommitStatus Status { get; init; }
    public required string OperationId { get; init; }
    public string? OperationFingerprint { get; init; }
    public required DatabaseIdentity DatabaseIdentity { get; init; }
    public required string CertificationId { get; init; }
    public CertificationPointer? PreviousPointer { get; init; }
    public CertificationPointer? CurrentPointer { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public CommitOutcomeState RecordPersistence { get; init; }
    public CommitOutcomeState PointerAdvance { get; init; }
    public bool RecoveryRequired { get; init; }
    public CertifiedStateRecord? Record { get; init; }
}

public enum CertificationCommitReceiptStatus { Prepared, Committed }

public sealed record CertificationCommitReceipt(
    string OperationId,
    string OperationFingerprint,
    DatabaseIdentity DatabaseIdentity,
    string CertificationId,
    string CertificationEvidenceHash,
    CertificationCommitReceiptStatus Status);

public enum PrepareRecordStatus { Created, AlreadyPrepared, IntegrityViolation }
public enum PointerCasStatus { Advanced, Conflict, IntegrityViolation }

public sealed record PrepareRecordResult(PrepareRecordStatus Status, CertifiedStateRecord? Record);
public sealed record PointerCasResult(PointerCasStatus Status, CertificationPointer? Previous, CertificationPointer? Current);

public interface ICertificationCommitStore
{
    CertificationCommitReceipt? GetReceipt(string operationId);
    bool HasEvidenceConflict(IReadOnlyList<EvidenceReference> evidenceReferences);
    CertifiedStateRecord? GetPrepared(string certificationId);
    CertificationPointer? GetCurrentPointer(DatabaseIdentity databaseIdentity);
    PrepareRecordResult CreatePreparedIfAbsent(
        string operationId,
        string operationFingerprint,
        CertifiedStateRecord record,
        IReadOnlyList<EvidenceReference> evidenceReferences);
    PointerCasResult CompareAndSwapCurrent(
        DatabaseIdentity databaseIdentity,
        ExpectedCertificationPointer? expected,
        string operationId,
        string certificationId);
    bool CompleteReceipt(string operationId, string operationFingerprint);
    CertifiedStateRecord? GetCertifiedById(string certificationId);
    IReadOnlyList<CertifiedStateRecord> ListCertifiedHistory(DatabaseIdentity databaseIdentity);
    int PreparedRecordCount { get; }
    int ReceiptCount { get; }
}

public sealed class InMemoryCertificationCommitStore : ICertificationCommitStore
{
    private sealed record PreparedEntry(
        string OperationId,
        string Fingerprint,
        CertifiedStateRecord Record,
        IReadOnlyList<EvidenceReference> Evidence);

    private readonly object _sync = new();
    private readonly InMemoryCertifiedStateStore _certified = new();
    private readonly Dictionary<string, PreparedEntry> _prepared = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CertificationCommitReceipt> _receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CertificationPointer> _pointers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _evidenceHashes = new(StringComparer.Ordinal);

    public int PreparedRecordCount { get { lock (_sync) return _prepared.Count; } }
    public int ReceiptCount { get { lock (_sync) return _receipts.Count; } }

    public CertificationCommitReceipt? GetReceipt(string operationId)
    {
        lock (_sync) return _receipts.TryGetValue(operationId, out var value) ? Clone(value) : null;
    }

    public bool HasEvidenceConflict(IReadOnlyList<EvidenceReference> evidenceReferences)
    {
        lock (_sync)
            return evidenceReferences.Any(evidence => evidence is not null
                && !string.IsNullOrEmpty(evidence.EvidenceId) &&
                _evidenceHashes.TryGetValue(evidence.EvidenceId, out var hash)
                && !string.Equals(hash, evidence.EvidenceSha256, StringComparison.OrdinalIgnoreCase));
    }

    public CertifiedStateRecord? GetPrepared(string certificationId)
    {
        lock (_sync) return _prepared.TryGetValue(certificationId, out var value) ? CloneRecord(value.Record) : null;
    }

    public CertificationPointer? GetCurrentPointer(DatabaseIdentity databaseIdentity)
    {
        lock (_sync) return _pointers.TryGetValue(Key(databaseIdentity), out var value) ? Clone(value) : null;
    }

    public CertifiedStateRecord? GetCertifiedById(string certificationId) => _certified.GetById(certificationId);

    public IReadOnlyList<CertifiedStateRecord> ListCertifiedHistory(DatabaseIdentity databaseIdentity) =>
        _certified.ListHistory(databaseIdentity);

    public PrepareRecordResult CreatePreparedIfAbsent(
        string operationId,
        string operationFingerprint,
        CertifiedStateRecord record,
        IReadOnlyList<EvidenceReference> evidenceReferences)
    {
        lock (_sync)
        {
            if (_receipts.TryGetValue(operationId, out var receipt)
                && (!string.Equals(receipt.OperationFingerprint, operationFingerprint, StringComparison.Ordinal)
                    || !string.Equals(receipt.CertificationId, record.CertificationId, StringComparison.OrdinalIgnoreCase)))
                return new(PrepareRecordStatus.IntegrityViolation, null);

            if (_prepared.TryGetValue(record.CertificationId, out var prepared))
            {
                var same = string.Equals(prepared.Fingerprint, operationFingerprint, StringComparison.Ordinal)
                    && string.Equals(prepared.Record.CertificationEvidenceHash,
                        record.CertificationEvidenceHash, StringComparison.OrdinalIgnoreCase);
                return new(same ? PrepareRecordStatus.AlreadyPrepared : PrepareRecordStatus.IntegrityViolation,
                    same ? CloneRecord(prepared.Record) : null);
            }

            var certified = _certified.GetById(record.CertificationId);
            if (certified is not null)
                return new(string.Equals(certified.CertificationEvidenceHash, record.CertificationEvidenceHash,
                        StringComparison.OrdinalIgnoreCase)
                    ? PrepareRecordStatus.AlreadyPrepared
                    : PrepareRecordStatus.IntegrityViolation, certified);

            foreach (var evidence in evidenceReferences)
                if (_evidenceHashes.TryGetValue(evidence.EvidenceId, out var hash)
                    && !string.Equals(hash, evidence.EvidenceSha256, StringComparison.OrdinalIgnoreCase))
                    return new(PrepareRecordStatus.IntegrityViolation, null);

            var clonedEvidence = evidenceReferences.Select(e => e with { }).ToArray();
            _prepared.Add(record.CertificationId,
                new PreparedEntry(operationId, operationFingerprint, CloneRecord(record), clonedEvidence));
            _receipts[operationId] = new CertificationCommitReceipt(operationId, operationFingerprint,
                record.DatabaseIdentity, record.CertificationId, record.CertificationEvidenceHash,
                CertificationCommitReceiptStatus.Prepared);
            foreach (var evidence in clonedEvidence)
                _evidenceHashes.TryAdd(evidence.EvidenceId, evidence.EvidenceSha256.ToLowerInvariant());
            return new(PrepareRecordStatus.Created, CloneRecord(record));
        }
    }

    public PointerCasResult CompareAndSwapCurrent(
        DatabaseIdentity databaseIdentity,
        ExpectedCertificationPointer? expected,
        string operationId,
        string certificationId)
    {
        lock (_sync)
        {
            var key = Key(databaseIdentity);
            _pointers.TryGetValue(key, out var current);
            if (!Matches(current, expected))
                return new(PointerCasStatus.Conflict, Clone(current), Clone(current));
            if (!_prepared.TryGetValue(certificationId, out var prepared)
                || !string.Equals(prepared.OperationId, operationId, StringComparison.Ordinal)
                || !CertifiedStateRecordValidator.SameIdentity(prepared.Record.DatabaseIdentity, databaseIdentity))
                return new(PointerCasStatus.IntegrityViolation, Clone(current), Clone(current));

            var append = _certified.Append(prepared.Record);
            if (append.Status != CertifiedStateAppendStatus.Appended)
                return new(PointerCasStatus.IntegrityViolation, Clone(current), Clone(current));

            var next = new CertificationPointer(Clone(databaseIdentity), prepared.Record.CertificationId,
                prepared.Record.CertificationEvidenceHash, (current?.Version ?? 0) + 1);
            _pointers[key] = next;
            _prepared.Remove(certificationId);
            return new(PointerCasStatus.Advanced, Clone(current), Clone(next));
        }
    }

    public bool CompleteReceipt(string operationId, string operationFingerprint)
    {
        lock (_sync)
        {
            if (!_receipts.TryGetValue(operationId, out var receipt)
                || !string.Equals(receipt.OperationFingerprint, operationFingerprint, StringComparison.Ordinal))
                return false;
            var pointer = GetCurrentPointer(receipt.DatabaseIdentity);
            if (pointer is null
                || !string.Equals(pointer.CertificationId, receipt.CertificationId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(pointer.CertificationEvidenceHash, receipt.CertificationEvidenceHash,
                    StringComparison.OrdinalIgnoreCase)) return false;
            _receipts[operationId] = receipt with { Status = CertificationCommitReceiptStatus.Committed };
            return true;
        }
    }

    private static bool Matches(CertificationPointer? current, ExpectedCertificationPointer? expected) =>
        current is null && expected is null
        || current is not null && expected is not null
        && current.Version == expected.Version
        && string.Equals(current.CertificationId, expected.CertificationId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(current.CertificationEvidenceHash, expected.CertificationEvidenceHash,
            StringComparison.OrdinalIgnoreCase);

    private static string Key(DatabaseIdentity identity) => string.Join("|",
        identity.ApplicationId.Trim().ToUpperInvariant(), identity.Environment.Trim().ToUpperInvariant(),
        identity.DatabaseName.Trim().ToUpperInvariant());
    private static DatabaseIdentity Clone(DatabaseIdentity value) => new(value.ApplicationId, value.Environment, value.DatabaseName);
    private static CertificationPointer? Clone(CertificationPointer? value) => value is null ? null
        : value with { DatabaseIdentity = Clone(value.DatabaseIdentity) };
    private static CertificationCommitReceipt Clone(CertificationCommitReceipt value) => value with
        { DatabaseIdentity = Clone(value.DatabaseIdentity) };
    private static CertifiedStateRecord CloneRecord(CertifiedStateRecord record) => record with
    {
        RegistryProvenance = CertifiedStateRecordBuilder.CloneRegistryProvenance(record.RegistryProvenance),
        QualifiedRelease = CertifiedStateRecordBuilder.CloneQualifiedRelease(record.QualifiedRelease),
        RunMetadata = new SortedDictionary<string, string>(record.RunMetadata, StringComparer.Ordinal),
        CanonicalSchemaEvidence = record.CanonicalSchemaEvidence is null ? null
            : CertifiedStateRecordBuilder.CloneCanonicalSchema(record.CanonicalSchemaEvidence)
    };
}

public enum CertificationCommitFaultPoint
{
    BeforeRecordPersistence,
    AfterRecordPersistenceBeforeCas,
    BeforeCompareAndSwap,
    AfterCasBeforeConfirmation
}

public interface ICertificationCommitFaultInjector
{
    void ThrowIfArmed(CertificationCommitFaultPoint point);
}

public sealed class NoCertificationCommitFaults : ICertificationCommitFaultInjector
{
    public void ThrowIfArmed(CertificationCommitFaultPoint point) { }
}

public sealed class InMemoryCertificationCommitFaultInjector(params CertificationCommitFaultPoint[] points)
    : ICertificationCommitFaultInjector
{
    private readonly object _sync = new();
    private readonly HashSet<CertificationCommitFaultPoint> _armed = [.. points];

    public void ThrowIfArmed(CertificationCommitFaultPoint point)
    {
        lock (_sync)
            if (_armed.Remove(point)) throw new CertificationCommitInjectedFaultException(point);
    }
}

public sealed class CertificationCommitInjectedFaultException(CertificationCommitFaultPoint point) : Exception(point.ToString())
{
    public CertificationCommitFaultPoint Point { get; } = point;
}

public sealed class CertificationCommitProtocol(
    ICertificationCommitStore store,
    ICertificationCommitFaultInjector? faults = null)
{
    private static readonly Regex SafeValue = new(@"\A[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}\z",
        RegexOptions.CultureInvariant);
    private readonly ICertificationCommitFaultInjector _faults = faults ?? new NoCertificationCommitFaults();

    public CertificationCommitResult Commit(CertificationCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var progress = new CommitProgress();
        try
        {
            return CommitCore(request, progress);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result(request, null, CertificationCommitStatus.IndeterminateFailure,
                CertificationCommitReasons.Indeterminate, recovery: true,
                recordPersistence: progress.RecordPersistence,
                pointerAdvance: progress.PointerAdvance,
                extraReasons: [SafeExceptionType(exception)]);
        }
    }

    private CertificationCommitResult CommitCore(CertificationCommitRequest request, CommitProgress progress)
    {
        var basic = Validate(request);
        if (basic is not null) return basic;
        var fingerprint = ComputeFingerprint(request);

        if (store.HasEvidenceConflict(request.EvidenceReferences))
            return Result(request, fingerprint, CertificationCommitStatus.IntegrityViolation,
                CertificationCommitReasons.IntegrityViolation);

        var receipt = store.GetReceipt(request.OperationId);
        if (receipt is not null)
        {
            if (!string.Equals(receipt.OperationFingerprint, fingerprint, StringComparison.Ordinal))
                return Result(request, fingerprint, CertificationCommitStatus.IdempotencyConflict,
                    CertificationCommitReasons.IdempotencyConflict);
            if (!ReceiptMatchesRequest(receipt, request))
                return Result(request, fingerprint, CertificationCommitStatus.IntegrityViolation,
                    CertificationCommitReasons.IntegrityViolation);
        }

        var current = store.GetCurrentPointer(request.DatabaseIdentity);
        if (receipt is { Status: CertificationCommitReceiptStatus.Committed })
            return VerifyCommitted(request, fingerprint, current, CertificationCommitStatus.IdempotentReplay,
                CertificationCommitReasons.IdempotentReplay);

        if (PointsToCandidate(current, request.Candidate))
        {
            if (!store.CompleteReceipt(request.OperationId, fingerprint))
                return Result(request, fingerprint, CertificationCommitStatus.IntegrityViolation,
                    CertificationCommitReasons.IntegrityViolation, current: current,
                    recordPersistence: CommitOutcomeState.ConfirmedYes,
                    pointerAdvance: CommitOutcomeState.ConfirmedYes);
            return VerifyCommitted(request, fingerprint, current, CertificationCommitStatus.RecoveredAndCommitted,
                CertificationCommitReasons.Recovered);
        }

        if (!MatchesExpected(current, request.ExpectedPredecessor))
            return Result(request, fingerprint,
                receipt is null ? CertificationCommitStatus.PredecessorMismatch : CertificationCommitStatus.ConcurrencyConflict,
                receipt is null ? CertificationCommitReasons.PredecessorMismatch : CertificationCommitReasons.ConcurrencyConflict,
                current: current,
                recordPersistence: receipt is null ? CommitOutcomeState.ConfirmedNo : CommitOutcomeState.ConfirmedYes);

        try { _faults.ThrowIfArmed(CertificationCommitFaultPoint.BeforeRecordPersistence); }
        catch (CertificationCommitInjectedFaultException)
        {
            return Result(request, fingerprint, CertificationCommitStatus.FailedBeforeRecord,
                CertificationCommitReasons.FailedBeforeRecord, current: current);
        }

        progress.RecordPersistence = CommitOutcomeState.Unknown;
        var prepared = store.CreatePreparedIfAbsent(request.OperationId, fingerprint, request.Candidate,
            request.EvidenceReferences);
        if (prepared.Status == PrepareRecordStatus.IntegrityViolation)
            return Result(request, fingerprint, CertificationCommitStatus.IntegrityViolation,
                CertificationCommitReasons.IntegrityViolation, current: current,
                recordPersistence: CommitOutcomeState.Unknown);
        progress.RecordPersistence = CommitOutcomeState.ConfirmedYes;

        try { _faults.ThrowIfArmed(CertificationCommitFaultPoint.AfterRecordPersistenceBeforeCas); }
        catch (CertificationCommitInjectedFaultException)
        {
            return Result(request, fingerprint, CertificationCommitStatus.RecordPersistedPointerPending,
                CertificationCommitReasons.PointerPending, current: current,
                recordPersistence: CommitOutcomeState.ConfirmedYes,
                pointerAdvance: CommitOutcomeState.ConfirmedNo, recovery: true);
        }

        _faults.ThrowIfArmed(CertificationCommitFaultPoint.BeforeCompareAndSwap);
        progress.PointerAdvance = CommitOutcomeState.Unknown;
        var cas = store.CompareAndSwapCurrent(request.DatabaseIdentity, request.ExpectedPredecessor,
            request.OperationId, request.Candidate.CertificationId);
        if (cas.Status == PointerCasStatus.Conflict)
        {
            progress.PointerAdvance = CommitOutcomeState.ConfirmedNo;
            return Result(request, fingerprint, CertificationCommitStatus.ConcurrencyConflict,
                CertificationCommitReasons.ConcurrencyConflict, previous: cas.Previous, current: cas.Current,
                recordPersistence: CommitOutcomeState.ConfirmedYes,
                pointerAdvance: CommitOutcomeState.ConfirmedNo);
        }
        if (cas.Status == PointerCasStatus.IntegrityViolation)
            return Result(request, fingerprint, CertificationCommitStatus.IntegrityViolation,
                CertificationCommitReasons.IntegrityViolation, previous: cas.Previous, current: cas.Current,
                recordPersistence: CommitOutcomeState.ConfirmedYes,
                pointerAdvance: CommitOutcomeState.Unknown);
        progress.PointerAdvance = CommitOutcomeState.ConfirmedYes;

        try { _faults.ThrowIfArmed(CertificationCommitFaultPoint.AfterCasBeforeConfirmation); }
        catch (CertificationCommitInjectedFaultException)
        {
            return Result(request, fingerprint, CertificationCommitStatus.IndeterminateFailure,
                CertificationCommitReasons.Indeterminate, previous: cas.Previous, current: cas.Current,
                recordPersistence: CommitOutcomeState.ConfirmedYes,
                pointerAdvance: CommitOutcomeState.ConfirmedYes, recovery: true);
        }

        var verified = VerifyCommitted(request, fingerprint, store.GetCurrentPointer(request.DatabaseIdentity),
            prepared.Status == PrepareRecordStatus.AlreadyPrepared
                ? CertificationCommitStatus.RecoveredAndCommitted : CertificationCommitStatus.Committed,
            prepared.Status == PrepareRecordStatus.AlreadyPrepared
                ? CertificationCommitReasons.Recovered : CertificationCommitReasons.Committed,
            cas.Previous);
        if (verified.Status is CertificationCommitStatus.Committed or CertificationCommitStatus.RecoveredAndCommitted)
            if (!store.CompleteReceipt(request.OperationId, fingerprint))
                return Result(request, fingerprint, CertificationCommitStatus.IndeterminateFailure,
                    CertificationCommitReasons.Indeterminate, previous: cas.Previous, current: cas.Current,
                    recordPersistence: CommitOutcomeState.ConfirmedYes,
                    pointerAdvance: CommitOutcomeState.ConfirmedYes, recovery: true);
        return verified;
    }

    public static string ComputeFingerprint(CertificationCommitRequest request)
    {
        var semantic = new
        {
            database = new
            {
                applicationId = request.DatabaseIdentity.ApplicationId.Trim().ToUpperInvariant(),
                environment = request.DatabaseIdentity.Environment.Trim().ToUpperInvariant(),
                databaseName = request.DatabaseIdentity.DatabaseName.Trim().ToUpperInvariant()
            },
            predecessor = request.ExpectedPredecessor is null ? null : new
            {
                certificationId = request.ExpectedPredecessor.CertificationId.ToLowerInvariant(),
                certificationEvidenceHash = request.ExpectedPredecessor.CertificationEvidenceHash.ToLowerInvariant(),
                request.ExpectedPredecessor.Version
            },
            certificationId = request.Candidate.CertificationId.ToLowerInvariant(),
            certificationEvidenceHash = request.Candidate.CertificationEvidenceHash.ToLowerInvariant(),
            releaseOrCorrelationId = request.ReleaseOrCorrelationId,
            evidence = request.EvidenceReferences
                .Select(e => new { e.EvidenceId, hash = e.EvidenceSha256.ToLowerInvariant() })
                .Distinct()
                .OrderBy(e => e.EvidenceId, StringComparer.Ordinal)
                .ThenBy(e => e.hash, StringComparer.Ordinal)
                .ToArray(),
            authorization = new
            {
                request.Authorization.ActorId,
                request.Authorization.Mechanism,
                request.Authorization.PolicyId,
                request.Authorization.AuthorityReference
            }
        };
        return Hashing.Sha256(JsonSerializer.Serialize(semantic, JsonDefaults.Compact));
    }

    private CertificationCommitResult? Validate(CertificationCommitRequest request)
    {
        if (request.DatabaseIdentity is null || request.Candidate is null || request.Certification is null
            || request.Authorization is null || request.EvidenceReferences is null
            || request.EvidenceReferences.Any(evidence => evidence is null)
            || !Safe(request.OperationId) || !Safe(request.ReleaseOrCorrelationId)
            || !Safe(request.Authorization.ActorId) || !Safe(request.Authorization.Mechanism)
            || !Safe(request.Authorization.PolicyId) || !Safe(request.Authorization.AuthorityReference)
            || !CertifiedStateRecordValidator.SameIdentity(request.DatabaseIdentity, request.Candidate.DatabaseIdentity)
            || !CandidateMatchesPredecessor(request))
            return Result(request, null, CertificationCommitStatus.ValidationFailed,
                CertificationCommitReasons.InvalidRequest);

        if (!request.Certification.ProducesCertifiedState)
        {
            var approval = request.Certification.DecisionReason is
                CertificationDecisionReasons.HumanApprovalEvidenceRequired
                or CertificationDecisionReasons.RequiredApproverNotSatisfied;
            return Result(request, null,
                approval ? CertificationCommitStatus.ApprovalInsufficient : CertificationCommitStatus.QualificationInvalid,
                approval ? CertificationCommitReasons.ApprovalInsufficient : CertificationCommitReasons.QualificationInvalid);
        }

        var decisionEvidenceHash = Hashing.Sha256(JsonSerializer.Serialize(
            request.Certification.Evidence, JsonDefaults.Compact));
        if (request.Candidate.CertificationOrigin != request.Certification.Origin
            || request.Candidate.CertificationDecision != request.Certification.Decision
            || !string.Equals(request.Candidate.CanonicalSchemaHash,
                request.Certification.NextCertifiedSchemaHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(request.Candidate.DecisionEvidence.EvidenceSha256,
                decisionEvidenceHash, StringComparison.OrdinalIgnoreCase))
            return Result(request, null, CertificationCommitStatus.QualificationInvalid,
                CertificationCommitReasons.QualificationInvalid);

        var validation = CertifiedStateRecordValidator.Validate(request.Candidate);
        if (validation.Count > 0)
        {
            var evidence = validation.Any(reason => reason.Contains("EVIDENCE", StringComparison.Ordinal)
                || reason.Contains("HASH", StringComparison.Ordinal));
            return Result(request, null,
                evidence ? CertificationCommitStatus.EvidenceInconsistent : CertificationCommitStatus.ValidationFailed,
                evidence ? CertificationCommitReasons.EvidenceInconsistent : CertificationCommitReasons.InvalidRequest,
                extraReasons: validation);
        }

        var candidateEvidence = CollectEvidence(request.Candidate)
            .Select(e => $"{e.EvidenceId}|{e.EvidenceSha256.ToLowerInvariant()}")
            .Order(StringComparer.Ordinal).ToArray();
        var suppliedEvidence = request.EvidenceReferences
            .Select(e => $"{e.EvidenceId}|{e.EvidenceSha256.ToLowerInvariant()}")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!candidateEvidence.SequenceEqual(suppliedEvidence, StringComparer.Ordinal)
            || request.EvidenceReferences.GroupBy(e => e.EvidenceId, StringComparer.Ordinal)
                .Any(group => group.Select(e => e.EvidenceSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            return Result(request, null, CertificationCommitStatus.EvidenceInconsistent,
                CertificationCommitReasons.EvidenceInconsistent);

        if (request.Candidate.QualifiedRelease is not null
            && !string.Equals(request.Candidate.QualifiedRelease.ReleaseId,
                request.ReleaseOrCorrelationId, StringComparison.Ordinal))
            return Result(request, null, CertificationCommitStatus.ValidationFailed,
                CertificationCommitReasons.InvalidRequest);
        return null;
    }

    public static IReadOnlyList<EvidenceReference> CollectEvidence(CertifiedStateRecord record)
    {
        var result = new List<EvidenceReference>
        {
            record.CanonicalSchemaEvidenceReference, record.DecisionEvidence,
            record.TransitionEvidence, record.LineageEvidence
        };
        if (record.ReconciliationEvidence is not null) result.Add(record.ReconciliationEvidence);
        if (record.QualifiedRelease is not null)
        {
            result.Add(record.QualifiedRelease.QualificationEvidence);
            result.Add(record.QualifiedRelease.DeploymentAuthorizationEvidence);
            result.Add(record.QualifiedRelease.ExecutionEvidence);
        }
        return result.GroupBy(e => $"{e.EvidenceId}|{e.EvidenceSha256}", StringComparer.Ordinal)
            .Select(group => group.First() with { }).OrderBy(e => e.EvidenceId, StringComparer.Ordinal).ToArray();
    }

    private CertificationCommitResult VerifyCommitted(
        CertificationCommitRequest request, string fingerprint, CertificationPointer? current,
        CertificationCommitStatus success, string reason, CertificationPointer? previous = null)
    {
        var record = store.GetCertifiedById(request.Candidate.CertificationId);
        if (!PointsToCandidate(current, request.Candidate) || record is null
            || !string.Equals(record.CertificationEvidenceHash,
                request.Candidate.CertificationEvidenceHash, StringComparison.OrdinalIgnoreCase)
            || CertifiedStateRecordValidator.Validate(record).Count > 0)
            return Result(request, fingerprint, CertificationCommitStatus.IntegrityViolation,
                CertificationCommitReasons.IntegrityViolation, previous, current,
                recordPersistence: CommitOutcomeState.ConfirmedYes,
                pointerAdvance: PointsToCandidate(current, request.Candidate)
                    ? CommitOutcomeState.ConfirmedYes : CommitOutcomeState.Unknown,
                recovery: true);
        return Result(request, fingerprint, success, reason, previous, current,
            recordPersistence: CommitOutcomeState.ConfirmedYes,
            pointerAdvance: CommitOutcomeState.ConfirmedYes, record: record);
    }

    private static bool CandidateMatchesPredecessor(CertificationCommitRequest request) =>
        request.ExpectedPredecessor is null
            ? request.Candidate.PreviousCertificationId is null
              && request.Candidate.PreviousCertificationEvidenceHash is null
            : string.Equals(request.Candidate.PreviousCertificationId,
                request.ExpectedPredecessor.CertificationId, StringComparison.OrdinalIgnoreCase)
              && string.Equals(request.Candidate.PreviousCertificationEvidenceHash,
                  request.ExpectedPredecessor.CertificationEvidenceHash, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesExpected(CertificationPointer? current, ExpectedCertificationPointer? expected) =>
        current is null && expected is null
        || current is not null && expected is not null && current.Version == expected.Version
        && string.Equals(current.CertificationId, expected.CertificationId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(current.CertificationEvidenceHash, expected.CertificationEvidenceHash,
            StringComparison.OrdinalIgnoreCase);

    private static bool PointsToCandidate(CertificationPointer? pointer, CertifiedStateRecord candidate) =>
        pointer is not null
        && string.Equals(pointer.CertificationId, candidate.CertificationId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(pointer.CertificationEvidenceHash, candidate.CertificationEvidenceHash,
            StringComparison.OrdinalIgnoreCase);

    private static bool ReceiptMatchesRequest(
        CertificationCommitReceipt receipt, CertificationCommitRequest request) =>
        string.Equals(receipt.OperationId, request.OperationId, StringComparison.Ordinal)
        && CertifiedStateRecordValidator.SameIdentity(receipt.DatabaseIdentity, request.DatabaseIdentity)
        && string.Equals(receipt.CertificationId, request.Candidate.CertificationId,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(receipt.CertificationEvidenceHash,
            request.Candidate.CertificationEvidenceHash, StringComparison.OrdinalIgnoreCase)
        && receipt.Status is CertificationCommitReceiptStatus.Prepared
            or CertificationCommitReceiptStatus.Committed;

    private static bool Safe(string? value) => value is not null && SafeValue.IsMatch(value);

    private static string SafeExceptionType(Exception exception) => exception switch
    {
        InvalidOperationException => nameof(InvalidOperationException),
        IOException => nameof(IOException),
        TimeoutException => nameof(TimeoutException),
        OperationCanceledException => nameof(OperationCanceledException),
        _ => nameof(Exception)
    };

    private static CertificationCommitResult Result(
        CertificationCommitRequest request, string? fingerprint, CertificationCommitStatus status,
        string reason, CertificationPointer? previous = null, CertificationPointer? current = null,
        CommitOutcomeState recordPersistence = CommitOutcomeState.ConfirmedNo,
        CommitOutcomeState pointerAdvance = CommitOutcomeState.ConfirmedNo,
        bool recovery = false,
        CertifiedStateRecord? record = null, IEnumerable<string>? extraReasons = null) => new()
    {
        Status = status,
        OperationId = request.OperationId ?? "",
        OperationFingerprint = fingerprint,
        DatabaseIdentity = request.DatabaseIdentity ?? new DatabaseIdentity("", "", ""),
        CertificationId = request.Candidate?.CertificationId ?? "",
        PreviousPointer = previous,
        CurrentPointer = current,
        Reasons = new[] { reason }.Concat(extraReasons ?? []).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray(),
        RecordPersistence = recordPersistence,
        PointerAdvance = pointerAdvance,
        RecoveryRequired = recovery,
        Record = record
    };

    private sealed class CommitProgress
    {
        public CommitOutcomeState RecordPersistence { get; set; } = CommitOutcomeState.ConfirmedNo;
        public CommitOutcomeState PointerAdvance { get; set; } = CommitOutcomeState.ConfirmedNo;
    }
}
