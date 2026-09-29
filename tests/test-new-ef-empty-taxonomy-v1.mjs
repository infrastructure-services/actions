import assert from "node:assert/strict";
import { composeEnvelope } from "../scripts/compose-classification-evidence-v2.mjs";
import { adaptEvidence } from "../scripts/adapt-classification-evidence-v2.mjs";
import { TECHNICAL_CATEGORIES, validateTaxonomy } from "../scripts/empty-for-new-ef-v1.mjs";
import { composeGovernedClassification } from "../scripts/compose-governed-classification-v2.mjs";
import { validateEvidence } from "../scripts/run-sql-discovery-v2-public.mjs";

let passed = 0;
const test = (name, fn) => { fn(); passed++; console.log(`PASS ${name}`); };
const counts = () => Object.fromEntries(TECHNICAL_CATEGORIES.map(key => [key, 0]));
const physical = () => ({ status: "OBSERVED", businessObjectCount: 0, technicalObjectCount: 0,
  taxonomy: { version: 1, coverage: "COMPLETE", counts: counts() } });
function sources() {
  return { producerContractVersion: 1, declarationsSource: { databaseLifecycle: "NEW", changeManagementMode: "EF_MIGRATIONS" },
    connectionSource: { status: "SUCCEEDED" }, databaseLookupSource: { status: "FOUND" }, targetConnectionSource: { status: "SUCCEEDED" },
    metadataSource: { status: "SUFFICIENT" }, physicalSource: physical(), historySource: { status: "ABSENT" },
    repositorySource: { status: "PRESENT_VALID", migrations: { count: 1, ids: ["20260101000000_First"] } },
    schemaSource: { status: "NOT_EVALUATED" }, registrySource: { status: "NOT_EVALUATED" }, onboardingSource: { status: "NOT_REQUIRED" } };
}
function run(value) { const produced = composeEnvelope(value); assert.equal(produced.exitCode, 0); return adaptEvidence(produced.raw); }
function blocked(value) {
  const actual = run(value); assert.notEqual(actual.result.classificationResult?.decision.status, "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE");
  return actual;
}
test("complete V1 empty evidence reaches real NEW_EF", () => {
  const actual = run(sources()); assert.equal(actual.exitCode, 0); assert.equal(actual.result.classificationInvoked, true);
  assert.equal(actual.result.classificationResult.inferences.scenario, "NEW_EF");
  assert.equal(actual.result.classificationResult.decision.status, "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE");
});
for (const category of TECHNICAL_CATEGORIES) test(`${category} alone blocks`, () => {
  const value = sources(); value.physicalSource.taxonomy.counts[category] = 1; value.physicalSource.technicalObjectCount = 1;
  assert.equal(blocked(value).result.normalizedFacts.physicalState, "TECHNICAL_ONLY");
});
for (const category of ["table", "view", "procedure", "function", "sequence", "synonym", "DML-trigger", "cicd-tooling"]) test(`${category} business count blocks`, () => {
  const value = sources(); value.physicalSource.businessObjectCount = 1;
  assert.equal(blocked(value).result.normalizedFacts.physicalState, "POPULATED");
});
test("historical zero counts never acquire V1", () => { const value = sources(); delete value.physicalSource.taxonomy;
  assert.equal(blocked(value).result.normalizedFacts.physicalState, "UNKNOWN"); });
test("missing technical count never becomes zero", () => { const value = sources(); value.physicalSource = { status: "OBSERVED", businessObjectCount: 0 };
  assert.equal(blocked(value).result.normalizedFacts.physicalState, "UNKNOWN"); });
test("partial coverage never credits zero", () => { const value = sources(); value.physicalSource = { status: "OBSERVED", businessObjectCount: 0, taxonomy: { version: 1, coverage: "PARTIAL" } };
  assert.equal(blocked(value).result.normalizedFacts.physicalState, "UNKNOWN"); });
for (const mutate of [
  p => p.taxonomy.version = 2,
  p => delete p.taxonomy.counts.databaseTriggers,
  p => p.taxonomy.counts.extra = 0,
  p => p.taxonomy.counts.customSchemas = -1,
  p => p.taxonomy.counts.customSchemas = 1,
  p => p.taxonomy.coverage = "PARTIAL",
  p => delete p.technicalObjectCount,
  p => p.taxonomy.counts.customSchemas = Number.MAX_SAFE_INTEGER + 1
]) test("invalid or contradictory taxonomy is rejected by producer and adapter", () => {
  const value = sources(); mutate(value.physicalSource); assert.notEqual(composeEnvelope(value).exitCode, 0);
  const raw = composeEnvelope(sources()).raw; raw.physical = value.physicalSource;
  assert.equal(adaptEvidence(raw).result.classificationInvoked, false);
});
test("taxonomy copied without aliasing", () => { const value = sources(); const output = composeEnvelope(value).raw;
  value.physicalSource.taxonomy.counts.customSchemas = 9; assert.equal(output.physical.taxonomy.counts.customSchemas, 0); });
for (const status of ["PRESENT", "UNKNOWN", "ERROR", "NOT_ATTEMPTED", "UNREADABLE", "INVALID_STRUCTURE"]) test(`history ${status} blocks`, () => {
  const value = sources(); value.historySource = status === "PRESENT" ? { status, migrationCount: 1, migrationIds: ["20260101000000_First"] } : { status }; blocked(value);
});
test("history empty blocks", () => { const value = sources(); value.historySource = { status: "PRESENT", migrationCount: 0, migrationIds: [] }; blocked(value); });
for (const status of ["ABSENT", "INVALID", "AMBIGUOUS", "UNKNOWN", "ERROR"]) test(`repository ${status} blocks`, () => { const value = sources(); value.repositorySource = { status }; blocked(value); });
for (const status of ["REQUIRED", "PENDING", "BLOCKED", "UNKNOWN", "ERROR"]) test(`onboarding ${status} blocks`, () => { const value = sources(); value.onboardingSource = { status }; blocked(value); });
for (const status of ["NOT_FOUND", "UNKNOWN"]) test(`lookup ${status} blocks`, () => {
  const value = sources(); value.databaseLookupSource.status = status; value.targetConnectionSource.status = "NOT_ATTEMPTED";
  value.metadataSource.status = "NOT_ATTEMPTED"; value.physicalSource = { status: "NOT_ATTEMPTED" }; value.historySource = { status: "NOT_ATTEMPTED" };
  const actual = blocked(value);
  if (status === "NOT_FOUND") assert.equal(actual.result.classificationResult.decision.primaryBlock, "BLOCKED_DATABASE_NOT_FOUND_NO_PROVISIONING");
});
for (const status of ["ERROR", "TIMEOUT", "CANCELLED"]) test(`physical ${status} blocks without fabricated counts`, () => { const value = sources(); value.physicalSource = { status }; blocked(value); });
test("coverage without observation rejected", () => { assert.notEqual(validateTaxonomy({ ...physical(), status: "NOT_ATTEMPTED" }), null); });
function governed() {
  const value = sources(); value.schemaSource.status = "CONSISTENT"; value.registrySource.status = "CERTIFIED";
  const provenance = { sourceRepository: "fixture/repository", sourcePath: "evidence.json", sourceRevision: "a".repeat(40), sourceSha256: "b".repeat(64) };
  const targetId = "controlled-new-ef";
  return { compositionContractVersion: 1, classificationSources: value, governanceEnvelope: {
    contractVersion: 1, targetId, governance: { applicationId: "fixture", environment: "TEST", databaseLifecycle: "NEW", changeManagementMode: "EF_MIGRATIONS",
      binding: { endpointReference: "fixture-sql", databaseName: "Fixture", serverMatchPolicy: "ALLOW_LIST", allowedServerInstances: ["SQL01"] },
      authorityReference: { kind: "REGISTRY_GIT_PR", policyPath: "CODEOWNERS" } }, sourceProvenance: provenance,
    observations: { physicalState: "EMPTY", repositoryState: "PRESENT_VALID", historyState: "ABSENT", registryState: "TARGET_REGISTERED", schemaRelation: "CONSISTENT", onboardingState: "NOT_REQUIRED" },
    evidence: ["REGISTRY", "SQL_DISCOVERY", "SCHEMA_CAPTURE", "REPOSITORY", "ONBOARDING"].map(evidenceKind => ({ evidenceKind, targetId, sourceProvenance: structuredClone(provenance),
      ...(["SQL_DISCOVERY", "SCHEMA_CAPTURE"].includes(evidenceKind) ? { bindingContext: { endpointReference: "fixture-sql" }, observedDatabaseIdentity: { serverInstance: "SQL01", databaseName: "Fixture" } } : {}) }))
  } };
}
test("governed V1 positive with controlled pre-existing certification fixture (bootstrap gap retained)", () => {
  const actual = composeGovernedClassification(governed()); assert.equal(actual.exitCode, 0);
  assert.equal(actual.result.classification.classificationResult.decision.status, "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE");
  assert.equal(actual.result.classification.classificationResult.inferences.scenario, "NEW_EF");
});
for (const [name, mutate, reason] of [
  ["server binding", x => x.governanceEnvelope.evidence[1].observedDatabaseIdentity.serverInstance = "SQL02", "BLOCKED_SQL_DISCOVERY_SERVER_BINDING_MISMATCH"],
  ["missing governance", x => delete x.governanceEnvelope.governance, "GOVERNANCE_CONTRACT_INVALID"],
  ["source contradiction", x => x.governanceEnvelope.observations.historyState = "PRESENT", "BLOCKED_CROSS_SOURCE_CONTRADICTION"],
  ["schema drift", x => { x.governanceEnvelope.observations.schemaRelation = "DRIFT_DETECTED"; x.classificationSources.schemaSource.status = "DRIFT_DETECTED"; }, "BLOCKED_SCHEMA_DRIFT"],
  ["onboarding blocked", x => { x.governanceEnvelope.observations.onboardingState = "BLOCKED"; x.classificationSources.onboardingSource.status = "BLOCKED"; }, "BLOCKED_ONBOARDING"]
]) test(`governed ${name} blocks before classification`, () => {
  const value = governed(); mutate(value); const actual = composeGovernedClassification(value);
  assert.equal(actual.result.classificationInvoked, false); assert.equal(actual.result.reason, reason);
});
test("public SQL boundary preserves complete coverage and rejects incoherent taxonomy", () => {
  const value = { serverConnectionStatus: "SUCCEEDED", ...Object.fromEntries(Object.entries(sources()).filter(([key]) => ["connectionSource", "databaseLookupSource", "targetConnectionSource", "metadataSource", "physicalSource", "historySource"].includes(key))), observedDatabaseIdentity: { serverInstance: "SQL01", databaseName: "Fixture" } };
  assert.deepEqual(validateEvidence(value).physicalSource, physical()); value.physicalSource.taxonomy.counts.databaseTriggers = 1;
  assert.throws(() => validateEvidence(value), /EVIDENCE_INVALID/);
});
console.log(`OK ${passed} NEW_EF taxonomy cases`);
