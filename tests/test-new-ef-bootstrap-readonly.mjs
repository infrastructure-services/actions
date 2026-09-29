import assert from "node:assert/strict";
import { composeGovernedClassification as compose } from "../scripts/compose-governed-classification-v2.mjs";
import { TECHNICAL_CATEGORIES } from "../scripts/empty-for-new-ef-v1.mjs";
import { executeRuntime } from "../scripts/run-classification-v2-test-runtime.mjs";

let passed = 0;
function test(name, fn) { fn(); passed++; console.log(`PASS ${name}`); }
export function fixture() {
  const provenance = { sourceRepository: "infrastructure-services/workflow", sourcePath: "database-registry/governance-targets.json", sourceRevision: "a".repeat(40), sourceSha256: "b".repeat(64) };
  const targetId = "new-orders-test";
  const sources = { producerContractVersion: 1, declarationsSource: { databaseLifecycle: "NEW", changeManagementMode: "EF_MIGRATIONS" },
    connectionSource: { status: "SUCCEEDED" }, databaseLookupSource: { status: "FOUND" }, targetConnectionSource: { status: "SUCCEEDED" }, metadataSource: { status: "SUFFICIENT" },
    physicalSource: { status: "OBSERVED", businessObjectCount: 0, technicalObjectCount: 0, taxonomy: { version: 1, coverage: "COMPLETE", counts: Object.fromEntries(TECHNICAL_CATEGORIES.map(key => [key, 0])) } },
    historySource: { status: "ABSENT" }, repositorySource: { status: "PRESENT_VALID", migrations: { count: 1, ids: ["20260101000000_First"] } },
    schemaSource: { status: "NOT_EVALUATED" }, registrySource: { status: "NOT_EVALUATED" }, onboardingSource: { status: "NOT_REQUIRED" } };
  return { compositionContractVersion: 2, classificationSources: sources, governanceEnvelope: {
    contractVersion: 2, targetId, governance: { applicationId: "orders", environment: "TEST", databaseLifecycle: "NEW", changeManagementMode: "EF_MIGRATIONS",
      binding: { endpointReference: "orders-test", databaseName: "Orders", serverMatchPolicy: "ALLOW_LIST", allowedServerInstances: ["SQL01"] }, authorityReference: { kind: "REGISTRY_GIT_PR", policyPath: "CODEOWNERS" } },
    sourceProvenance: provenance, observations: { physicalState: "EMPTY", repositoryState: "PRESENT_VALID", historyState: "ABSENT", registryState: "TARGET_REGISTERED", schemaRelation: "NOT_EVALUATED", onboardingState: "NOT_REQUIRED" },
    evidence: ["REGISTRY", "SQL_DISCOVERY", "SCHEMA_CAPTURE", "REPOSITORY", "ONBOARDING"].map(evidenceKind => ({ evidenceKind, targetId,
      sourceProvenance: evidenceKind === "SCHEMA_CAPTURE" ? { ...provenance, sourceRepository: "infrastructure-services/actions", sourcePath: "schema-capture-new-ef-bootstrap/action.yml" } : { ...provenance },
      ...(["SQL_DISCOVERY", "SCHEMA_CAPTURE"].includes(evidenceKind) ? { bindingContext: { endpointReference: "orders-test" }, observedDatabaseIdentity: { serverInstance: "SQL01", databaseName: "Orders" } } : {}) }))
  } };
}
function positive(value) {
  const actual = compose(value);
  assert.equal(actual.exitCode, 0);
  assert.equal(actual.result.compositionContractVersion, 2);
  assert.equal(actual.result.classificationInvoked, true);
  assert.equal(actual.result.classification.classificationResult.inferences.scenario, "NEW_EF");
  assert.equal(actual.result.classification.classificationResult.decision.status, "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE");
}
function blocked(value) {
  const before = structuredClone(value), actual = compose(value);
  assert.deepEqual(value, before);
  assert.notEqual(actual.result.classification?.classificationResult?.decision.status, "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE");
  return actual;
}
test("NEW intentional NOT_EVALUATED positive", () => positive(fixture()));
test("NEW MANAGED positive", () => { const x = fixture(); x.classificationSources.onboardingSource.status = x.governanceEnvelope.observations.onboardingState = "MANAGED"; positive(x); });
test("public TEST runtime preserves bootstrap", () => { const x = fixture(); const actual = executeRuntime({ environment: "TEST", inputText: JSON.stringify(x) }); assert.equal(actual.exitCode, 0); assert.equal(actual.result.classificationInvoked, true); });
for (const status of ["BASELINE_REQUIRED", "CERTIFIED", "NOT_CERTIFIED", "ERROR", "UNKNOWN", "CONTRADICTORY", "TARGET_NOT_REGISTERED"]) test(`observed Registry ${status} never rewritten`, () => { const x = fixture(); x.classificationSources.registrySource.status = status; assert.equal(blocked(x).result.reason, "BLOCKED_BOOTSTRAP_SOURCE_CONTRACT"); });
for (const [name, mutate] of [
  ["Registry missing", x => delete x.classificationSources.registrySource],
  ["governance missing", x => delete x.governanceEnvelope.governance],
  ["registration absent", x => x.governanceEnvelope.observations.registryState = "TARGET_NOT_REGISTERED"],
  ["onboarding source missing", x => delete x.classificationSources.onboardingSource],
  ["onboarding evidence missing", x => x.governanceEnvelope.evidence.pop()],
  ["taxonomy incomplete", x => x.classificationSources.physicalSource.taxonomy = { version: 1, coverage: "PARTIAL" }],
  ["taxonomy historical absent", x => delete x.classificationSources.physicalSource.taxonomy],
  ["physical populated", x => { x.classificationSources.physicalSource.businessObjectCount = 1; x.governanceEnvelope.observations.physicalState = "POPULATED"; }],
  ["history EMPTY", x => { x.classificationSources.historySource = { status: "PRESENT", migrationCount: 0, migrationIds: [] }; x.governanceEnvelope.observations.historyState = "EMPTY"; }],
  ["repo invalid", x => x.classificationSources.repositorySource.status = x.governanceEnvelope.observations.repositoryState = "INVALID"],
  ["binding mismatch", x => x.governanceEnvelope.evidence[1].observedDatabaseIdentity.databaseName = "Other"],
  ["producer path wrong", x => x.governanceEnvelope.evidence[2].sourceProvenance.sourcePath = "schema-capture/action.yml"],
  ["producer repository wrong", x => x.governanceEnvelope.evidence[2].sourceProvenance.sourceRepository = "other/actions"],
  ["producer provenance missing", x => delete x.governanceEnvelope.evidence[2].sourceProvenance.sourceSha256],
  ["Registry provenance inconsistent", x => x.governanceEnvelope.evidence[0].sourceProvenance.sourceSha256 = "c".repeat(64)],
  ["source contradiction", x => x.governanceEnvelope.observations.historyState = "PRESENT"],
  ["EXISTING bootstrap", x => x.classificationSources.declarationsSource.databaseLifecycle = x.governanceEnvelope.governance.databaseLifecycle = "EXISTING"],
  ["unknown lifecycle", x => x.governanceEnvelope.governance.databaseLifecycle = "UNKNOWN"],
  ["unknown mode", x => x.governanceEnvelope.governance.changeManagementMode = "UNKNOWN"],
  ["old composition version", x => x.compositionContractVersion = x.governanceEnvelope.contractVersion = 1],
  ["version mismatch", x => x.governanceEnvelope.contractVersion = 1]
]) test(name, () => { const x = fixture(); mutate(x); blocked(x); });
for (const status of ["REQUIRED", "PENDING", "BLOCKED", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"]) test(`onboarding ${status}`, () => { const x = fixture(); x.classificationSources.onboardingSource.status = x.governanceEnvelope.observations.onboardingState = status; blocked(x); });
test("NOT_FOUND retains no-provisioning block", () => {
  const x = fixture(); x.classificationSources.databaseLookupSource.status = "NOT_FOUND";
  for (const key of ["targetConnectionSource", "metadataSource", "physicalSource", "historySource"]) x.classificationSources[key] = { status: "NOT_ATTEMPTED" };
  // Historical authority cannot claim EMPTY or ABSENT after an unattempted target.
  x.governanceEnvelope.observations.physicalState = x.governanceEnvelope.observations.historyState = "NOT_ATTEMPTED";
  blocked(x);
});
for (const status of ["BASELINE_REQUIRED", "CERTIFIED"]) test(`V1 NEW real ${status} compatible`, () => {
  const x = fixture(); x.compositionContractVersion = x.governanceEnvelope.contractVersion = 1;
  x.classificationSources.registrySource.status = status; x.classificationSources.schemaSource.status = x.governanceEnvelope.observations.schemaRelation = "CONSISTENT";
  x.governanceEnvelope.evidence[2].sourceProvenance.sourcePath = "schema-capture/action.yml";
  const actual = compose(x); assert.equal(actual.result.classificationInvoked, true);
  const decision = actual.result.classification.classificationResult.decision;
  if (status === "BASELINE_REQUIRED") assert.ok(decision.blocks.includes("BLOCKED_BASELINE_REQUIRED")); else assert.equal(decision.status, "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE");
});
console.log(`OK ${passed} bootstrap governed cases`);
