using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record LegacyRehearsalRequestV1(int ContractVersion,
    LegacyArtifactSelectionV1 ArtifactSelection, string TargetId,
    string ExpectedPackageIdentity, string AuthorizationSelector,
    string? DataContractSelector = null, bool ExecutionAuthorized = false);

public static class LegacyRehearsalCli
{
    internal static readonly JsonSerializerOptions Strict = new(JsonDefaults.Compact) {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static async Task<int> RunAsync(string[] args, bool rehearse = true)
    {
        if (args.Length != 4 || args[0] != "--request" || args[2] != "--output") return 65;
        var output = Path.GetFullPath(args[3]);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var token = cancelled.Token;
            var request = Parse<LegacyRehearsalRequestV1>(await File.ReadAllBytesAsync(args[1], token));
            if (request.ContractVersion != 1 || request.ExecutionAuthorized != rehearse
                || request.ArtifactSelection.ContractVersion != 1
                || !Regex.IsMatch(request.ExpectedPackageIdentity, @"\Alpqv1:[0-9a-f]{64}\z")
                || rehearse && Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") != "1")
                throw new LegacyContractException("EXECUTION_AUTHORIZATION_INVALID");
            ValidateSelector(request.AuthorizationSelector);
            if (request.DataContractSelector is not null) ValidateSelector(request.DataContractSelector);
            var mutationConnection = rehearse ? Environment.GetEnvironmentVariable("LEGACY_MUTATION_CONNECTION_STRING") : null;
            // Child read-only producers must never inherit the mutating credential.
            Environment.SetEnvironmentVariable("LEGACY_MUTATION_CONNECTION_STRING", null);
            var session = new LegacyProductionRuntimeAcquisition();
            var git = new ProcessLegacyGitTransport(session.ApplicationRoot);
            var resolver = new TrustedLegacyRuntimeEvidenceResolver(session, git);
            var adapter = new LegacyPackageQualificationAdapter(resolver, git,
                new Safety(session), new Security(session));
            var qualificationRequest = new LegacyQualificationRequestV1(1, request.ArtifactSelection, request.TargetId, null);
            var qualification = await adapter.EvaluateAsync(qualificationRequest, token);
            LegacyDataValidationDescriptorV1? data = null;
            if (qualification.StaticSafety is { Impact.DataRequired: true } safety)
            {
                if (request.DataContractSelector is null) throw new LegacyContractException("DATA_VALIDATOR_REQUIRED");
                var (scope, hash) = LegacyReadinessHash.DataScope(safety.ForwardAnalysis, safety.RollbackAnalysis);
                var source = await session.ReadGovernedDocumentAsync(
                    ".github/workflows/legacy-data-contracts/" + request.DataContractSelector + ".json", token);
                var definition = Parse<DataDefinitionDocument>(source.Bytes);
                if (definition.Selector != request.DataContractSelector
                    || LegacyRuntimeEvidenceHash.Hash(definition.Scope) != hash)
                    throw new LegacyContractException("DATA_PROVIDER_CONTRACT_INVALID");
                var governed = new LegacyDataContractDefinitionV1(definition.ContractVersion,
                    definition.Selector, definition.Version, definition.TargetId, hash,
                    definition.ProviderKind, definition.Equality, source.Document, scope, definition.MaximumRowsPerTable);
                var binding = qualification.TrustedRuntime!.Evidence!.Governance.Binding;
                var dataResolver = new LegacyDataContractResolverV1([governed],
                    new Dictionary<string, Func<LegacyDataContractDefinitionV1, ILegacyDataEvidenceReaderV1>> {
                        ["SCOPED_ROWSET_SHA256_V1"] = _ => new SqlLegacyDataEvidenceReaderV1(session.InspectionConnection, binding)
                    });
                data = dataResolver.Resolve(request.DataContractSelector, request.TargetId, hash,
                    source.Document.Repository, source.Document.Revision);
                qualificationRequest = qualificationRequest with { DataValidationDescriptor = data };
                qualification = await adapter.EvaluateAsync(qualificationRequest, token);
            }
            else if (request.DataContractSelector is not null)
                throw new LegacyContractException("DATA_PROVIDER_NOT_REQUIRED");
            LegacyRehearsalHarnessV1.VerifyQualification(qualification);
            if (qualification.Package!.Evidence.PackageIdentity != request.ExpectedPackageIdentity)
                throw new LegacyContractException("REQUALIFICATION_REQUIRED");
            if (!rehearse)
            {
                if (data?.Provider is LegacyDataValidationProviderV1 preflightData)
                    await preflightData.CapturePreDataAsync(token);
                Directory.CreateDirectory(output);
                var h = qualification.Readiness!.Handoff!;
                WriteNew(output,"legacy-rehearsal-preflight.json",new {
                    contractVersion=1, status="READY_FOR_AUTHORIZATION", executionAuthorized=false,
                    targetId=h.TargetId, packageIdentity=h.PackageIdentity, engineCommit=h.EngineCommit,
                    preconditionIdentity=GovernedLegacyRehearsalAuthorityV1.PreconditionIdentity(h),
                    dataContractIdentity=(data?.Provider as LegacyDataValidationProviderV1)?.ApprovalIdentity,
                    qualification=qualification.Readiness, trustedRuntime=qualification.TrustedRuntime
                });
                return 0;
            }
            var grantSource = await session.ReadGovernedDocumentAsync(
                ".github/workflows/legacy-rehearsal-authorizations/" + request.AuthorizationSelector + ".json", token);
            var grant = Parse<LegacyRehearsalAuthorizationV1>(grantSource.Bytes);
            if (grant.AuthorizationId != request.AuthorizationSelector)
                throw new LegacyContractException("EXECUTION_AUTHORIZATION_INVALID");
            async Task Freshness(CancellationToken ct)
            {
                await resolver.VerifyFreshnessAsync(qualificationRequest, ct);
                var package = await new LegacyArtifactDiscovery(git).DiscoverAsync(
                    request.ArtifactSelection, request.TargetId, ct);
                if (package.Evidence.PackageIdentity != request.ExpectedPackageIdentity)
                    throw new LegacyContractException("REQUALIFICATION_REQUIRED");
            }
            var authority = new GovernedLegacyRehearsalAuthorityV1(grant, grantSource.Document,
                grantSource.Document.Repository, grantSource.Document.Revision,
                Environment.GetEnvironmentVariable("GITHUB_ACTOR") ?? "", true, Freshness,
                (data?.Provider as LegacyDataValidationProviderV1)?.ApprovalIdentity);
            await authority.VerifyAsync(qualification.Readiness!.Handoff!, "PRE", token);
            var runtime = new SqlLegacyRehearsalRuntimeV1(qualification, session.InspectionConnection,
                () => mutationConnection ?? throw new LegacyContractException("MUTATION_CONNECTION_REQUIRED"),
                Freshness, qualification.TrustedRuntime!.Runtime!, authority);
            // Append-only local journal persists STARTED before each mutation.
            // Absence of a final receipt can never mean success.
            Directory.CreateDirectory(output);
            using var journal = new FileStream(Path.Combine(output, "phase-journal.jsonl"), FileMode.CreateNew, FileAccess.Write);
            void Append(LegacyPhaseReceiptV1 phase)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(phase, JsonDefaults.Compact) + "\n");
                journal.Write(bytes); journal.Flush(true);
            }
            var receipt = await new LegacyRehearsalHarnessV1(runtime, authority, Append)
                .RunAsync(qualification, TimeSpan.FromMinutes(20), data, token);
            WriteNew(output, "legacy-rehearsal-receipt.json", receipt);
            if (receipt.Status == "REHEARSAL_COMPLETE")
                WriteNew(output, "legacy-promotion-freeze.json", LegacyPromotionFreeze.CreateFromTrustedProducer(receipt));
            return receipt.Status == "REHEARSAL_COMPLETE" ? 0 : 66;
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(output);
            var code = exception is LegacyContractException contract ? contract.Code
                : exception is OperationCanceledException ? "REHEARSAL_CANCELLED" : "REHEARSAL_TECHNICAL_ERROR";
            WriteNew(output, "legacy-rehearsal-blocked.json", new { contractVersion = 1, status = "BLOCKED", reason = code });
            Console.Error.WriteLine("LEGACY_REHEARSAL_BLOCKED:" + code);
            return exception is OperationCanceledException ? 130 : 66;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    internal static T Parse<T>(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 65536) throw new LegacyContractException("CONTRACT_INVALID");
        using var document = JsonDocument.Parse(bytes);
        CheckUnique(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Strict) ?? throw new LegacyContractException("CONTRACT_INVALID");
    }
    private static void CheckUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new LegacyContractException("CONTRACT_DUPLICATE_PROPERTY");
                CheckUnique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckUnique(item);
    }
    private static void ValidateSelector(string selector)
    {
        if (!Regex.IsMatch(selector, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z"))
            throw new LegacyContractException("CONTRACT_SELECTOR_INVALID");
    }
    private static void WriteNew<T>(string output, string name, T value)
    {
        using var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, value, JsonDefaults.Compact);
        stream.Flush(true);
    }
    private sealed record DataDefinitionDocument(int ContractVersion, string Selector, int Version,
        string TargetId, string ProviderKind, string Equality, LegacyDataScopeV1 Scope, int MaximumRowsPerTable);
    private sealed class Safety(LegacyProductionRuntimeAcquisition session) : ILegacyScopeSafetySource
    {
        public Task<LegacyScopeSafetySnapshotV1> CaptureAsync(IReadOnlyList<RecoverySecuritySecurable> scope, CancellationToken token) =>
            new SqlLegacyScopeSafetySource(session.SecurityTransport(), session.Binding()).CaptureAsync(scope, token);
    }
    private sealed class Security(LegacyProductionRuntimeAcquisition session) : IRecoverySecurityCatalogReader
    {
        public Task<RecoverySecuritySnapshot> ReadAsync(RecoverySecurityScope scope, RecoveryPhase phase, CancellationToken cancellationToken = default) =>
            new SqlRecoverySecurityCatalogReader(session.SecurityTransport(), session.Binding()).ReadAsync(scope, phase, cancellationToken);
    }
}
