import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const source = readFileSync(new URL('../../../../Parrot.SharedCompnents/wwwroot/js/feedback.js', import.meta.url), 'utf8');
function createFeedback({ failRead = false, failWrite = false, ignoreWrite = false } = {}) {
  const stored = new Map();
  const window = {};
  runInNewContext(source, {
    window,
    navigator: {},
    performance: { now: () => 100 },
    localStorage: {
      getItem(key) { if (failRead) throw new Error('Storage blocked'); return stored.get(key) ?? null; },
      setItem(key, value) { if (failWrite) throw new Error('Storage full'); if (!ignoreWrite) stored.set(key, value); }
    }
  });
  return { feedback: window.parrotFeedback, stored };
}

test('a saved choice is acknowledged and restored on storage', () => {
  const { feedback, stored } = createFeedback();
  const result = feedback.setEnabled(false);
  assert.equal(result.applied, true);
  assert.equal(result.saved, true);
  assert.equal(result.enabled, false);
  assert.equal(feedback.isEnabled(), false);
  assert.equal(stored.get('parrot-feedback'), 'off');
});

for (const mode of [{ failWrite: true }, { failRead: true }, { ignoreWrite: true }]) {
  test(`storage failure never claims persistence: ${JSON.stringify(mode)}`, () => {
    const { feedback } = createFeedback(mode);
    for (const desired of [false, true]) {
      const result = feedback.setEnabled(desired);
      assert.equal(result.applied, true);
      assert.equal(result.saved, false);
      assert.equal(result.enabled, desired);
      assert.equal(feedback.isEnabled(), desired); // The session still honours the choice.
    }
  });
}
