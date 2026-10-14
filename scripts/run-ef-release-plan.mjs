import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const hash = value => typeof value === 'string' && /^[0-9a-f]{64}$/u.test(value);
const sha = value => typeof value === 'string' && /^[0-9a-f]{40}$/u.test(value);
const repository = value => typeof value === 'string' && /^[A-Za-z0-9_-][A-Za-z0-9_.-]*\/[A-Za-z0-9_-][A-Za-z0-9_.-]*$/u.test(value) && value.length <= 200;
const exact = (value, keys) => value !== null && typeof value === 'object' && !Array.isArray(value)
  && Object.keys(value).sort().join(',') === [...keys].sort().join(',');
const fields = (value, required, optional) => exact(value, [...required, ...optional.filter(key => Object.hasOwn(value ?? {}, key))]);
const requireValue = (ok, code) => { if (!ok) throw new Error(code); };
const codes = { PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED: 0, NO_CHANGES: 0, INVALID: 65, BLOCKED: 66, ERROR: 70, TIMEOUT: 124, CANCELLED: 130 };
const adapterReasons = new Set(['EF_PLAN_RESULT_INVALID', 'EF_PLAN_REVIEW_REQUIRED', 'EF_PLAN_REVIEW_INVALID',
  'EF_PLAN_PATH_INVALID', 'EF_PLAN_FILE_INVALID', 'EF_PLAN_ARTIFACTS_INVALID', 'EF_PLAN_OUTPUT_MISMATCH',
  'EF_PLAN_REQUEST_INVALID', 'EF_PLAN_TIMEOUT_INVALID', 'EF_PLAN_CHECKOUT_MISMATCH', 'EF_PLAN_ENGINE_REQUIRED',
  'EF_PLAN_TEMP_REQUIRED', 'EF_PLAN_PROCESS_INCOMPLETE']);

// The existing CLI emits compact ASCII JSON for these sanitized contracts. The
// round trip also rejects duplicate keys, alternate encodings and partial JSON.
function compactJson(text) {
  requireValue(typeof text === 'string' && Buffer.byteLength(text) <= 1048576, 'EF_PLAN_RESULT_INVALID');
  const value = JSON.parse(text);
  requireValue(JSON.stringify(value) === text.trim(), 'EF_PLAN_RESULT_INVALID');
  return value;
}

export function validatePlanResult(text, exitCode, reviewText = null) {
  const r = compactJson(text);
  requireValue(fields(r, ['contractVersion', 'operation', 'status', 'reasons', 'verifiedGitSources', 'executionAuthorized', 'certified'], ['planHash', 'evidenceSetHash'])
    && r.contractVersion === 1 && (r.operation === 'PLAN' || r.operation === 'UNKNOWN' && ['INVALID', 'ERROR', 'TIMEOUT', 'CANCELLED'].includes(r.status))
    && Object.hasOwn(codes, r.status) && codes[r.status] === exitCode
    && r.executionAuthorized === false && r.certified === false
    && Array.isArray(r.reasons) && r.reasons.every(x => typeof x === 'string' && /^[A-Z][A-Z0-9_]{0,95}$/u.test(x))
    && new Set(r.reasons).size === r.reasons.length && (exitCode === 0 ? r.reasons.length === 0 : r.reasons.length > 0)
    && (r.planHash == null || hash(r.planHash)) && (r.evidenceSetHash == null || hash(r.evidenceSetHash))
    && Array.isArray(r.verifiedGitSources) && r.verifiedGitSources.length <= 6
    && r.verifiedGitSources.every(x => exact(x, ['repository', 'revision', 'path', 'sha256'])
      && x.repository === 'infrastructure-services/workflow' && sha(x.revision) && hash(x.sha256)
      && ['resolve-governance-runtime.mjs', 'validate-ef-source.mjs', 'prepare-ef-source-runtime.mjs', 'governance-targets.json', 'onboarding-targets.json', 'targets.json'].some(name => x.path === `database-registry/${name}`)), 'EF_PLAN_RESULT_INVALID');
  requireValue(new Set(r.verifiedGitSources.map(x => x.path)).size === r.verifiedGitSources.length, 'EF_PLAN_RESULT_INVALID');
  if (r.status === 'NO_CHANGES') requireValue(r.planHash == null && hash(r.evidenceSetHash) && reviewText === null, 'EF_PLAN_RESULT_INVALID');
  if (r.status === 'PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED') requireValue(hash(r.planHash) && hash(r.evidenceSetHash), 'EF_PLAN_RESULT_INVALID');
  requireValue((r.planHash != null) === (reviewText !== null), 'EF_PLAN_REVIEW_REQUIRED');
  if (reviewText !== null) {
    const p = compactJson(reviewText);
    requireValue(fields(p, ['contractVersion', 'planHash', 'payloadHash', 'forwardHash', 'rollbackHash', 'appliedCount', 'pendingCount', 'scenario', 'recovery', 'executionAuthorized'], ['finalRisk'])
      && ['BLOCKED', 'PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED'].includes(r.status)
      && p.contractVersion === 1 && p.planHash === r.planHash && p.executionAuthorized === false
      && [p.payloadHash, p.forwardHash, p.rollbackHash].every(hash)
      && Number.isSafeInteger(p.appliedCount) && p.appliedCount >= 0 && Number.isSafeInteger(p.pendingCount) && p.pendingCount > 0
      && ['NEW_EF', 'EXISTING_EF'].includes(p.scenario) && [undefined, null, 'LOW', 'MEDIUM', 'HIGH'].includes(p.finalRisk)
      && exact(p.recovery, ['strategy', 'scriptAvailable', 'potentialDataLoss', 'exerciseVerified'])
      && ['FULL_REVERSIBLE', 'SCHEMA_ONLY', 'RESTORE_REQUIRED', 'FORWARD_FIX_ONLY', 'UNKNOWN'].includes(p.recovery.strategy)
      && [p.recovery.scriptAvailable, p.recovery.potentialDataLoss, p.recovery.exerciseVerified].every(x => typeof x === 'boolean'), 'EF_PLAN_REVIEW_INVALID');
    if (r.status === 'PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED') requireValue(p.finalRisk != null
      && p.recovery.exerciseVerified && p.recovery.strategy !== 'UNKNOWN', 'EF_PLAN_REVIEW_INVALID');
  }
  return r;
}

function noLinks(file) {
  for (let p = path.resolve(file); ; p = path.dirname(p)) {
    try { requireValue(!fs.lstatSync(p).isSymbolicLink(), 'EF_PLAN_PATH_INVALID'); }
    catch (e) { if (e.code !== 'ENOENT') throw e; }
    if (path.dirname(p) === p) break;
  }
}
function readBounded(file, maximum) {
  noLinks(file);
  const stat = fs.statSync(file);
  requireValue(stat.isFile() && stat.nlink === 1 && stat.size > 0 && stat.size <= maximum, 'EF_PLAN_FILE_INVALID');
  return fs.readFileSync(file, 'utf8');
}

export function readPlanArtifacts(directory, exitCode, stdout) {
  noLinks(directory);
  const names = fs.readdirSync(directory);
  requireValue(names.includes('ef-result.json') && names.every(x => ['ef-result.json', 'ef-plan-review.json'].includes(x)), 'EF_PLAN_ARTIFACTS_INVALID');
  const result = readBounded(path.join(directory, 'ef-result.json'), 1048576);
  requireValue(result.trim() === stdout.trim(), 'EF_PLAN_OUTPUT_MISMATCH');
  return validatePlanResult(result, exitCode, names.includes('ef-plan-review.json') ? readBounded(path.join(directory, 'ef-plan-review.json'), 65536) : null);
}

export function childEnvironment(env) {
  // No SQL, Owner, build/restore credentials, runtime injection, or caller plugin
  // selectors enter the trusted CLI subprocess. PATH/runtime remain host trust.
  const names = ['PATH', 'Path', 'SystemRoot', 'WINDIR', 'TEMP', 'TMP', 'TMPDIR', 'HOME', 'USERPROFILE', 'DOTNET_ROOT', 'DOTNET_ROOT_X64',
    'GITHUB_ACTIONS', 'GITHUB_API_URL', 'GITHUB_REPOSITORY', 'GITHUB_SHA', 'GITHUB_REF', 'GITHUB_EVENT_NAME',
    'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'EF_GOVERNANCE_READ_TOKEN'];
  return Object.fromEntries(names.filter(name => typeof env[name] === 'string').map(name => [name, env[name]]));
}

export function runPlan(env = process.env) {
  let exitCode = 65;
  try {
    if (env.GITHUB_OUTPUT) fs.appendFileSync(env.GITHUB_OUTPUT, 'artifacts-ready=false\n');
    const requestPath = path.resolve(env.EF_PLAN_REQUEST);
    const request = JSON.parse(readBounded(requestPath, 65536));
    requireValue(request.operation === 'PLAN' && request.environment === 'TEST' && !Object.hasOwn(request, 'apply')
      && sha(request.actionsRevision) && repository(request.selection?.scope?.sourceRepository), 'EF_PLAN_REQUEST_INVALID');
    const seconds = env.EF_PLAN_TIMEOUT ?? '120';
    requireValue(/^[1-9][0-9]{0,2}$/u.test(seconds) && Number(seconds) <= 900, 'EF_PLAN_TIMEOUT_INVALID');
    const runtime = childEnvironment(env);
    exitCode = 66;
    const gitEnvironment = { ...runtime }; delete gitEnvironment.EF_GOVERNANCE_READ_TOKEN;
    const head = spawnSync('git', ['-C', root, 'rev-parse', 'HEAD'], { env: gitEnvironment, encoding: 'utf8', windowsHide: true, timeout: 10000 });
    requireValue(head.status === 0 && head.stdout.trim() === request.actionsRevision, 'EF_PLAN_CHECKOUT_MISMATCH');
    exitCode = 70;
    // Exact checkout correlation is not attestation of the DLL/build toolchain.
    const engine = path.join(root, 'tools/DatabaseReleaseQualification/bin/Release/net10.0/DatabaseReleaseQualification.dll');
    noLinks(engine); requireValue(fs.statSync(engine).isFile(), 'EF_PLAN_ENGINE_REQUIRED');
    requireValue(typeof env.RUNNER_TEMP === 'string' && path.isAbsolute(env.RUNNER_TEMP)
      && !/[\r\n\0]/u.test(env.RUNNER_TEMP), 'EF_PLAN_TEMP_REQUIRED');
    noLinks(env.RUNNER_TEMP);
    const directory = path.join(env.RUNNER_TEMP, `ef-plan-${randomUUID()}`);
    const child = spawnSync('dotnet', [engine, 'prepare-ef-release', '--request', requestPath, '--output', directory, '--timeout-seconds', seconds],
      { env: runtime, encoding: 'utf8', windowsHide: true, timeout: (Number(seconds) + 10) * 1000, maxBuffer: 1048576, killSignal: 'SIGKILL' });
    if (child.error?.code === 'ETIMEDOUT') exitCode = 124;
    else if (child.signal === 'SIGTERM' || child.signal === 'SIGINT') exitCode = 130;
    else if (Number.isInteger(child.status) && Object.values(codes).includes(child.status)) exitCode = child.status;
    requireValue(!child.error && child.signal === null && child.stderr === '', 'EF_PLAN_PROCESS_INCOMPLETE');
    const result = readPlanArtifacts(directory, child.status, child.stdout);
    if (env.GITHUB_OUTPUT) fs.appendFileSync(env.GITHUB_OUTPUT,
      `status=${result.status}\nreasons-json=${JSON.stringify(result.reasons)}\nexit-code=${child.status}\nexecution-authorized=false\ncertified=false\nartifact-directory=${directory}\nartifacts-ready=true\n`);
    process.stdout.write(`${JSON.stringify(result)}\n`);
    return child.status;
  } catch (error) {
    // Raw child stdout/stderr, request, exception messages and paths stay private.
    // Invalid/partial output must never inherit child exit 0 or publish artifacts.
    const failure = exitCode === 0 ? 70 : exitCode;
    process.stderr.write(`${adapterReasons.has(error?.message) ? error.message : 'EF_PLAN_ADAPTER_FAILED'}\n`);
    return failure;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = runPlan();
