#!/usr/bin/env node

import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { formatTerminalDiagnostic, runPublicSqlDiscovery, validateEnvironment as validateSqlEnvironment, validateTlsMode } from "../scripts/run-sql-discovery-v2-public.mjs";
import { buildRequest, runPublicRepositoryDiscovery, validateEnvironment as validateRepositoryEnvironment } from "../scripts/run-repository-discovery-v2-public.mjs";
import { TECHNICAL_CATEGORIES } from "../scripts/empty-for-new-ef-v1.mjs";

const root = path.resolve(new URL("..", import.meta.url).pathname.replace(/^\/(?:([A-Za-z]:))/, "$1"));
const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), "public-discovery-v2-"));
let passed = 0;
const test = (name, fn) => { try { fn(); passed += 1; console.log(`PASS: ${name}`); } catch (error) { console.error(`FAIL: ${name}`); throw error; } };
const output = name => path.join(temporaryRoot, `${name}.txt`);
const sqlEnv = name => ({ ENVIRONMENT_NAME: "TEST", SQL_SERVER_CONNECTION: "Server=secret.example;Password=do-not-log", SQL_DATABASE_NAME: "Db", GITHUB_OUTPUT: output(name), GITHUB_ACTION_PATH: path.join(root, "sql-discovery-v2"), RUNNER_TEMP: temporaryRoot });
const sqlEvidence = targetStatus => ({
  tls: { tlsRequestedMode: "STRICT", tlsInitialMode: "STRICT", tlsInitialResult: "SUCCEEDED", tlsFallbackAllowed: false,
    tlsFallbackAttempted: false, tlsEffectiveMode: "STRICT", tlsCertificateValidated: true, transportEncrypted: true,
    tlsPolicySource: "DEFAULT_STRICT" },
  serverConnectionStatus: targetStatus,
  connectionSource: { status: targetStatus === "NOT_ATTEMPTED" ? "NOT_ATTEMPTED" : targetStatus === "CANCELLED" ? "CANCELLED" : targetStatus === "TIMEOUT" ? "TIMEOUT" : "SUCCEEDED" },
  databaseLookupSource: { status: targetStatus === "NOT_ATTEMPTED" ? "NOT_ATTEMPTED" : "FOUND" },
  targetConnectionSource: { status: targetStatus },
  metadataSource: { status: targetStatus === "SUCCEEDED" ? "SUFFICIENT" : "NOT_ATTEMPTED" },
  physicalSource: { status: targetStatus === "SUCCEEDED" ? "OBSERVED" : "NOT_ATTEMPTED", ...(targetStatus === "SUCCEEDED" ? { businessObjectCount: 2 } : {}) },
  historySource: { status: targetStatus === "SUCCEEDED" ? "PRESENT" : "NOT_ATTEMPTED", ...(targetStatus === "SUCCEEDED" ? { migrationCount: 1, migrationIds: ["20260101000000_A"] } : {}) },
  ...(targetStatus === "SUCCEEDED" ? { observedDatabaseIdentity: { serverInstance: "SQLNODE01\\INSTANCE", databaseName: "ObservedDb" } } : {})
});
const failureEvidence = (exitCode, reason, withStages = true) => ({
  failureContractVersion: 1, executionExitCode: exitCode, executionReasonCode: reason,
  tls: { tlsRequestedMode: "TEST_UNTRUSTED_CERTIFICATE", tlsInitialMode: "TEST_UNTRUSTED_CERTIFICATE", tlsInitialResult: "OTHER_FAILURE",
    tlsFallbackAllowed: false, tlsFallbackAttempted: false, tlsEffectiveMode: "TEST_UNTRUSTED_CERTIFICATE",
    tlsCertificateValidated: false, transportEncrypted: true, tlsPolicySource: "EXPLICIT_TEST_CONFIGURATION",
    diagnosticFingerprint: { exceptionType: "System.InvalidOperationException", hResult: "0x80131509", sqlExceptionNumber: null,
      sqlErrorNumbers: [], sqlErrorStates: [], sqlErrorClasses: [], innerExceptionType: null, innerHResult: null,
      nativeErrorCode: null, tlsFailureCategory: "UNKNOWN" } },
  projectionRepresentable: exitCode === 75 ? false : null,
  projectionGaps: exitCode === 75 ? ["OBSERVED_DATABASE_IDENTITY_UNAVAILABLE"] : [],
  ...(withStages ? { serverConnectionStatus: "SUCCEEDED", databaseLookupStatus: "FOUND", targetConnectionStatus: "SUCCEEDED",
    metadataStatus: "SUFFICIENT", physicalStatus: "OBSERVED", historyStatus: "PRESENT" } : {})
});
const sqlExecutor = (evidence, observe = () => {}) => {
  let call = 0;
  return (command, args, options) => {
    observe({ command, args, options, call });
    call += 1;
    return call < 3 ? { status: 0, stdout: "restore/build log\n", stderr: "" } : { status: 0, stdout: JSON.stringify(evidence), stderr: "" };
  };
};
const captureTerminal = (env, execute) => {
  try { runPublicSqlDiscovery(env, execute); }
  catch (error) { return { error, line: formatTerminalDiagnostic(error) }; }
  assert.fail("expected terminal failure");
};

try {
  test("public SQL preserves V1 coverage through serialized evidence", () => {
    const env = sqlEnv("sql-taxonomy"); const value = sqlEvidence("SUCCEEDED");
    value.physicalSource = { status: "OBSERVED", businessObjectCount: 0, technicalObjectCount: 0,
      taxonomy: { version: 1, coverage: "COMPLETE", counts: Object.fromEntries(TECHNICAL_CATEGORIES.map(key => [key, 0])) } };
    runPublicSqlDiscovery(env, sqlExecutor(value));
    const raw = JSON.parse(fs.readFileSync(env.GITHUB_OUTPUT, "utf8").match(/evidence-json<<SQL_DISCOVERY_V2_EOF\n([^\n]+)/u)[1]);
    assert.deepEqual(raw.physicalSource, value.physicalSource);
    value.physicalSource.taxonomy.counts.customSchemas = 1;
    assert.throws(() => runPublicSqlDiscovery(sqlEnv("sql-taxonomy-invalid"), sqlExecutor(value)), error => error.terminal.terminalReasonCode === "ENVELOPE_VALIDATION_FAILED");
  });
  test("SQL acepta TEST explícito", () => assert.doesNotThrow(() => validateSqlEnvironment("TEST")));
  for (const value of [undefined, "", "QA", "PROD", "test"]) test(`SQL rechaza ambiente ${String(value)}`, () => assert.throws(() => validateSqlEnvironment(value), /ENVIRONMENT_NOT_ALLOWED/));
  for (const value of [undefined, "STRICT", "TEST_UNTRUSTED_CERTIFICATE"]) test(`SQL acepta modo TLS ${String(value)}`, () => assert.doesNotThrow(() => validateTlsMode(value)));
  for (const value of ["strict", "true", "1", "yes", ""]) test(`SQL rechaza modo TLS ${String(value)}`, () => assert.throws(() => validateTlsMode(value), /TLS_MODE_INVALID/));
  test("SQL preserva modo TEST explícito sin secretos ni fallback", () => {
    const env = { ...sqlEnv("sql-tls-explicit"), SQL_TLS_MODE: "TEST_UNTRUSTED_CERTIFICATE" };
    const value = sqlEvidence("SUCCEEDED");
    value.tls = { tlsRequestedMode: "TEST_UNTRUSTED_CERTIFICATE", tlsInitialMode: "TEST_UNTRUSTED_CERTIFICATE", tlsInitialResult: "SUCCEEDED", tlsFallbackAllowed: false,
      tlsFallbackAttempted: false, tlsEffectiveMode: "TEST_UNTRUSTED_CERTIFICATE", tlsCertificateValidated: false, transportEncrypted: true,
      tlsPolicySource: "EXPLICIT_TEST_CONFIGURATION" };
    const evidence = runPublicSqlDiscovery(env, sqlExecutor(value));
    assert.deepEqual(evidence.tls, value.tls);
    assert.equal(fs.readFileSync(env.GITHUB_OUTPUT, "utf8").includes(env.SQL_SERVER_CONNECTION), false);
  });
  test("SQL publica fingerprint TLS cerrado y sanitizado para OTHER_FAILURE", () => {
    const env = sqlEnv("sql-tls-diagnostic");
    const value = sqlEvidence("TRANSPORT_FAILED");
    value.tls.tlsInitialResult = "OTHER_FAILURE";
    value.tls.tlsCertificateValidated = false;
    value.tls.diagnosticFingerprint = {
      exceptionType: "Microsoft.Data.SqlClient.SqlException", hResult: "0x80131904", sqlExceptionNumber: 0,
      sqlErrorNumbers: [0, 10054], sqlErrorStates: [0, 0], sqlErrorClasses: [20, 20],
      innerExceptionType: "System.Security.Authentication.AuthenticationException", innerHResult: "0x80131501",
      nativeErrorCode: null, tlsFailureCategory: "TRANSPORT_OTHER"
    };
    const evidence = runPublicSqlDiscovery(env, sqlExecutor(value));
    const persisted = fs.readFileSync(env.GITHUB_OUTPUT, "utf8");
    assert.deepEqual(evidence.tls.diagnosticFingerprint.sqlErrorNumbers, [0, 10054]);
    for (const forbidden of [env.SQL_SERVER_CONNECTION, "Password=", "token=", "stackTrace", "message"]) assert.equal(persisted.toLowerCase().includes(forbidden.toLowerCase()), false);
  });
  for (const status of ["AUTHENTICATION_FAILED", "TRANSPORT_FAILED", "TIMEOUT", "CANCELLED", "NOT_ATTEMPTED", "SUCCEEDED"]) {
    test(`SQL preserva ${status}`, () => {
      const env = sqlEnv(`sql-${status}`);
      const execute = sqlExecutor(sqlEvidence(status));
      const evidence = runPublicSqlDiscovery(env, execute);
      assert.equal(evidence.serverConnectionStatus, status);
      const persisted = fs.readFileSync(env.GITHUB_OUTPUT, "utf8");
      assert.match(persisted, new RegExp(status));
      assert.equal(persisted.includes(env.SQL_SERVER_CONNECTION), false);
    });
  }
  test("SQL restore failure publica sólo terminal sanitizado", () => {
    const env = sqlEnv("sql-restore-error");
    const terminal = captureTerminal(env, () => ({ status: 1, stdout: env.SQL_SERVER_CONNECTION, stderr: "raw restore secret" }));
    assert.equal(terminal.line, "sql-discovery-v2: terminalPhase=RESTORE terminalReasonCode=RESTORE_FAILED childExitCode=1");
    const persisted = fs.readFileSync(env.GITHUB_OUTPUT, "utf8");
    assert.match(persisted, /terminal-phase[\s\S]*RESTORE/u);
    assert.doesNotMatch(terminal.line + persisted, /raw restore secret|Password=|secret\.example/iu);
  });
  test("SQL build failure se distingue de restore", () => {
    const env = sqlEnv("sql-build-error"); let call = 0;
    const terminal = captureTerminal(env, () => (++call === 1 ? { status: 0, stdout: "ok", stderr: "" }
      : { status: 2, stdout: "raw build stdout", stderr: "raw build stderr" }));
    assert.equal(terminal.line, "sql-discovery-v2: terminalPhase=BUILD terminalReasonCode=BUILD_FAILED childExitCode=2");
    assert.doesNotMatch(terminal.line + fs.readFileSync(env.GITHUB_OUTPUT, "utf8"), /raw build/iu);
  });
  test("SQL child start failure no publica excepción", () => {
    const env = sqlEnv("sql-child-start"); let call = 0;
    const terminal = captureTerminal(env, () => (++call < 3 ? { status: 0, stdout: "ok", stderr: "" }
      : { status: null, stdout: "raw stdout", stderr: "raw stderr", error: new Error(env.SQL_SERVER_CONNECTION) }));
    assert.equal(terminal.line, "sql-discovery-v2: terminalPhase=CHILD_START terminalReasonCode=CHILD_START_FAILED");
    assert.doesNotMatch(terminal.line + fs.readFileSync(env.GITHUB_OUTPUT, "utf8"), /raw stdout|raw stderr|Password=|secret\.example/iu);
  });
  test("SQL success normal no publica terminal failure", () => {
    const env = sqlEnv("sql-success-terminal");
    assert.equal(runPublicSqlDiscovery(env, sqlExecutor(sqlEvidence("SUCCEEDED"))).serverConnectionStatus, "SUCCEEDED");
    assert.doesNotMatch(fs.readFileSync(env.GITHUB_OUTPUT, "utf8"), /terminal-(?:phase|reason-code|child-exit-code)/u);
  });
  test("SQL falla cerrado sin identidad target", () => {
    const env = sqlEnv("sql-missing-identity");
    const evidence = sqlEvidence("SUCCEEDED"); delete evidence.observedDatabaseIdentity;
    assert.throws(() => runPublicSqlDiscovery(env, sqlExecutor(evidence)), error => error.terminal.terminalReasonCode === "ENVELOPE_VALIDATION_FAILED");
    assert.match(fs.readFileSync(env.GITHUB_OUTPUT, "utf8"), /terminal-reason-code[\s\S]*ENVELOPE_VALIDATION_FAILED/);
  });
  test("SQL projection blocked preserva envelope y mantiene failure", () => {
    const env = sqlEnv("sql-identity-cli-blocked");
    const envelope = failureEvidence(75, "SQL_DISCOVERY_PROJECTION_BLOCKED");
    let call = 0;
    const execute = () => (++call < 3
      ? { status: 0, stdout: "restore/build log\n", stderr: "" }
      : { status: 75, stdout: JSON.stringify(envelope), stderr: "SQL_DISCOVERY_PROJECTION_BLOCKED" });
    const terminal = captureTerminal(env, execute);
    assert.equal(terminal.line, "sql-discovery-v2: terminalPhase=CLI_EXIT terminalReasonCode=CLI_FAILURE_ENVELOPE_ACCEPTED childExitCode=75");
    const persisted = fs.readFileSync(env.GITHUB_OUTPUT, "utf8");
    assert.match(persisted, /execution-exit-code[\s\S]*75/u);
    assert.match(persisted, /OBSERVED_DATABASE_IDENTITY_UNAVAILABLE/u);
    assert.equal(persisted.includes(env.SQL_SERVER_CONNECTION), false);
  });
  test("SQL internal error con transport preserva TLS y mantiene failure", () => {
    const env = sqlEnv("sql-internal-error");
    const envelope = failureEvidence(70, "SQL_DISCOVERY_INTERNAL_ERROR", false);
    let call = 0;
    const execute = () => (++call < 3 ? { status: 0, stdout: "ok", stderr: "" }
      : { status: 70, stdout: JSON.stringify(envelope), stderr: "private" });
    const terminal = captureTerminal(env, execute);
    assert.equal(terminal.line, "sql-discovery-v2: terminalPhase=CLI_EXIT terminalReasonCode=CLI_FAILURE_ENVELOPE_ACCEPTED childExitCode=70");
    const persisted = fs.readFileSync(env.GITHUB_OUTPUT, "utf8");
    assert.match(persisted, /TEST_UNTRUSTED_CERTIFICATE/u);
    assert.doesNotMatch(persisted, /private|Password=|Server=|stackTrace|message/iu);
  });
  test("SQL malformed child output falla cerrado sin evidencia falsa", () => {
    const env = sqlEnv("sql-malformed-failure"); let call = 0;
    const execute = () => (++call < 3 ? { status: 0, stdout: "ok", stderr: "" }
      : { status: 75, stdout: '{"executionExitCode":75,"secret":"Password=do-not-expose"}', stderr: env.SQL_SERVER_CONNECTION });
    const terminal = captureTerminal(env, execute);
    assert.equal(terminal.line, "sql-discovery-v2: terminalPhase=ENVELOPE_VALIDATION terminalReasonCode=ENVELOPE_VALIDATION_FAILED childExitCode=75");
    const persisted = fs.readFileSync(env.GITHUB_OUTPUT, "utf8");
    assert.doesNotMatch(terminal.line + persisted, /do-not-expose|Password=|secret/u);
  });
  test("SQL exit desconocido publica sólo el número", () => {
    const env = sqlEnv("sql-unknown-exit"); let call = 0;
    const execute = () => (++call < 3 ? { status: 0, stdout: "ok", stderr: "" }
      : { status: 9, stdout: env.SQL_SERVER_CONNECTION, stderr: "raw unknown stderr" });
    const terminal = captureTerminal(env, execute);
    assert.equal(terminal.line, "sql-discovery-v2: terminalPhase=CLI_EXIT terminalReasonCode=CLI_EXIT_UNRECOGNIZED childExitCode=9");
    assert.doesNotMatch(terminal.line + fs.readFileSync(env.GITHUB_OUTPUT, "utf8"), /raw unknown|Password=|secret\.example/iu);
  });
  test("SQL publica outputs de identidad observada", () => {
    const env = sqlEnv("sql-identity");
    runPublicSqlDiscovery(env, sqlExecutor(sqlEvidence("SUCCEEDED")));
    const persisted = fs.readFileSync(env.GITHUB_OUTPUT, "utf8");
    assert.match(persisted, /observed-server-instance[\s\S]*SQLNODE01\\INSTANCE/u);
    assert.match(persisted, /observed-database-name[\s\S]*ObservedDb/u);
    const evidenceOutput = persisted.match(/evidence-json<<SQL_DISCOVERY_V2_EOF\n([^\n]+)/u)?.[1] ?? "";
    assert.equal(evidenceOutput.includes("observedDatabaseIdentity"), false);
    assert.equal(persisted.includes(env.SQL_SERVER_CONNECTION), false);
  });
  test("SQL mantiene secreto fuera de command line, evidence y outputs", () => {
    const env = sqlEnv("sql-secret-boundary");
    const invocations = [];
    const execute = sqlExecutor(sqlEvidence("SUCCEEDED"), invocation => invocations.push(invocation));
    const evidence = runPublicSqlDiscovery(env, execute);
    assert.equal(invocations.length, 3);
    assert.equal(invocations.every(invocation => `${invocation.command} ${invocation.args.join(" ")}`.includes(env.SQL_SERVER_CONNECTION) === false), true);
    assert.equal(invocations[0].args.includes("restore"), true);
    assert.equal(invocations[0].args.some(value => value.endsWith("NuGet.Config")), true);
    assert.equal(invocations[0].options.env.SQL_SERVER_CONNECTION, undefined);
    assert.equal(invocations[1].args.includes("--no-restore"), true);
    assert.equal(invocations[1].options.env.SQL_SERVER_CONNECTION, undefined);
    assert.deepEqual(invocations[2].args.slice(-1), ["--v2"]);
    assert.equal(invocations[2].options.env.SQL_SERVER_CONNECTION, env.SQL_SERVER_CONNECTION);
    assert.equal(JSON.stringify(evidence).includes(env.SQL_SERVER_CONNECTION), false);
    assert.equal(fs.readFileSync(env.GITHUB_OUTPUT, "utf8").includes(env.SQL_SERVER_CONNECTION), false);
  });

  test("Repository acepta TEST explícito", () => assert.doesNotThrow(() => validateRepositoryEnvironment("TEST")));
  for (const value of [undefined, "", "QA", "PROD", "test"]) test(`Repository rechaza ambiente ${String(value)}`, () => assert.throws(() => validateRepositoryEnvironment(value), /ENVIRONMENT_NOT_ALLOWED/));
  test("Repository preserva UNKNOWN", () => assert.deepEqual(buildRequest({ ENVIRONMENT_NAME: "TEST", INSPECTION_STATUS: "UNKNOWN" }), { repositoryDiscoveryContractVersion: 1, inspectionStatus: "UNKNOWN" }));
  test("Repository preserva NOT_ATTEMPTED", () => assert.deepEqual(buildRequest({ ENVIRONMENT_NAME: "TEST", INSPECTION_STATUS: "NOT_ATTEMPTED" }), { repositoryDiscoveryContractVersion: 1, inspectionStatus: "NOT_ATTEMPTED" }));
  test("Repository bloquea traversal", () => assert.throws(() => buildRequest({ ENVIRONMENT_NAME: "TEST", INSPECTION_STATUS: "READY", GITHUB_WORKSPACE: temporaryRoot, WORKSPACE: os.tmpdir() }), /WORKSPACE_NOT_ALLOWED/));
  test("Repository no filtra path cuando realpath falla", () => {
    const secretPath = path.join(temporaryRoot, "customer-secret-path");
    const child = spawnSync(process.execPath, [path.join(root, "scripts", "run-repository-discovery-v2-public.mjs")], {
      encoding: "utf8",
      env: { ...process.env, ENVIRONMENT_NAME: "TEST", INSPECTION_STATUS: "READY", GITHUB_WORKSPACE: temporaryRoot, WORKSPACE: secretPath, GITHUB_OUTPUT: output("missing-path"), GITHUB_ACTION_PATH: path.join(root, "repository-discovery-v2") }
    });
    assert.equal(child.status, 1);
    assert.equal(child.stderr.includes(secretPath), false);
    assert.match(child.stderr, /EXECUTION_FAILED/);
  });
  test("Repository invoca implementación real y conserva estados", () => {
    for (const status of ["PRESENT_VALID", "ABSENT", "INVALID", "AMBIGUOUS", "UNKNOWN", "NOT_ATTEMPTED"]) {
      const env = { ENVIRONMENT_NAME: "TEST", INSPECTION_STATUS: status === "UNKNOWN" || status === "NOT_ATTEMPTED" ? status : "READY", WORKSPACE: temporaryRoot, GITHUB_WORKSPACE: temporaryRoot, GITHUB_OUTPUT: output(`repo-${status}`), GITHUB_ACTION_PATH: path.join(root, "repository-discovery-v2") };
      const payload = status === "PRESENT_VALID" ? { status, migrations: { count: 1, ids: ["20260101000000_A"] } } : { status };
      const evidence = runPublicRepositoryDiscovery(env, () => ({ status: 0, stdout: JSON.stringify(payload), stderr: "" }));
      assert.equal(evidence.status, status);
    }
  });
  test("Repository ERROR se publica como evidence contractual", () => {
    const env = { ENVIRONMENT_NAME: "TEST", INSPECTION_STATUS: "READY", WORKSPACE: temporaryRoot, GITHUB_WORKSPACE: temporaryRoot, GITHUB_OUTPUT: output("repo-error"), GITHUB_ACTION_PATH: path.join(root, "repository-discovery-v2") };
    const evidence = runPublicRepositoryDiscovery(env, () => ({ status: 75, stdout: '{"status":"ERROR"}\n', stderr: "private path" }));
    assert.equal(evidence.status, "ERROR");
    assert.match(fs.readFileSync(env.GITHUB_OUTPUT, "utf8"), /ERROR/);
  });
  test("Repository exige coherencia entre ERROR y exit 75", () => {
    const env = { ENVIRONMENT_NAME: "TEST", INSPECTION_STATUS: "READY", WORKSPACE: temporaryRoot, GITHUB_WORKSPACE: temporaryRoot, GITHUB_OUTPUT: output("repo-error-mismatch"), GITHUB_ACTION_PATH: path.join(root, "repository-discovery-v2") };
    assert.throws(() => runPublicRepositoryDiscovery(env, () => ({ status: 0, stdout: '{"status":"ERROR"}\n', stderr: "" })), /EVIDENCE_EXIT_MISMATCH/);
  });
  test("Repository agrega adoption sólo cuando ambos inputs están presentes", () => {
    const efSource = { sourceRepository: "infrastructure-services/source", sourceRevision: "a".repeat(40), projectPath: "src/App.csproj", managedMigrationStartId: "20260101000000_A", preAdoptionArtifacts: [] };
    const env = { ENVIRONMENT_NAME: "TEST", INSPECTION_STATUS: "READY", WORKSPACE: temporaryRoot, GITHUB_WORKSPACE: temporaryRoot, EF_SOURCE_JSON: JSON.stringify(efSource), ACTUAL_SOURCE_REVISION: "a".repeat(40) };
    assert.deepEqual(buildRequest(env), { repositoryDiscoveryContractVersion: 1, inspectionStatus: "READY", workspace: fs.realpathSync(temporaryRoot), efSource, actualSourceRevision: "a".repeat(40) });
    assert.throws(() => buildRequest({ ...env, ACTUAL_SOURCE_REVISION: "" }), /ADOPTION_INPUT_PARTIAL/);
    assert.throws(() => buildRequest({ ...env, EF_SOURCE_JSON: "{" }), /EF_SOURCE_JSON_INVALID/);
  });
  test("Repository publica raw y managed por separado sin contaminar evidence-json", () => {
    const env = { ENVIRONMENT_NAME: "TEST", INSPECTION_STATUS: "READY", WORKSPACE: temporaryRoot, GITHUB_WORKSPACE: temporaryRoot, GITHUB_OUTPUT: output("repo-adopted"), GITHUB_ACTION_PATH: path.join(root, "repository-discovery-v2"), EF_SOURCE_JSON: "{}", ACTUAL_SOURCE_REVISION: "a".repeat(40) };
    const payload = { status: "PRESENT_VALID", migrations: { count: 1, ids: ["20260101000000_A"] }, adoption: { status: "VALID", issues: [] }, rawEvidence: { rawEvidenceContractVersion: 1, migrationArtifacts: [] } };
    runPublicRepositoryDiscovery(env, () => ({ status: 0, stdout: JSON.stringify(payload), stderr: "" }));
    const persisted = fs.readFileSync(env.GITHUB_OUTPUT, "utf8");
    const source = JSON.parse(persisted.match(/evidence-json<<REPOSITORY_DISCOVERY_V2_EOF\n([^\n]+)/u)[1]);
    assert.deepEqual(source, { status: payload.status, migrations: payload.migrations });
    assert.match(persisted, /raw-evidence-json[\s\S]*rawEvidenceContractVersion/u);
    assert.match(persisted, /managed-evidence-json[\s\S]*"adoption":\{"status":"VALID"/u);
  });

  test("contratos action.yml son explícitos y no tienen default TEST", () => {
    for (const action of ["sql-discovery-v2", "repository-discovery-v2"]) {
      const text = fs.readFileSync(path.join(root, action, "action.yml"), "utf8");
      assert.match(text, /environment-name:\n\s+description:[^\n]+\n\s+required: true/);
      assert.doesNotMatch(text, /environment-name:[\s\S]{0,160}default:\s*TEST/);
      assert.match(text, /shell: bash/);
      if (action === "sql-discovery-v2") {
        for (const name of ["terminal-phase", "terminal-reason-code", "terminal-child-exit-code"])
          assert.match(text, new RegExp(`^  ${name}:`, "mu"));
      }
    }
  });
  test("runners no contienen mutaciones SQL ni eval", () => {
    for (const file of ["run-sql-discovery-v2-public.mjs", "run-repository-discovery-v2-public.mjs"]) {
      const text = fs.readFileSync(path.join(root, "scripts", file), "utf8");
      assert.doesNotMatch(text, /\beval\s*\(|\b(?:INSERT\s+INTO|UPDATE\s+[^\s]+\s+SET|DELETE\s+FROM|MERGE\s+INTO|CREATE\s+(?:TABLE|DATABASE)|ALTER\s+(?:TABLE|DATABASE)|DROP\s+(?:TABLE|DATABASE)|TRUNCATE\s+TABLE)/i);
    }
  });
  test("public repository passes caller acquisition separately from adoption", () => {
    const env = { ENVIRONMENT_NAME: "TEST", INSPECTION_STATUS: "READY", GITHUB_WORKSPACE: temporaryRoot, WORKSPACE: temporaryRoot, EF_SOURCE_JSON: JSON.stringify({ sourceRevision: "a".repeat(40) }), ACTUAL_SOURCE_REVISION: "b".repeat(40), CALLER_SOURCE_JSON: JSON.stringify({ sourceRepository: "org/app", sourceRevision: "b".repeat(40) }) };
    const request = buildRequest(env);
    assert.equal(request.efSource.sourceRevision, "a".repeat(40));
    assert.equal(request.callerSource.sourceRevision, "b".repeat(40));
    assert.throws(() => buildRequest({ ...env, EF_SOURCE_JSON: "", ACTUAL_SOURCE_REVISION: "" }), /ADOPTION_INPUT_PARTIAL/);
    assert.throws(() => buildRequest({ ...env, CALLER_SOURCE_JSON: "{" }));
  });
} finally {
  fs.rmSync(temporaryRoot, { recursive: true, force: true });
}

console.log(`OK: ${passed} casos de public Discovery V2`);
