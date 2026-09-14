import assert from "node:assert/strict";
import { adaptEvidence, classifierArguments, parseInput, runCli, validateContract } from "../scripts/adapt-classification-evidence-v2.mjs";

let passed = 0;
function test(name, fn) { fn(); passed += 1; console.log(`PASS: ${name}`); }

const ids = ["20240101010101_Initial", "20240202020202_AddOrders", "20240303030303_Final"];
function evidence(overrides = {}) {
  const base = {
    contractVersion: 2,
    declarations: { databaseLifecycle: "EXISTING", changeManagementMode: "EF_MIGRATIONS" },
    connection: { status: "SUCCEEDED" }, databaseLookup: { status: "FOUND" }, metadata: { status: "SUFFICIENT" },
    physical: { status: "OBSERVED", businessObjectCount: 5, technicalObjectCount: 1 },
    history: { status: "PRESENT", migrationCount: 2, migrationIds: ids.slice(0, 2) },
    repository: { status: "PRESENT_VALID", migrations: { count: 3, ids: [...ids] } },
    schema: { status: "CONSISTENT" }, registry: { status: "CERTIFIED" }, onboarding: { status: "MANAGED" }
  };
  return structuredClone(Object.assign(base, overrides));
}

function withoutInternal(overrides = {}) {
  return evidence({ metadata: { status: "UNKNOWN" }, physical: { status: "UNKNOWN" }, history: { status: "UNKNOWN" }, schema: { status: "UNKNOWN" }, ...overrides });
}

function validClassification(facts) {
  const unknownOrder = ["physicalExistence", "metadataVisibility", "physicalState", "repositoryState", "historyState", "repoHistoryRelation", "schemaRelation", "registryState"];
  return {
    contractVersion: 2, rulesVersion: "1",
    declarations: { databaseLifecycle: facts.databaseLifecycle, changeManagementMode: facts.changeManagementMode },
    observations: {
      physicalExistence: facts.physicalExistence, metadataVisibility: facts.metadataVisibility, physicalState: facts.physicalState,
      repositoryState: facts.repositoryState, historyState: facts.historyState, repoHistoryRelation: facts.repoHistoryRelation,
      schemaRelation: facts.schemaRelation, registryState: facts.registryState, onboardingState: facts.onboardingState
    },
    inferences: { scenario: "UNCLASSIFIED", hasPendingMigrations: false },
    decision: { status: "BLOCKED", primaryBlock: "BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT", blocks: ["BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT"] },
    warnings: [], unknownFields: unknownOrder.filter(key => facts[key] === "UNKNOWN")
  };
}

function outcome(raw, classifier = validClassification) {
  let calls = 0;
  const result = adaptEvidence(raw, facts => { calls += 1; return classifier(facts); });
  return { ...result, calls };
}

function expectExit(raw, exitCode, code) {
  const actual = outcome(raw);
  assert.equal(actual.exitCode, exitCode); assert.equal(actual.calls, 0);
  assert.equal(actual.result.classificationInvoked, false); assert.equal(actual.result.normalizedFacts, null);
  assert.equal(Object.hasOwn(actual.result, "classificationResult"), false); assert.equal(actual.result.errors[0].code, code);
}

test("JSON inválido", () => { const x = parseInput("{"); assert.equal(x.exitCode, 65); assert.equal(x.result.errors[0].code, "JSON_INVALID"); });
test("stdin vacío usa exit 64", () => assert.equal(parseInput("").exitCode, 64));
test("root no objeto", () => assert.equal(parseInput("[]").result.errors[0].code, "ROOT_OBJECT_REQUIRED"));
test("propiedad obligatoria ausente", () => { const x = evidence(); delete x.metadata; expectExit(x, 65, "REQUIRED_PROPERTY_MISSING"); });
test("propiedad desconocida", () => { const x = evidence(); x.extra = "do-not-reflect"; expectExit(x, 65, "UNKNOWN_PROPERTY"); });
test("enum desconocido", () => { const x = evidence(); x.metadata.status = "OTHER"; expectExit(x, 65, "ENUM_INVALID"); });
test("contractVersion inválida", () => { const x = evidence(); x.contractVersion = 3; expectExit(x, 65, "CONTRACT_VERSION_INVALID"); });
test("null rechazado", () => { const x = evidence(); x.registry = null; expectExit(x, 65, "OBJECT_REQUIRED"); });
test("tipo incorrecto", () => { const x = evidence(); x.physical.businessObjectCount = true; expectExit(x, 65, "COUNT_INVALID"); });

test("connection failed", () => expectExit(withoutInternal({ connection: { status: "FAILED" }, databaseLookup: { status: "UNKNOWN" } }), 75, "CONNECTION_FAILED"));
test("connection timeout", () => expectExit(withoutInternal({ connection: { status: "TIMEOUT" }, databaseLookup: { status: "UNKNOWN" } }), 75, "CONNECTION_TIMEOUT"));
for (const [key, code] of [["metadata", "METADATA_ERROR"], ["history", "HISTORY_ERROR"], ["schema", "SCHEMA_ERROR"], ["registry", "REGISTRY_ERROR"], ["onboarding", "ONBOARDING_ERROR"], ["physical", "PHYSICAL_OBSERVATION_ERROR"], ["repository", "REPOSITORY_ERROR"]]) {
  test(`${key} error`, () => { const x = evidence(); x[key] = { status: "ERROR" }; expectExit(x, 75, code); });
}
test("database lookup error sin evidencia interna", () => expectExit(withoutInternal({ databaseLookup: { status: "ERROR" } }), 75, "DATABASE_LOOKUP_ERROR"));

test("count cero válido produce EMPTY por frontera", () => { const x = evidence({ history: { status: "PRESENT", migrationCount: 0, migrationIds: [] } }); assert.equal(outcome(x).result.normalizedFacts.historyState, "EMPTY"); });
test("count positivo válido", () => assert.equal(validateContract(evidence()).length, 0));
for (const [name, value] of [["negativo", -1], ["decimal", 1.5], ["string", "1"], ["null", null], ["overflow", 9007199254740992]]) {
  test(`count ${name}`, () => { const x = evidence(); x.physical.businessObjectCount = value; expectExit(x, 65, "COUNT_INVALID"); });
}
test("count obligatorio ausente", () => { const x = evidence(); delete x.physical.businessObjectCount; expectExit(x, 65, "REQUIRED_PROPERTY_MISSING"); });
test("history mismatch", () => { const x = evidence(); x.history.migrationCount = 1; expectExit(x, 66, "HISTORY_COUNT_MISMATCH"); });

test("lookup positivo sin conexión", () => { const x = evidence({ connection: { status: "FAILED" } }); expectExit(x, 66, "LOOKUP_WITHOUT_CONNECTION"); });
test("NOT_FOUND con evidencia interna", () => { const x = evidence({ databaseLookup: { status: "NOT_FOUND" } }); expectExit(x, 66, "NOT_FOUND_WITH_DATABASE_EVIDENCE"); });
for (const lookupStatus of ["UNKNOWN", "ERROR", "NOT_ATTEMPTED"]) {
  test(`evidencia interna con lookup ${lookupStatus}`, () => expectExit(evidence({ databaseLookup: { status: lookupStatus } }), 66, "INTERNAL_EVIDENCE_WITHOUT_FOUND_DATABASE"));
}
test("contradicción precede error técnico", () => expectExit(evidence({ connection: { status: "FAILED" }, databaseLookup: { status: "UNKNOWN" } }), 66, "INTERNAL_EVIDENCE_WITHOUT_FOUND_DATABASE"));
test("counts sin observación", () => { const x = evidence(); x.physical.status = "UNKNOWN"; expectExit(x, 66, "COUNTS_WITHOUT_OBSERVATION"); });
test("history ABSENT con detalles", () => { const x = evidence(); x.history.status = "ABSENT"; expectExit(x, 66, "HISTORY_DETAILS_NOT_ALLOWED"); });
test("history PRESENT requiere detalles", () => expectExit(evidence({ history: { status: "PRESENT" } }), 65, "REQUIRED_PROPERTY_MISSING"));
test("repository PRESENT_VALID vacío", () => expectExit(evidence({ repository: { status: "PRESENT_VALID", migrations: { count: 0, ids: [] } } }), 66, "PRESENT_VALID_REPOSITORY_EMPTY"));
test("IDs repo duplicados", () => expectExit(evidence({ repository: { status: "PRESENT_VALID", migrations: { count: 2, ids: [ids[0], ids[0]] } } }), 66, "DUPLICATE_MIGRATION_ID"));
test("IDs history duplicados", () => expectExit(evidence({ history: { status: "PRESENT", migrationCount: 2, migrationIds: [ids[0], ids[0]] } }), 66, "DUPLICATE_MIGRATION_ID"));

test("technical count ausente con business positivo", () => { const x = evidence(); delete x.physical.technicalObjectCount; const actual = outcome(x); assert.equal(actual.exitCode, 0); assert.equal(actual.result.normalizedFacts.physicalState, "POPULATED"); });
test("technical count ausente con business cero", () => { const x = evidence(); x.physical.businessObjectCount = 0; delete x.physical.technicalObjectCount; const actual = outcome(x); assert.equal(actual.exitCode, 0); assert.equal(actual.result.normalizedFacts.physicalState, "UNKNOWN"); });

for (const [name, repositoryIds, historyIds, expected] of [
  ["EXACT_MATCH", ids.slice(0, 2), ids.slice(0, 2), "EXACT_MATCH"], ["VALID_PREFIX", ids, ids.slice(0, 2), "VALID_PREFIX"],
  ["HISTORY_AHEAD", ids.slice(0, 2), ids, "HISTORY_AHEAD"], ["REORDERED", ids.slice(0, 2), [ids[1], ids[0]], "REORDERED"],
  ["DIVERGED", ids, [ids[0], ids[2]], "DIVERGED"], ["UNKNOWN_ID", ids.slice(0, 2), [ids[0], "20249999999999_Unknown"], "UNKNOWN_ID"]
]) {
  test(`relación ${name} por frontera`, () => {
    const x = evidence({ history: { status: "PRESENT", migrationCount: historyIds.length, migrationIds: historyIds }, repository: { status: "PRESENT_VALID", migrations: { count: repositoryIds.length, ids: repositoryIds } } });
    assert.equal(outcome(x).result.normalizedFacts.repoHistoryRelation, expected);
  });
}
test("relación NOT_APPLICABLE con ABSENT válido", () => assert.equal(outcome(evidence({ history: { status: "ABSENT" } })).result.normalizedFacts.repoHistoryRelation, "NOT_APPLICABLE"));
test("relación UNKNOWN conserva repository independiente", () => { const result = outcome(evidence({ repository: { status: "ABSENT" } })).result.normalizedFacts; assert.equal(result.repoHistoryRelation, "UNKNOWN"); assert.equal(result.repositoryState, "ABSENT"); });
test("casing de IDs es significativo", () => { const x = evidence(); x.history.migrationIds[0] = ids[0].toLowerCase(); assert.equal(outcome(x).result.normalizedFacts.repoHistoryRelation, "UNKNOWN_ID"); });
test("whitespace en ID es inválido", () => { const x = evidence(); x.history.migrationIds[0] = ` ${ids[0]}`; expectExit(x, 65, "MIGRATION_ID_INVALID"); });

test("ABSENT no es EMPTY por frontera", () => { const absent = outcome(evidence({ history: { status: "ABSENT" } })).result.normalizedFacts; const empty = outcome(evidence({ history: { status: "PRESENT", migrationCount: 0, migrationIds: [] } })).result.normalizedFacts; assert.equal(absent.historyState, "ABSENT"); assert.equal(empty.historyState, "EMPTY"); });
test("UNKNOWN no es ERROR", () => { assert.equal(outcome(evidence({ metadata: { status: "UNKNOWN" } })).exitCode, 0); assert.equal(outcome(evidence({ metadata: { status: "ERROR" } })).exitCode, 75); });
test("onboarding UNKNOWN no se transforma", () => expectExit(evidence({ onboarding: { status: "UNKNOWN" } }), 67, "ONBOARDING_STATE_UNREPRESENTABLE"));
test("evidencia insuficiente no produce UNCLASSIFIED", () => assert.equal(JSON.stringify(outcome(evidence({ onboarding: { status: "UNKNOWN" } })).result).includes("UNCLASSIFIED"), false));
test("mismo input produce mismo output", () => { const x = evidence(); assert.deepEqual(outcome(x).result, outcome(x).result); });
test("input no se modifica", () => { const x = evidence(); const before = JSON.stringify(x); outcome(x); assert.equal(JSON.stringify(x), before); });
test("clasificador invocado máximo una vez", () => assert.equal(outcome(evidence()).calls, 1));
test("argumentos V2 completos", () => assert.equal(classifierArguments(outcome(evidence()).result.normalizedFacts).length, 24));
test("resultado estructural", () => assert.deepEqual(Object.keys(outcome(evidence()).result), ["adapterVersion", "adapterStatus", "classificationInvoked", "normalizedFacts", "errors", "warnings", "classificationResult"]));
test("errors deterministas sin duplicados", () => { const errors = outcome(evidence({ connection: { status: "FAILED" }, databaseLookup: { status: "UNKNOWN" } })).result.errors; assert.deepEqual(errors.map(e => e.code), ["INTERNAL_EVIDENCE_WITHOUT_FOUND_DATABASE"]); assert.equal(new Set(errors.map(e => `${e.code}:${e.path}`)).size, errors.length); });
test("nombre raw desconocido no se refleja", () => { const x = evidence(); x["sensitive-control-\u0001"] = "raw-value"; const text = JSON.stringify(outcome(x).result); assert.equal(text.includes("sensitive-control"), false); assert.equal(text.includes("raw-value"), false); });

for (const [name, mutate] of [
  ["salida V2 incompleta", x => { delete x.decision.blocks; }], ["escenario V2 inválido", x => { x.inferences.scenario = "OTHER"; }],
  ["blocks V2 no es array", x => { x.decision.blocks = "BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT"; }], ["status V2 inválido", x => { x.decision.status = "OTHER"; }],
  ["hechos V2 diferentes", x => { x.observations.physicalState = "EMPTY"; }]
]) {
  test(name, () => {
    const actual = outcome(evidence(), facts => { const output = validClassification(facts); mutate(output); return output; });
    assert.equal(actual.exitCode, 70); assert.equal(actual.calls, 1); assert.equal(actual.result.adapterStatus, "CLASSIFIER_ERROR");
    assert.equal(actual.result.classificationInvoked, false); assert.equal(Object.hasOwn(actual.result, "classificationResult"), false);
  });
}

test("excepción inesperada no expone stack ni path", () => {
  let stdout = "", stderr = "";
  const exitCode = runCli({ argv: ["node", "adapter"], readInput: () => { throw new Error("unsafe C:/private/path"); }, writeStdout: value => { stdout += value; }, writeStderr: value => { stderr += value; } });
  const parsed = JSON.parse(stdout);
  assert.equal(exitCode, 70); assert.equal(parsed.errors[0].code, "ADAPTER_INTERNAL_ERROR");
  assert.equal(stderr, "classification-adapter-v2: ADAPTER_INTERNAL_ERROR\n"); assert.equal(`${stdout}${stderr}`.includes("private/path"), false); assert.equal(`${stdout}${stderr}`.includes("Error:"), false);
});

console.log(`OK: ${passed} casos unitarios del adapter V2`);
