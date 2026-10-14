import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { test, after } from 'node:test';
import { validatePlanResult, readPlanArtifacts, childEnvironment } from '../scripts/run-ef-release-plan.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ef-plan-adapter-test-'));
after(() => {
  assert.equal(path.dirname(tmp), path.resolve(os.tmpdir()));
  assert.match(path.basename(tmp), /^ef-plan-adapter-test-/u);
  fs.rmSync(tmp, { recursive: true });
});
const hash = 'a'.repeat(64);
const blocked = { contractVersion: 1, operation: 'PLAN', status: 'BLOCKED', reasons: ['EXECUTED_HOST_ATTESTATION_REQUIRED'], verifiedGitSources: [], executionAuthorized: false, certified: false };
const noChanges = { ...blocked, status: 'NO_CHANGES', reasons: [], evidenceSetHash: hash };
const stringify = JSON.stringify;
const run = (exe, args, env = process.env) => spawnSync(exe, args, { cwd: root, env, encoding: 'utf8', windowsHide: true, timeout: 60000, maxBuffer: 1048576 });

for (const [status, code] of Object.entries({ INVALID: 65, BLOCKED: 66, ERROR: 70, TIMEOUT: 124, CANCELLED: 130 })) {
  test(`preserves ${status} and code ${code}, never accepts it as success`, () => {
    const value = { ...blocked, status };
    assert.deepEqual(validatePlanResult(stringify(value), code), value);
    assert.throws(() => validatePlanResult(stringify(value), 0));
  });
}
test('NO_CHANGES requires evidence hash, no plan and no write authority', () => {
  assert.deepEqual(validatePlanResult(stringify(noChanges), 0), noChanges);
  for (const change of [{ evidenceSetHash: null }, { planHash: hash }, { executionAuthorized: true }, { certified: true }, { operation: 'REVALIDATE' }, { reasons: ['BLOCK'] }])
    assert.throws(() => validatePlanResult(stringify({ ...noChanges, ...change }), 0));
});
test('rejects partial output, duplicate keys, unknown fields, credentials and invalid Git receipts', () => {
  for (const text of ['{', 'null', stringify(blocked).replace('{', '{"status":"NO_CHANGES",'),
    stringify({ ...blocked, password: 'SECRET_SENTINEL' }), stringify({ ...blocked, reasons: ['private connection string'] }),
    stringify({ ...blocked, verifiedGitSources: [{ repository: 'other/repo', revision: 'b'.repeat(40), path: 'private.json', sha256: hash }] })])
    assert.throws(() => validatePlanResult(text, 66));
});
test('environment strips SQL/Owner/restore credentials and runtime/plugin injection', () => {
  const stripped = childEnvironment({ PATH: '/trusted/bin', EF_GOVERNANCE_READ_TOKEN: 'fake-git', GITHUB_SHA: 'a'.repeat(40),
    EF_TEST_MUTATION_CONNECTION_STRING: 'OWNER', DB_CONNECTION: 'SQL', SQL_SERVER_CONNECTION: 'SQL', NUGET_AUTH_TOKEN: 'BUILD',
    DOTNET_STARTUP_HOOKS: 'bad.dll', DOTNET_ADDITIONAL_DEPS: 'bad', NODE_OPTIONS: '--import evil', EF_PROVIDER_ASSEMBLY: 'fake', EF_HOST_AUTHENTICATED: 'true' });
  assert.deepEqual(stripped, { PATH: '/trusted/bin', GITHUB_SHA: 'a'.repeat(40), EF_GOVERNANCE_READ_TOKEN: 'fake-git' });
});
test('requires completion marker and stdout parity; denies extra files and hard links', () => {
  const dir = path.join(tmp, 'artifacts'); fs.mkdirSync(dir);
  assert.throws(() => readPlanArtifacts(dir, 66, stringify(blocked)));
  const result = path.join(dir, 'ef-result.json'); fs.writeFileSync(result, stringify(blocked));
  assert.deepEqual(readPlanArtifacts(dir, 66, stringify(blocked)), blocked);
  assert.throws(() => readPlanArtifacts(dir, 0, stringify(noChanges)));
  fs.writeFileSync(path.join(dir, 'raw.json'), 'SECRET_SENTINEL');
  assert.throws(() => readPlanArtifacts(dir, 66, stringify(blocked)));
  fs.unlinkSync(path.join(dir, 'raw.json'));
  fs.linkSync(result, path.join(tmp, 'linked-result'));
  assert.throws(() => readPlanArtifacts(dir, 66, stringify(blocked)));
});

const testDll = path.join(root, 'tests/DatabaseReleaseQualification.Tests/bin/Release/net10.0/DatabaseReleaseQualification.Tests.dll');
for (const mode of ['PLAN', 'NO_CHANGES']) {
  test(`consumes real CLI ${mode} output from test-only authorized provider (offline)`, () => {
    const directory = path.join(tmp, mode); const request = path.join(tmp, `${mode}-request.json`);
    const result = run('dotnet', [testDll, '--ef-host-child', mode, request, directory], childEnvironment(process.env));
    assert.equal(result.status, 0, result.stderr + result.stdout);
    const checked = readPlanArtifacts(directory, result.status, result.stdout);
    assert.equal(checked.status, mode === 'PLAN' ? 'PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED' : 'NO_CHANGES');
    if (mode === 'PLAN') {
      const review = JSON.parse(fs.readFileSync(path.join(directory, 'ef-plan-review.json'), 'utf8'));
      assert.throws(() => validatePlanResult(result.stdout, 0, stringify({ ...review, planHash: 'b'.repeat(64) })));
      assert.throws(() => validatePlanResult(result.stdout, 0, stringify({ ...review, sql: 'SECRET_SENTINEL' })));
      assert.throws(() => validatePlanResult(result.stdout, 0, stringify({ ...review, finalRisk: null })));
      assert.throws(() => validatePlanResult(result.stdout, 0, stringify({ ...review, recovery: { ...review.recovery, exerciseVerified: false } })));
    }
  });
}
test('production adapter invokes fixed real CLI, preserves BLOCKED and publishes only checked result', () => {
  const source = JSON.parse(fs.readFileSync(path.join(tmp, 'PLAN-request.json'), 'utf8'));
  source.actionsRevision = run('git', ['rev-parse', 'HEAD']).stdout.trim();
  const request = path.join(tmp, 'production-request.json'); fs.writeFileSync(request, stringify(source));
  const output = path.join(tmp, 'github-output');
  const result = run('node', ['scripts/run-ef-release-plan.mjs'], { ...childEnvironment(process.env), GITHUB_ACTIONS: 'false',
    EF_PLAN_REQUEST: request, RUNNER_TEMP: tmp, GITHUB_OUTPUT: output, EF_PROVIDER_ASSEMBLY: testDll, EF_TEST_MODE: 'PLAN' });
  assert.equal(result.status, 66, result.stderr + result.stdout);
  assert.equal(result.stderr, '');
  assert.deepEqual(JSON.parse(result.stdout).reasons, ['RUN_CONTEXT_UNVERIFIED']);
  const outputs = fs.readFileSync(output, 'utf8'); assert.match(outputs, /artifacts-ready=true/u);
  assert.match(outputs, /execution-authorized=false/u); assert.match(outputs, /exit-code=66/u);
  const directory = outputs.split('\n').find(x => x.startsWith('artifact-directory=')).slice('artifact-directory='.length);
  assert.deepEqual(fs.readdirSync(directory), ['ef-result.json']);
  for (const mutation of [{ apply: true }, { operation: 'APPLY' }, { environment: 'PROD' }, { operation: 'REVALIDATE' }]) {
    fs.writeFileSync(request, stringify({ ...source, ...mutation })); fs.writeFileSync(output, '');
    const denied = run('node', ['scripts/run-ef-release-plan.mjs'], { ...childEnvironment(process.env), GITHUB_ACTIONS: 'false',
      EF_PLAN_REQUEST: request, RUNNER_TEMP: tmp, GITHUB_OUTPUT: output });
    assert.equal(denied.status, 65); assert.equal(denied.stdout, '');
    assert.equal(denied.stderr, 'EF_PLAN_REQUEST_INVALID\n');
    assert.equal(fs.readFileSync(output, 'utf8'), 'artifacts-ready=false\n');
  }
  fs.writeFileSync(request, stringify({ ...source, actionsRevision: 'd'.repeat(40) }));
  const mismatch = run('node', ['scripts/run-ef-release-plan.mjs'], { ...childEnvironment(process.env), EF_PLAN_REQUEST: request });
  assert.equal(mismatch.status, 66); assert.equal(mismatch.stderr, 'EF_PLAN_CHECKOUT_MISMATCH\n'); assert.equal(mismatch.stdout, '');
});
test('composite only invokes PLAN adapter and never builds under credentials or invokes SQL/APPLY', () => {
  const action = fs.readFileSync(path.join(root, 'ef-release-plan/action.yml'), 'utf8');
  assert.match(action, /run-ef-release-plan\.mjs/u);
  assert.doesNotMatch(action, /continue-on-error|dotnet build|dotnet restore|execute-ef|ef-test-plan\.sh|keyvault|environment:|secrets: inherit|\bapply:/iu);
});
