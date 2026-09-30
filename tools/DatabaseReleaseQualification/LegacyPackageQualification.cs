using System.Text.Json;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record LegacyQualificationRequestV1(int ContractVersion,
    LegacyArtifactSelectionV1 ArtifactSelection, string TargetId,
    LegacyDataValidationDescriptorV1? DataValidationDescriptor);

public sealed record LegacyVerifiedEvidenceV1(
    LegacyRepositoryV1 Repository, string Commit, string EngineCommit,
    LegacyGovernanceV1 Governance, byte[] GovernanceBytes,
    LegacyOnboardingV1 Onboarding, byte[] OnboardingBytes,
    string ClassificationScenario, byte[] ClassificationBytes,
    DatabaseStateEvaluation DatabaseState, byte[] RegistryBytes,
    CertifiedStructuralBaselineV1 CertifiedBaseline,
    ObservedCurrentSnapshotV1 ObservedSnapshot,
    TrustedLegacyRuntimeEvidenceV1 TrustedRuntime);

public interface ILegacyVerifiedEvidenceSource
{
    Task<LegacyVerifiedEvidenceV1> ResolveAsync(LegacyQualificationRequestV1 request,
        CancellationToken token);
    Task VerifyFreshnessAsync(LegacyQualificationRequestV1 request,
        CancellationToken token);
}

public sealed record LegacyQualificationOutcomeV1(
    LegacyReadinessV1? Readiness, LegacyRequestRejectedV1? Rejected,
    LegacyArtifactBlockedV1? ArtifactBlocked,
    LegacyFrozenPackage? Package, LegacyStaticSafetyEvidenceV1? StaticSafety,
    ReleasePayloadMetadata? Payload, CertifiedStructuralBaselineV1? CertifiedBaseline,
    ObservedCurrentSnapshotV1? ObservedSnapshot = null,
    string? BaselineEvidenceHash = null, string? ObservedEvidenceHash = null,
    TrustedLegacyRuntimeEvidenceV1? TrustedRuntime = null);

public sealed class LegacyPackageQualificationAdapter(
    ILegacyVerifiedEvidenceSource evidenceSource,
    ILegacyGitTransport git, ILegacyScopeSafetySource safetySource,
    IRecoverySecurityCatalogReader securityCatalog)
{
    public async Task<LegacyQualificationOutcomeV1> EvaluateAsync(
        LegacyQualificationRequestV1 request, CancellationToken token = default)
    {
        if (request.ContractVersion != 1)
            return new(null, new(1, "BLOCKED", ["TECHNICAL_ERROR"]), null, null, null, null, null);
        LegacyVerifiedEvidenceV1 verified;
        try { verified = await evidenceSource.ResolveAsync(request, token); }
        catch (OperationCanceledException) { throw; }
        catch (LegacyArtifactResolutionException exception)
        {
            var readiness = LegacyReadinessHash.Bind(new(1, "EXISTING_LEGACY",
                "BLOCKED", request.TargetId, null, exception.Blocked.ReasonCodes,
                new("PASS", "PASS", "PASS", "NOT_EVALUATED", "BLOCKED"),
                [], "", "BLOCKED_PRECONDITIONS", false, null));
            return new(readiness, null, exception.Blocked, null, null, null,
                null, TrustedRuntime: exception.Runtime);
        }
        catch (LegacyClassifiedResolutionException exception)
        {
            var readiness = LegacyReadinessHash.Bind(new(1, "EXISTING_LEGACY",
                "BLOCKED", request.TargetId, null, [exception.Code],
                new("NOT_EVALUATED", "NOT_EVALUATED", "BLOCKED", "NOT_EVALUATED",
                    "BLOCKED"), [], "", "BLOCKED_PRECONDITIONS", false, null));
            return new(readiness, null, null, null, null, null, null,
                TrustedRuntime: exception.Runtime);
        }
        catch (LegacyContractException exception) { return new(null,
            new(1, "BLOCKED", [exception.Code]), null, null, null, null, null,
            TrustedRuntime: LegacyRuntimeEvidenceHash.Blocked(new(1, request.TargetId,
                request.ArtifactSelection), exception.Code)); }
        catch { return new(null, new(1, "BLOCKED", ["TECHNICAL_ERROR"]),
            null, null, null, null, null,
            TrustedRuntime: LegacyRuntimeEvidenceHash.Blocked(new(1, request.TargetId,
                request.ArtifactSelection), "TECHNICAL_ERROR", "TECHNICAL_ERROR")); }
        if (!LegacyRuntimeEvidenceHash.Verify(verified.TrustedRuntime)
            || verified.TrustedRuntime.Request != new LegacyResolverRequestV1(1,
                request.TargetId, request.ArtifactSelection)
            || verified.TrustedRuntime.Evidence is not { } trusted
            || LegacyRuntimeEvidenceHash.Hash(trusted.Governance)
                != LegacyRuntimeEvidenceHash.Hash(verified.Governance)
            || LegacyRuntimeEvidenceHash.Hash(trusted.Onboarding)
                != LegacyRuntimeEvidenceHash.Hash(verified.Onboarding)
            || LegacyRuntimeEvidenceHash.Hash(trusted.CertifiedStructuralBaseline)
                != LegacyRuntimeEvidenceHash.Hash(verified.CertifiedBaseline)
            || LegacyRuntimeEvidenceHash.Hash(trusted.ObservedSnapshot)
                != LegacyRuntimeEvidenceHash.Hash(verified.ObservedSnapshot)
            || LegacyRuntimeEvidenceHash.Hash(trusted.DatabaseStateEvaluation)
                != LegacyRuntimeEvidenceHash.Hash(verified.DatabaseState)
            || trusted.SchemaRelation != "CONSISTENT"
            || !ClassificationMatches(trusted.Classification, request.TargetId,
                verified.ClassificationScenario))
            return new(null, new(1, "BLOCKED", ["EVIDENCE_CORRELATION_MISMATCH"]),
                null, null, null, null, null);
        if (verified.ClassificationScenario != "EXISTING_LEGACY")
            return new(null, new(1, "BLOCKED", ["GOVERNANCE_INVALID"]), null, null, null, null, null);
        var checks = new LegacyReadinessChecksV1("NOT_EVALUATED", "NOT_EVALUATED",
            "NOT_EVALUATED", "NOT_EVALUATED", "NOT_EVALUATED");
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var refs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        LegacyFrozenPackage? package = null;
        LegacyArtifactBlockedV1? artifactBlocked = null;
        LegacyStaticSafetyEvidenceV1? safety = null;
        ReleasePayloadMetadata? payload = null;
        var governance = verified.Governance;
        var registry = verified.DatabaseState.RegistryProvenance;
        var classificationHash = Hashing.Sha256(verified.ClassificationBytes);
        refs.Add("CLASSIFICATION", classificationHash);
        if (governance.TargetId != request.TargetId
            || governance.Environment != "TEST"
            || governance.DatabaseLifecycle != "EXISTING"
            || governance.ChangeManagementMode != "LEGACY_UNMANAGED"
            || governance.AuthorityReference.Kind != "REGISTRY_GIT_PR"
            || !BindingValid(governance.Binding)
            || governance.Binding.DatabaseName != verified.DatabaseState.DatabaseName
            || governance.ApplicationId != verified.DatabaseState.ApplicationId
            || verified.Repository != request.ArtifactSelection.ExpectedRepository
            || verified.Commit != request.ArtifactSelection.ExpectedCommit
            || !Provenance(governance.SourceProvenance, verified.GovernanceBytes)
            || !Provenance(verified.Onboarding.SourceProvenance, verified.OnboardingBytes)
            || governance.SourceProvenance.SourceRepository
                != verified.Onboarding.SourceProvenance.SourceRepository
            || governance.SourceProvenance.SourceRevision
                != verified.Onboarding.SourceProvenance.SourceRevision)
        {
            reasons.Add("GOVERNANCE_INVALID");
            checks = checks with { Governance = "BLOCKED" };
        }
        else
        {
            refs.Add("GOVERNANCE", governance.SourceProvenance.SourceSha256);
            checks = checks with { Governance = "PASS" };
        }

        if (checks.Governance == "PASS")
        {
            if (verified.Onboarding.Status != "MANAGED"
                || verified.Onboarding.AuthorityReference.Kind != "ONBOARDING_GIT_PR")
            {
                reasons.Add("ONBOARDING_NOT_MANAGED");
                checks = checks with { Onboarding = "BLOCKED" };
            }
            else
            {
                var onboarding = new DatabaseOnboardingEvaluator().Evaluate(new DatabaseOnboardingRequest {
                    DatabaseLifecycle = "EXISTING",
                    LineageAssessment = new DatabaseLineageAssessment {
                        Discovery = new DiscoveryGate { ConsistencyStatus = "CONSISTENT", ConsistencyReason = "NONE" },
                        Scenario = "EXISTING_LEGACY", SourceKind = "SQL"
                    },
                    DatabaseState = verified.DatabaseState
                });
                if (!onboarding.IsManaged)
                {
                    reasons.Add("ONBOARDING_NOT_MANAGED");
                    checks = checks with { Onboarding = "BLOCKED" };
                }
                else
                {
                    refs.Add("ONBOARDING", verified.Onboarding.SourceProvenance.SourceSha256);
                    checks = checks with { Onboarding = "PASS" };
                }
            }
        }

        string? observedHash = null;
        if (checks.Governance == "PASS")
        {
            try
            {
                observedHash = LegacyStructuralEvidence.Verify(verified.CertifiedBaseline,
                    verified.ObservedSnapshot, governance, verified.DatabaseState);
                var registryDocument = JsonSerializer.Deserialize<DatabaseRegistryDocument>(
                    verified.RegistryBytes, DatabaseStateJson.Compact);
                if (registry is null || registryDocument is null)
                    throw new LegacyContractException("REGISTRY_SOURCE_INVALID");
                var registryValidation = DatabaseRegistryLoader.Validate(registryDocument, registry);
                if (!registryValidation.IsValid)
                    throw new LegacyContractException("REGISTRY_SOURCE_INVALID");
                var recomputed = new DatabaseStateEvaluator().Evaluate(registryValidation,
                    new DatabaseStateObservation {
                        ApplicationId = governance.ApplicationId,
                        Environment = governance.Environment,
                        DatabaseName = governance.Binding.DatabaseName,
                        ObservedSchemaHash = observedHash,
                        SchemaCoverage = verified.ObservedSnapshot.Metadata.SchemaCoverage,
                        UnsupportedSchemaFeatures = verified.ObservedSnapshot.Snapshot.UnsupportedSchemaFeatures,
                        CaptureTimestampUtc = verified.ObservedSnapshot.CapturedAtUtc,
                        RunId = "legacy-qualification-v1", RunAttempt = "1"
                    });
                if (recomputed.RegistryStatus != verified.DatabaseState.RegistryStatus
                    || recomputed.DriftStatus != verified.DatabaseState.DriftStatus
                    || recomputed.GateStatus != verified.DatabaseState.GateStatus
                    || recomputed.Reason != verified.DatabaseState.Reason
                    || recomputed.CertifiedSchemaHash != verified.DatabaseState.CertifiedSchemaHash
                    || recomputed.ObservedSchemaHash != verified.DatabaseState.ObservedSchemaHash)
                    throw new LegacyContractException("EVIDENCE_CORRELATION_MISMATCH");
                if (registry is null || Hashing.Sha256(verified.RegistryBytes) != registry.RegistryFileSha256
                    || registry?.RegistryRepository != governance.SourceProvenance.SourceRepository
                    || registry?.RegistryCommitSha != governance.SourceProvenance.SourceRevision
                    || verified.DatabaseState.RegistryFormatVersion != 1
                    || verified.DatabaseState.RegistryStatus != DatabaseCertificationStatuses.Certified
                    || verified.DatabaseState.Target?.CertificationStatus != DatabaseCertificationStatuses.Certified
                    || verified.DatabaseState.Target.Lifecycle != "EXISTING"
                    || verified.DatabaseState.Target.Environment != "TEST"
                    || verified.DatabaseState.Target.ApplicationId != governance.ApplicationId
                    || verified.DatabaseState.Target.DatabaseName != governance.Binding.DatabaseName
                    || verified.DatabaseState.Environment != "TEST"
                    || verified.DatabaseState.DriftStatus != DatabaseDriftStatuses.Match
                    || verified.DatabaseState.GateStatus != DatabaseGateStatuses.Eligible
                    || verified.DatabaseState.CertifiedSchemaHash != verified.CertifiedBaseline.CertifiedSchemaHash
                    || verified.DatabaseState.Target.CertifiedSchemaHash != verified.CertifiedBaseline.CertifiedSchemaHash
                    || verified.DatabaseState.ObservedSchemaHash != observedHash)
                    throw new LegacyContractException("BASELINE_NOT_CERTIFIED");
                refs.Add("REGISTRY", registry.RegistryFileSha256);
                refs.Add("CERTIFIED_SCHEMA", verified.CertifiedBaseline.CertifiedSchemaHash);
                refs.Add("OBSERVED_SCHEMA", observedHash);
                checks = checks with { Baseline = "PASS" };
            }
            catch (LegacyContractException exception)
            {
                reasons.Add(exception.Code);
                checks = checks with { Baseline = "BLOCKED" };
            }
            catch (JsonException)
            {
                reasons.Add("REGISTRY_SOURCE_INVALID");
                checks = checks with { Baseline = "BLOCKED" };
            }
            catch
            {
                reasons.Add("BASELINE_NOT_CERTIFIED");
                checks = checks with { Baseline = "BLOCKED" };
            }
        }

        if (checks.Governance == "PASS")
        {
            var discovery = await new LegacyArtifactDiscovery(git).DiscoverEvidenceAsync(
                request.ArtifactSelection, request.TargetId, token);
            if (discovery.Valid is not null && discovery.Package is not null)
            {
                package = discovery.Package;
                if (LegacyRuntimeEvidenceHash.Hash(package.Evidence)
                    != LegacyRuntimeEvidenceHash.Hash(trusted.Artifact))
                {
                    package = null;
                    reasons.Add("ARTIFACT_MUTATED");
                }
            }
            if (package is not null)
            {
                var artifactHash = Hashing.Sha256(JsonSerializer.Serialize(package.Evidence, JsonDefaults.Compact));
                refs.Add("ARTIFACT", artifactHash);
            }
            else if (artifactBlocked is null && !reasons.Contains("ARTIFACT_MUTATED"))
            {
                artifactBlocked = discovery.Blocked;
                foreach (var reason in artifactBlocked?.ReasonCodes ?? ["TECHNICAL_ERROR"]) reasons.Add(reason);
            }
        }

        if (package is not null && checks.Baseline == "PASS" && observedHash is not null)
        {
            safety = await new LegacyStaticSafety().EvaluateAsync(package, verified.ObservedSnapshot.Snapshot,
                safetySource, token, governance.Binding);
            refs.Add("STATIC_SAFETY", safety.EvidenceHash);
            if (safety.State == "PASS") checks = checks with { Safety = "PASS" };
            else
            {
                foreach (var reason in safety.ReasonCodes) reasons.Add(reason);
                checks = checks with { Safety = "BLOCKED" };
            }
        }

        LegacyHandoffV1? handoff = null;
        if (package is not null && safety is { State: "PASS", ScopeSafetyHash: not null }
            && checks.Governance == "PASS" && checks.Onboarding == "PASS"
            && checks.Baseline == "PASS" && observedHash is not null
            && registry is not null)
        {
            var impact = safety.Impact;
            if (!impact.Complete) reasons.Add("RECOVERY_IMPACT_INCOMPLETE");
            var (dataScope, dataScopeHash) = LegacyReadinessHash.DataScope(
                safety.ForwardAnalysis, safety.RollbackAnalysis);
            LegacyDataValidatorLinkV1? dataLink = null;
            if (impact.DataRequired)
            {
                var descriptor = request.DataValidationDescriptor;
                if (descriptor is null || descriptor.ScopeHash != dataScopeHash
                    || descriptor.Provider is not IDataReapplyValidationContract
                    || descriptor.Version < 1 || string.IsNullOrWhiteSpace(descriptor.ContractId))
                    reasons.Add("DATA_VALIDATOR_REQUIRED");
                else dataLink = new(descriptor.ContractId, descriptor.Version, descriptor.ScopeHash);
            }
            string? securityHash = null;
            if (impact.SecurityRequired)
            {
                try
                {
                    var security = await new ReadOnlyRecoverySecurityProvider(securityCatalog)
                        .CaptureSecurityAsync(impact.SecurityScope, RecoveryPhase.Pre, token);
                    if (security.DatabaseName != governance.Binding.DatabaseName
                        || governance.Binding.ServerMatchPolicy == "ALLOW_LIST"
                            && !governance.Binding.AllowedServerInstances.Contains(
                                security.ServerInstance, StringComparer.OrdinalIgnoreCase))
                        throw new LegacyContractException("TARGET_IDENTITY_MISMATCH");
                    var canonical = RecoverySecurityCanonicalizer.Canonicalize(
                        impact.SecurityScope, RecoveryPhase.Pre, security);
                    securityHash = canonical?.Sha256;
                    if (securityHash is null) reasons.Add("SECURITY_EVIDENCE_INCOMPLETE");
                    else refs.Add("SECURITY_READINESS", securityHash);
                }
                catch (OperationCanceledException) { throw; }
                catch (SecurityCatalogException exception) { reasons.Add(exception.Code); }
                catch (LegacyContractException exception) { reasons.Add(exception.Code); }
                catch { reasons.Add("SECURITY_ADAPTER_FAILURE"); }
            }
            var risk = new RiskEngine().Evaluate(new DependencyAnalysisReport {
                Forward = safety.ForwardAnalysis, Rollback = safety.RollbackAnalysis
            }, verified.ObservedSnapshot.Snapshot);
            if (risk.SensitiveOperationBlockedByConfidence || risk.AnalysisConfidence != AnalysisConfidence.Complete)
                reasons.Add("RECOVERY_IMPACT_INCOMPLETE");
            if (reasons.Count == 0)
            {
                var scriptForward = new ReleaseScript("forward", package.ForwardBytes);
                var scriptRollback = new ReleaseScript("rollback", package.RollbackBytes);
                payload = ReleasePayloadBuilder.Build(new ReleaseDescriptor {
                    ReleaseId = package.ParsedManifest.ReleaseId, Environment = "TEST",
                    SourceKind = "SQL", Scenario = "EXISTING_LEGACY",
                    DatabaseLifecycle = "EXISTING",
                    ChangeOrigin = package.ParsedManifest.ChangeOrigin,
                    ChangeReference = package.ParsedManifest.ChangeReference,
                    ChangeReason = package.ParsedManifest.ChangeReason
                }, scriptForward, scriptRollback);
                var baselineIdentity = LegacyReadinessHash.LengthPrefix(request.TargetId,
                    registry.RegistryRepository, registry.RegistryFilePath,
                    registry.RegistryCommitSha, registry.RegistryFileSha256,
                    verified.CertifiedBaseline.CertifiedSchemaHash);
                var impactHash = Hashing.Sha256(JsonSerializer.Serialize(new {
                    forwardAnalysis = safety.ForwardAnalysis,
                    rollbackAnalysis = safety.RollbackAnalysis,
                    impact, scopeSafetyHash = safety.ScopeSafetyHash
                }, JsonDefaults.Compact));
                var approvalReasons = new SortedSet<string>(StringComparer.Ordinal);
                if (risk.RequiresDbaApproval) approvalReasons.Add("RISK_POLICY_APPROVAL");
                if (safety.ForwardAnalysis.Operations.Concat(safety.RollbackAnalysis.Operations)
                    .Any(x => x.IsDestructive)) approvalReasons.Add("DESTRUCTIVE_APPROVAL");
                if (safety.ForwardAnalysis.Operations.Concat(safety.RollbackAnalysis.Operations)
                    .Any(x => x.Operation == "DATABASE_SECURITY"))
                    approvalReasons.Add("SECURITY_CHANGE_APPROVAL");
                handoff = new(1, package.Evidence.PackageIdentity, payload.PayloadHash,
                    request.TargetId, package.ParsedManifest.ReleaseId, package.Evidence,
                    new(governance.SourceProvenance, governance.AuthorityReference,
                        governance.Binding, governance.ApplicationId, governance.Environment),
                    verified.Onboarding,
                    new(baselineIdentity, registry, verified.CertifiedBaseline.CertifiedSchemaHash),
                    classificationHash, observedHash, safety.ScopeSafetyHash,
                    new(safety.PolicyVersion, safety.ParserVersion, safety.EvidenceHash),
                    new("REQUIRED", impact.DataRequired ? "REQUIRED" : "NOT_REQUIRED",
                        impact.SecurityRequired ? "REQUIRED" : "NOT_REQUIRED",
                        impactHash, impact.SecurityScope, impact.SecurityScope.Sha256,
                        securityHash, "METADATA_ONLY_V1", dataLink),
                    risk, new(approvalReasons.Count != 0, "LEGACY_RISK_APPROVAL_V1",
                        approvalReasons.ToArray()),
                    "HARNESS_OWNS_PHASE_TRANSACTIONS_V1", verified.EngineCommit,
                    verified.TrustedRuntime.EvidenceSetHash!, false);
                refs.Add("RECOVERY_REQUIREMENTS", impactHash);
                checks = checks with { QualificationPreconditions = "PASS" };
            }
            else checks = checks with { QualificationPreconditions = "BLOCKED" };
        }
        if (handoff is not null)
        {
            try { await evidenceSource.VerifyFreshnessAsync(request, token); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                reasons.Add("SOURCE_FRESHNESS_UNVERIFIED");
                checks = checks with { QualificationPreconditions = "BLOCKED" };
                handoff = null;
            }
        }
        if (handoff is null && reasons.Count == 0) reasons.Add("TECHNICAL_ERROR");
        var ready = handoff is not null;
        var result = LegacyReadinessHash.Bind(new(1, "EXISTING_LEGACY",
            ready ? "READY_FOR_TEST_REHEARSAL" : "BLOCKED", request.TargetId,
            package?.Evidence.PackageIdentity, reasons.ToArray(), checks,
            refs.Select(x => new LegacyEvidenceReferenceV1(x.Key, x.Value)).ToArray(),
            "", ready ? "ANALYZED_NOT_REHEARSED" : "BLOCKED_PRECONDITIONS", false,
            handoff));
        return new(result, null, artifactBlocked, package, safety, payload,
            verified.CertifiedBaseline, verified.ObservedSnapshot,
            Hashing.Sha256(LegacyStructuralEvidence.Serialize(verified.CertifiedBaseline)),
            Hashing.Sha256(LegacyStructuralEvidence.Serialize(verified.ObservedSnapshot)),
            verified.TrustedRuntime);
    }

    private static bool ClassificationMatches(JsonElement result, string targetId,
        string scenario)
    {
        try
        {
            return result.GetProperty("status").GetString() == "CLASSIFIED"
                && result.GetProperty("classificationInvoked").GetBoolean()
                && result.GetProperty("targetId").GetString() == targetId
                && result.GetProperty("classification").GetProperty("classificationResult")
                    .GetProperty("inferences").GetProperty("scenario").GetString()
                    == scenario;
        }
        catch { return false; }
    }

    private static bool Provenance(LegacySourceProvenanceV1 provenance, byte[] bytes) =>
        provenance.SourceSha256 == Hashing.Sha256(bytes)
        && Regex.IsMatch(provenance.SourceRepository,
            @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")
        && Regex.IsMatch(provenance.SourcePath,
            @"\A[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\z")
        && !provenance.SourcePath.Split('/').Any(x => x is "." or "..")
        && Regex.IsMatch(provenance.SourceRevision,
            @"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z");

    private static bool BindingValid(LegacyBindingV1 binding) =>
        binding.EndpointReference.Length is > 0 and <= 128
        && binding.DatabaseName.Length is > 0 and <= 128
        && binding.ServerMatchPolicy is "ALLOW_LIST" or "MANAGED_ENDPOINT"
        && (binding.ServerMatchPolicy == "ALLOW_LIST"
            ? binding.AllowedServerInstances.Count > 0 : binding.AllowedServerInstances.Count == 0)
        && binding.AllowedServerInstances.All(x =>
            Regex.IsMatch(x, @"\A[A-Za-z0-9][A-Za-z0-9._\\-]{0,254}\z"))
        && binding.AllowedServerInstances.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            == binding.AllowedServerInstances.Count;
}
