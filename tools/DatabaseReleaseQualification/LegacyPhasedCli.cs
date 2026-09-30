using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record LegacyPhasedRequestV1(int ContractVersion,
    LegacyArtifactSelectionV1 ArtifactSelection, string TargetId,
    string ExpectedPackageIdentity, string? DataContractSelector);

public static class LegacyPhasedCli
{
    internal static readonly JsonSerializerOptions CheckpointJson = new(JsonDefaults.Compact) {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    public static async Task<int> RunAsync(string[] args, string phase)
    {
        var continuation = phase != "PRE";
        if ((!continuation && args.Length != 4) || continuation && args.Length != 8
            || args[0] != "--request" || args[2] != "--output"
            || continuation && (args[4] != "--checkpoint" || args[6] != "--checkpoint-hash"))
            return 65;
        var output = Path.GetFullPath(args[3]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var token = deadline.Token;
            var request = LegacyRehearsalCli.Parse<LegacyPhasedRequestV1>(
                await File.ReadAllBytesAsync(args[1], token));
            if (request.ContractVersion != 1 || request.ArtifactSelection.ContractVersion != 1
                || !Regex.IsMatch(request.ExpectedPackageIdentity, @"\Alpqv1:[0-9a-f]{64}\z")
                || Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") != "1")
                throw new LegacyContractException("EXECUTION_AUTHORIZATION_INVALID");
            if (request.DataContractSelector is not null
                && !Regex.IsMatch(request.DataContractSelector,
                    @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z"))
                throw new LegacyContractException("CONTRACT_SELECTOR_INVALID");
            var mutationConnection = continuation
                ? Environment.GetEnvironmentVariable("LEGACY_MUTATION_CONNECTION_STRING") : null;
            Environment.SetEnvironmentVariable("LEGACY_MUTATION_CONNECTION_STRING", null);
            var session = new LegacyProductionRuntimeAcquisition();
            var git = new ProcessLegacyGitTransport(session.ApplicationRoot);
            var resolver = new TrustedLegacyRuntimeEvidenceResolver(session, git);
            var qualificationRequest = new LegacyQualificationRequestV1(1,
                request.ArtifactSelection, request.TargetId, null);
            LegacyQualificationOutcomeV1 qualification;
            LegacyFrozenPackage package;
            LegacyRehearsalCheckpointV1? previous = null;
            LegacyAcquiredRuntimeV1? phaseSource = null;
            if (!continuation)
            {
                var adapter = new LegacyPackageQualificationAdapter(resolver, git,
                    new Safety(session), new Security(session));
                qualification = await adapter.EvaluateAsync(qualificationRequest, token);
                if (qualification.StaticSafety is { Impact.DataRequired: true } safety)
                {
                    var data = await ResolveData(session, qualification, request.DataContractSelector, token);
                    qualificationRequest = qualificationRequest with { DataValidationDescriptor = data };
                    qualification = await adapter.EvaluateAsync(qualificationRequest, token);
                }
                LegacyRehearsalHarnessV1.VerifyQualification(qualification);
                package = qualification.Package!;
            }
            else
            {
                previous = LegacyRehearsalCli.Parse<LegacyRehearsalCheckpointV1>(
                    await File.ReadAllBytesAsync(args[5], token), 8 * 1024 * 1024);
                // Reacquire current governed sources and SQL identity without
                // requiring the post-mutation schema to equal the PRE baseline.
                phaseSource = await session.AcquireForPhaseAsync(
                    new(1, request.TargetId, request.ArtifactSelection), token);
                package = await new LegacyArtifactDiscovery(git).DiscoverAsync(
                    request.ArtifactSelection, request.TargetId, token);
                qualification = LegacyPhaseCheckpoint.Rehydrate(previous, package);
                VerifySource(previous, phaseSource);
            }
            if (package.Evidence.PackageIdentity != request.ExpectedPackageIdentity)
                throw new LegacyContractException("REQUALIFICATION_REQUIRED");
            var handoff = qualification.Readiness!.Handoff!;
            var dataProvider = handoff.Recovery.Data == "REQUIRED"
                ? (await ResolveData(session, qualification, request.DataContractSelector, token)).Provider
                    as LegacyDataValidationProviderV1
                : null;
            if (handoff.Recovery.Data != "REQUIRED" && request.DataContractSelector is not null)
                throw new LegacyContractException("DATA_PROVIDER_NOT_REQUIRED");
            async Task Freshness(CancellationToken ct)
            {
                await session.VerifyFreshnessAsync(
                    new(1, request.TargetId, request.ArtifactSelection), ct);
                var currentPackage = await new LegacyArtifactDiscovery(git).DiscoverAsync(
                    request.ArtifactSelection, request.TargetId, ct);
                if (currentPackage.Evidence.PackageIdentity != package.Evidence.PackageIdentity)
                    throw new LegacyContractException("REQUALIFICATION_REQUIRED");
            }
            var context = continuation ? phaseSource!.Runtime : qualification.TrustedRuntime!.Runtime!;
            var authority = new ProtectedEnvironmentLegacyAuthorityV1(phase, context);
            var runtime = new SqlLegacyRehearsalRuntimeV1(qualification, session.InspectionConnection,
                () => mutationConnection ?? throw new LegacyContractException("MUTATION_CONNECTION_REQUIRED"),
                Freshness, context, authority, continuation ? phase : null);
            Directory.CreateDirectory(output);
            var journalPath = Path.Combine(output, "phase-journal.jsonl");
            using var journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write);
            void Append(string status, string? reason = null)
            {
                var entry = new { contractVersion = 1, phase, status, reason,
                    runId = context.RunId, jobId = context.JobId, atUtc = DateTimeOffset.UtcNow };
                var bytes = System.Text.Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(entry, JsonDefaults.Compact) + "\n");
                journal.Write(bytes); journal.Flush(true);
            }
            Append("JOB_STARTED");
            var engine = new LegacyPhasedRehearsalV1(runtime, authority, status => Append(status));
            if (!continuation)
            {
                var checkpoint = await engine.CapturePreAsync(qualification, dataProvider, token);
                WriteNew(output, "legacy-phase-checkpoint.json", checkpoint);
                Console.WriteLine("CHECKPOINT_HASH=" + checkpoint.CheckpointHash);
            }
            else
            {
                var result = await engine.RunNextAsync(previous!, args[7], phase,
                    package, dataProvider, token);
                if (result.Checkpoint is { } checkpoint)
                {
                    WriteNew(output, "legacy-phase-checkpoint.json", checkpoint);
                    Console.WriteLine("CHECKPOINT_HASH=" + checkpoint.CheckpointHash);
                }
                if (result.Receipt is { } receipt)
                {
                    WriteNew(output, "legacy-rehearsal-receipt.json", receipt);
                    WriteNew(output, "legacy-promotion-freeze.json",
                        LegacyPromotionFreeze.CreateFromTrustedProducer(receipt));
                }
            }
            Append("COMPLETE");
            return 0;
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(output);
            var reason = error is LegacyContractException contract ? contract.Code
                : error is OperationCanceledException ? "REHEARSAL_CANCELLED" : "REHEARSAL_TECHNICAL_ERROR";
            WriteNew(output, "legacy-phase-blocked.json",
                new { contractVersion = 1, phase, status = "BLOCKED", reason });
            Console.Error.WriteLine("LEGACY_PHASE_BLOCKED:" + reason);
            return error is OperationCanceledException ? 130 : 66;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static void VerifySource(LegacyRehearsalCheckpointV1 checkpoint,
        LegacyAcquiredRuntimeV1 source)
    {
        var original = checkpoint.Qualification.TrustedRuntime.Evidence
            ?? throw new LegacyContractException("PHASE_CHECKPOINT_INVALID");
        if (source.Governance.Environment != "TEST"
            || LegacyRuntimeEvidenceHash.Hash(source.Governance)
                != LegacyRuntimeEvidenceHash.Hash(original.Governance)
            || LegacyRuntimeEvidenceHash.Hash(source.Onboarding)
                != LegacyRuntimeEvidenceHash.Hash(original.Onboarding)
            || LegacyRuntimeEvidenceHash.Hash(source.RegistryProvenance)
                != LegacyRuntimeEvidenceHash.Hash(original.CertifiedStructuralBaseline.RegistryProvenance)
            || source.EngineCommit != checkpoint.Qualification.Readiness.Handoff?.EngineCommit
            || source.HashContract != checkpoint.Qualification.InitialObservation.HashContract
            || source.Runtime.WorkflowRevision != checkpoint.Runtime.WorkflowRevision
            || source.ApplicationCommit != checkpoint.Qualification.TrustedRuntime.Request
                .ArtifactSelection.ExpectedCommit)
            throw new LegacyContractException("PHASE_SOURCE_CHANGED");
    }

    private static async Task<LegacyDataValidationDescriptorV1> ResolveData(
        LegacyProductionRuntimeAcquisition session, LegacyQualificationOutcomeV1 qualification,
        string? selector, CancellationToken token)
    {
        if (selector is null) throw new LegacyContractException("DATA_VALIDATOR_REQUIRED");
        var safety = qualification.StaticSafety
            ?? throw new LegacyContractException("DATA_VALIDATOR_REQUIRED");
        var (scope, hash) = LegacyReadinessHash.DataScope(
            safety.ForwardAnalysis, safety.RollbackAnalysis);
        var source = await session.ReadGovernedDocumentAsync(
            ".github/workflows/legacy-data-contracts/" + selector + ".json", token);
        var definition = LegacyRehearsalCli.Parse<DataDefinitionDocument>(source.Bytes);
        if (definition.Selector != selector
            || LegacyRuntimeEvidenceHash.Hash(definition.Scope) != hash)
            throw new LegacyContractException("DATA_PROVIDER_CONTRACT_INVALID");
        var governed = new LegacyDataContractDefinitionV1(definition.ContractVersion,
            definition.Selector, definition.Version, definition.TargetId, hash,
            definition.ProviderKind, definition.Equality, source.Document, scope,
            definition.MaximumRowsPerTable);
        var binding = qualification.TrustedRuntime!.Evidence!.Governance.Binding;
        return new LegacyDataContractResolverV1([governed],
            new Dictionary<string, Func<LegacyDataContractDefinitionV1, ILegacyDataEvidenceReaderV1>> {
                ["SCOPED_ROWSET_SHA256_V1"] = _ =>
                    new SqlLegacyDataEvidenceReaderV1(session.InspectionConnection, binding)
            }).Resolve(selector, qualification.TrustedRuntime!.Request.TargetId, hash,
                source.Document.Repository, source.Document.Revision);
    }

    private static void WriteNew<T>(string output, string name, T value)
    {
        using var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, value, CheckpointJson);
        stream.Flush(true);
    }

    private sealed record DataDefinitionDocument(int ContractVersion, string Selector, int Version,
        string TargetId, string ProviderKind, string Equality, LegacyDataScopeV1 Scope,
        int MaximumRowsPerTable);
    private sealed class Safety(LegacyProductionRuntimeAcquisition session) : ILegacyScopeSafetySource
    {
        public Task<LegacyScopeSafetySnapshotV1> CaptureAsync(
            IReadOnlyList<RecoverySecuritySecurable> scope, CancellationToken token) =>
            new SqlLegacyScopeSafetySource(session.SecurityTransport(), session.Binding())
                .CaptureAsync(scope, token);
    }
    private sealed class Security(LegacyProductionRuntimeAcquisition session) : IRecoverySecurityCatalogReader
    {
        public Task<RecoverySecuritySnapshot> ReadAsync(RecoverySecurityScope scope,
            RecoveryPhase phase, CancellationToken token = default) =>
            new SqlRecoverySecurityCatalogReader(session.SecurityTransport(), session.Binding())
                .ReadAsync(scope, phase, token);
    }
}
