#!/usr/bin/env node

import fs from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const MAX_COUNT = Number.MAX_SAFE_INTEGER;
const MIGRATION_ID_PATTERN = /^[0-9]{14}_[A-Za-z0-9_]+$/;
const ROOT_KEYS = [
  "producerContractVersion", "declarationsSource", "connectionSource",
  "databaseLookupSource", "metadataSource", "physicalSource", "historySource",
  "repositorySource", "schemaSource", "registrySource", "onboardingSource"
];
const ENUMS = Object.freeze({
  databaseLifecycle: ["NEW", "EXISTING"],
  changeManagementMode: ["EF_MIGRATIONS", "LEGACY_UNMANAGED"],
  connectionSource: ["SUCCEEDED", "FAILED", "TIMEOUT", "NOT_ATTEMPTED"],
  databaseLookupSource: ["FOUND", "NOT_FOUND", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  metadataSource: ["SUFFICIENT", "INSUFFICIENT", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  physicalSource: ["OBSERVED", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  historySource: ["ABSENT", "PRESENT", "UNREADABLE", "INVALID_STRUCTURE", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  repositorySource: ["ABSENT", "PRESENT_VALID", "INVALID", "AMBIGUOUS", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  schemaSource: ["NOT_EVALUATED", "CONSISTENT", "DRIFT_DETECTED", "INSUFFICIENT_EVIDENCE", "UNKNOWN", "ERROR"],
  registrySource: ["NOT_EVALUATED", "TARGET_NOT_REGISTERED", "BASELINE_REQUIRED", "CERTIFIED", "INVALID", "CONTRADICTORY", "UNKNOWN", "ERROR"],
  onboardingSource: ["NOT_REQUIRED", "REQUIRED", "PENDING", "MANAGED", "BLOCKED", "UNKNOWN", "ERROR"]
});

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value) && Object.getPrototypeOf(value) === Object.prototype;
}

function hasExactKeys(value, required, allowed = required) {
  return isPlainObject(value) && required.every(key => Object.hasOwn(value, key)) && Object.keys(value).every(key => allowed.includes(key));
}

function sourceError(code = "SOURCE_EVIDENCE_INVALID") {
  return { exitCode: 65, code };
}

function contradiction(code = "SOURCE_EVIDENCE_CONTRADICTORY") {
  return { exitCode: 66, code };
}

function checkStatusSource(source, key) {
  return hasExactKeys(source, ["status"]) && typeof source.status === "string" && ENUMS[key].includes(source.status);
}

function validCount(value) {
  return Number.isSafeInteger(value) && value >= 0 && value <= MAX_COUNT;
}

function validIds(value, repository) {
  return Array.isArray(value) && value.every(id =>
    typeof id === "string" && id.length > 0 && id.trim() === id &&
    !/[\u0000-\u001f\u007f]/u.test(id) && (!repository || MIGRATION_ID_PATTERN.test(id))
  );
}

export function composeEnvelope(envelope) {
  if (!isPlainObject(envelope)) return sourceError("ROOT_OBJECT_REQUIRED");
  if (envelope.producerContractVersion !== 1) return sourceError("PRODUCER_CONTRACT_VERSION_INVALID");
  if (!ROOT_KEYS.every(key => Object.hasOwn(envelope, key))) return sourceError("SOURCE_SECTION_REQUIRED");
  if (Object.keys(envelope).some(key => !ROOT_KEYS.includes(key))) return sourceError("UNKNOWN_SOURCE_PROPERTY");

  const declarations = envelope.declarationsSource;
  if (!hasExactKeys(declarations, ["databaseLifecycle", "changeManagementMode"])) return sourceError("DECLARATIONS_SOURCE_INVALID");
  if (!ENUMS.databaseLifecycle.includes(declarations.databaseLifecycle)) return sourceError("DATABASE_LIFECYCLE_INVALID");
  if (!ENUMS.changeManagementMode.includes(declarations.changeManagementMode)) return sourceError("CHANGE_MANAGEMENT_MODE_INVALID");

  for (const key of ["connectionSource", "databaseLookupSource", "metadataSource", "schemaSource", "registrySource", "onboardingSource"]) {
    if (!checkStatusSource(envelope[key], key)) return sourceError(`${key.replace(/Source$/, "").toUpperCase()}_SOURCE_INVALID`);
  }

  const physical = envelope.physicalSource;
  if (!hasExactKeys(physical, ["status"], ["status", "businessObjectCount", "technicalObjectCount"]) ||
      typeof physical.status !== "string" || !ENUMS.physicalSource.includes(physical.status)) {
    return sourceError("PHYSICAL_SOURCE_INVALID");
  }
  if (physical.status === "OBSERVED" && !Object.hasOwn(physical, "businessObjectCount")) return sourceError("BUSINESS_OBJECT_COUNT_REQUIRED");
  for (const key of ["businessObjectCount", "technicalObjectCount"]) {
    if (Object.hasOwn(physical, key) && !validCount(physical[key])) return sourceError("COUNT_INVALID");
  }

  const history = envelope.historySource;
  if (!hasExactKeys(history, ["status"], ["status", "migrationCount", "migrationIds"]) ||
      typeof history.status !== "string" || !ENUMS.historySource.includes(history.status)) {
    return sourceError("HISTORY_SOURCE_INVALID");
  }
  if (history.status === "PRESENT") {
    if (!Object.hasOwn(history, "migrationCount") || !Object.hasOwn(history, "migrationIds")) return sourceError("HISTORY_DETAILS_REQUIRED");
  }
  if (Object.hasOwn(history, "migrationCount") && !validCount(history.migrationCount)) return sourceError("COUNT_INVALID");
  if (Object.hasOwn(history, "migrationIds") && !validIds(history.migrationIds, false)) return sourceError("MIGRATION_IDS_INVALID");

  const repository = envelope.repositorySource;
  if (!hasExactKeys(repository, ["status"], ["status", "migrations"]) ||
      typeof repository.status !== "string" || !ENUMS.repositorySource.includes(repository.status)) {
    return sourceError("REPOSITORY_SOURCE_INVALID");
  }
  if (repository.status === "PRESENT_VALID" && !Object.hasOwn(repository, "migrations")) return sourceError("REPOSITORY_MIGRATIONS_REQUIRED");
  if (Object.hasOwn(repository, "migrations")) {
    if (!hasExactKeys(repository.migrations, ["count", "ids"]) || !validCount(repository.migrations.count) || !validIds(repository.migrations.ids, true)) {
      return sourceError("REPOSITORY_MIGRATIONS_INVALID");
    }
  }

  if (physical.status !== "OBSERVED" && (Object.hasOwn(physical, "businessObjectCount") || Object.hasOwn(physical, "technicalObjectCount"))) {
    return contradiction("PHYSICAL_DETAILS_WITHOUT_OBSERVATION");
  }
  if (history.status !== "PRESENT" && (Object.hasOwn(history, "migrationCount") || Object.hasOwn(history, "migrationIds"))) {
    return contradiction("HISTORY_DETAILS_WITHOUT_PRESENT_STATUS");
  }
  if (history.status === "PRESENT" && history.migrationCount !== history.migrationIds.length) {
    return contradiction("HISTORY_COUNT_MISMATCH");
  }
  if (repository.status !== "PRESENT_VALID" && Object.hasOwn(repository, "migrations")) {
    return contradiction("REPOSITORY_DETAILS_WITHOUT_VALID_STATUS");
  }
  if (repository.status === "PRESENT_VALID" && repository.migrations.count !== repository.migrations.ids.length) {
    return contradiction("REPOSITORY_COUNT_MISMATCH");
  }

  const raw = {
    contractVersion: 2,
    declarations: {
      databaseLifecycle: declarations.databaseLifecycle,
      changeManagementMode: declarations.changeManagementMode
    },
    connection: { status: envelope.connectionSource.status },
    databaseLookup: { status: envelope.databaseLookupSource.status },
    metadata: { status: envelope.metadataSource.status },
    physical: { status: physical.status },
    history: { status: history.status },
    repository: { status: repository.status },
    schema: { status: envelope.schemaSource.status },
    registry: { status: envelope.registrySource.status },
    onboarding: { status: envelope.onboardingSource.status }
  };
  for (const key of ["businessObjectCount", "technicalObjectCount"]) {
    if (Object.hasOwn(physical, key)) raw.physical[key] = physical[key];
  }
  for (const key of ["migrationCount", "migrationIds"]) {
    if (Object.hasOwn(history, key)) raw.history[key] = key === "migrationIds" ? [...history[key]] : history[key];
  }
  if (Object.hasOwn(repository, "migrations")) {
    raw.repository.migrations = { count: repository.migrations.count, ids: [...repository.migrations.ids] };
  }
  return { exitCode: 0, code: "COMPOSED", raw };
}

export function parseInput(text) {
  if (text.length === 0) return { exitCode: 64, code: "STDIN_REQUIRED" };
  try {
    return composeEnvelope(JSON.parse(text));
  } catch {
    return sourceError("JSON_INVALID");
  }
}

export function runCli({ argv = process.argv, readInput = () => fs.readFileSync(0, "utf8"), writeStdout = value => fs.writeSync(1, value), writeStderr = value => fs.writeSync(2, value) } = {}) {
  let outcome;
  try {
    outcome = argv.length === 2 ? parseInput(readInput()) : { exitCode: 64, code: "ARGUMENTS_NOT_ALLOWED" };
  } catch {
    outcome = { exitCode: 70, code: "PRODUCER_INTERNAL_ERROR" };
  }
  if (outcome.exitCode === 0) {
    try { writeStdout(`${JSON.stringify(outcome.raw)}\n`); }
    catch { outcome = { exitCode: 70, code: "PRODUCER_INTERNAL_ERROR" }; }
  }
  if (outcome.exitCode !== 0) {
    try { writeStderr(`classification-evidence-v2-producer: ${outcome.code}\n`); } catch { /* Diagnostic output unavailable. */ }
  }
  return outcome.exitCode;
}

function main() {
  process.exitCode = runCli();
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
