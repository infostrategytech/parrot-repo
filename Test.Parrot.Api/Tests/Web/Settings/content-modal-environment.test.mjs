import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const source = readFileSync(new URL('../../../../Parrot.SharedCompnents/wwwroot/js/aurora-interop.js', import.meta.url), 'utf8');
const helpers = source.slice(source.indexOf('// Content-modal environment only.'), source.indexOf('// ── Media queries')).replaceAll('export ', '');

class Element {
  constructor(tagName = 'DIV', overflow = '') { this.tagName = tagName; this.children = []; this.parentElement = null; this.attrs = new Map(); this.computedOverflow = overflow; this.isConnected = true; this.styles = new Map(); this.style = { getPropertyValue: k => this.styles.get(k)?.value ?? '', getPropertyPriority: k => this.styles.get(k)?.priority ?? '', setProperty: (k, value, priority = '') => this.styles.set(k, { value, priority }), removeProperty: k => this.styles.delete(k) }; }
  add(child) { child.parentElement = this; this.children.push(child); return child; }
  getAttribute(key) { return this.attrs.get(key) ?? null; }
  setAttribute(key, value) { this.attrs.set(key, value); }
  removeAttribute(key) { this.attrs.delete(key); }
}
function setup() {
  const html = new Element('HTML'); const body = html.add(new Element('BODY'));
  const page = body.add(new Element('MAIN', 'auto')); const background = page.add(new Element());
  const backdrop = page.add(new Element()); const surface = backdrop.add(new Element('SECTION'));
  const script = body.add(new Element('SCRIPT'));
  const context = { document: { documentElement: html, body }, getComputedStyle: el => ({ overflowY: el.computedOverflow }) };
  runInNewContext(helpers, context);
  return { html, body, page, background, backdrop, surface, script, hold: context.holdModalEnvironment };
}
test('a content modal makes background branches inert, not itself or its ancestors', () => {
  const x = setup(); const lease = x.hold(x.surface);
  assert.equal(x.background.getAttribute('inert'), ''); assert.equal(x.surface.getAttribute('inert'), null);
  assert.equal(x.backdrop.getAttribute('inert'), null); assert.equal(x.page.getAttribute('inert'), null);
  assert.equal(x.script.getAttribute('inert'), null); lease.release(); assert.equal(x.background.getAttribute('inert'), null);
});
test('background scrolling is locked and previous inline styles are restored exactly', () => {
  const x = setup(); x.body.style.setProperty('overflow', 'clip', 'important');
  const lease = x.hold(x.surface); assert.equal(x.body.style.getPropertyValue('overflow'), 'hidden'); assert.equal(x.page.style.getPropertyValue('overflow'), 'hidden');
  lease.release(); assert.equal(x.body.style.getPropertyValue('overflow'), 'clip'); assert.equal(x.body.style.getPropertyPriority('overflow'), 'important');
  assert.equal(x.page.style.getPropertyValue('overflow'), ''); assert.equal(x.html.style.getPropertyValue('overflow'), '');
});
test('pre-existing inert is preserved and release is idempotent', () => {
  const x = setup(); x.background.setAttribute('inert', 'inert'); const lease = x.hold(x.surface);
  lease.release(); lease.release(); assert.equal(x.background.getAttribute('inert'), 'inert');
});
test('nested leases do not unlock shared background before the last modal closes', () => {
  const x = setup(); const first = x.hold(x.surface); const nestedBackdrop = x.backdrop.add(new Element()); const nested = nestedBackdrop.add(new Element('SECTION'));
  const second = x.hold(nested); first.release(); assert.equal(x.body.style.getPropertyValue('overflow'), 'hidden'); assert.equal(x.background.getAttribute('inert'), '');
  second.release(); assert.equal(x.body.style.getPropertyValue('overflow'), ''); assert.equal(x.background.getAttribute('inert'), null);
});
test('a later confirmation can open without being made inert by its parent modal', () => {
  const x = setup(); const lease = x.hold(x.surface); const confirmation = x.page.add(new Element('SECTION'));
  assert.equal(confirmation.getAttribute('inert'), null); lease.release();
});
test('a surface removed before interop runs is a harmless no-op', () => {
  const x = setup(); x.surface.isConnected = false; x.hold(x.surface).release();
  assert.equal(x.body.style.getPropertyValue('overflow'), ''); assert.equal(x.background.getAttribute('inert'), null);
  x.hold(null).release();
});
