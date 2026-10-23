import assert from 'node:assert/strict';
import { adaptEvidence } from '../scripts/adapt-classification-evidence-v2.mjs';
import { TECHNICAL_CATEGORIES } from '../scripts/empty-for-new-ef-v1.mjs';

const ids = ['20260101120000_Init', '20260201120000_Next'];
function raw(initial = false) {
  return { contractVersion: 2, efInitialHistoryPolicy: initial ? 'HISTORY_ABSENT_V1' : 'NOT_VERIFIED',
    declarations: { databaseLifecycle: initial ? 'NEW' : 'EXISTING', changeManagementMode: 'EF_MIGRATIONS' },
    connection: { status: 'SUCCEEDED' }, databaseLookup: { status: 'FOUND' }, targetConnection: { status: 'SUCCEEDED' },
    metadata: { status: 'SUFFICIENT' }, physical: { status: 'OBSERVED', businessObjectCount: initial ? 0 : 1,
      technicalObjectCount: 0, taxonomy: { version: 1, coverage: 'COMPLETE', counts: Object.fromEntries(TECHNICAL_CATEGORIES.map(x => [x, 0])) } },
    history: initial ? { status: 'ABSENT' } : { status: 'PRESENT', migrationCount: 1, migrationIds: ids.slice(0, 1) },
    repository: { status: 'PRESENT_VALID', migrations: { count: 2, ids } },
    registry: { status: initial ? 'NOT_EVALUATED' : 'CERTIFIED' },
    schema: { status: initial ? 'NOT_EVALUATED' : 'CONSISTENT' }, onboarding: { status: initial ? 'NOT_REQUIRED' : 'MANAGED' } };
}
let count = 0;
function check(input, variant, reason) {
  const r = adaptEvidence(input);
  assert.equal(r.exitCode, 0, JSON.stringify(r));
  assert.equal(r.result.classificationResult.ef.variant, variant);
  assert.equal(r.result.classificationResult.ef.reason, reason);
  assert.equal(r.result.classificationResult.ef.executionAuthorized, false);
  count++;
}
let x = raw(true);
x.databaseLookup.status = 'NOT_FOUND'; x.targetConnection.status = 'NOT_ATTEMPTED';
x.metadata.status = 'NOT_ATTEMPTED'; x.physical = { status: 'NOT_ATTEMPTED' }; x.history = { status: 'NOT_ATTEMPTED' };
check(x, 'V1', 'DATABASE_PROVISIONING_REQUIRED');
check(raw(true), 'V2', 'INITIAL_MIGRATIONS_PENDING');
x = raw(true); x.history = { status: 'PRESENT', migrationCount: 0, migrationIds: [] }; x.efInitialHistoryPolicy = 'EMPTY_HISTORY_ATTESTED_V1';
check(x, 'V3', 'EMPTY_HISTORY_INITIAL_STATE');
x.efInitialHistoryPolicy = 'NOT_VERIFIED'; check(x, 'V6', 'INITIAL_HISTORY_STATE_UNVERIFIED');
check(raw(), 'V4', 'PENDING_MIGRATIONS');
x = raw(); x.history = { status: 'PRESENT', migrationCount: 2, migrationIds: ids }; check(x, 'V5', 'NO_CHANGES');
x = raw(); x.schema.status = 'DRIFT_DETECTED'; check(x, 'V6', 'BLOCKED_PHYSICAL_DRIFT');
x = raw(); x.history.migrationIds = [ids[1]]; check(x, 'V6', 'BLOCKED_HISTORY_DIVERGED');
for (const failure of ['TIMEOUT', 'CANCELLED', 'FAILED']) {
  x = raw(true); x.connection.status = failure; x.databaseLookup.status = 'UNKNOWN'; x.targetConnection.status = 'NOT_ATTEMPTED';
  x.metadata.status = 'UNKNOWN'; x.physical = { status: 'UNKNOWN' }; x.history = { status: 'UNKNOWN' };
  const r = adaptEvidence(x); assert.notEqual(r.exitCode, 0); assert.equal(r.result.classificationInvoked, false); count++;
}
x = raw(); x.history = { status: 'PRESENT', migrationCount: 2, migrationIds: [ids[0], ids[0]] };
assert.notEqual(adaptEvidence(x).exitCode, 0); count++;
console.log(`PASS: EF unified classifier ${count} directed cases (no authority or SQL)`);
