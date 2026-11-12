import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

const source = readFileSync(new URL("../../../../Parrot.SharedCompnents/wwwroot/js/aurora-interop.js", import.meta.url), "utf8");
const code = source.slice(source.indexOf("// Content-modal scrollbar alignment."), source.indexOf("// ── Media queries")).replaceAll("export ", "");
const property = "--ds-content-modal-scrollbar";
function setup(offsetWidth = 560, clientWidth = 545) {
  const styles = new Map(), frames = new Map(), listeners = new Map(), observers = [];
  let next = 1, writes = 0;
  const element = { isConnected: true, offsetWidth, clientWidth, style: {
    getPropertyValue: key => styles.get(key)?.value ?? "",
    getPropertyPriority: key => styles.get(key)?.priority ?? "",
    setProperty(key, value, priority = "") { styles.set(key, { value, priority }); writes++; },
    removeProperty(key) { styles.delete(key); }
  }};
  const context = {
    window: { addEventListener: (name, fn) => listeners.set(name, fn), removeEventListener: name => listeners.delete(name) },
    requestAnimationFrame: fn => { const id = next++; frames.set(id, fn); return id; },
    cancelAnimationFrame: id => frames.delete(id),
    ResizeObserver: class { constructor(fn) { this.fn = fn; this.active = false; observers.push(this); } observe() { this.active = true; } disconnect() { this.active = false; } }
  };
  runInNewContext(code, context);
  return { element, styles, frames, listeners, observers, bind: context.balanceModalGutter,
    width: () => styles.get(property)?.value, writes: () => writes,
    resize() { listeners.get("resize")?.(); },
    flush() { const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(fn => fn()); } };
}
test("a classic reserved gutter is measured even before a short form needs scrolling", () => {
  const x = setup(); x.bind(x.element); assert.equal(x.width(), "15px");
  // 32px of end padding, less 15px compensation plus the 15px native gutter, remains 32px.
  assert.equal(32 - parseInt(x.width(), 10) + 15, 32);
});
test("overlay scrollbars and a zero-width gutter do not reduce the design-system padding", () => {
  const x = setup(560, 560); x.bind(x.element); assert.equal(x.width(), "0px");
});
test("changes from a native to an overlay scrollbar update without growing negative compensation", () => {
  const x = setup(); x.bind(x.element); x.element.clientWidth = 560; x.resize(); x.flush(); assert.equal(x.width(), "0px");
  x.element.clientWidth = 561; x.resize(); x.flush(); assert.equal(x.width(), "0px");
});
test("resize notifications share one frame and do not write an unchanged measurement repeatedly", () => {
  const x = setup(); x.bind(x.element); const writes = x.writes();
  x.resize(); x.resize(); x.observers[0].fn(); assert.equal(x.frames.size, 1); x.flush(); assert.equal(x.writes(), writes);
});
test("release cancels pending work, detaches observers and restores an existing inline value exactly", () => {
  const x = setup(); x.element.style.setProperty(property, "7px", "important");
  const handle = x.bind(x.element); x.resize(); handle.release(); handle.release(); x.flush();
  assert.deepEqual(x.styles.get(property), { value: "7px", priority: "important" });
  assert.equal(x.frames.size, 0); assert.equal(x.listeners.size, 0); assert.ok(x.observers.every(o => !o.active));
});
test("an initially absent variable is removed rather than leaving a stale width for the next open", () => {
  const x = setup(); const handle = x.bind(x.element); handle.release(); assert.equal(x.styles.has(property), false);
});
test("removed or missing bodies are harmless and release active subscriptions", () => {
  const x = setup(); x.bind(null).release(); const handle = x.bind(x.element); x.element.isConnected = false; x.resize(); x.flush();
  assert.equal(x.listeners.size, 0); assert.equal(x.styles.has(property), false); handle.release();
  x.bind(x.element).release(); assert.equal(x.listeners.size, 0);
});
