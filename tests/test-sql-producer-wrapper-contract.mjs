#!/usr/bin/env node

import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { runPublicSqlDiscovery } from "../scripts/run-sql-discovery-v2-public.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const project = path.join(root, "tests", "SqlDiscovery.V2.Tests", "SqlDiscovery.V2.Tests.csproj");
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), "sql-producer-wrapper-"));
const producer = argument => {
  const result = spawnSync("dotnet", ["run", "--project", project, "--no-restore", "--", argument], {
    encoding: "utf8", cwd: root, timeout: 180_000
  });
  assert.equal(result.status, 0, "synthetic .NET producer must succeed");
  assert.equal(result.stderr, "", "synthetic .NET producer must not emit diagnostics");
  return result.stdout;
};
const env = name => ({ ENVIRONMENT_NAME: "TEST", SQL_SERVER_CONNECTION: "Server=synthetic;Password=do-not-print",
  SQL_DATABASE_NAME: "ObservedDb", SQL_TLS_MODE: "TEST_UNTRUSTED_CERTIFICATE", GITHUB_OUTPUT: path.join(temporary, `${name}.txt`),
  GITHUB_ACTION_PATH: path.join(root, "sql-discovery-v2"), RUNNER_TEMP: temporary });
const execute = stdout => {
  let calls = 0;
  return () => (++calls < 3 ? { status: 0, stdout: "", stderr: "" } : { status: 0, stdout, stderr: "" });
};
const run = (name, stdout) => runPublicSqlDiscovery(env(name), execute(stdout));
const rejected = (name, value) => assert.throws(() => run(name, `${JSON.stringify(value)}\n`),
  error => error.terminal?.terminalPhase === "ENVELOPE_VALIDATION" && error.terminal?.childExitCode === 0);

try {
  const successStdout = producer("--emit-success-envelope");
  const success = JSON.parse(successStdout);
  assert.deepEqual(Object.keys(success.observedDatabaseIdentity).sort(), ["databaseName", "serverInstance"]);
  assert.equal(run("success", successStdout).observedDatabaseIdentity.databaseName, "ObservedDb");
  for (const key of ["serverInstance", "databaseName"]) {
    const invalid = structuredClone(success);
    delete invalid.observedDatabaseIdentity[key];
    rejected(`missing-${key}`, invalid);
  }
  const invalidType = structuredClone(success);
  invalidType.observedDatabaseIdentity.serverInstance = 42;
  rejected("wrong-type", invalidType);
  const missingIdentity = structuredClone(success);
  delete missingIdentity.observedDatabaseIdentity;
  rejected("missing-identity-on-success", missingIdentity);
  const failedTargetStdout = producer("--emit-target-failure-envelope");
  const failedTarget = JSON.parse(failedTargetStdout);
  assert.equal(failedTarget.targetConnectionSource.status, "TRANSPORT_FAILED");
  assert.equal(Object.hasOwn(failedTarget, "observedDatabaseIdentity"), false);
  assert.equal(run("failed-target", failedTargetStdout).targetConnectionSource.status, "TRANSPORT_FAILED");
  console.log("OK: real .NET producer stdout → public Node wrapper; identity contract and fail-closed cases");
} finally {
  fs.rmSync(temporary, { recursive: true, force: true });
}
