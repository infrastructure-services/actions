#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { composeEnvelope } from "./compose-classification-evidence-v2.mjs";
import { adaptEvidence } from "./adapt-classification-evidence-v2.mjs";

const EVIDENCE_KINDS = ["REGISTRY", "SQL_DISCOVERY", "SCHEMA_CAPTURE", "REPOSITORY", "ONBOARDING"];
const OBSERVATION_KEYS = ["physicalState", "repositoryState", "historyState", "registryState", "schemaRelation", "onboardingState"];
const ENUMS = Object.freeze({
  environment: ["TEST", "QA", "PROD"],
  databaseLifecycle: ["NEW", "EXISTING"],
  changeManagementMode: ["EF_MIGRATIONS", "LEGACY_UNMANAGED"],
  serverMatchPolicy: ["ALLOW_LIST", "MANAGED_ENDPOINT"],
  physicalState: ["EMPTY", "TECHNICAL_ONLY", "POPULATED", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  repositoryState: ["PRESENT_VALID", "ABSENT", "INVALID", "AMBIGUOUS", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  historyState: ["ABSENT", "EMPTY", "PRESENT", "UNREADABLE", "INVALID_STRUCTURE", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  registryState: ["TARGET_REGISTERED", "TARGET_NOT_REGISTERED", "INVALID", "CONTRADICTORY", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  schemaRelation: ["NOT_EVALUATED", "CONSISTENT", "DRIFT_DETECTED", "INSUFFICIENT_EVIDENCE", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"],
  onboardingState: ["NOT_REQUIRED", "REQUIRED", "PENDING", "MANAGED", "BLOCKED", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"]
});
const SHA = /^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$/u;
const SHA256 = /^[0-9a-fA-F]{64}$/u;
const REPOSITORY = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u;
const REPOSITORY_PATH = /^[A-Za-z0-9_.-]+(?:\/[A-Za-z0-9_.-]+)*$/u;
const SIMPLE_ID = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/u;
const TARGET_ID = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/u;
const SERVER_INSTANCE = /^[A-Za-z0-9][A-Za-z0-9._\\-]{0,254}$/u;

function plain(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value) && Object.getPrototypeOf(value) === Object.prototype;
}

function exact(value, required, allowed = required) {
  return plain(value) && required.every(key => Object.hasOwn(value, key)) && Object.keys(value).every(key => allowed.includes(key));
}

function normalized(value) { return value.trim().toUpperCase(); }
function enumValue(value, name) { return typeof value === "string" && ENUMS[name].includes(value); }
function validPath(value) { return typeof value === "string" && REPOSITORY_PATH.test(value) && !value.split("/").some(part => part === "." || part === ".."); }
function validDatabase(value) { return typeof value === "string" && value.length >= 1 && value.length <= 128 && !/[\u0000-\u001f\u007f]/u.test(value); }
function outcome(exitCode, status, reason, extra = {}) { return { exitCode, result: { compositionContractVersion: 1, status, reason, classificationInvoked: false, ...extra } }; }

function validProvenance(value) {
  return exact(value, ["sourceRepository", "sourcePath", "sourceRevision", "sourceSha256"]) &&
    REPOSITORY.test(value.sourceRepository) && validPath(value.sourcePath) && SHA.test(value.sourceRevision) && SHA256.test(value.sourceSha256);
}

function validGovernance(value) {
  if (!exact(value, ["applicationId", "environment", "binding", "databaseLifecycle", "changeManagementMode", "authorityReference"])) return false;
  if (!SIMPLE_ID.test(value.applicationId) || !enumValue(value.environment, "environment") ||
      !enumValue(value.databaseLifecycle, "databaseLifecycle") || !enumValue(value.changeManagementMode, "changeManagementMode")) return false;
  const binding = value.binding;
  if (!exact(binding, ["endpointReference", "databaseName", "serverMatchPolicy", "allowedServerInstances"]) ||
      !SIMPLE_ID.test(binding.endpointReference) || !validDatabase(binding.databaseName) ||
      !enumValue(binding.serverMatchPolicy, "serverMatchPolicy") || !Array.isArray(binding.allowedServerInstances) ||
      binding.allowedServerInstances.some(item => typeof item !== "string" || !SERVER_INSTANCE.test(item))) return false;
  const servers = binding.allowedServerInstances.map(normalized);
  if (new Set(servers).size !== servers.length || (binding.serverMatchPolicy === "ALLOW_LIST" ? servers.length === 0 : servers.length !== 0)) return false;
  return exact(value.authorityReference, ["kind", "policyPath"]) && value.authorityReference.kind === "REGISTRY_GIT_PR" && validPath(value.authorityReference.policyPath);
}

function validateEvidenceItem(item) {
  const base = ["evidenceKind", "targetId", "sourceProvenance"];
  if (!plain(item) || !base.every(key => Object.hasOwn(item, key)) || !EVIDENCE_KINDS.includes(item.evidenceKind) ||
      !TARGET_ID.test(item.targetId) || !validProvenance(item.sourceProvenance)) return false;
  const observesSql = item.evidenceKind === "SQL_DISCOVERY" || item.evidenceKind === "SCHEMA_CAPTURE";
  const allowed = observesSql ? [...base, "bindingContext", "observedDatabaseIdentity"] : base;
  if (Object.keys(item).some(key => !allowed.includes(key))) return false;
  if (!observesSql) return true;
  return exact(item.bindingContext, ["endpointReference"]) && SIMPLE_ID.test(item.bindingContext.endpointReference) &&
    exact(item.observedDatabaseIdentity, ["serverInstance", "databaseName"]) &&
    SERVER_INSTANCE.test(item.observedDatabaseIdentity.serverInstance) && validDatabase(item.observedDatabaseIdentity.databaseName);
}

function validateAuthority(envelope) {
  const root = ["contractVersion", "targetId", "governance", "sourceProvenance", "observations", "evidence"];
  if (!exact(envelope, root) || envelope.contractVersion !== 1 || !TARGET_ID.test(envelope.targetId) ||
      !validGovernance(envelope.governance) || !validProvenance(envelope.sourceProvenance)) return outcome(65, "INVALID_EVIDENCE", "GOVERNANCE_CONTRACT_INVALID");
  if (!exact(envelope.observations, OBSERVATION_KEYS) || OBSERVATION_KEYS.some(key => !enumValue(envelope.observations[key], key))) {
    return outcome(65, "INVALID_EVIDENCE", "OBSERVATION_CONTRACT_INVALID");
  }
  if (!Array.isArray(envelope.evidence) || envelope.evidence.length === 0 || envelope.evidence.some(item => !validateEvidenceItem(item))) {
    return outcome(65, "INVALID_EVIDENCE", "EVIDENCE_CONTRACT_INVALID");
  }
  const kinds = new Set();
  for (const item of envelope.evidence) {
    if (kinds.has(item.evidenceKind)) return outcome(65, "INVALID_EVIDENCE", "DUPLICATE_EVIDENCE_KIND");
    kinds.add(item.evidenceKind);
    if (normalized(item.targetId) !== normalized(envelope.targetId)) return outcome(66, "BLOCKED", `BLOCKED_${item.evidenceKind}_TARGET_ID_MISMATCH`);
  }
  for (const kind of EVIDENCE_KINDS) if (!kinds.has(kind)) return outcome(65, "INVALID_EVIDENCE", `${kind}_EVIDENCE_REQUIRED`);
  const { binding } = envelope.governance;
  for (const item of envelope.evidence.filter(value => value.evidenceKind === "SQL_DISCOVERY" || value.evidenceKind === "SCHEMA_CAPTURE")) {
    if (normalized(item.bindingContext.endpointReference) !== normalized(binding.endpointReference)) return outcome(66, "BLOCKED", `BLOCKED_${item.evidenceKind}_ENDPOINT_REFERENCE_MISMATCH`);
    if (normalized(item.observedDatabaseIdentity.databaseName) !== normalized(binding.databaseName)) return outcome(66, "BLOCKED", `BLOCKED_${item.evidenceKind}_DATABASE_MISMATCH`);
    if (binding.serverMatchPolicy === "ALLOW_LIST" && !binding.allowedServerInstances.map(normalized).includes(normalized(item.observedDatabaseIdentity.serverInstance))) {
      return outcome(66, "BLOCKED", `BLOCKED_${item.evidenceKind}_SERVER_BINDING_MISMATCH`);
    }
  }
  return null;
}

function derivedPhysical(source) {
  if (source.status !== "OBSERVED") return source.status;
  if (source.businessObjectCount > 0) return "POPULATED";
  if (!Object.hasOwn(source, "technicalObjectCount")) return "UNKNOWN";
  return source.technicalObjectCount > 0 ? "TECHNICAL_ONLY" : "EMPTY";
}

function derivedHistory(source) {
  if (source.status !== "PRESENT") return source.status;
  return source.migrationCount === 0 ? "EMPTY" : "PRESENT";
}

function sourcesCoherent(authority, sources) {
  if (!plain(sources) || sources.declarationsSource?.databaseLifecycle !== authority.governance.databaseLifecycle ||
      sources.declarationsSource?.changeManagementMode !== authority.governance.changeManagementMode) return false;
  const observed = authority.observations;
  const registryCompatible = observed.registryState === "TARGET_REGISTERED"
    ? ["BASELINE_REQUIRED", "CERTIFIED"].includes(sources.registrySource?.status)
    : observed.registryState === sources.registrySource?.status;
  return observed.physicalState === derivedPhysical(sources.physicalSource ?? {}) &&
    observed.repositoryState === sources.repositorySource?.status &&
    observed.historyState === derivedHistory(sources.historySource ?? {}) &&
    registryCompatible && observed.schemaRelation === sources.schemaSource?.status && observed.onboardingState === sources.onboardingSource?.status;
}

export function composeGovernedClassification(input) {
  if (!exact(input, ["compositionContractVersion", "governanceEnvelope", "classificationSources"]) || input.compositionContractVersion !== 1) {
    return outcome(65, "INVALID_EVIDENCE", "COMPOSITION_CONTRACT_INVALID");
  }
  const invalidAuthority = validateAuthority(input.governanceEnvelope);
  if (invalidAuthority) return invalidAuthority;
  if (!sourcesCoherent(input.governanceEnvelope, input.classificationSources)) return outcome(66, "BLOCKED", "BLOCKED_CROSS_SOURCE_CONTRADICTION");

  const observations = input.governanceEnvelope.observations;
  const { databaseLifecycle, changeManagementMode } = input.governanceEnvelope.governance;
  if (databaseLifecycle === "NEW" && changeManagementMode === "LEGACY_UNMANAGED") return outcome(66, "BLOCKED", "BLOCKED_NEW_LEGACY_CONFLICT");
  if (changeManagementMode === "LEGACY_UNMANAGED" && observations.repositoryState === "PRESENT_VALID") return outcome(66, "BLOCKED", "BLOCKED_LEGACY_WITH_EF_REPOSITORY");
  if (changeManagementMode === "LEGACY_UNMANAGED" && ["EMPTY", "PRESENT"].includes(observations.historyState)) return outcome(66, "BLOCKED", "BLOCKED_LEGACY_WITH_EF_HISTORY");
  if (databaseLifecycle === "NEW" && ["TECHNICAL_ONLY", "POPULATED"].includes(observations.physicalState)) return outcome(66, "BLOCKED", "BLOCKED_NEW_WITH_PREEXISTING_STATE");
  if (["INVALID", "CONTRADICTORY", "TARGET_NOT_REGISTERED"].includes(observations.registryState)) return outcome(66, "BLOCKED", "BLOCKED_REGISTRY_CONTRADICTION");
  if (observations.schemaRelation === "DRIFT_DETECTED") return outcome(66, "BLOCKED", "BLOCKED_SCHEMA_DRIFT");
  if (observations.onboardingState === "BLOCKED") return outcome(66, "BLOCKED", "BLOCKED_ONBOARDING");
  if (Object.values(observations).includes("ERROR")) return outcome(75, "TECHNICAL_ERROR", "SOURCE_TECHNICAL_ERROR");
  if (Object.values(observations).includes("NOT_ATTEMPTED") || observations.schemaRelation === "NOT_EVALUATED") {
    return outcome(68, "NOT_EVALUATED", "SOURCE_STAGE_NOT_ATTEMPTED");
  }
  if (Object.values(observations).includes("UNKNOWN") || observations.schemaRelation === "INSUFFICIENT_EVIDENCE") {
    return outcome(67, "UNCLASSIFIED", "INSUFFICIENT_EVIDENCE");
  }

  const produced = composeEnvelope(input.classificationSources);
  if (produced.exitCode !== 0) return outcome(produced.exitCode, produced.exitCode === 66 ? "BLOCKED" : "INVALID_EVIDENCE", produced.code);
  const adapted = adaptEvidence(produced.raw);
  const provenance = Object.fromEntries(input.governanceEnvelope.evidence.map(item => [item.evidenceKind, structuredClone(item.sourceProvenance)]));
  return {
    exitCode: adapted.exitCode,
    result: {
      compositionContractVersion: 1,
      status: adapted.result.adapterStatus,
      reason: adapted.result.adapterStatus,
      classificationInvoked: adapted.result.classificationInvoked,
      targetId: input.governanceEnvelope.targetId,
      governance: structuredClone(input.governanceEnvelope.governance),
      sourceProvenance: structuredClone(input.governanceEnvelope.sourceProvenance),
      evidenceProvenance: provenance,
      classification: adapted.result
    }
  };
}

export function parseInput(text) {
  if (text.length === 0) return outcome(64, "INVALID_EVIDENCE", "STDIN_REQUIRED");
  try { return composeGovernedClassification(JSON.parse(text)); }
  catch { return outcome(65, "INVALID_EVIDENCE", "JSON_INVALID"); }
}

export function runCli({ argv = process.argv, readInput = () => fs.readFileSync(0, "utf8"), writeStdout = value => fs.writeSync(1, value), writeStderr = value => fs.writeSync(2, value) } = {}) {
  let composed;
  try { composed = argv.length === 2 ? parseInput(readInput()) : outcome(64, "INVALID_EVIDENCE", "ARGUMENTS_NOT_ALLOWED"); }
  catch { composed = outcome(70, "TECHNICAL_ERROR", "COMPOSITION_INTERNAL_ERROR"); }
  try { writeStdout(`${JSON.stringify(composed.result)}\n`); }
  catch { composed = outcome(70, "TECHNICAL_ERROR", "COMPOSITION_INTERNAL_ERROR"); }
  if (composed.exitCode !== 0) {
    try { writeStderr(`governed-classification-v2: ${composed.result.reason}\n`); } catch { /* Diagnostic output unavailable. */ }
  }
  return composed.exitCode;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = runCli();
