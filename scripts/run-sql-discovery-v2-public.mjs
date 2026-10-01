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

export function validateTlsFallback(value) {
  if (value !== undefined && value !== "true" && value !== "false") throw new Error("TLS_FALLBACK_INPUT_INVALID");
}

export function validateEvidence(value) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error("EVIDENCE_INVALID");
  if (!CONNECTION_STATUSES.has(value.serverConnectionStatus)) throw new Error("EVIDENCE_INVALID");
  const tls = value.tls;
  if (tls === null || typeof tls !== "object" || Array.isArray(tls)
      || Object.keys(tls).some(key => !["diagnosticFingerprint", "tlsCertificateValidated", "tlsEffectiveMode", "tlsFallbackAllowed", "tlsFallbackAttempted", "tlsInitialMode", "tlsInitialResult", "transportEncrypted"].includes(key))
      || tls.tlsInitialMode !== "STRICT"
      || !["NOT_ATTEMPTED", "SUCCEEDED", "CERTIFICATE_VALIDATION_FAILED", "OTHER_FAILURE"].includes(tls.tlsInitialResult)
      || typeof tls.tlsFallbackAllowed !== "boolean" || typeof tls.tlsFallbackAttempted !== "boolean"
      || !["STRICT", "TEST_UNTRUSTED_CERTIFICATE"].includes(tls.tlsEffectiveMode)
      || typeof tls.tlsCertificateValidated !== "boolean" || tls.transportEncrypted !== true
      || (tls.tlsFallbackAttempted && (!tls.tlsFallbackAllowed || tls.tlsEffectiveMode !== "TEST_UNTRUSTED_CERTIFICATE" || tls.tlsCertificateValidated)))
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
  validateTlsFallback(env.ALLOW_TEST_UNTRUSTED_CERTIFICATE_FALLBACK);
  if (!env.SQL_SERVER_CONNECTION || !env.SQL_DATABASE_NAME || !env.GITHUB_OUTPUT || !env.GITHUB_ACTION_PATH || !env.RUNNER_TEMP) throw new Error("INPUT_REQUIRED");
  const executable = prepareSqlDiscovery(env, execute);
  const child = execute("dotnet", [executable, "--v2"], {
    encoding: "utf8", env, timeout: 180_000, maxBuffer: 1024 * 1024
  });
  if (child.error || child.status !== 0) throw new Error("SQL_DISCOVERY_EXECUTION_FAILED");
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
