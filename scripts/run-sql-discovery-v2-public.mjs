#!/usr/bin/env node

import fs from "node:fs";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const STATUS = {
  connectionSource: new Set(["SUCCEEDED", "FAILED", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]),
  databaseLookupSource: new Set(["FOUND", "NOT_FOUND", "UNKNOWN", "ERROR", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]),
  targetConnectionSource: new Set(["SUCCEEDED", "AUTHENTICATION_FAILED", "TRANSPORT_FAILED", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]),
  metadataSource: new Set(["SUFFICIENT", "INSUFFICIENT", "ERROR", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]),
  physicalSource: new Set(["OBSERVED", "ERROR", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]),
  historySource: new Set(["ABSENT", "PRESENT", "UNREADABLE", "INVALID_STRUCTURE", "ERROR", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"])
};
const CONNECTION_STATUSES = new Set(["SUCCEEDED", "AUTHENTICATION_FAILED", "TRANSPORT_FAILED", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED"]);

export function validateEnvironment(value) {
  if (value !== "TEST") throw new Error("ENVIRONMENT_NOT_ALLOWED");
}

export function validateEvidence(value) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error("EVIDENCE_INVALID");
  if (!CONNECTION_STATUSES.has(value.serverConnectionStatus)) throw new Error("EVIDENCE_INVALID");
  for (const [section, allowed] of Object.entries(STATUS)) {
    const candidate = value[section];
    if (candidate === null || typeof candidate !== "object" || Array.isArray(candidate) || !allowed.has(candidate.status)) {
      throw new Error("EVIDENCE_INVALID");
    }
  }
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
  if (!env.SQL_SERVER_CONNECTION || !env.SQL_DATABASE_NAME || !env.GITHUB_OUTPUT || !env.GITHUB_ACTION_PATH || !env.RUNNER_TEMP) throw new Error("INPUT_REQUIRED");
  const executable = prepareSqlDiscovery(env, execute);
  const child = execute("dotnet", [executable, "--v2"], {
    encoding: "utf8", env, timeout: 180_000, maxBuffer: 1024 * 1024
  });
  if (child.error || child.status !== 0) throw new Error("SQL_DISCOVERY_EXECUTION_FAILED");
  const evidence = validateEvidence(JSON.parse(child.stdout));
  const serialized = JSON.stringify(evidence);
  appendOutput(env.GITHUB_OUTPUT, "evidence-json", serialized);
  appendOutput(env.GITHUB_OUTPUT, "connection-status", evidence.serverConnectionStatus);
  for (const section of Object.keys(STATUS).filter(value => value !== "connectionSource")) appendOutput(env.GITHUB_OUTPUT, section.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`).replace("-source", "-status"), evidence[section].status);
  return evidence;
}

function main() {
  try { runPublicSqlDiscovery(); }
  catch { console.error("sql-discovery-v2: EXECUTION_FAILED"); process.exitCode = 1; }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
