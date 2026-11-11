import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const source = readFileSync(new URL('../../../../Parrot.SharedCompnents/wwwroot/js/aurora-interop.js', import.meta.url), 'utf8');
const copy = source.slice(source.indexOf('export async function copyText('), source.indexOf('export function saveTextFile(')).replace('export ', '');
test('clipboard success is explicitly acknowledged', async () => {
  const context = { navigator: { clipboard: { writeText: async () => {} } } };
  runInNewContext(copy, context);
  assert.equal(await context.copyText('controlled code'), true);
});
test('denied clipboard access cannot report Copied', async () => {
  const context = { navigator: { clipboard: { writeText: async () => { throw new Error('Denied'); } } } };
  runInNewContext(copy, context);
  assert.equal(await context.copyText('controlled code'), false);
});
test('an unavailable clipboard returns a recoverable failure', async () => {
  const context = { navigator: {} };
  runInNewContext(copy, context);
  assert.equal(await context.copyText('controlled code'), false);
});
