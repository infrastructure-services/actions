#!/usr/bin/env node

import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { composeEnvelope } from "../scripts/compose-classification-evidence-v2.mjs";
import { adaptEvidence } from "../scripts/adapt-classification-evidence-v2.mjs";
import { discoverRepository } from "../scripts/discover-repository-v2.mjs";

const id = "20260101000000_Initial";
const repositoryFixture = fs.mkdtempSync(path.join(os.tmpdir(), "local-composition-v2-"));
fs.writeFileSync(path.join(repositoryFixture, "App.csproj"), "<Project />\n");
fs.mkdirSync(path.join(repositoryFixture, "Migrations"));
fs.writeFileSync(path.join(repositoryFixture, "Migrations", `${id}.cs`), "public partial class Initial {}\n");
fs.writeFileSync(path.join(repositoryFixture, "Migrations", `${id}.Designer.cs`), `[Migration("${id}")]\npartial class InitialMetadata {}\n`);
const observedRepositorySource = discoverRepository({ repositoryDiscoveryContractVersion: 1, inspectionStatus: "READY", workspace: repositoryFixture }).repositorySource;
process.on("exit", () => fs.rmSync(repositoryFixture, { recursive: true, force: true }));
let passed = 0;
const test = (name, fn) => { try { fn(); passed += 1; console.log(`PASS: ${name}`); } catch (error) { console.error(`FAIL: ${name}`); throw error; } };

function envelope(overrides = {}) {
  return {
    producerContractVersion: 1,
    declarationsSource: { databaseLifecycle: "EXISTING", changeManagementMode: "EF_MIGRATIONS" },
    connectionSource: { status: "SUCCEEDED" },
    databaseLookupSource: { status: "FOUND" },
    targetConnectionSource: { status: "SUCCEEDED" },
    metadataSource: { status: "SUFFICIENT" },
    physicalSource: { status: "OBSERVED", businessObjectCount: 2, technicalObjectCount: 1 },
    historySource: { status: "PRESENT", migrationCount: 1, migrationIds: [id] },
    repositorySource: { status: "PRESENT_VALID", migrations: { count: 1, ids: [id] } },
    schemaSource: { status: "CONSISTENT" },
    registrySource: { status: "CERTIFIED" },
    onboardingSource: { status: "MANAGED" },
    ...overrides
  };
}

function composeAndAdapt(value) {
  const composed = composeEnvelope(value);
  assert.equal(composed.exitCode, 0);
  return adaptEvidence(composed.raw);
}

function classification(value) {
  const adapted = composeAndAdapt(value);
  assert.equal(adapted.exitCode, 0);
  assert.equal(adapted.result.classificationInvoked, true);
  return adapted.result.classificationResult;
}

function hasBlock(result, block) { assert.equal(result.decision.blocks.includes(block), true); }

test("A EXISTING_EF válido", () => {
  const result = classification(envelope({ repositorySource: observedRepositorySource }));
  assert.equal(result.inferences.scenario, "EXISTING_EF"); assert.equal(result.decision.status, "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE");
});
test("B history presente y repo sin migrations", () => {
  const result = classification(envelope({ repositorySource: { status: "ABSENT" } }));
  assert.equal(result.decision.status, "BLOCKED"); hasBlock(result, "BLOCKED_HISTORY_WITHOUT_REPOSITORY");
});
test("C repo ERROR conserva error técnico", () => {
  const observedError = discoverRepository({ repositoryDiscoveryContractVersion: 1, inspectionStatus: "READY", workspace: "C:/private" }, { statSync() { throw new Error("C:/private"); } });
  assert.equal(observedError.exitCode, 75);
  const result = composeAndAdapt(envelope({ repositorySource: observedError.repositorySource }));
  assert.equal(result.exitCode, 75); assert.equal(result.result.errors[0].code, "REPOSITORY_ERROR"); assert.equal(result.result.classificationInvoked, false);
});
for (const [label, status] of [["D SQL target TIMEOUT", "TIMEOUT"], ["E SQL target AUTHENTICATION_FAILED", "AUTHENTICATION_FAILED"]]) {
  test(label, () => {
    const value = envelope({ targetConnectionSource: { status }, metadataSource: { status: "NOT_ATTEMPTED" }, physicalSource: { status: "NOT_ATTEMPTED" }, historySource: { status: "NOT_ATTEMPTED" }, schemaSource: { status: "NOT_EVALUATED" } });
    const result = composeAndAdapt(value); assert.equal(result.exitCode, 75); assert.equal(result.result.classificationInvoked, false);
  });
}
test("F SQL parcial falla cerrado", () => {
  const result = classification(envelope({ metadataSource: { status: "UNKNOWN" }, physicalSource: { status: "UNKNOWN" }, historySource: { status: "UNKNOWN" }, schemaSource: { status: "UNKNOWN" } }));
  assert.equal(result.decision.status, "BLOCKED");
});
test("G contradicción repo SQL falla cerrado", () => {
  const result = classification(envelope({ historySource: { status: "PRESENT", migrationCount: 1, migrationIds: ["20260101000000_Unexpected"] } }));
  assert.equal(result.decision.status, "BLOCKED"); hasBlock(result, "BLOCKED_HISTORY_UNKNOWN_ID");
});
test("H NEW candidate conserva bloqueo técnico", () => {
  const result = classification(envelope({
    declarationsSource: { databaseLifecycle: "NEW", changeManagementMode: "EF_MIGRATIONS" },
    physicalSource: { status: "OBSERVED", businessObjectCount: 0, technicalObjectCount: 1 },
    historySource: { status: "ABSENT" }, schemaSource: { status: "NOT_EVALUATED" }, registrySource: { status: "NOT_EVALUATED" }, onboardingSource: { status: "NOT_REQUIRED" }
  }));
  assert.equal(result.inferences.scenario, "UNCLASSIFIED"); hasBlock(result, "BLOCKED_NEW_TECHNICAL_ONLY_UNDECIDED");
});
test("I ausencia de migrations no infiere LEGACY_UNMANAGED", () => {
  const result = classification(envelope({ repositorySource: { status: "ABSENT" } }));
  assert.notEqual(result.inferences.scenario, "EXISTING_LEGACY"); assert.equal(result.declarations.changeManagementMode, "EF_MIGRATIONS");
});

console.log(`OK: ${passed} casos de composición local V2`);
