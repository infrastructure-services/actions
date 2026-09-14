#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import fs from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const ADAPTER_VERSION = "1";
const MAX_COUNT = Number.MAX_SAFE_INTEGER;
const MIGRATION_ID_PATTERN = /^[0-9]{14}_[A-Za-z0-9_]+$/;

const ENUMS = Object.freeze({
  databaseLifecycle: ["NEW", "EXISTING"],
  changeManagementMode: ["EF_MIGRATIONS", "LEGACY_UNMANAGED"],
  connection: ["SUCCEEDED", "FAILED", "TIMEOUT", "NOT_ATTEMPTED"],
  databaseLookup: ["FOUND", "NOT_FOUND", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  metadata: ["SUFFICIENT", "INSUFFICIENT", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  physical: ["OBSERVED", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  history: ["ABSENT", "PRESENT", "UNREADABLE", "INVALID_STRUCTURE", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  repository: ["ABSENT", "PRESENT_VALID", "INVALID", "AMBIGUOUS", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  schema: ["NOT_EVALUATED", "CONSISTENT", "DRIFT_DETECTED", "INSUFFICIENT_EVIDENCE", "UNKNOWN", "ERROR"],
  registry: ["NOT_EVALUATED", "TARGET_NOT_REGISTERED", "BASELINE_REQUIRED", "CERTIFIED", "INVALID", "CONTRADICTORY", "UNKNOWN", "ERROR"],
  onboarding: ["NOT_REQUIRED", "REQUIRED", "PENDING", "MANAGED", "BLOCKED", "UNKNOWN", "ERROR"]
});

const ROOT_KEYS = ["contractVersion", "declarations", "connection", "databaseLookup", "metadata", "physical", "history", "repository", "schema", "registry", "onboarding"];
const OUTPUT_ROOT_KEYS = ["contractVersion", "rulesVersion", "declarations", "observations", "inferences", "decision", "warnings", "unknownFields"];
const BLOCK_CODES = new Set([
  "BLOCKED_UNSUPPORTED_CONTRACT_VERSION", "BLOCKED_INVALID_LIFECYCLE", "BLOCKED_CHANGE_MODE_REQUIRED",
  "BLOCKED_INVALID_CHANGE_MODE", "BLOCKED_DECLARATION_CONFLICT", "BLOCKED_DATABASE_NOT_FOUND_NO_PROVISIONING",
  "BLOCKED_PHYSICAL_EXISTENCE_UNKNOWN", "BLOCKED_METADATA_VISIBILITY", "BLOCKED_PHYSICAL_STATE_UNKNOWN",
  "BLOCKED_NEW_NOT_EMPTY", "BLOCKED_NEW_TECHNICAL_ONLY_UNDECIDED", "BLOCKED_NEW_WITHOUT_MIGRATIONS",
  "BLOCKED_NEW_UNEXPECTED_HISTORY", "BLOCKED_EF_REPOSITORY_REQUIRED", "BLOCKED_EF_REPOSITORY_INVALID",
  "BLOCKED_AMBIGUOUS_MIGRATION_PROJECT", "BLOCKED_EF_HISTORY_REQUIRED", "BLOCKED_EF_HISTORY_EMPTY",
  "BLOCKED_EF_HISTORY_UNREADABLE", "BLOCKED_EF_HISTORY_INVALID_STRUCTURE", "BLOCKED_HISTORY_WITHOUT_REPOSITORY",
  "BLOCKED_HISTORY_AHEAD", "BLOCKED_HISTORY_REORDERED", "BLOCKED_HISTORY_DIVERGED", "BLOCKED_HISTORY_UNKNOWN_ID",
  "BLOCKED_HISTORY_RELATION_CONFLICT", "BLOCKED_LEGACY_NOT_DECLARED", "BLOCKED_LEGACY_WITH_EF_REPOSITORY",
  "BLOCKED_LEGACY_WITH_EF_HISTORY", "BLOCKED_LEGACY_EMPTY_UNDECIDED", "BLOCKED_REGISTRY_INVALID",
  "BLOCKED_REGISTRY_CONTRADICTION", "BLOCKED_PHYSICAL_DRIFT", "BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT",
  "BLOCKED_BASELINE_REQUIRED", "BLOCKED_BASELINE_APPROVAL_PENDING", "BLOCKED_ONBOARDING_PENDING"
]);

function issue(code, issuePath, message) {
  return { code, path: issuePath, message };
}

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value) && Object.getPrototypeOf(value) === Object.prototype;
}

function checkObject(value, objectPath, requiredKeys, errors, allowedKeys = requiredKeys) {
  if (!isPlainObject(value)) {
    errors.push(issue("OBJECT_REQUIRED", objectPath, "Value must be an object."));
    return false;
  }
  for (const key of requiredKeys) {
    if (!Object.hasOwn(value, key)) {
      errors.push(issue("REQUIRED_PROPERTY_MISSING", `${objectPath}.${key}`, "Required property is missing."));
    }
  }
  if (Object.keys(value).some(key => !allowedKeys.includes(key))) {
    errors.push(issue("UNKNOWN_PROPERTY", `${objectPath}.*`, "Unknown property is not allowed."));
  }
  return true;
}

function checkEnum(value, allowed, valuePath, errors) {
  if (typeof value !== "string" || !allowed.includes(value)) {
    errors.push(issue("ENUM_INVALID", valuePath, "Value is not an allowed enum member."));
    return false;
  }
  return true;
}

function checkCount(value, valuePath, errors) {
  if (!Number.isSafeInteger(value) || value < 0 || value > MAX_COUNT) {
    errors.push(issue("COUNT_INVALID", valuePath, "Count must be a non-negative safe integer."));
    return false;
  }
  return true;
}

function checkIds(value, valuePath, errors, requireRepositoryFormat) {
  if (!Array.isArray(value)) {
    errors.push(issue("ID_LIST_REQUIRED", valuePath, "Migration IDs must be an array."));
    return false;
  }
  let valid = true;
  value.forEach((id, index) => {
    const idPath = `${valuePath}[${index}]`;
    if (typeof id !== "string" || id.length === 0 || id.trim() !== id || /[\u0000-\u001f\u007f]/u.test(id)) {
      errors.push(issue("MIGRATION_ID_INVALID", idPath, "Migration ID must be a non-empty trimmed string without control characters."));
      valid = false;
    } else if (requireRepositoryFormat && !MIGRATION_ID_PATTERN.test(id)) {
      errors.push(issue("REPOSITORY_MIGRATION_ID_INVALID", idPath, "Repository migration ID has an invalid format."));
      valid = false;
    }
  });
  return valid;
}

export function validateContract(raw) {
  const errors = [];
  if (!isPlainObject(raw)) {
    return [issue("ROOT_OBJECT_REQUIRED", "$", "Input root must be an object.")];
  }
  checkObject(raw, "$", ROOT_KEYS, errors);
  if (Object.hasOwn(raw, "contractVersion") && raw.contractVersion !== 2) {
    errors.push(issue("CONTRACT_VERSION_INVALID", "contractVersion", "Contract version must be integer 2."));
  }

  if (checkObject(raw.declarations, "declarations", ["databaseLifecycle", "changeManagementMode"], errors)) {
    checkEnum(raw.declarations.databaseLifecycle, ENUMS.databaseLifecycle, "declarations.databaseLifecycle", errors);
    checkEnum(raw.declarations.changeManagementMode, ENUMS.changeManagementMode, "declarations.changeManagementMode", errors);
  }
  for (const key of ["connection", "databaseLookup", "metadata", "schema", "registry", "onboarding"]) {
    if (checkObject(raw[key], key, ["status"], errors)) {
      checkEnum(raw[key].status, ENUMS[key], `${key}.status`, errors);
    }
  }

  if (checkObject(raw.physical, "physical", ["status"], errors, ["status", "businessObjectCount", "technicalObjectCount"])) {
    checkEnum(raw.physical.status, ENUMS.physical, "physical.status", errors);
    if (raw.physical.status === "OBSERVED") {
      for (const key of ["businessObjectCount"]) {
        if (!Object.hasOwn(raw.physical, key)) errors.push(issue("REQUIRED_PROPERTY_MISSING", `physical.${key}`, "Required property is missing."));
      }
    }
    for (const key of ["businessObjectCount", "technicalObjectCount"]) {
      if (Object.hasOwn(raw.physical, key)) checkCount(raw.physical[key], `physical.${key}`, errors);
    }
  }

  if (checkObject(raw.history, "history", ["status"], errors, ["status", "migrationCount", "migrationIds"])) {
    checkEnum(raw.history.status, ENUMS.history, "history.status", errors);
    if (raw.history.status === "PRESENT") {
      for (const key of ["migrationCount", "migrationIds"]) {
        if (!Object.hasOwn(raw.history, key)) errors.push(issue("REQUIRED_PROPERTY_MISSING", `history.${key}`, "Required property is missing."));
      }
    }
    if (Object.hasOwn(raw.history, "migrationCount")) checkCount(raw.history.migrationCount, "history.migrationCount", errors);
    if (Object.hasOwn(raw.history, "migrationIds")) checkIds(raw.history.migrationIds, "history.migrationIds", errors, false);
  }

  if (checkObject(raw.repository, "repository", ["status"], errors, ["status", "migrations"])) {
    checkEnum(raw.repository.status, ENUMS.repository, "repository.status", errors);
    if (raw.repository.status === "PRESENT_VALID" && !Object.hasOwn(raw.repository, "migrations")) {
      errors.push(issue("REQUIRED_PROPERTY_MISSING", "repository.migrations", "Required property is missing."));
    }
    if (Object.hasOwn(raw.repository, "migrations") && checkObject(raw.repository.migrations, "repository.migrations", ["count", "ids"], errors)) {
      if (Object.hasOwn(raw.repository.migrations, "count")) checkCount(raw.repository.migrations.count, "repository.migrations.count", errors);
      if (Object.hasOwn(raw.repository.migrations, "ids")) checkIds(raw.repository.migrations.ids, "repository.migrations.ids", errors, true);
    }
  }
  return errors;
}

function duplicateIssue(ids, idsPath) {
  if (!Array.isArray(ids)) return null;
  return new Set(ids).size === ids.length ? null : issue("DUPLICATE_MIGRATION_ID", idsPath, "Migration ID list must not contain duplicates.");
}

export function findContradictions(raw) {
  const errors = [];
  const has = (object, key) => Object.hasOwn(object, key);

  if (raw.connection.status !== "SUCCEEDED" && ["FOUND", "NOT_FOUND"].includes(raw.databaseLookup.status)) {
    errors.push(issue("LOOKUP_WITHOUT_CONNECTION", "databaseLookup.status", "A conclusive lookup requires a successful connection."));
  }
  if (raw.databaseLookup.status === "NOT_FOUND") {
    if (!["UNKNOWN", "NOT_ATTEMPTED"].includes(raw.metadata.status) ||
        !["UNKNOWN", "NOT_ATTEMPTED"].includes(raw.physical.status) ||
        !["UNKNOWN", "NOT_ATTEMPTED"].includes(raw.history.status) ||
        !["NOT_EVALUATED", "UNKNOWN"].includes(raw.schema.status)) {
      errors.push(issue("NOT_FOUND_WITH_DATABASE_EVIDENCE", "databaseLookup.status", "A missing database cannot include internal database evidence."));
    }
  }
  const hasConclusiveInternalEvidence =
    ["SUFFICIENT", "INSUFFICIENT"].includes(raw.metadata.status) ||
    raw.physical.status === "OBSERVED" ||
    ["ABSENT", "PRESENT", "UNREADABLE", "INVALID_STRUCTURE"].includes(raw.history.status) ||
    ["CONSISTENT", "DRIFT_DETECTED", "INSUFFICIENT_EVIDENCE"].includes(raw.schema.status);
  if (hasConclusiveInternalEvidence && (raw.connection.status !== "SUCCEEDED" || raw.databaseLookup.status !== "FOUND")) {
    errors.push(issue("INTERNAL_EVIDENCE_WITHOUT_FOUND_DATABASE", "$", "Conclusive internal evidence requires a successful connection and a found database."));
  }

  if (raw.physical.status !== "OBSERVED" && (has(raw.physical, "businessObjectCount") || has(raw.physical, "technicalObjectCount"))) {
    errors.push(issue("COUNTS_WITHOUT_OBSERVATION", "physical", "Counts are not allowed when physical state was not observed."));
  }

  if (raw.history.status === "PRESENT") {
    if (raw.history.migrationCount !== raw.history.migrationIds.length) {
      errors.push(issue("HISTORY_COUNT_MISMATCH", "history.migrationCount", "History count must equal the number of IDs."));
    }
  } else if (has(raw.history, "migrationCount") || has(raw.history, "migrationIds")) {
    errors.push(issue("HISTORY_DETAILS_NOT_ALLOWED", "history", "History details are allowed only when history is present."));
  }

  if (raw.repository.status === "PRESENT_VALID") {
    if (raw.repository.migrations.count !== raw.repository.migrations.ids.length) {
      errors.push(issue("REPOSITORY_COUNT_MISMATCH", "repository.migrations.count", "Repository count must equal the number of IDs."));
    }
    if (raw.repository.migrations.count === 0) {
      errors.push(issue("PRESENT_VALID_REPOSITORY_EMPTY", "repository.migrations", "A valid migrations repository cannot be empty."));
    }
  } else if (has(raw.repository, "migrations")) {
    errors.push(issue("REPOSITORY_MIGRATIONS_NOT_ALLOWED", "repository.migrations", "Migrations are allowed only for a valid repository."));
  }

  const repoDuplicate = duplicateIssue(raw.repository.migrations?.ids, "repository.migrations.ids");
  const historyDuplicate = duplicateIssue(raw.history.migrationIds, "history.migrationIds");
  if (repoDuplicate) errors.push(repoDuplicate);
  if (historyDuplicate) errors.push(historyDuplicate);
  return errors;
}

export function findTechnicalErrors(raw) {
  const errors = [];
  if (raw.connection.status === "FAILED") errors.push(issue("CONNECTION_FAILED", "connection.status", "Database connection failed."));
  if (raw.connection.status === "TIMEOUT") errors.push(issue("CONNECTION_TIMEOUT", "connection.status", "Database connection timed out."));
  for (const [key, code, message] of [
    ["databaseLookup", "DATABASE_LOOKUP_ERROR", "Database lookup failed."],
    ["metadata", "METADATA_ERROR", "Metadata observation failed."],
    ["physical", "PHYSICAL_OBSERVATION_ERROR", "Physical observation failed."],
    ["history", "HISTORY_ERROR", "Migration history observation failed."],
    ["repository", "REPOSITORY_ERROR", "Repository observation failed."],
    ["schema", "SCHEMA_ERROR", "Schema observation failed."],
    ["registry", "REGISTRY_ERROR", "Registry observation failed."],
    ["onboarding", "ONBOARDING_ERROR", "Onboarding observation failed."]
  ]) {
    if (raw[key].status === "ERROR") errors.push(issue(code, `${key}.status`, message));
  }
  return errors;
}

export function relationFor(raw) {
  if (["ABSENT"].includes(raw.history.status) || (raw.history.status === "PRESENT" && raw.history.migrationCount === 0)) return "NOT_APPLICABLE";
  if (raw.repository.status === "ABSENT" && raw.history.status === "PRESENT") return "UNKNOWN";
  if (raw.repository.status !== "PRESENT_VALID" || raw.history.status !== "PRESENT") return "UNKNOWN";
  const repository = raw.repository.migrations.ids;
  const history = raw.history.migrationIds;
  const prefix = (left, right) => left.every((id, index) => id === right[index]);
  if (repository.length === history.length && prefix(repository, history)) return "EXACT_MATCH";
  if (history.length < repository.length && prefix(history, repository)) return "VALID_PREFIX";
  if (repository.length < history.length && prefix(repository, history)) return "HISTORY_AHEAD";
  if (repository.length === history.length && repository.every(id => history.includes(id))) return "REORDERED";
  if (history.some(id => !repository.includes(id))) return "UNKNOWN_ID";
  return "DIVERGED";
}

export function normalize(raw) {
  const physicalState = raw.physical.status !== "OBSERVED" ? "UNKNOWN"
    : raw.physical.businessObjectCount > 0 ? "POPULATED"
      : !Object.hasOwn(raw.physical, "technicalObjectCount") ? "UNKNOWN"
        : raw.physical.technicalObjectCount > 0 ? "TECHNICAL_ONLY" : "EMPTY";
  const historyState = raw.history.status === "PRESENT"
    ? (raw.history.migrationCount === 0 ? "EMPTY" : "PRESENT")
    : raw.history.status === "NOT_ATTEMPTED" ? "UNKNOWN" : raw.history.status;
  return {
    contractVersion: 2,
    databaseLifecycle: raw.declarations.databaseLifecycle,
    changeManagementMode: raw.declarations.changeManagementMode,
    physicalExistence: raw.databaseLookup.status === "FOUND" ? "EXISTS" : raw.databaseLookup.status === "NOT_FOUND" ? "NOT_FOUND" : "UNKNOWN",
    metadataVisibility: raw.metadata.status === "NOT_ATTEMPTED" ? "UNKNOWN" : raw.metadata.status,
    physicalState,
    repositoryState: raw.repository.status === "NOT_ATTEMPTED" ? "UNKNOWN" : raw.repository.status,
    historyState,
    repoHistoryRelation: relationFor(raw),
    schemaRelation: raw.schema.status,
    registryState: raw.registry.status,
    onboardingState: raw.onboarding.status
  };
}

function combined(adapterStatus, classificationInvoked, normalizedFacts, errors, warnings, classificationResult) {
  const result = { adapterVersion: ADAPTER_VERSION, adapterStatus, classificationInvoked, normalizedFacts, errors, warnings };
  if (classificationInvoked) result.classificationResult = classificationResult;
  return result;
}

export function classifierArguments(facts) {
  return [
    "--contract-version", String(facts.contractVersion),
    "--database-lifecycle", facts.databaseLifecycle,
    "--change-management-mode", facts.changeManagementMode,
    "--physical-existence", facts.physicalExistence,
    "--metadata-visibility", facts.metadataVisibility,
    "--physical-state", facts.physicalState,
    "--repository-state", facts.repositoryState,
    "--history-state", facts.historyState,
    "--repo-history-relation", facts.repoHistoryRelation,
    "--schema-relation", facts.schemaRelation,
    "--registry-state", facts.registryState,
    "--onboarding-state", facts.onboardingState
  ];
}

function invokeRealClassifier(facts) {
  const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
  const classifierPath = path.join(scriptDirectory, "classify-scenario-v2.sh");
  const child = spawnSync("bash", [classifierPath, ...classifierArguments(facts)], { encoding: "utf8", windowsHide: true });
  if (child.error || child.status !== 0) throw new Error("CLASSIFIER_PROCESS_FAILED");
  return JSON.parse(child.stdout);
}

function hasExactKeys(value, keys) {
  return isPlainObject(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
}

function equalArray(left, right) {
  return Array.isArray(left) && Array.isArray(right) && left.length === right.length && left.every((value, index) => value === right[index]);
}

function safeCodeArray(value, allowed) {
  return Array.isArray(value) && new Set(value).size === value.length && value.every(item => typeof item === "string" && allowed(item));
}

export function validateClassifierOutput(output, facts) {
  if (!hasExactKeys(output, OUTPUT_ROOT_KEYS)) return false;
  if (output.contractVersion !== 2 || output.rulesVersion !== "1") return false;
  if (!hasExactKeys(output.declarations, ["databaseLifecycle", "changeManagementMode"]) ||
      output.declarations.databaseLifecycle !== facts.databaseLifecycle ||
      output.declarations.changeManagementMode !== facts.changeManagementMode) return false;
  const observationKeys = ["physicalExistence", "metadataVisibility", "physicalState", "repositoryState", "historyState", "repoHistoryRelation", "schemaRelation", "registryState", "onboardingState"];
  if (!hasExactKeys(output.observations, observationKeys) || observationKeys.some(key => output.observations[key] !== facts[key])) return false;
  if (!hasExactKeys(output.inferences, ["scenario", "hasPendingMigrations"]) ||
      !["NEW_EF", "EXISTING_EF", "EXISTING_LEGACY", "UNCLASSIFIED"].includes(output.inferences.scenario) ||
      typeof output.inferences.hasPendingMigrations !== "boolean") return false;
  if (!hasExactKeys(output.decision, ["status", "primaryBlock", "blocks"]) ||
      !["ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE", "BLOCKED"].includes(output.decision.status) ||
      !safeCodeArray(output.decision.blocks, item => BLOCK_CODES.has(item))) return false;
  if (output.decision.status === "ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE") {
    if (output.decision.primaryBlock !== null || output.decision.blocks.length !== 0) return false;
  } else if (typeof output.decision.primaryBlock !== "string" || output.decision.blocks.length === 0 || output.decision.primaryBlock !== output.decision.blocks[0]) {
    return false;
  }
  if (!safeCodeArray(output.warnings, item => /^[A-Z0-9_]+$/.test(item))) return false;
  const unknownOrder = ["physicalExistence", "metadataVisibility", "physicalState", "repositoryState", "historyState", "repoHistoryRelation", "schemaRelation", "registryState"];
  const expectedUnknown = unknownOrder.filter(key => facts[key] === "UNKNOWN");
  if (!equalArray(output.unknownFields, expectedUnknown)) return false;
  return true;
}

export function adaptEvidence(raw, invokeClassifier = invokeRealClassifier) {
  const validationErrors = validateContract(raw);
  if (validationErrors.length) return { exitCode: 65, result: combined("INVALID_EVIDENCE", false, null, validationErrors, []) };
  const contradictions = findContradictions(raw);
  if (contradictions.length) return { exitCode: 66, result: combined("CONTRADICTORY_EVIDENCE", false, null, contradictions, []) };
  const technicalErrors = findTechnicalErrors(raw);
  if (technicalErrors.length) return { exitCode: 75, result: combined("TECHNICAL_ERROR", false, null, technicalErrors, []) };
  if (raw.onboarding.status === "UNKNOWN") {
    return { exitCode: 67, result: combined("INSUFFICIENT_EVIDENCE", false, null, [issue("ONBOARDING_STATE_UNREPRESENTABLE", "onboarding.status", "Onboarding state cannot be represented by Classification V2.")], []) };
  }
  const facts = normalize(raw);
  try {
    const classificationResult = invokeClassifier(facts);
    if (!validateClassifierOutput(classificationResult, facts)) throw new Error("CLASSIFIER_OUTPUT_INVALID");
    return { exitCode: 0, result: combined("CLASSIFIED", true, facts, [], [], classificationResult) };
  } catch {
    return { exitCode: 70, result: combined("CLASSIFIER_ERROR", false, null, [issue("CLASSIFIER_EXECUTION_FAILED", "classificationResult", "Classification V2 could not produce a valid result.")], []) };
  }
}

export function parseInput(text) {
  if (text.length === 0) return { exitCode: 64, result: combined("INVALID_USAGE", false, null, [issue("STDIN_REQUIRED", "$", "A JSON document is required on stdin.")], []) };
  let raw;
  try {
    raw = JSON.parse(text);
  } catch {
    return { exitCode: 65, result: combined("INVALID_EVIDENCE", false, null, [issue("JSON_INVALID", "$", "Input must be valid JSON.")], []) };
  }
  return adaptEvidence(raw);
}

function internalFailure() {
  return { exitCode: 70, result: combined("CLASSIFIER_ERROR", false, null, [issue("ADAPTER_INTERNAL_ERROR", "$", "Adapter could not produce a valid result.")], []) };
}

export function runCli({ argv = process.argv, readInput = () => fs.readFileSync(0, "utf8"), writeStdout = value => fs.writeSync(1, value), writeStderr = value => fs.writeSync(2, value) } = {}) {
  let outcome;
  try {
    if (argv.length !== 2) {
      outcome = { exitCode: 64, result: combined("INVALID_USAGE", false, null, [issue("ARGUMENTS_NOT_ALLOWED", "$", "Adapter accepts JSON only through stdin.")], []) };
    } else {
      outcome = parseInput(readInput());
    }
  } catch {
    outcome = internalFailure();
  }
  try {
    writeStdout(`${JSON.stringify(outcome.result)}\n`);
  } catch {
    outcome = internalFailure();
    try { writeStdout(`${JSON.stringify(outcome.result)}\n`); } catch { /* Output is no longer writable. */ }
  }
  if (outcome.exitCode !== 0) {
    try { writeStderr(`classification-adapter-v2: ${outcome.result.errors[0].code}\n`); } catch { /* Diagnostic channel is no longer writable. */ }
  }
  return outcome.exitCode;
}

function main() {
  try {
    process.exitCode = runCli();
  } catch {
    const outcome = internalFailure();
    try { fs.writeSync(1, `${JSON.stringify(outcome.result)}\n`); } catch { /* Output is no longer writable. */ }
    try { fs.writeSync(2, "classification-adapter-v2: ADAPTER_INTERNAL_ERROR\n"); } catch { /* Diagnostic channel is no longer writable. */ }
    process.exitCode = 70;
  }
}

/* istanbul ignore next -- direct CLI boundary */
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
