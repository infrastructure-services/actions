#!/usr/bin/env node

import fs from "node:fs";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const STATUSES = new Set(["PRESENT_VALID", "ABSENT", "INVALID", "AMBIGUOUS", "UNKNOWN", "ERROR", "NOT_ATTEMPTED"]);

export function validateEnvironment(value) {
  if (value !== "TEST") throw new Error("ENVIRONMENT_NOT_ALLOWED");
}

function within(parent, candidate) {
  const relative = path.relative(parent, candidate);
  return relative === "" || (!relative.startsWith(`..${path.sep}`) && relative !== ".." && !path.isAbsolute(relative));
}

export function buildRequest(env, io = fs) {
  validateEnvironment(env.ENVIRONMENT_NAME);
  if (!env.INSPECTION_STATUS) throw new Error("INSPECTION_STATUS_REQUIRED");
  if (!new Set(["READY", "UNKNOWN", "NOT_ATTEMPTED"]).has(env.INSPECTION_STATUS)) throw new Error("INSPECTION_STATUS_INVALID");
  if (env.INSPECTION_STATUS !== "READY") return { repositoryDiscoveryContractVersion: 1, inspectionStatus: env.INSPECTION_STATUS };
  if (!env.WORKSPACE || !env.GITHUB_WORKSPACE) throw new Error("WORKSPACE_REQUIRED");
  const governed = io.realpathSync(env.GITHUB_WORKSPACE);
  const requested = io.realpathSync(env.WORKSPACE);
  if (!within(governed, requested) || io.lstatSync(env.WORKSPACE).isSymbolicLink()) throw new Error("WORKSPACE_NOT_ALLOWED");
  return { repositoryDiscoveryContractVersion: 1, inspectionStatus: "READY", workspace: requested };
}

function appendOutput(file, name, value) {
  fs.appendFileSync(file, `${name}<<REPOSITORY_DISCOVERY_V2_EOF\n${value}\nREPOSITORY_DISCOVERY_V2_EOF\n`, { encoding: "utf8" });
}

export function runPublicRepositoryDiscovery(env = process.env, execute = spawnSync, io = fs) {
  if (!env.GITHUB_OUTPUT || !env.GITHUB_ACTION_PATH) throw new Error("INPUT_REQUIRED");
  const request = buildRequest(env, io);
  const implementation = path.resolve(env.GITHUB_ACTION_PATH, "..", "scripts", "discover-repository-v2.mjs");
  const child = execute(process.execPath, [implementation], { input: JSON.stringify(request), encoding: "utf8", timeout: 30_000, maxBuffer: 1024 * 1024 });
  if (child.error || ![0, 75].includes(child.status)) throw new Error("REPOSITORY_DISCOVERY_EXECUTION_FAILED");
  const evidence = JSON.parse(child.stdout);
  if (evidence === null || typeof evidence !== "object" || !STATUSES.has(evidence.status)) throw new Error("EVIDENCE_INVALID");
  if ((child.status === 75) !== (evidence.status === "ERROR")) throw new Error("EVIDENCE_EXIT_MISMATCH");
  appendOutput(env.GITHUB_OUTPUT, "status", evidence.status);
  appendOutput(env.GITHUB_OUTPUT, "evidence-json", JSON.stringify(evidence));
  appendOutput(env.GITHUB_OUTPUT, "migration-ids-json", JSON.stringify(evidence.migrations?.ids ?? []));
  return evidence;
}

function main() {
  try { runPublicRepositoryDiscovery(); }
  catch { console.error("repository-discovery-v2: EXECUTION_FAILED"); process.exitCode = 1; }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
