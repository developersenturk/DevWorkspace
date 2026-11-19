import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import { handle } from '../core.mjs';
import { createStore } from '../sql-store.mjs';

const token = 'test-only-'.repeat(6);
const id = '12345678-1234-4123-8123-123456789abc';
function fixture() {
  const database = new DatabaseSync(':memory:');
  database.exec(readFileSync(new URL('../migrations/0001_installations.sql', import.meta.url), 'utf8'));
  let clock = new Date('2026-11-01T12:00:00Z');
  const store = createStore({ run: (sql, ...args) => database.prepare(sql).run(...args), all: (sql, ...args) => database.prepare(sql).all(...args) });
  const send = body => handle(new Request('https://test/v1/heartbeat', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) }), { store, reportToken: token, now: () => clock });
  const summary = async (auth = token) => handle(new Request('https://test/v1/summary', { headers: { authorization: `Bearer ${auth}` } }), { store, reportToken: token, now: () => clock });
  return { send, summary, advance: date => clock = new Date(date), database };
}
test('deduplicates installations, preserves first seen, updates version and active windows', async () => {
  const f = fixture();
  try {
    const payload = { protocol: 1, installationId: id, version: '1.0.0' };
    assert.equal((await f.send(payload)).status, 204);
    assert.equal((await f.send(payload)).status, 204);
    f.advance('2026-11-19T12:00:00Z');
    let summary = await (await f.summary()).json();
    assert.equal(summary.totalInstallations, 1);
    assert.equal(summary.active7Days, 0);
    assert.equal(summary.active30Days, 1);
    await f.send({ ...payload, version: '1.1.0' });
    summary = await (await f.summary()).json();
    assert.equal(summary.active24Hours, 1);
    assert.deepEqual(summary.versions, [{ version: '1.1.0', installations: 1 }]);
    assert.equal(f.database.prepare('SELECT first_seen FROM installations').get().first_seen, '2026-11-01T12:00:00.000Z');
    assert.ok(!JSON.stringify(summary).includes(id));
  } finally { f.database.close(); }
});
test('summary is protected, and rejects malformed, identifying and oversized data', async () => {
  const f = fixture();
  try {
    assert.equal((await f.summary('wrong')).status, 401);
    assert.equal((await f.send({ protocol: 1, installationId: id, version: '1.0', machineName: 'private' })).status, 400);
    assert.equal((await f.send({ protocol: 1, installationId: 'bad', version: '1.0' })).status, 400);
    assert.equal((await f.send({ protocol: 2, installationId: id, version: '1.0' })).status, 400);
    assert.equal((await f.send('x'.repeat(1025))).status, 413);
    assert.equal((await f.send(null)).status, 400);
    assert.equal((await f.send([])).status, 400);
    assert.equal((await (await f.summary()).json()).totalInstallations, 0);
  } finally { f.database.close(); }
});
