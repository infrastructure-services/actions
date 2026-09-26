#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { composeGovernedClassification } from "./compose-governed-classification-v2.mjs";

const MAX_INPUT_BYTES = 1024 * 1024;
const ARTIFACT_NAME = "classification-v2-test.json";
const SAFE_SOURCE_STATES = new Set([
  "SUCCEEDED", "FAILED", "FOUND", "NOT_FOUND", "SUFFICIENT", "INSUFFICIENT", "OBSERVED",
  "PRESENT", "ABSENT", "PRESENT_VALID", "INVALID", "AMBIGUOUS", "UNREADABLE", "INVALID_STRUCTURE",
  "CONSISTENT", "DRIFT_DETECTED", "BASELINE_REQUIRED", "CERTIFIED", "TARGET_NOT_REGISTERED", "CONTRADICTORY",
  "NOT_REQUIRED", "REQUIRED", "PENDING", "MANAGED", "BLOCKED", "AUTHENTICATION_FAILED", "TRANSPORT_FAILED",
  "TIMEOUT", "CANCELLED", "UNKNOWN", "ERROR", "NOT_ATTEMPTED", "NOT_EVALUATED"
]);

function boundary(exitCode, status, reason) {
  return { exitCode, result: { status, reason, classificationInvoked: false } };
}

function inside(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));
}

function allowedRoots(env) {
  return [env.GITHUB_WORKSPACE, env.RUNNER_TEMP]
    .filter(value => typeof value === "string" && value.length > 0)
    .map(value => path.resolve(value));
}

export function resolveInputPath(value, env, io = fs) {
  const roots = allowedRoots(env);
  if (typeof value !== "string" || value.length === 0 || roots.length === 0) throw new Error("PATH_INVALID");
  const candidate = io.realpathSync(path.isAbsolute(value) ? value : path.resolve(env.GITHUB_WORKSPACE ?? "", value));
  if (!roots.some(root => inside(root, candidate)) || !io.statSync(candidate).isFile()) throw new Error("PATH_INVALID");
  return candidate;
}

export function resolveOutputDirectory(value, env, io = fs) {
  const roots = allowedRoots(env);
  if (typeof value !== "string" || value.length === 0 || roots.length === 0) throw new Error("PATH_INVALID");
  const candidate = path.resolve(env.GITHUB_WORKSPACE ?? "", value);
  if (!roots.some(root => inside(root, candidate))) throw new Error("PATH_INVALID");
  io.mkdirSync(candidate, { recursive: true });
  const real = io.realpathSync(candidate);
  if (!roots.some(root => inside(root, real))) throw new Error("PATH_INVALID");
  return real;
}

export function executeRuntime({ environment, inputText }) {
  if (environment !== "TEST") return boundary(66, "BLOCKED", "TEST_ENVIRONMENT_REQUIRED");
  if (typeof inputText !== "string" || Buffer.byteLength(inputText, "utf8") > MAX_INPUT_BYTES) {
    return boundary(65, "INVALID_EVIDENCE", "EVIDENCE_INPUT_INVALID");
  }

  let input;
  try { input = JSON.parse(inputText); }
  catch { return boundary(65, "INVALID_EVIDENCE", "EVIDENCE_JSON_INVALID"); }
  if (input?.governanceEnvelope?.governance?.environment !== "TEST") {
    return boundary(66, "BLOCKED", "GOVERNED_TEST_ENVIRONMENT_REQUIRED");
  }

  const composed = composeGovernedClassification(input);
  const sources = input.classificationSources;
  if (!sources || composed.result.classificationInvoked || !["SOURCE_TECHNICAL_ERROR", "SOURCE_STAGE_NOT_ATTEMPTED"].includes(composed.result.reason)) return composed;

  const exactFailure = [
    [sources.repositorySource?.status === "ERROR", "REPOSITORY_ERROR"],
    [["AUTHENTICATION_FAILED", "TRANSPORT_FAILED", "TIMEOUT", "CANCELLED"].includes(sources.targetConnectionSource?.status), `SQL_${sources.targetConnectionSource?.status}`],
    [sources.schemaSource?.status === "ERROR", "SCHEMA_ERROR"]
  ].find(([matches]) => matches);
  return exactFailure ? boundary(75, "TECHNICAL_ERROR", exactFailure[1]) : composed;
}

function sourceStates(result, input) {
  if (!input?.classificationSources || result.result.reason.endsWith("CONTRACT_INVALID")) return null;
  const sources = input.classificationSources;
  const state = value => SAFE_SOURCE_STATES.has(value?.status) ? value.status : null;
  return {
    connection: state(sources.connectionSource),
    databaseLookup: state(sources.databaseLookupSource),
    targetConnection: state(sources.targetConnectionSource),
    metadata: state(sources.metadataSource),
    physical: state(sources.physicalSource),
    history: state(sources.historySource),
    repository: state(sources.repositorySource),
    schema: state(sources.schemaSource),
    registry: state(sources.registrySource),
    onboarding: state(sources.onboardingSource)
  };
}

export function createArtifact(result, input = null) {
  const composed = result.result;
  return {
    artifactContractVersion: 1,
    artifactKind: "CLASSIFICATION_V2_TEST_READ_ONLY",
    environment: "TEST",
    targetId: composed.targetId ?? null,
    status: composed.status,
    reason: composed.reason,
    classificationInvoked: composed.classificationInvoked,
    governance: composed.governance ?? null,
    sourceProvenance: composed.sourceProvenance ?? null,
    evidenceProvenance: composed.evidenceProvenance ?? null,
    sourceStates: sourceStates(result, input),
    classification: composed.classificationInvoked ? (composed.classification ?? null) : null
  };
}

function appendOutput(file, key, value) {
  if (!file) return;
  fs.appendFileSync(file, `${key}=${String(value)}\n`, { encoding: "utf8" });
}

export function runCli({
  env = process.env,
  readFile = value => fs.readFileSync(value, "utf8"),
  writeFile = (name, value) => fs.writeFileSync(name, value, { encoding: "utf8", flag: "w" }),
  writeStderr = value => fs.writeSync(2, value)
} = {}) {
  let runtime, parsedInput = null;
  try {
    if (!env.EVIDENCE_FILE || !env.OUTPUT_DIRECTORY) runtime = boundary(64, "INVALID_EVIDENCE", "RUNTIME_INPUT_REQUIRED");
    else {
      const evidencePath = resolveInputPath(env.EVIDENCE_FILE, env);
      const inputText = readFile(evidencePath);
      try { parsedInput = JSON.parse(inputText); } catch { /* executeRuntime returns the sanitized error. */ }
      runtime = executeRuntime({ environment: env.ENVIRONMENT_NAME, inputText });
    }
  } catch {
    runtime = boundary(70, "TECHNICAL_ERROR", "RUNTIME_IO_ERROR");
  }

  const outputDirectory = env.OUTPUT_DIRECTORY;
  let artifactPath = "";
  if (outputDirectory) {
    try {
      const safeOutputDirectory = resolveOutputDirectory(outputDirectory, env);
      artifactPath = path.join(safeOutputDirectory, ARTIFACT_NAME);
      writeFile(artifactPath, `${JSON.stringify(createArtifact(runtime, parsedInput), null, 2)}\n`);
    } catch {
      runtime = boundary(70, "TECHNICAL_ERROR", "ARTIFACT_WRITE_ERROR");
      artifactPath = "";
    }
  }

  try {
    appendOutput(env.GITHUB_OUTPUT, "artifact-path", artifactPath);
    appendOutput(env.GITHUB_OUTPUT, "status", runtime.result.status);
    appendOutput(env.GITHUB_OUTPUT, "reason", runtime.result.reason);
    appendOutput(env.GITHUB_OUTPUT, "classification-invoked", runtime.result.classificationInvoked);
    appendOutput(env.GITHUB_OUTPUT, "target-id", runtime.result.targetId ?? "");
  } catch {
    runtime = boundary(70, "TECHNICAL_ERROR", "OUTPUT_WRITE_ERROR");
  }

  if (runtime.exitCode !== 0) {
    try { writeStderr(`classification-v2-test-runtime: ${runtime.result.reason}\n`); } catch { /* Diagnostic unavailable. */ }
  }
  return runtime.exitCode;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = runCli();
