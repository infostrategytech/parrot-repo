import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const source = readFileSync(new URL('../../../../Parrot.SharedCompnents/wwwroot/js/aurora-interop.js', import.meta.url), 'utf8');
const helpers = source.slice(source.indexOf('export function saveTextFile('), source.indexOf('export async function downloadFile(')).replaceAll('export ', '');

function downloads({ blocked = false, brokenClick = false } = {}) {
  const state = { clicks: 0, appended: 0, removed: 0, revoked: [], parts: null, link: null };
  const context = {
    Blob: class { constructor(parts, options) { state.parts = parts; assert.equal(options.type, 'text/plain;charset=utf-8'); } },
    URL: { createObjectURL() { if (blocked) throw new Error('Blocked'); return 'blob:controlled'; }, revokeObjectURL(url) { state.revoked.push(url); } },
    document: {
      body: { appendChild() { state.appended++; } },
      createElement(name) { assert.equal(name, 'a'); return state.link = { click() { if (brokenClick) throw new Error('Blocked'); state.clicks++; }, remove() { state.removed++; } }; }
    },
    setTimeout(callback) { callback(); },
    fetch() { throw new Error('Security codes must not be sent to a remote file service'); }
  };
  runInNewContext(helpers, context);
  return { context, state };
}

test('text downloads stay local and release their temporary URL', () => {
  const { context, state } = downloads();
  assert.equal(context.saveTextFile('controlled codes', 'yára-recovery-codes.txt'), true);
  assert.equal(state.parts.join(''), 'controlled codes');
  assert.equal(state.link.download, 'yára-recovery-codes.txt');
  assert.equal(state.link.href, 'blob:controlled');
  assert.equal(state.clicks, 1);
  assert.equal(state.removed, 1);
  assert.deepEqual(state.revoked, ['blob:controlled']);
});

test('blocked file creation reports failure without leaving DOM or URLs', () => {
  const { context, state } = downloads({ blocked: true });
  assert.equal(context.saveTextFile('controlled codes', 'codes.txt'), false);
  assert.equal(state.appended, 0);
  assert.deepEqual(state.revoked, []);
});

test('a blocked download click still cleans up the temporary link and URL', () => {
  const { context, state } = downloads({ brokenClick: true });
  assert.equal(context.saveTextFile('controlled codes', 'codes.txt'), false);
  assert.equal(state.removed, 1);
  assert.deepEqual(state.revoked, ['blob:controlled']);
});

function focus({ outside = false, disabled = false, missing = false } = {}) {
  const doc = { activeElement: null };
  const target = { disabled, scrolled: false, focus() { doc.activeElement = target; }, scrollIntoView() { this.scrolled = true; } };
  doc.getElementById = () => missing ? null : target;
  const context = { document: doc };
  runInNewContext(helpers, context);
  return { context, target, container: { contains(node) { return !outside && node === target; } } };
}

test('step focus only moves to an enabled control within its own dialog', () => {
  const { context, target, container } = focus();
  assert.equal(context.focusWithin(container, 'controlled-id'), true);
  assert.equal(context.document.activeElement, target);
  assert.equal(target.scrolled, true);
});

for (const options of [{ outside: true }, { disabled: true }, { missing: true }]) {
  test(`step focus does not leak outside or into unavailable controls: ${JSON.stringify(options)}`, () => {
    const { context, container } = focus(options);
    assert.equal(context.focusWithin(container, 'controlled-id'), false);
    assert.equal(context.document.activeElement, null);
  });
}
