using System.Text.Json;

namespace DatabaseReleaseQualification;

// The result is produced by the existing classifier V2/adapter, then acquired by
// the purpose-aware host. Scope and exact producer inputs travel with the result.
public sealed record EfUnifiedClassificationEvidenceV1(EfEvidenceScopeV1 Scope, EvidenceReference Pre,
    EvidenceReference Source, EvidenceReference? Bootstrap, JsonElement Result);

public sealed partial class EfEvidencePreparationV1
{
    private static void ValidateUnifiedClassification(EfUnifiedClassificationEvidenceV1 evidence,
        EfPreparationSelectionV1 selection, EfGovernedPreparationV1 governed, EfObservationEvidenceV1 pre,
        EfPlanSourceV1 source, EfPreprovisionedBootstrapV1? bootstrap)
    {
        Require(evidence.Scope == selection.Scope && evidence.Pre == selection.Pre && evidence.Source == selection.Source
            && evidence.Bootstrap == governed.Authority.BootstrapEvidence, "CLASSIFICATION_INPUT_BINDING_MISMATCH");
        var result = evidence.Result;
        Require(result.GetProperty("contractVersion").GetInt32() == 2 && result.GetProperty("rulesVersion").GetString() == "2",
            "CLASSIFICATION_RULES_UNSUPPORTED");
        var declarations = result.GetProperty("declarations"); var facts = result.GetProperty("observations");
        var inference = result.GetProperty("inferences"); var decision = result.GetProperty("decision"); var ef = result.GetProperty("ef");
        var o = pre.Observation; var physical = pre.Discovery.Physical;
        var physicalState = physical.BusinessObjectCount > 0 ? "POPULATED" : physical.TechnicalObjectCount > 0 ? "TECHNICAL_ONLY" : "EMPTY";
        var relation = o.HistoryStatus is "ABSENT" or "EMPTY" ? "NOT_APPLICABLE"
            : o.AppliedMigrationIds.SequenceEqual(source.ManagedMigrationIds) ? "EXACT_MATCH" : "VALID_PREFIX";
        Require(declarations.GetProperty("databaseLifecycle").GetString() == governed.Authority.Lifecycle
            && declarations.GetProperty("changeManagementMode").GetString() == "EF_MIGRATIONS"
            && facts.GetProperty("physicalExistence").GetString() == "EXISTS"
            && facts.GetProperty("metadataVisibility").GetString() == "SUFFICIENT"
            && facts.GetProperty("physicalState").GetString() == physicalState
            && facts.GetProperty("repositoryState").GetString() == "PRESENT_VALID"
            && facts.GetProperty("historyState").GetString() == o.HistoryStatus
            && facts.GetProperty("repoHistoryRelation").GetString() == relation
            && facts.GetProperty("onboardingState").GetString() == governed.Authority.OnboardingState
            && inference.GetProperty("scenario").GetString() == governed.Authority.Scenario,
            "CLASSIFICATION_OBSERVATION_MISMATCH");
        var initial = governed.Authority.Scenario == "NEW_EF";
        Require(facts.GetProperty("registryState").GetString() == (initial ? "NOT_EVALUATED" : "CERTIFIED")
            && facts.GetProperty("schemaRelation").GetString() == (initial ? "NOT_EVALUATED" : "CONSISTENT"),
            "CLASSIFICATION_BASELINE_MISMATCH");
        Require(decision.GetProperty("status").GetString() == "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE"
            && decision.GetProperty("primaryBlock").ValueKind == JsonValueKind.Null
            && decision.GetProperty("blocks").GetArrayLength() == 0 && !ef.GetProperty("executionAuthorized").GetBoolean(),
            "CLASSIFICATION_BLOCKED");
        var pending = o.AppliedMigrationIds.Length < source.ManagedMigrationIds.Length;
        Require(inference.GetProperty("hasPendingMigrations").GetBoolean() == pending, "CLASSIFICATION_PENDING_MISMATCH");
        var expectedVariant = initial ? o.HistoryStatus == "ABSENT" ? "V2" : "V3" : pending ? "V4" : "V5";
        var expectedReason = expectedVariant switch { "V2" => "INITIAL_MIGRATIONS_PENDING", "V3" => "EMPTY_HISTORY_INITIAL_STATE",
            "V4" => "PENDING_MIGRATIONS", _ => "NO_CHANGES" };
        Require(ef.GetProperty("variant").GetString() == expectedVariant && ef.GetProperty("reason").GetString() == expectedReason
            && ef.GetProperty("initialHistoryPolicy").GetString() == (bootstrap?.HistoryPolicy ?? "NOT_VERIFIED"),
            "CLASSIFICATION_VARIANT_MISMATCH");
        Require(ef.GetProperty("forbiddenStages").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["APPLY", "CERTIFY", "CREATE_DATABASE"])
            && ef.GetProperty("allowedStages").EnumerateArray().Select(x => x.GetString()).SequenceEqual(
                expectedVariant == "V5" ? ["NO_CHANGES"] : new[] { "PLAN", "QUALIFICATION", "REVALIDATE" }), "CLASSIFICATION_STAGE_MISMATCH");
    }
}
