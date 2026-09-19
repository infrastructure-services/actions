#!/usr/bin/env node

import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { composeEnvelope, parseInput, runCli } from "../scripts/compose-classification-evidence-v2.mjs";
import { adaptEvidence, normalize } from "../scripts/adapt-classification-evidence-v2.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fixtureRoot = path.join(root, "tests", "fixtures", "classification-evidence-v2");
let passed = 0;

function test(name, fn) {
  try { fn(); passed += 1; console.log(`PASS: ${name}`); }
  catch (error) { console.error(`FAIL: ${name}`); throw error; }
}

function envelope(overrides = {}) {
  return {
    producerContractVersion: 1,
    declarationsSource: { databaseLifecycle: "EXISTING", changeManagementMode: "EF_MIGRATIONS" },
    connectionSource: { status: "SUCCEEDED" },
    databaseLookupSource: { status: "FOUND" },
    targetConnectionSource: { status: "SUCCEEDED" },
    metadataSource: { status: "SUFFICIENT" },
    physicalSource: { status: "OBSERVED", businessObjectCount: 4, technicalObjectCount: 1 },
    historySource: { status: "PRESENT", migrationCount: 1, migrationIds: ["20260101000000_Initial"] },
    repositorySource: { status: "PRESENT_VALID", migrations: { count: 2, ids: ["20260101000000_Initial", "20260201000000_AddOrders"] } },
    schemaSource: { status: "CONSISTENT" },
    registrySource: { status: "CERTIFIED" },
    onboardingSource: { status: "MANAGED" },
    ...overrides
  };
}

function clone(value) { return structuredClone(value); }
function result(value) { return composeEnvelope(value); }
function raw(value) { const outcome = result(value); assert.equal(outcome.exitCode, 0); return outcome.raw; }
function expectCode(value, exitCode) { assert.equal(result(value).exitCode, exitCode); }

test("fixture completo coincide byte por byte", () => {
  const source = fs.readFileSync(path.join(fixtureRoot, "sources", "valid-complete.json"), "utf8");
  const expected = fs.readFileSync(path.join(fixtureRoot, "raw", "valid-complete.json"), "utf8");
  assert.equal(`${JSON.stringify(parseInput(source).raw)}\n`, expected);
});
test("JSON inválido", () => assert.equal(parseInput("{").exitCode, 65));
test("stdin ausente", () => assert.equal(parseInput("").exitCode, 64));
test("raíz inválida", () => assert.equal(parseInput("[]").exitCode, 65));
test("versión incorrecta", () => expectCode(envelope({ producerContractVersion: 2 }), 65));
test("versión precede forma de secciones", () => assert.equal(composeEnvelope({ producerContractVersion: 2 }).code, "PRODUCER_CONTRACT_VERSION_INVALID"));
test("sección faltante", () => { const x = envelope(); delete x.metadataSource; expectCode(x, 65); });
test("propiedad raíz desconocida", () => expectCode(envelope({ unexpected: true }), 65));
test("propiedad fuente desconocida", () => expectCode(envelope({ connectionSource: { status: "SUCCEEDED", extra: true } }), 65));
test("tipo incorrecto", () => expectCode(envelope({ connectionSource: "SUCCEEDED" }), 65));
test("enum inválido", () => expectCode(envelope({ metadataSource: { status: "VISIBLE" } }), 65));
test("enum target inválido", () => expectCode(envelope({ targetConnectionSource: { status: "FAILED" } }), 65));
test("count negativo", () => expectCode(envelope({ physicalSource: { status: "OBSERVED", businessObjectCount: -1 } }), 65));
test("count decimal", () => expectCode(envelope({ physicalSource: { status: "OBSERVED", businessObjectCount: 1.5 } }), 65));
test("count fuera de rango seguro", () => expectCode(envelope({ physicalSource: { status: "OBSERVED", businessObjectCount: Number.MAX_SAFE_INTEGER + 1 } }), 65));

test("technicalObjectCount ausente se conserva ausente", () => {
  const output = raw(envelope({ physicalSource: { status: "OBSERVED", businessObjectCount: 0 } }));
  assert.equal(Object.hasOwn(output.physical, "technicalObjectCount"), false);
});
test("businessObjectCount cero sin técnico normaliza UNKNOWN en adapter", () => {
  const output = raw(envelope({ physicalSource: { status: "OBSERVED", businessObjectCount: 0 } }));
  assert.equal(normalize(output).physicalState, "UNKNOWN");
});

for (const status of ["FAILED", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]) {
  test(`connection ${status} preservado`, () => assert.equal(raw(envelope({ connectionSource: { status } })).connection.status, status));
}
for (const status of ["NOT_FOUND", "ERROR", "UNKNOWN", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]) {
  test(`database lookup ${status} preservado`, () => assert.equal(raw(envelope({ databaseLookupSource: { status } })).databaseLookup.status, status));
}
for (const status of ["ABSENT", "UNREADABLE", "INVALID_STRUCTURE", "ERROR", "UNKNOWN", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]) {
  test(`history ${status} preservado`, () => assert.equal(raw(envelope({ historySource: { status } })).history.status, status));
}
for (const status of ["SUCCEEDED", "AUTHENTICATION_FAILED", "TRANSPORT_FAILED", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]) {
  test(`target ${status} preservado`, () => assert.equal(raw(envelope({ targetConnectionSource: { status } })).targetConnection.status, status));
}
for (const key of ["metadataSource", "physicalSource"]) {
  for (const status of ["TIMEOUT", "CANCELLED"]) {
    test(`${key} ${status} preservado`, () => assert.equal(raw(envelope({ [key]: { status } }))[key.replace("Source", "")].status, status));
  }
}
test("fuente target histórica ausente se conserva ausente", () => {
  const input = envelope(); delete input.targetConnectionSource;
  assert.equal(Object.hasOwn(raw(input), "targetConnection"), false);
});
test("history PRESENT vacío", () => assert.deepEqual(raw(envelope({ historySource: { status: "PRESENT", migrationCount: 0, migrationIds: [] } })).history, { status: "PRESENT", migrationCount: 0, migrationIds: [] }));
test("history PRESENT con IDs y orden estable", () => {
  const ids = ["20260201000000_Second", "20260101000000_First"];
  assert.deepEqual(raw(envelope({ historySource: { status: "PRESENT", migrationCount: 2, migrationIds: ids } })).history.migrationIds, ids);
});

for (const status of ["ABSENT", "INVALID", "AMBIGUOUS", "ERROR", "UNKNOWN", "NOT_ATTEMPTED"]) {
  test(`repository ${status} preservado`, () => assert.equal(raw(envelope({ repositorySource: { status } })).repository.status, status));
}
test("repository PRESENT_VALID conserva IDs", () => {
  const migrations = { count: 2, ids: ["20260201000000_Second", "20260101000000_First"] };
  assert.deepEqual(raw(envelope({ repositorySource: { status: "PRESENT_VALID", migrations } })).repository.migrations.ids, migrations.ids);
});
test("changeManagementMode ausente", () => { const x = envelope(); delete x.declarationsSource.changeManagementMode; expectCode(x, 65); });
test("changeManagementMode inválido", () => expectCode(envelope({ declarationsSource: { databaseLifecycle: "EXISTING", changeManagementMode: "INFER" } }), 65));
test("NOT_EVALUATED y UNKNOWN preservados", () => {
  const output = raw(envelope({ schemaSource: { status: "NOT_EVALUATED" }, registrySource: { status: "NOT_EVALUATED" }, onboardingSource: { status: "UNKNOWN" } }));
  assert.deepEqual([output.schema.status, output.registry.status, output.onboarding.status], ["NOT_EVALUATED", "NOT_EVALUATED", "UNKNOWN"]);
});
test("recorrido incompleto falla cerrado sin invocar V2", () => {
  const source = JSON.parse(fs.readFileSync(path.join(fixtureRoot, "sources", "incomplete-not-evaluated.json"), "utf8"));
  let calls = 0;
  const adapted = adaptEvidence(raw(source), () => { calls += 1; throw new Error("must not run"); });
  assert.equal(adapted.exitCode, 67);
  assert.equal(adapted.result.classificationInvoked, false);
  assert.equal(calls, 0);
});

test("details physical contradictorios", () => expectCode(envelope({ physicalSource: { status: "ERROR", businessObjectCount: 0 } }), 66));
test("details history contradictorios", () => expectCode(envelope({ historySource: { status: "ABSENT", migrationCount: 0, migrationIds: [] } }), 66));
test("count history contradictorio", () => expectCode(envelope({ historySource: { status: "PRESENT", migrationCount: 2, migrationIds: ["one"] } }), 66));
test("details repository contradictorios", () => expectCode(envelope({ repositorySource: { status: "ABSENT", migrations: { count: 0, ids: [] } } }), 66));
test("count repository contradictorio", () => expectCode(envelope({ repositorySource: { status: "PRESENT_VALID", migrations: { count: 2, ids: ["20260101000000_Initial"] } } }), 66));
test("duplicados no se deduplican y adapter los rechaza", () => {
  const ids = ["20260101000000_Initial", "20260101000000_Initial"];
  const output = raw(envelope({ repositorySource: { status: "PRESENT_VALID", migrations: { count: 2, ids } } }));
  assert.deepEqual(output.repository.migrations.ids, ids);
  assert.equal(adaptEvidence(output).exitCode, 66);
});
test("determinismo byte por byte", () => {
  const x = envelope();
  assert.equal(JSON.stringify(raw(x)), JSON.stringify(raw(x)));
});
test("input inmutable", () => {
  const x = envelope(); const before = clone(x); raw(x); assert.deepEqual(x, before);
});
test("mutar arrays del raw no modifica el envelope", () => {
  const x = envelope(); const output = raw(x);
  output.history.migrationIds[0] = "raw-history-change";
  output.repository.migrations.ids[0] = "raw-repository-change";
  assert.equal(x.historySource.migrationIds[0], "20260101000000_Initial");
  assert.equal(x.repositorySource.migrations.ids[0], "20260101000000_Initial");
});
test("mutar arrays del envelope no modifica el raw", () => {
  const x = envelope(); const output = raw(x);
  x.historySource.migrationIds[0] = "source-history-change";
  x.repositorySource.migrations.ids[0] = "source-repository-change";
  assert.equal(output.history.migrationIds[0], "20260101000000_Initial");
  assert.equal(output.repository.migrations.ids[0], "20260101000000_Initial");
});
test("CLI rechaza argumentos", () => {
  let stderr = ""; const exit = runCli({ argv: ["node", "producer", "arg"], writeStderr: x => { stderr += x; } });
  assert.equal(exit, 64); assert.equal(stderr, "classification-evidence-v2-producer: ARGUMENTS_NOT_ALLOWED\n");
});
test("diagnóstico sanitizado no refleja datos sensibles", () => {
  let stdout = "", stderr = "";
  const secret = "Password=forbidden;Server=private-host";
  const exit = runCli({ argv: ["node", "producer"], readInput: () => JSON.stringify({ secret }), writeStdout: x => { stdout += x; }, writeStderr: x => { stderr += x; } });
  assert.equal(exit, 65); assert.equal(stdout, ""); assert.equal(stderr.includes(secret), false); assert.match(stderr, /^classification-evidence-v2-producer: [A-Z_]+\n$/);
});
test("fallo de lectura usa exit 70 y diagnóstico sanitizado", () => {
  let stdout = "", stderr = "";
  const exit = runCli({ argv: ["node", "producer"], readInput: () => { throw new Error("sensitive read failure"); }, writeStdout: x => { stdout += x; }, writeStderr: x => { stderr += x; } });
  assert.equal(exit, 70); assert.equal(stdout, ""); assert.equal(stderr, "classification-evidence-v2-producer: PRODUCER_INTERNAL_ERROR\n"); assert.equal(stderr.includes("sensitive"), false);
});
test("fallo de escritura usa exit 70 y diagnóstico sanitizado", () => {
  let stderr = "";
  const exit = runCli({ argv: ["node", "producer"], readInput: () => JSON.stringify(envelope()), writeStdout: () => { throw new Error("sensitive write failure"); }, writeStderr: x => { stderr += x; } });
  assert.equal(exit, 70); assert.equal(stderr, "classification-evidence-v2-producer: PRODUCER_INTERNAL_ERROR\n"); assert.equal(stderr.includes("sensitive"), false);
});
test("adapter recibe exactamente stdout del productor y usa V2 real", () => {
  const sourcePath = path.join(fixtureRoot, "sources", "valid-complete.json");
  const producer = spawnSync(process.execPath, [path.join(root, "scripts", "compose-classification-evidence-v2.mjs")], { input: fs.readFileSync(sourcePath), encoding: "utf8" });
  assert.equal(producer.status, 0);
  const adapter = spawnSync(process.execPath, [path.join(root, "scripts", "adapt-classification-evidence-v2.mjs")], { input: producer.stdout, encoding: "utf8" });
  assert.equal(adapter.status, 0);
  const combined = JSON.parse(adapter.stdout);
  assert.equal(combined.adapterStatus, "CLASSIFIED");
  assert.equal(combined.classificationInvoked, true);
  assert.equal(combined.classificationResult.inferences.scenario, "EXISTING_EF");
});

console.log(`OK: ${passed} casos del productor raw V2`);
