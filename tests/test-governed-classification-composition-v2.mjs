#!/usr/bin/env node

import assert from "node:assert/strict";
import { composeGovernedClassification } from "../scripts/compose-governed-classification-v2.mjs";

const migrationId = "20260101000000_Initial";
const revision = "a".repeat(40);
const hash = "b".repeat(64);
const targetId = "db-target:orders-test";
const provenance = Object.freeze({
  sourceRepository: "infrastructure-services/workflow",
  sourcePath: "database-registry/targets.json",
  sourceRevision: revision,
  sourceSha256: hash
});

function evidenceItem(evidenceKind) {
  const item = { evidenceKind, targetId, sourceProvenance: { ...provenance } };
  if (["SQL_DISCOVERY", "SCHEMA_CAPTURE"].includes(evidenceKind)) {
    item.bindingContext = { endpointReference: "orders-test-sql" };
    item.observedDatabaseIdentity = { serverInstance: "SQL01\\MAIN", databaseName: "Orders" };
  }
  return item;
}

function governance(lifecycle = "EXISTING", mode = "EF_MIGRATIONS") {
  return {
    contractVersion: 1,
    targetId,
    governance: {
      applicationId: "orders", environment: "TEST",
      binding: { endpointReference: "orders-test-sql", databaseName: "Orders", serverMatchPolicy: "ALLOW_LIST", allowedServerInstances: ["SQL01\\MAIN", "SQL02\\MAIN"] },
      databaseLifecycle: lifecycle, changeManagementMode: mode,
      authorityReference: { kind: "REGISTRY_GIT_PR", policyPath: "database-registry/CODEOWNERS" }
    },
    sourceProvenance: { ...provenance },
    observations: {
      physicalState: lifecycle === "NEW" ? "EMPTY" : "POPULATED",
      repositoryState: mode === "EF_MIGRATIONS" ? "PRESENT_VALID" : "ABSENT",
      historyState: lifecycle === "EXISTING" && mode === "EF_MIGRATIONS" ? "PRESENT" : "ABSENT",
      registryState: "TARGET_REGISTERED", schemaRelation: "CONSISTENT",
      onboardingState: lifecycle === "EXISTING" ? "MANAGED" : "NOT_REQUIRED"
    },
    evidence: ["REGISTRY", "SQL_DISCOVERY", "SCHEMA_CAPTURE", "REPOSITORY", "ONBOARDING"].map(evidenceItem)
  };
}

function sources(lifecycle = "EXISTING", mode = "EF_MIGRATIONS") {
  return {
    producerContractVersion: 1,
    declarationsSource: { databaseLifecycle: lifecycle, changeManagementMode: mode },
    connectionSource: { status: "SUCCEEDED" }, databaseLookupSource: { status: "FOUND" }, targetConnectionSource: { status: "SUCCEEDED" },
    metadataSource: { status: "SUFFICIENT" },
    physicalSource: { status: "OBSERVED", businessObjectCount: lifecycle === "NEW" ? 0 : 4, technicalObjectCount: 0 },
    historySource: lifecycle === "EXISTING" && mode === "EF_MIGRATIONS"
      ? { status: "PRESENT", migrationCount: 1, migrationIds: [migrationId] }
      : { status: "ABSENT" },
    repositorySource: mode === "EF_MIGRATIONS"
      ? { status: "PRESENT_VALID", migrations: { count: 1, ids: [migrationId] } }
      : { status: "ABSENT" },
    schemaSource: { status: "CONSISTENT" }, registrySource: { status: "CERTIFIED" },
    onboardingSource: { status: lifecycle === "EXISTING" ? "MANAGED" : "NOT_REQUIRED" }
  };
}

function input(lifecycle = "EXISTING", mode = "EF_MIGRATIONS") {
  return { compositionContractVersion: 1, governanceEnvelope: governance(lifecycle, mode), classificationSources: sources(lifecycle, mode) };
}

function result(value) { return composeGovernedClassification(value); }
function classifier(value) {
  const actual = result(value);
  assert.equal(actual.result.classificationInvoked, true);
  return actual.result.classification.classificationResult;
}
function expectBoundary(value, exitCode, status, reason) {
  const actual = result(value);
  assert.equal(actual.exitCode, exitCode);
  assert.equal(actual.result.status, status);
  assert.equal(actual.result.reason, reason);
  assert.equal(actual.result.classificationInvoked, false);
}

let passed = 0;
function test(name, fn) {
  try { fn(); passed += 1; process.stdout.write(`PASS ${name}\n`); }
  catch (error) { process.stderr.write(`FAIL ${name}\n`); throw error; }
}

test("A EXISTING_EF coherente", () => {
  const actual = result(input());
  assert.equal(actual.exitCode, 0); assert.equal(actual.result.targetId, targetId);
  assert.equal(actual.result.classificationInvoked, true);
  assert.equal(actual.result.classification.classificationResult.inferences.scenario, "EXISTING_EF");
  assert.equal(Object.keys(actual.result.evidenceProvenance).length, 5);
});
test("B BASELINE_REQUIRED no equivale a CERTIFIED", () => {
  const value = input(); value.classificationSources.registrySource.status = "BASELINE_REQUIRED";
  const actual = classifier(value); assert.equal(actual.decision.status, "BLOCKED");
  assert.equal(actual.decision.blocks.includes("BLOCKED_BASELINE_REQUIRED"), true);
});
test("C schema DRIFT_DETECTED bloquea antes de clasificar", () => {
  const value = input(); value.governanceEnvelope.observations.schemaRelation = "DRIFT_DETECTED"; value.classificationSources.schemaSource.status = "DRIFT_DETECTED";
  expectBoundary(value, 66, "BLOCKED", "BLOCKED_SCHEMA_DRIFT");
});
test("D schema ERROR no es NOT_EVALUATED", () => {
  const value = input(); value.governanceEnvelope.observations.schemaRelation = "ERROR"; value.classificationSources.schemaSource.status = "ERROR";
  expectBoundary(value, 75, "TECHNICAL_ERROR", "SOURCE_TECHNICAL_ERROR");
});
test("E Registry INVALID falla cerrado", () => {
  const value = input(); value.governanceEnvelope.observations.registryState = "INVALID"; value.classificationSources.registrySource.status = "INVALID";
  expectBoundary(value, 66, "BLOCKED", "BLOCKED_REGISTRY_CONTRADICTION");
});
test("F Onboarding BLOCKED impide resultado gestionable", () => {
  const value = input(); value.governanceEnvelope.observations.onboardingState = "BLOCKED"; value.classificationSources.onboardingSource.status = "BLOCKED";
  expectBoundary(value, 66, "BLOCKED", "BLOCKED_ONBOARDING");
});
test("G history presente sin repo permanece bloqueado", () => {
  const value = input(); value.governanceEnvelope.observations.repositoryState = "ABSENT"; value.classificationSources.repositorySource = { status: "ABSENT" };
  const actual = classifier(value); assert.equal(actual.decision.blocks.includes("BLOCKED_HISTORY_WITHOUT_REPOSITORY"), true);
});
test("H LEGACY_UNMANAGED sin autoridad es inválido", () => {
  const value = input("EXISTING", "LEGACY_UNMANAGED"); delete value.governanceEnvelope.governance.authorityReference;
  expectBoundary(value, 65, "INVALID_EVIDENCE", "GOVERNANCE_CONTRACT_INVALID");
});
test("I LEGACY_UNMANAGED gobernado clasifica sin inferencia", () => {
  const value = input("EXISTING", "LEGACY_UNMANAGED");
  const actual = classifier(value); assert.equal(actual.inferences.scenario, "EXISTING_LEGACY");
  assert.equal(actual.declarations.changeManagementMode, "LEGACY_UNMANAGED");
});
test("J NEW técnico conserva bloqueo contractual", () => {
  const value = input("NEW", "EF_MIGRATIONS");
  value.governanceEnvelope.observations.physicalState = "TECHNICAL_ONLY";
  value.classificationSources.physicalSource = { status: "OBSERVED", businessObjectCount: 0, technicalObjectCount: 1 };
  expectBoundary(value, 66, "BLOCKED", "BLOCKED_NEW_WITH_PREEXISTING_STATE");
});
test("K contradicción lateral falla cerrado", () => {
  const value = input(); value.governanceEnvelope.observations.onboardingState = "PENDING";
  expectBoundary(value, 66, "BLOCKED", "BLOCKED_CROSS_SOURCE_CONTRADICTION");
});
test("L UNKNOWN nunca degrada a éxito", () => {
  const value = input(); value.governanceEnvelope.observations.schemaRelation = "UNKNOWN"; value.classificationSources.schemaSource.status = "UNKNOWN";
  expectBoundary(value, 67, "UNCLASSIFIED", "INSUFFICIENT_EVIDENCE");
});
test("L NOT_ATTEMPTED nunca degrada a evaluado", () => {
  const value = input(); value.governanceEnvelope.observations.schemaRelation = "NOT_ATTEMPTED"; value.classificationSources.schemaSource.status = "NOT_ATTEMPTED";
  expectBoundary(value, 68, "NOT_EVALUATED", "SOURCE_STAGE_NOT_ATTEMPTED");
});
test("targetId de otra fuente bloquea", () => {
  const value = input(); value.governanceEnvelope.evidence[3].targetId = "db-target:other";
  expectBoundary(value, 66, "BLOCKED", "BLOCKED_REPOSITORY_TARGET_ID_MISMATCH");
});
test("Schema Capture de otro target bloquea", () => {
  const value = input(); value.governanceEnvelope.evidence[2].targetId = "db-target:other";
  expectBoundary(value, 66, "BLOCKED", "BLOCKED_SCHEMA_CAPTURE_TARGET_ID_MISMATCH");
});
test("endpoint observado distinto bloquea", () => {
  const value = input(); value.governanceEnvelope.evidence[1].bindingContext.endpointReference = "other-endpoint";
  expectBoundary(value, 66, "BLOCKED", "BLOCKED_SQL_DISCOVERY_ENDPOINT_REFERENCE_MISMATCH");
});
test("SQL observed database distinta bloquea", () => {
  const value = input(); value.governanceEnvelope.evidence[1].observedDatabaseIdentity.databaseName = "Other";
  expectBoundary(value, 66, "BLOCKED", "BLOCKED_SQL_DISCOVERY_DATABASE_MISMATCH");
});
test("server fuera del allow-list bloquea", () => {
  const value = input(); value.governanceEnvelope.evidence[1].observedDatabaseIdentity.serverInstance = "SQL99\\MAIN";
  expectBoundary(value, 66, "BLOCKED", "BLOCKED_SQL_DISCOVERY_SERVER_BINDING_MISMATCH");
});
test("Repository no puede declarar observación SQL", () => {
  const value = input();
  value.governanceEnvelope.evidence[3].observedDatabaseIdentity = { serverInstance: "SQL01\\MAIN", databaseName: "Orders" };
  expectBoundary(value, 65, "INVALID_EVIDENCE", "EVIDENCE_CONTRACT_INVALID");
});
test("failover permitido conserva targetId", () => {
  const value = input(); value.governanceEnvelope.evidence[1].observedDatabaseIdentity.serverInstance = "SQL02\\MAIN";
  assert.equal(result(value).result.classificationInvoked, true);
});
test("managed endpoint acepta cambio de nodo", () => {
  const value = input(); value.governanceEnvelope.governance.binding.serverMatchPolicy = "MANAGED_ENDPOINT";
  value.governanceEnvelope.governance.binding.allowedServerInstances = [];
  value.governanceEnvelope.evidence[1].observedDatabaseIdentity.serverInstance = "SQL77\\AG";
  value.governanceEnvelope.evidence[2].observedDatabaseIdentity.serverInstance = "SQL78\\AG";
  assert.equal(result(value).result.classificationInvoked, true);
});
test("provenance inválida no alcanza Producer", () => {
  const value = input(); delete value.governanceEnvelope.evidence[0].sourceProvenance.sourceSha256;
  expectBoundary(value, 65, "INVALID_EVIDENCE", "EVIDENCE_CONTRACT_INVALID");
});
test("input determinístico e inmutable", () => {
  const value = input(), before = structuredClone(value);
  assert.deepEqual(result(value), result(value)); assert.deepEqual(value, before);
});

process.stdout.write(`OK: ${passed} casos de composición gobernada HG5\n`);
