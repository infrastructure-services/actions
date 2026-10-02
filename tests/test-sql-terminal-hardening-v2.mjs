import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { formatTerminalDiagnostic } from "../scripts/run-sql-discovery-v2-public.mjs";

// Each case runs the real CLI entry in a separate Node process. The only
// replacement is the dotnet executor: no restore, build, network or SQL occurs.
const wrapper = new URL("../scripts/run-sql-discovery-v2-public.mjs", import.meta.url).href;
const sentinel = "PRIVATE_SENTINEL_SERVER_PASSWORD_EXCEPTION_RAW";
const tls = { tlsRequestedMode: "TEST_UNTRUSTED_CERTIFICATE", tlsInitialMode: "TEST_UNTRUSTED_CERTIFICATE",
  tlsInitialResult: "NOT_ATTEMPTED", tlsFallbackAllowed: false, tlsFallbackAttempted: false,
  tlsEffectiveMode: "TEST_UNTRUSTED_CERTIFICATE", tlsPolicySource: "EXPLICIT_TEST_CONFIGURATION",
  transportEncrypted: true, tlsCertificateValidated: false };
export const failureEnvelope = { failureContractVersion: 1, executionExitCode: 70,
  executionReasonCode: "SQL_DISCOVERY_INTERNAL_ERROR", tls, projectionRepresentable: null, projectionGaps: [] };
const projectionEnvelope = { ...failureEnvelope, executionExitCode: 75, executionReasonCode: "SQL_DISCOVERY_PROJECTION_BLOCKED",
  tls: { ...tls, tlsInitialResult: "SUCCEEDED" }, projectionRepresentable: false, projectionGaps: ["OBSERVED_DATABASE_IDENTITY_UNAVAILABLE"],
  serverConnectionStatus: "SUCCEEDED", databaseLookupStatus: "FOUND", targetConnectionStatus: "SUCCEEDED",
  metadataStatus: "SUFFICIENT", physicalStatus: "OBSERVED", historyStatus: "PRESENT" };
const validEvidence = { tls, serverConnectionStatus: "NOT_ATTEMPTED",
  ...Object.fromEntries(["connectionSource", "databaseLookupSource", "targetConnectionSource", "metadataSource", "physicalSource", "historySource"]
    .map(key => [key, {status:"NOT_ATTEMPTED"}])) };
export const terminalCases = [
  { name: "restore", phase: "RESTORE", reason: "RESTORE_FAILED", exit: 1, at: 1, status: 1 },
  { name: "build", phase: "BUILD", reason: "BUILD_FAILED", exit: 2, at: 2, status: 2 },
  { name: "start", phase: "CHILD_START", reason: "CHILD_START_FAILED", at: 3, error: true },
  { name: "unknown-exit", phase: "CLI_EXIT", reason: "CLI_EXIT_UNRECOGNIZED", exit: 9, at: 3, status: 9 },
  { name: "invalid-failure", phase: "ENVELOPE_VALIDATION", reason: "ENVELOPE_VALIDATION_FAILED", exit: 75, at: 3, status: 75 },
  { name: "invalid-success", phase: "ENVELOPE_VALIDATION", reason: "ENVELOPE_VALIDATION_FAILED", exit: 0, at: 3, status: 0 },
  { name: "accepted", phase: "CLI_EXIT", reason: "CLI_FAILURE_ENVELOPE_ACCEPTED", exit: 70, at: 3, status: 70, envelope: failureEnvelope },
  { name: "accepted-projection", phase: "CLI_EXIT", reason: "CLI_FAILURE_ENVELOPE_ACCEPTED", exit: 75, at: 3, status: 75, envelope: projectionEnvelope },
  { name: "precondition", phase: "WRAPPER", reason: "PRECONDITION_FAILED", at: 0 },
  { name: "persist-restore", phase: "WRAPPER", reason: "OUTPUT_PERSISTENCE_FAILED", exit: 1, at: 1, status: 1, brokenOutput: true },
  { name: "persist-envelope", phase: "WRAPPER", reason: "OUTPUT_PERSISTENCE_FAILED", exit: 70, at: 3, status: 70, envelope: failureEnvelope, brokenOutput: true },
  { name: "persist-success", phase: "WRAPPER", reason: "OUTPUT_PERSISTENCE_FAILED", exit: 0, at: 3, status: 0, evidence: validEvidence, brokenOutput: true },
  { name: "restore-thrown", phase: "RESTORE", reason: "RESTORE_FAILED", at: 1, throws: true },
  { name: "build-thrown", phase: "BUILD", reason: "BUILD_FAILED", at: 2, throws: true },
  { name: "start-thrown", phase: "CHILD_START", reason: "CHILD_START_FAILED", at: 3, throws: true },
  { name: "unexpected", phase: "WRAPPER", reason: "UNEXPECTED_WRAPPER_FAILURE", at: 1, invalidResult: true }
];

export function executeCase(item, directory) {
  const output = item.brokenOutput ? directory : path.join(directory, `${item.name}.out`);
  const env = { ENVIRONMENT_NAME: "TEST", SQL_TLS_MODE: "TEST_UNTRUSTED_CERTIFICATE", SQL_SERVER_CONNECTION: sentinel,
    SQL_DATABASE_NAME: "Synthetic", GITHUB_ACTION_PATH: fileURLToPath(new URL("../sql-discovery-v2", import.meta.url)),
    RUNNER_TEMP: directory, GITHUB_OUTPUT: output };
  if (item.name === "precondition") delete env.SQL_DATABASE_NAME;
  const program = `import {runSqlDiscoveryCli} from ${JSON.stringify(wrapper)};
    const item=${JSON.stringify(item)}, env=${JSON.stringify(env)}; let calls=0;
    process.exitCode=runSqlDiscoveryCli(env,()=>{
      calls++;
      if(calls !== item.at) return {status:0,stdout:'',stderr:''};
      if(item.throws) throw new Error(${JSON.stringify(sentinel)});
      if(item.invalidResult) return null;
      return {status:item.error?null:item.status,stdout:item.envelope||item.evidence?JSON.stringify(item.envelope||item.evidence):${JSON.stringify(sentinel)},
        stderr:${JSON.stringify(sentinel)}, ...(item.error?{error:new Error(${JSON.stringify(sentinel)})}:{})};
    });
    if(calls !== item.at) process.exitCode=99;`;
  const child = spawnSync(process.execPath, ["--input-type=module", "-e", program], { encoding: "utf8" });
  const persisted = item.brokenOutput ? "" : fs.readFileSync(output, "utf8");
  const outputs = Object.fromEntries([...persisted.matchAll(/([^\n]+)<<SQL_DISCOVERY_V2_EOF\n([^\n]*)\nSQL_DISCOVERY_V2_EOF\n/gu)].map(m => [m[1], m[2]]));
  return { child, outputs, persisted };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), "sql-terminal-v2-"));
try {
  for (const item of terminalCases) {
    const { child, outputs, persisted } = executeCase(item, temporary);
    assert.equal(child.status, 1, item.name);
    assert.equal(child.stdout, "");
    const expected = `sql-discovery-v2: terminalPhase=${item.phase} terminalReasonCode=${item.reason}${item.exit === undefined ? "" : ` childExitCode=${item.exit}`}\n`;
    assert.equal(child.stderr, expected);
    assert.equal((child.stderr + persisted).includes(sentinel), false);
    if (!item.brokenOutput) {
      assert.equal(outputs["terminal-phase"], item.phase);
      assert.equal(outputs["terminal-reason-code"], item.reason);
      assert.equal(outputs["terminal-child-exit-code"], item.exit === undefined ? "" : String(item.exit));
      if (item.envelope) assert.deepEqual(JSON.parse(outputs["failure-evidence-json"]), item.envelope);
    }
    console.log(`PASS: process failure + sanitized terminal: ${item.name}`);
  }
  assert.equal(formatTerminalDiagnostic({terminal:{terminalPhase:sentinel,terminalReasonCode:sentinel,childExitCode:sentinel}}),
    "sql-discovery-v2: terminalPhase=WRAPPER terminalReasonCode=UNEXPECTED_WRAPPER_FAILURE");
  console.log(`OK: ${terminalCases.length + 1} terminal hardening checks`);
} finally { fs.rmSync(temporary, { recursive: true, force: true }); }
}
