#!/usr/bin/env node

import fs from "node:fs";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { validateTaxonomy } from "./empty-for-new-ef-v1.mjs";

const STATUS = {
  connectionSource: new Set(["SUCCEEDED", "FAILED", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]),
  databaseLookupSource: new Set(["FOUND", "NOT_FOUND", "UNKNOWN", "ERROR", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]),
  targetConnectionSource: new Set(["SUCCEEDED", "AUTHENTICATION_FAILED", "TRANSPORT_FAILED", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]),
  metadataSource: new Set(["SUFFICIENT", "INSUFFICIENT", "ERROR", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]),
  physicalSource: new Set(["OBSERVED", "ERROR", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]),
  historySource: new Set(["ABSENT", "PRESENT", "UNREADABLE", "INVALID_STRUCTURE", "ERROR", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"])
};
const CONNECTION_STATUSES = new Set(["SUCCEEDED", "AUTHENTICATION_FAILED", "TRANSPORT_FAILED", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]);
const SERVER_INSTANCE = /^[A-Za-z0-9][A-Za-z0-9._\\-]{0,254}$/u;
const validDatabaseName = value => typeof value === "string" && value.length >= 1 && value.length <= 128 && !/[\u0000-\u001f\u007f]/u.test(value);

export function validateEnvironment(value) {
  if (value !== "TEST") throw new Error("ENVIRONMENT_NOT_ALLOWED");
}

export function validateTlsMode(value) {
  if (value !== undefined && value !== "STRICT" && value !== "TEST_UNTRUSTED_CERTIFICATE") throw new Error("TLS_MODE_INVALID");
}

export function validateEvidence(value) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error("EVIDENCE_INVALID");
  if (!CONNECTION_STATUSES.has(value.serverConnectionStatus)) throw new Error("EVIDENCE_INVALID");
  const tls = value.tls;
  if (tls === null || typeof tls !== "object" || Array.isArray(tls)
      || Object.keys(tls).some(key => !["diagnosticFingerprint", "tlsCertificateValidated", "tlsEffectiveMode", "tlsFallbackAllowed", "tlsFallbackAttempted", "tlsInitialMode", "tlsInitialResult", "tlsPolicySource", "tlsRequestedMode", "transportEncrypted"].includes(key))
      || !["STRICT", "TEST_UNTRUSTED_CERTIFICATE"].includes(tls.tlsRequestedMode)
      || tls.tlsInitialMode !== tls.tlsRequestedMode
      || !["NOT_ATTEMPTED", "SUCCEEDED", "OTHER_FAILURE"].includes(tls.tlsInitialResult)
      || tls.tlsFallbackAllowed !== false || tls.tlsFallbackAttempted !== false
      || !["STRICT", "TEST_UNTRUSTED_CERTIFICATE"].includes(tls.tlsEffectiveMode)
      || tls.tlsEffectiveMode !== tls.tlsRequestedMode
      || tls.tlsPolicySource !== (tls.tlsRequestedMode === "STRICT" ? "DEFAULT_STRICT" : "EXPLICIT_TEST_CONFIGURATION")
      || typeof tls.tlsCertificateValidated !== "boolean" || tls.transportEncrypted !== true
      || (tls.tlsRequestedMode === "TEST_UNTRUSTED_CERTIFICATE" && tls.tlsCertificateValidated)
      || (tls.tlsCertificateValidated && (tls.tlsRequestedMode !== "STRICT" || tls.tlsInitialResult !== "SUCCEEDED")))
    throw new Error("EVIDENCE_INVALID");
  if (tls.diagnosticFingerprint !== undefined) {
    const fingerprint = tls.diagnosticFingerprint;
    const keys = ["exceptionType", "hResult", "innerExceptionType", "innerHResult", "nativeErrorCode", "sqlErrorClasses", "sqlErrorNumbers", "sqlErrorStates", "sqlExceptionNumber", "tlsFailureCategory"];
    const categories = ["KNOWN_CERTIFICATE_TRUST", "HOSTNAME_OR_IDENTITY_MISMATCH", "AUTHENTICATION", "TIMEOUT", "CANCELLED", "TRANSPORT_OTHER", "UNKNOWN"];
    if (fingerprint === null || typeof fingerprint !== "object" || Array.isArray(fingerprint)
        || Object.keys(fingerprint).sort().join(",") !== keys.sort().join(",")
        || typeof fingerprint.exceptionType !== "string" || !/^([A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*$/u.test(fingerprint.exceptionType)
        || typeof fingerprint.hResult !== "string" || !/^0x[0-9A-F]{8}$/u.test(fingerprint.hResult)
        || !(fingerprint.innerExceptionType === null || typeof fingerprint.innerExceptionType === "string")
        || !(fingerprint.innerHResult === null || (typeof fingerprint.innerHResult === "string" && /^0x[0-9A-F]{8}$/u.test(fingerprint.innerHResult)))
        || !(fingerprint.nativeErrorCode === null || Number.isInteger(fingerprint.nativeErrorCode))
        || !(fingerprint.sqlExceptionNumber === null || Number.isInteger(fingerprint.sqlExceptionNumber))
        || ![fingerprint.sqlErrorNumbers, fingerprint.sqlErrorStates, fingerprint.sqlErrorClasses].every(item => Array.isArray(item) && item.every(Number.isInteger))
        || fingerprint.sqlErrorNumbers.length !== fingerprint.sqlErrorStates.length || fingerprint.sqlErrorNumbers.length !== fingerprint.sqlErrorClasses.length
        || fingerprint.sqlErrorStates.some(item => item < 0 || item > 255) || fingerprint.sqlErrorClasses.some(item => item < 0 || item > 255)
        || !categories.includes(fingerprint.tlsFailureCategory)) throw new Error("EVIDENCE_INVALID");
  }
  if ((tls.tlsInitialResult === "OTHER_FAILURE") !== (tls.diagnosticFingerprint !== undefined)) throw new Error("EVIDENCE_INVALID");
  for (const [section, allowed] of Object.entries(STATUS)) {
    const candidate = value[section];
    if (candidate === null || typeof candidate !== "object" || Array.isArray(candidate) || !allowed.has(candidate.status)) {
      throw new Error("EVIDENCE_INVALID");
    }
  }
  const physical = value.physicalSource;
  if (Object.keys(physical).some(key => !["status", "businessObjectCount", "technicalObjectCount", "taxonomy"].includes(key)) ||
      (physical.status === "OBSERVED" && !Number.isSafeInteger(physical.businessObjectCount)) ||
      ["businessObjectCount", "technicalObjectCount"].some(key => Object.hasOwn(physical, key) &&
        (physical.status !== "OBSERVED" || !Number.isSafeInteger(physical[key]) || physical[key] < 0)) || validateTaxonomy(physical)) throw new Error("EVIDENCE_INVALID");
  const identity = value.observedDatabaseIdentity;
  if (value.targetConnectionSource.status === "SUCCEEDED") {
    if (identity === null || typeof identity !== "object" || Array.isArray(identity)
      || Object.keys(identity).sort().join(",") !== "databaseName,serverInstance"
      || typeof identity.serverInstance !== "string" || !SERVER_INSTANCE.test(identity.serverInstance)
      || !validDatabaseName(identity.databaseName)) throw new Error("EVIDENCE_INVALID");
  } else if (identity !== undefined) throw new Error("EVIDENCE_INVALID");
  return value;
}

const FAILURE_REASONS = new Map([[70, "SQL_DISCOVERY_INTERNAL_ERROR"], [75, "SQL_DISCOVERY_PROJECTION_BLOCKED"]]);
const PROJECTION_GAPS = new Set([
  "OBSERVED_DATABASE_IDENTITY_UNAVAILABLE", "CONNECTION_STATE_UNREPRESENTABLE", "TARGET_CONNECTION_STATE_UNREPRESENTABLE",
  "DATABASE_LOOKUP_STATE_UNREPRESENTABLE", "METADATA_STATE_UNREPRESENTABLE", "PHYSICAL_PARTIAL_UNREPRESENTABLE",
  "PHYSICAL_STATE_UNREPRESENTABLE", "PHYSICAL_COMPLETE_COUNT_REQUIRED", "PHYSICAL_TAXONOMY_INVALID",
  "PHYSICAL_TAXONOMY_REQUIRED", "HISTORY_STATE_UNREPRESENTABLE"
]);
const STAGE_FAILURE_STATUS = {
  serverConnectionStatus: CONNECTION_STATUSES,
  databaseLookupStatus: STATUS.databaseLookupSource,
  targetConnectionStatus: CONNECTION_STATUSES,
  metadataStatus: STATUS.metadataSource,
  physicalStatus: new Set([...STATUS.physicalSource, "PARTIAL"]),
  historyStatus: STATUS.historySource
};

export function validateFailureEnvelope(value, exitCode) {
  if (value === null || typeof value !== "object" || Array.isArray(value)
      || Object.keys(value).some(key => !["failureContractVersion", "executionExitCode", "executionReasonCode", "tls", "projectionRepresentable", "projectionGaps", ...Object.keys(STAGE_FAILURE_STATUS)].includes(key))
      || value.failureContractVersion !== 1 || value.executionExitCode !== exitCode
      || value.executionReasonCode !== FAILURE_REASONS.get(exitCode)
      || value.projectionRepresentable !== (exitCode === 75 ? false : null)
      || !Array.isArray(value.projectionGaps)
      || value.projectionGaps.some(code => !PROJECTION_GAPS.has(code))
      || new Set(value.projectionGaps).size !== value.projectionGaps.length)
    throw new Error("FAILURE_EVIDENCE_INVALID");
  validateEvidence({
    tls: value.tls,
    serverConnectionStatus: "NOT_ATTEMPTED",
    connectionSource: { status: "NOT_ATTEMPTED" }, databaseLookupSource: { status: "NOT_ATTEMPTED" },
    targetConnectionSource: { status: "NOT_ATTEMPTED" }, metadataSource: { status: "NOT_ATTEMPTED" },
    physicalSource: { status: "NOT_ATTEMPTED" }, historySource: { status: "NOT_ATTEMPTED" }
  });
  const present = Object.keys(STAGE_FAILURE_STATUS).filter(key => Object.hasOwn(value, key));
  if (present.length !== 0 && present.length !== Object.keys(STAGE_FAILURE_STATUS).length) throw new Error("FAILURE_EVIDENCE_INVALID");
  for (const key of present) if (!STAGE_FAILURE_STATUS[key].has(value[key])) throw new Error("FAILURE_EVIDENCE_INVALID");
  if (exitCode === 75 && (value.projectionGaps.length === 0 || present.length === 0)) throw new Error("FAILURE_EVIDENCE_INVALID");
  return value;
}

function appendOutput(file, name, value) {
  fs.appendFileSync(file, `${name}<<SQL_DISCOVERY_V2_EOF\n${value}\nSQL_DISCOVERY_V2_EOF\n`, { encoding: "utf8" });
}

function runDotnet(execute, args, options, failureCode) {
  const child = execute("dotnet", args, options);
  if (child.error || child.status !== 0) throw new Error(failureCode);
  return child;
}

export function prepareSqlDiscovery(env = process.env, execute = spawnSync) {
  if (!env.GITHUB_ACTION_PATH || !env.RUNNER_TEMP) throw new Error("INPUT_REQUIRED");
  const project = path.resolve(env.GITHUB_ACTION_PATH, "..", "tools", "SqlDiscovery", "SqlDiscovery.csproj");
  const config = path.resolve(env.GITHUB_ACTION_PATH, "..", "tools", "SqlDiscovery", "NuGet.Config");
  const buildRoot = path.join(env.RUNNER_TEMP, "sql-discovery-v2-build");
  const baseOutput = `${path.join(buildRoot, "bin")}${path.sep}`;
  const baseIntermediate = `${path.join(buildRoot, "obj")}${path.sep}`;
  const buildEnv = { ...env };
  delete buildEnv.SQL_SERVER_CONNECTION;

  const common = { encoding: "utf8", env: buildEnv, timeout: 180_000, maxBuffer: 1024 * 1024 };
  runDotnet(execute, ["restore", project, "--configfile", config, `--property:BaseIntermediateOutputPath=${baseIntermediate}`, "--verbosity", "minimal"], common, "SQL_DISCOVERY_RESTORE_FAILED");
  runDotnet(execute, ["build", project, "--no-restore", "--configuration", "Release", `--property:BaseOutputPath=${baseOutput}`, `--property:BaseIntermediateOutputPath=${baseIntermediate}`, "--verbosity", "minimal"], common, "SQL_DISCOVERY_BUILD_FAILED");
  return path.join(baseOutput, "Release", "net10.0", "SqlDiscovery.dll");
}

export function runPublicSqlDiscovery(env = process.env, execute = spawnSync) {
  validateEnvironment(env.ENVIRONMENT_NAME);
  validateTlsMode(env.SQL_TLS_MODE);
  if (!env.SQL_SERVER_CONNECTION || !env.SQL_DATABASE_NAME || !env.GITHUB_OUTPUT || !env.GITHUB_ACTION_PATH || !env.RUNNER_TEMP) throw new Error("INPUT_REQUIRED");
  const executable = prepareSqlDiscovery(env, execute);
  const child = execute("dotnet", [executable, "--v2"], {
    encoding: "utf8", env, timeout: 180_000, maxBuffer: 1024 * 1024
  });
  if (child.error) throw new Error("SQL_DISCOVERY_EXECUTION_FAILED");
  if (child.status !== 0) {
    if (!FAILURE_REASONS.has(child.status)) throw new Error("SQL_DISCOVERY_EXECUTION_FAILED");
    let failure;
    try { failure = validateFailureEnvelope(JSON.parse(child.stdout), child.status); }
    catch { throw new Error("SQL_DISCOVERY_EXECUTION_FAILED"); }
    appendOutput(env.GITHUB_OUTPUT, "failure-evidence-json", JSON.stringify(failure));
    appendOutput(env.GITHUB_OUTPUT, "execution-exit-code", String(failure.executionExitCode));
    appendOutput(env.GITHUB_OUTPUT, "execution-reason-code", failure.executionReasonCode);
    console.error(`sql-discovery-v2: ${failure.executionReasonCode} (exit ${failure.executionExitCode}); sanitized failure evidence published`);
    throw new Error(failure.executionReasonCode);
  }
  const evidence = validateEvidence(JSON.parse(child.stdout));
  const { observedDatabaseIdentity, ...classificationEvidence } = evidence;
  const serialized = JSON.stringify(classificationEvidence);
  appendOutput(env.GITHUB_OUTPUT, "evidence-json", serialized);
  appendOutput(env.GITHUB_OUTPUT, "connection-status", evidence.serverConnectionStatus);
  appendOutput(env.GITHUB_OUTPUT, "observed-server-instance", observedDatabaseIdentity?.serverInstance ?? "");
  appendOutput(env.GITHUB_OUTPUT, "observed-database-name", observedDatabaseIdentity?.databaseName ?? "");
  appendOutput(env.GITHUB_OUTPUT, "observed-database-identity-json", observedDatabaseIdentity ? JSON.stringify(observedDatabaseIdentity) : "");
  for (const section of Object.keys(STATUS).filter(value => value !== "connectionSource")) appendOutput(env.GITHUB_OUTPUT, section.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`).replace("-source", "-status"), evidence[section].status);
  return evidence;
}

function main() {
  try { runPublicSqlDiscovery(); }
  catch { console.error("sql-discovery-v2: EXECUTION_FAILED"); process.exitCode = 1; }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
