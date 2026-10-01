import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

// Execute the production shell with fake dotnet and jq: no SQL, SDK restore or network.
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const tempRoot = process.env.ACTIONS_TEST_TEMP_ROOT ?? os.tmpdir();
const temp = fs.mkdtempSync(path.join(tempRoot, "new-ef-producer-"));
const bin = path.join(temp, "bin"); fs.mkdirSync(bin);
const bash = process.env.ACTIONS_BASH_DIRECTORY ? path.join(process.env.ACTIONS_BASH_DIRECTORY, process.platform === "win32" ? "bash.exe" : "bash") : "bash";
const dotnet = path.join(temp, "dotnet.mjs"), jq = path.join(temp, "jq.mjs");
const posix = value => value.replaceAll("\\", "/").replace(/^([A-Za-z]):/, (_, drive) => `/${drive.toLowerCase()}`);
fs.writeFileSync(dotnet, `import fs from 'node:fs';
const args=process.argv.slice(2), command=args[0]==='restore'||args[0]==='build'?args[0]:args[1];
fs.appendFileSync(process.env.MOCK_CALLS,command+'\\n');
if(command==='restore'||command==='build') process.exit(0);
const result=args[args.indexOf('--result')+1], hash='a'.repeat(64);
const failed=process.env.MOCK_FAILURE==='capture'&&command==='capture-schema';
const nondeterministic=process.env.MOCK_FAILURE==='determinism'&&command==='compare-schema-captures';
const tls={tlsRequestedMode:'TEST_UNTRUSTED_CERTIFICATE',tlsInitialMode:'TEST_UNTRUSTED_CERTIFICATE',tlsInitialResult:failed?'OTHER_FAILURE':'SUCCEEDED',tlsFallbackAllowed:false,tlsFallbackAttempted:false,tlsEffectiveMode:'TEST_UNTRUSTED_CERTIFICATE',tlsCertificateValidated:false,transportEncrypted:true,tlsPolicySource:'EXPLICIT_TEST_CONFIGURATION'};
const value=command==='capture-schema'?{status:failed?'FAIL_DATABASE_UNREACHABLE':'SUCCESS',diagnosticCode:failed?'SQL_0':'MOCK_CAPTURE',tls,...(failed?{diagnosticFingerprint:{exceptionType:'Microsoft.Data.SqlClient.SqlException',hResult:'0x80131904',sqlExceptionNumber:0,sqlErrorNumbers:[0],sqlErrorStates:[0],sqlErrorClasses:[20],innerExceptionType:'System.Security.Authentication.AuthenticationException',innerHResult:'0x80131501',nativeErrorCode:null,tlsFailureCategory:'TRANSPORT_OTHER'}}:{}),databaseName:'Orders',serverVersion:'16',schemaCoverage:'COMPLETE',metricsAvailability:'COMPLETE',objectCounts:{},unsupportedSchemaFeatures:[]}:command==='compare-schema-captures'?{status:'SUCCESS',diagnosticCode:'MOCK_COMPARE',identityConsistent:true,observedServerInstance:'SQL01',observedDatabaseName:'Orders',capture1SchemaHash:hash,capture2SchemaHash:hash,deterministic:process.env.MOCK_FAILURE!=='determinism'}:{status:'SUCCESS',registryStatus:'BASELINE_REQUIRED',driftStatus:'BASELINE_REQUIRED',gateStatus:'BLOCKED',reason:'ONBOARDING_BASELINE_REQUIRED',baselineCandidate:true,observedSchemaHash:hash,certifiedSchemaHash:null,registryFormatVersion:1,registryProvenance:{registryCommitSha:'b'.repeat(40)}};
if(nondeterministic)value.status='FAIL_SCHEMA_CAPTURE_NONDETERMINISTIC';
fs.writeFileSync(result,JSON.stringify(value));process.exit(failed||nondeterministic?6:0);
`);
fs.writeFileSync(jq, `import fs from 'node:fs';
const args=process.argv.slice(2), query=args.find(a=>!a.startsWith('-')), file=args.at(-1)!==query?args.at(-1):null;
if(args.includes('-cn')) { console.log(JSON.stringify({serverInstance:'SQL01',databaseName:'Orders'})); process.exit(0); }
const value=JSON.parse(fs.readFileSync(file||0,'utf8'));
if(args.includes('--arg')&&args.some(a=>a.includes('contractVersion:1'))) { const captureId=args[args.indexOf('--arg')+2], expression=args.find(a=>a.includes('contractVersion:1'))||''; console.log(JSON.stringify(expression.includes('tls:.tls')?{contractVersion:1,captureId,tls:value.tls}:{contractVersion:1,captureId,diagnosticFingerprint:value.diagnosticFingerprint})); process.exit(0); }
if(query==='.')process.exit(0);
if(query.includes('to_entries')||query.includes('[]'))process.exit(0);
const key=/^\\.([A-Za-z0-9_.]+)/.exec(query)?.[1]; let result=key?.split('.').reduce((v,k)=>v?.[k],value);
if(query.includes('| length')) result=result?.length??0;
if(result===undefined||result===null)result=query.includes('// false')?false:query.includes('// {}')?{}:'';
console.log(typeof result==='object'?JSON.stringify(result):String(result));
`);
for (const [name, file] of [["dotnet", dotnet], ["jq", jq]]) fs.writeFileSync(path.join(bin, name), `#!/usr/bin/env bash\nexec node "${posix(file)}" "$@"\n`, { mode: 0o755 });
let passed = 0;
function run({ ordinary = false, failure = "", registryContext = false } = {}) {
  const root = fs.mkdtempSync(path.join(temp, "case-"));
  const output = path.join(root, "output.txt"), calls = path.join(root, "calls.txt"); fs.writeFileSync(output, ""); fs.writeFileSync(calls, "");
  const env = { ...process.env, GITHUB_ACTION_PATH: posix(path.join(repo, ordinary ? "schema-capture" : "schema-capture-new-ef-bootstrap")), GITHUB_WORKSPACE: posix(root), RUNNER_TEMP: posix(root), GITHUB_OUTPUT: posix(output), GITHUB_STEP_SUMMARY: "", ENVIRONMENT_NAME: "TEST", TLS_MODE: "TEST_UNTRUSTED_CERTIFICATE", OUTPUT_DIRECTORY: "artifacts", DB_CONNECTION: "synthetic-never-opened", MOCK_CALLS: calls, MOCK_FAILURE: failure,
    APPLICATION_ID: "", REGISTRY_FILE: "", REGISTRY_REPOSITORY: "", REGISTRY_REF: "", REGISTRY_COMMIT_SHA: "", REGISTRY_LOGICAL_FILE_PATH: "", REGISTRY_FILE_SHA256: "" };
  if (ordinary) {
    const registry = path.join(root, "database-registry", "targets.json"); fs.mkdirSync(path.dirname(registry)); fs.writeFileSync(registry, "{}");
    Object.assign(env, { APPLICATION_ID: "orders", REGISTRY_FILE: posix(registry), REGISTRY_REPOSITORY: "fixture/registry", REGISTRY_REF: "main", REGISTRY_COMMIT_SHA: "b".repeat(40), REGISTRY_LOGICAL_FILE_PATH: "database-registry/targets.json", REGISTRY_FILE_SHA256: crypto.createHash("sha256").update("{}").digest("hex") });
  }
  if (registryContext) env.REGISTRY_FILE = "already-observed-registry.json";
  const script = posix(path.join(repo, "schema-capture", "run-schema-capture.sh"));
  const nodeDir = posix(path.dirname(process.execPath));
  const result = spawnSync(bash, ["-c", `export PATH="${posix(bin)}:${nodeDir}:/c/Program Files/Git/usr/bin:/usr/bin:/bin"; bash -o igncr "${script}"${ordinary ? "" : " --new-ef-bootstrap-readonly"}`], { env, encoding: "utf8", windowsHide: true });
  const diagnostic = path.join(root, "artifacts", "tls-diagnostic.json");
  const tlsEvidence = path.join(root, "artifacts", "tls-evidence.json");
  return { ...result, outputs: fs.readFileSync(output, "utf8"), calls: fs.readFileSync(calls, "utf8"), tlsDiagnostic: fs.existsSync(diagnostic) ? fs.readFileSync(diagnostic, "utf8") : "", tlsEvidence: fs.existsSync(tlsEvidence) ? fs.readFileSync(tlsEvidence, "utf8") : "" };
}
function test(name, fn) { fn(); passed++; console.log(`PASS ${name}`); }
try {
  test("dedicated public action exposes observation outputs without Registry inputs", () => {
    const action = fs.readFileSync(path.join(repo, "schema-capture-new-ef-bootstrap", "action.yml"), "utf8");
    assert.match(action, /DB_CONNECTION: \$\{\{ inputs.connection-string \}\}/);
    assert.ok(!action.includes("registry-file:") && !action.includes("registry-ref:") && !action.includes("registry-commit-sha:"));
    for (const name of ["status", "deterministic", "registry-status", "drift-status", "gate-status", "gate-reason", "observed-database-identity-json"]) assert.ok(action.includes(`  ${name}:`));
    assert.match(action, /run: bash "\$GITHUB_ACTION_PATH\/run-schema-capture.sh"/);
  });
  test("bootstrap captures twice, never evaluates Registry", () => { const r = run(); assert.equal(r.status, 0, r.stderr); assert.equal(r.calls.split("capture-schema").length - 1, 2); assert.ok(!r.calls.includes("evaluate-database-state")); assert.match(r.outputs, /registry_status=NOT_EVALUATED/); assert.match(r.outputs, /gate_status=BLOCKED/); assert.match(r.outputs, /gate_reason=NEW_EF_CERTIFICATION_NOT_EVALUATED/); });
  test("supplied Registry context rejected before capture/evaluation", () => { const r = run({ registryContext: true }); assert.equal(r.status, 2, r.stderr); assert.equal(r.calls, ""); assert.ok(!r.outputs.includes("NOT_EVALUATED")); });
  for (const failure of ["capture", "determinism"]) test(`${failure} failure never becomes intentional NOT_EVALUATED`, () => { const r = run({ failure }); assert.notEqual(r.status, 0); assert.ok(!r.outputs.includes("NOT_EVALUATED")); assert.ok(!r.calls.includes("evaluate-database-state")); });
  test("capture failure publishes only sanitized TLS fingerprint", () => { const r = run({ failure: "capture" }); const value = JSON.parse(r.tlsDiagnostic); assert.equal(value.captureId, "capture-1"); assert.equal(value.diagnosticFingerprint.sqlExceptionNumber, 0); assert.equal(value.diagnosticFingerprint.tlsFailureCategory, "TRANSPORT_OTHER"); for (const forbidden of ["password", "token", "connectionString", "message", "stackTrace"]) assert.equal(r.tlsDiagnostic.toLowerCase().includes(forbidden.toLowerCase()), false); });
  test("schema capture publishes explicit encrypted TLS policy", () => { const r = run({ ordinary: true }); const value = JSON.parse(r.tlsEvidence); assert.equal(value.tls.tlsRequestedMode, "TEST_UNTRUSTED_CERTIFICATE"); assert.equal(value.tls.tlsEffectiveMode, "TEST_UNTRUSTED_CERTIFICATE"); assert.equal(value.tls.tlsPolicySource, "EXPLICIT_TEST_CONFIGURATION"); assert.equal(value.tls.tlsFallbackAttempted, false); assert.equal(value.tls.transportEncrypted, true); });
  test("ordinary producer still evaluates and preserves BASELINE_REQUIRED", () => { const r = run({ ordinary: true }); assert.equal(r.status, 0, r.stderr); assert.match(r.calls, /evaluate-database-state/); assert.match(r.outputs, /registry_status=BASELINE_REQUIRED/); assert.ok(!r.outputs.includes("registry_status=NOT_EVALUATED")); });
  console.log(`OK ${passed} isolated producer cases`);
} finally {
  const resolved = path.resolve(temp), expected = path.resolve(tempRoot);
  assert.ok(resolved.startsWith(expected + path.sep) && path.basename(resolved).startsWith("new-ef-producer-"));
  fs.rmSync(resolved, { recursive: true, force: true });
}
