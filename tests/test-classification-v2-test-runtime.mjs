#!/usr/bin/env node

import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { createArtifact, executeRuntime, resolveInputPath, resolveOutputDirectory, runCli } from "../scripts/run-classification-v2-test-runtime.mjs";

const migrationId = "20260101000000_Initial";
const revision = "a".repeat(40);
const hash = "b".repeat(64);
const targetId = "db-target:orders-test";
const provenance = { sourceRepository: "infrastructure-services/workflow", sourcePath: "database-registry/targets.json", sourceRevision: revision, sourceSha256: hash };

function evidenceItem(evidenceKind) {
  const item = { evidenceKind, targetId, sourceProvenance: { ...provenance } };
  if (["SQL_DISCOVERY", "SCHEMA_CAPTURE"].includes(evidenceKind)) {
    item.bindingContext = { endpointReference: "orders-test-sql" };
    item.observedDatabaseIdentity = { serverInstance: "SQL01\\MAIN", databaseName: "Orders" };
  }
  return item;
}

function input(lifecycle = "EXISTING", mode = "EF_MIGRATIONS") {
  const legacy = mode === "LEGACY_UNMANAGED";
  const existingEf = lifecycle === "EXISTING" && !legacy;
  return {
    compositionContractVersion: 1,
    governanceEnvelope: {
      contractVersion: 1, targetId,
      governance: {
        applicationId: "orders", environment: "TEST",
        binding: { endpointReference: "orders-test-sql", databaseName: "Orders", serverMatchPolicy: "ALLOW_LIST", allowedServerInstances: ["SQL01\\MAIN"] },
        databaseLifecycle: lifecycle, changeManagementMode: mode,
        authorityReference: { kind: "REGISTRY_GIT_PR", policyPath: "database-registry/CODEOWNERS" }
      },
      sourceProvenance: { ...provenance },
      observations: {
        physicalState: lifecycle === "NEW" ? "EMPTY" : "POPULATED",
        repositoryState: legacy ? "ABSENT" : "PRESENT_VALID",
        historyState: existingEf ? "PRESENT" : "ABSENT",
        registryState: "TARGET_REGISTERED", schemaRelation: "CONSISTENT",
        onboardingState: lifecycle === "EXISTING" ? "MANAGED" : "NOT_REQUIRED"
      },
      evidence: ["REGISTRY", "SQL_DISCOVERY", "SCHEMA_CAPTURE", "REPOSITORY", "ONBOARDING"].map(evidenceItem)
    },
    classificationSources: {
      producerContractVersion: 1,
      declarationsSource: { databaseLifecycle: lifecycle, changeManagementMode: mode },
      connectionSource: { status: "SUCCEEDED" }, databaseLookupSource: { status: "FOUND" }, targetConnectionSource: { status: "SUCCEEDED" },
      metadataSource: { status: "SUFFICIENT" },
      physicalSource: { status: "OBSERVED", businessObjectCount: lifecycle === "NEW" ? 0 : 4, technicalObjectCount: 0 },
      historySource: existingEf ? { status: "PRESENT", migrationCount: 1, migrationIds: [migrationId] } : { status: "ABSENT" },
      repositorySource: legacy ? { status: "ABSENT" } : { status: "PRESENT_VALID", migrations: { count: 1, ids: [migrationId] } },
      schemaSource: { status: "CONSISTENT" }, registrySource: { status: "CERTIFIED" },
      onboardingSource: { status: lifecycle === "EXISTING" ? "MANAGED" : "NOT_REQUIRED" }
    }
  };
}

const run = (value, environment = "TEST") => executeRuntime({ environment, inputText: JSON.stringify(value) });
const boundary = (value, reason, exitCode) => { const actual = run(value); assert.equal(actual.exitCode, exitCode); assert.equal(actual.result.reason, reason); assert.equal(actual.result.classificationInvoked, false); };
let passed = 0;
function test(name, fn) { try { fn(); passed += 1; console.log(`PASS ${name}`); } catch (error) { console.error(`FAIL ${name}`); throw error; } }

test("EXISTING_EF coherente", () => { const actual = run(input()); assert.equal(actual.exitCode, 0); assert.equal(actual.result.classification.classificationResult.inferences.scenario, "EXISTING_EF"); });
test("Repository ERROR", () => { const value = input(); value.governanceEnvelope.observations.repositoryState = "ERROR"; value.classificationSources.repositorySource = { status: "ERROR" }; boundary(value, "REPOSITORY_ERROR", 75); });
test("Repository ABSENT con history", () => { const value = input(); value.governanceEnvelope.observations.repositoryState = "ABSENT"; value.classificationSources.repositorySource = { status: "ABSENT" }; assert.equal(run(value).result.classification.classificationResult.decision.blocks.includes("BLOCKED_HISTORY_WITHOUT_REPOSITORY"), true); });
for (const status of ["AUTHENTICATION_FAILED", "TRANSPORT_FAILED", "TIMEOUT", "CANCELLED"]) test(`SQL ${status}`, () => { const value = input(); value.classificationSources.targetConnectionSource.status = status; value.classificationSources.metadataSource.status = "NOT_ATTEMPTED"; value.classificationSources.physicalSource = { status: "NOT_ATTEMPTED" }; value.classificationSources.historySource = { status: "NOT_ATTEMPTED" }; value.classificationSources.schemaSource.status = "NOT_EVALUATED"; value.governanceEnvelope.observations.physicalState = "NOT_ATTEMPTED"; value.governanceEnvelope.observations.historyState = "NOT_ATTEMPTED"; value.governanceEnvelope.observations.schemaRelation = "NOT_EVALUATED"; const actual = run(value); assert.equal(actual.exitCode, 75); assert.equal(actual.result.reason, `SQL_${status}`); assert.equal(actual.result.classificationInvoked, false); });
test("targetId mismatch", () => { const value = input(); value.governanceEnvelope.evidence[3].targetId = "db-target:other"; boundary(value, "BLOCKED_REPOSITORY_TARGET_ID_MISMATCH", 66); });
test("binding mismatch", () => { const value = input(); value.governanceEnvelope.evidence[1].observedDatabaseIdentity.databaseName = "Other"; boundary(value, "BLOCKED_SQL_DISCOVERY_DATABASE_MISMATCH", 66); });
test("Registry BASELINE_REQUIRED", () => { const value = input(); value.classificationSources.registrySource.status = "BASELINE_REQUIRED"; assert.equal(run(value).result.classification.classificationResult.decision.blocks.includes("BLOCKED_BASELINE_REQUIRED"), true); });
test("Registry INVALID", () => { const value = input(); value.governanceEnvelope.observations.registryState = "INVALID"; value.classificationSources.registrySource.status = "INVALID"; boundary(value, "BLOCKED_REGISTRY_CONTRADICTION", 66); });
test("Schema DRIFT_DETECTED", () => { const value = input(); value.governanceEnvelope.observations.schemaRelation = "DRIFT_DETECTED"; value.classificationSources.schemaSource.status = "DRIFT_DETECTED"; boundary(value, "BLOCKED_SCHEMA_DRIFT", 66); });
test("Schema ERROR", () => { const value = input(); value.governanceEnvelope.observations.schemaRelation = "ERROR"; value.classificationSources.schemaSource.status = "ERROR"; boundary(value, "SCHEMA_ERROR", 75); });
test("Onboarding BLOCKED", () => { const value = input(); value.governanceEnvelope.observations.onboardingState = "BLOCKED"; value.classificationSources.onboardingSource.status = "BLOCKED"; boundary(value, "BLOCKED_ONBOARDING", 66); });
test("LEGACY sin authority", () => { const value = input("EXISTING", "LEGACY_UNMANAGED"); delete value.governanceEnvelope.governance.authorityReference; boundary(value, "GOVERNANCE_CONTRACT_INVALID", 65); });
test("LEGACY gobernado válido", () => { const actual = run(input("EXISTING", "LEGACY_UNMANAGED")); assert.equal(actual.result.classification.classificationResult.inferences.scenario, "EXISTING_LEGACY"); });
test("NEW técnico bloqueado", () => { const value = input("NEW"); value.governanceEnvelope.observations.physicalState = "TECHNICAL_ONLY"; value.classificationSources.physicalSource = { status: "OBSERVED", businessObjectCount: 0, technicalObjectCount: 1 }; boundary(value, "BLOCKED_NEW_WITH_PREEXISTING_STATE", 66); });
test("evidencia incompleta", () => { const value = input(); delete value.classificationSources.schemaSource; boundary(value, "BLOCKED_CROSS_SOURCE_CONTRADICTION", 66); });
test("provenance inválida", () => { const value = input(); delete value.governanceEnvelope.evidence[0].sourceProvenance.sourceSha256; boundary(value, "EVIDENCE_CONTRACT_INVALID", 65); });
test("environment gate explícito", () => { for (const environment of [undefined, "", "QA", "PROD", "test", "STAGING"]) { const actual = executeRuntime({ environment, inputText: JSON.stringify(input()) }); assert.equal(actual.result.reason, "TEST_ENVIRONMENT_REQUIRED"); assert.equal(actual.result.classificationInvoked, false); assert.equal(createArtifact(actual).classification, null); } assert.equal(run(input(), "TEST").result.classificationInvoked, true); });
test("governance no TEST rechazada", () => { const value = input(); value.governanceEnvelope.governance.environment = "QA"; boundary(value, "GOVERNED_TEST_ENVIRONMENT_REQUIRED", 66); });
test("artifact sanitizado y determinístico", () => { const value = input(); const runtime = run(value); const first = JSON.stringify(createArtifact(runtime, value)); const second = JSON.stringify(createArtifact(run(input()), input())); assert.equal(first, second); for (const forbidden of ["password=", "server=", "C:\\\\", "stderr"]) assert.equal(first.toLowerCase().includes(forbidden.toLowerCase()), false); assert.equal(JSON.parse(first).targetId, targetId); assert.equal(JSON.parse(first).sourceStates.targetConnection, "SUCCEEDED"); });
test("artifact sin resultado stale cuando classifier no se invoca", () => { const actual = run(input(), "QA"); assert.equal(createArtifact(actual).classification, null); });
test("paths limitados a workspace o runner temp", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "hg6a-paths-"));
  const workspace = path.join(root, "workspace"), runner = path.join(root, "runner");
  fs.mkdirSync(workspace); fs.mkdirSync(runner);
  const evidence = path.join(workspace, "evidence.json"); fs.writeFileSync(evidence, "{}\n");
  const env = { GITHUB_WORKSPACE: workspace, RUNNER_TEMP: runner };
  try {
    assert.equal(resolveInputPath("evidence.json", env), evidence);
    assert.equal(resolveOutputDirectory(path.join(runner, "artifact"), env), path.join(runner, "artifact"));
    assert.throws(() => resolveInputPath(path.join(root, "secret.txt"), env));
    assert.throws(() => resolveOutputDirectory(path.join(root, "outside"), env));
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("runner sanitiza errores de path", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "hg6a-cli-"));
  const workspace = path.join(root, "workspace"), runner = path.join(root, "runner"), outside = path.join(root, "customer-secret.json");
  fs.mkdirSync(workspace); fs.mkdirSync(runner); fs.writeFileSync(outside, "password=do-not-log\n");
  let stderr = "";
  try {
    const exit = runCli({ env: { ENVIRONMENT_NAME: "TEST", EVIDENCE_FILE: outside, OUTPUT_DIRECTORY: path.join(runner, "artifact"), GITHUB_WORKSPACE: workspace, RUNNER_TEMP: runner }, writeStderr: value => { stderr += value; } });
    assert.equal(exit, 70); assert.equal(stderr, "classification-v2-test-runtime: RUNTIME_IO_ERROR\n");
    assert.equal(stderr.includes("customer-secret"), false); assert.equal(stderr.includes("do-not-log"), false);
    const artifact = JSON.parse(fs.readFileSync(path.join(runner, "artifact", "classification-v2-test.json"), "utf8"));
    assert.equal(artifact.classification, null); assert.equal(artifact.targetId, null);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("action.yml coincide con contrato público", () => {
  const action = fs.readFileSync(new URL("../classification-v2-test-runtime/action.yml", import.meta.url), "utf8");
  const environmentBlock = action.match(/  environment-name:\n([\s\S]*?)(?=  evidence-file:)/u)?.[1] ?? "";
  assert.match(environmentBlock, /required: true/u); assert.doesNotMatch(environmentBlock, /default:/u);
  assert.match(action, /  evidence-file:[\s\S]*?required: true/u);
  for (const output of ["artifact-path", "status", "reason", "classification-invoked", "target-id"]) assert.match(action, new RegExp(`^  ${output}:`, "mu"));
  assert.doesNotMatch(action, /secrets\.|QA|PROD/u);
  assert.match(action, /run: node "\$GITHUB_ACTION_PATH\/\.\.\/scripts\/run-classification-v2-test-runtime\.mjs"/u);
});

console.log(`OK: ${passed} casos de HG6 runtime TEST-only`);
