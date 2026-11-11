import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

const source = readFileSync(new URL("../../../../Parrot.SharedCompnents/wwwroot/js/tooltip.js", import.meta.url), "utf8").replaceAll("export ", "");
class Events {
  constructor() { this.events = new Map(); }
  addEventListener(name, fn) { if (!this.events.has(name)) this.events.set(name, new Set()); this.events.get(name).add(fn); }
  removeEventListener(name, fn) { this.events.get(name)?.delete(fn); }
  emit(name, event = {}) { for (const fn of [...this.events.get(name) ?? []]) fn(event); }
  count() { return [...this.events.values()].reduce((n, set) => n + set.size, 0); }
}
function environment() {
  const timers = new Map(), frames = new Map(), observers = [];
  let next = 1;
  const win = new Events();
  const context = {
    window: win, document: { documentElement: { clientWidth: 800, clientHeight: 600 } },
    getComputedStyle: () => ({ getPropertyValue: () => "8px" }),
    setTimeout(fn) { const id = next++; timers.set(id, fn); return id; }, clearTimeout: id => timers.delete(id),
    requestAnimationFrame(fn) { const id = next++; frames.set(id, fn); return id; }, cancelAnimationFrame: id => frames.delete(id),
    ResizeObserver: class { constructor(fn) { this.fn = fn; this.items = new Set(); observers.push(this); } observe(item) { this.items.add(item); } disconnect() { this.items.clear(); } }
  };
  runInNewContext(source, context);
  function make(rect = { left: 100, top: 100, bottom: 118, width: 18, height: 18 }, controls = []) {
    const trigger = { rect, querySelectorAll() { return controls; }, getBoundingClientRect() { return this.rect; } };
    const bubble = { id: "controlled-help", isConnected: true, hidden: true, dataset: {}, style: {}, shows: 0, hides: 0,
      getBoundingClientRect() { return { width: 120, height: 28 }; },
      showPopover() { this.shows++; }, hidePopover() { this.hides++; } };
    const root = new Events(); root.isConnected = true; root.inert = false;
    root.querySelector = () => trigger; root.closest = () => root.inert ? {} : null;
    root.contains = target => target === trigger || target === bubble || target === root;
    const binding = context.bindTooltip(root, bubble);
    return { root, trigger, bubble, binding };
  }
  function flush(map) { const callbacks = [...map.values()]; map.clear(); callbacks.forEach(fn => fn()); }
  return { make, win, timers, frames, observers, bind: context.bindTooltip, runTimers: () => flush(timers), runFrames: () => flush(frames) };
}
test("hover waits briefly and leaves the bubble readable while the pointer moves into it", () => {
  const e = environment(), x = e.make(); x.root.emit("pointerenter", { pointerType: "mouse" });
  assert.equal(x.bubble.hidden, true); e.runTimers(); assert.equal(x.bubble.hidden, false);
  x.root.emit("pointerleave", { relatedTarget: x.bubble }); e.runTimers(); assert.equal(x.bubble.hidden, false);
  x.root.emit("pointerleave", { relatedTarget: null }); e.runTimers(); assert.equal(x.bubble.hidden, true);
});
test("focus opens immediately and blur dismisses without moving focus", () => {
  const e = environment(), x = e.make(); x.root.emit("focusin"); assert.equal(x.bubble.hidden, false);
  x.root.emit("focusout", { relatedTarget: x.trigger }); assert.equal(x.bubble.hidden, false);
  x.root.emit("focusout", { relatedTarget: null }); assert.equal(x.bubble.hidden, true);
});
test("Escape closes help before a parent dialog and suppresses reopening until a new interaction", () => {
  const e = environment(), x = e.make(); x.root.emit("focusin"); let prevented = 0, stopped = 0;
  e.win.emit("keydown", { key: "Escape", preventDefault() { prevented++; }, stopImmediatePropagation() { stopped++; } });
  assert.equal(prevented, 1); assert.equal(stopped, 1); assert.equal(x.bubble.hidden, true);
  x.root.emit("focusin"); assert.equal(x.bubble.hidden, true);
  x.root.emit("focusout", { relatedTarget: null }); x.root.emit("focusin"); assert.equal(x.bubble.hidden, false);
});
test("placement flips below at the top edge and stays within either horizontal edge", () => {
  const e = environment(), x = e.make({ left: 0, top: 0, bottom: 18, width: 18, height: 18 });
  x.root.emit("focusin"); assert.equal(x.bubble.dataset.side, "below"); assert.equal(x.bubble.style.top, "26px"); assert.equal(x.bubble.style.left, "8px");
  x.trigger.rect = { left: 790, top: 400, bottom: 418, width: 18, height: 18 }; e.win.emit("resize"); e.runFrames();
  assert.equal(x.bubble.dataset.side, "above"); assert.equal(x.bubble.style.left, "672px");
});
test("only one shared tooltip stays open", () => {
  const e = environment(), a = e.make(), b = e.make(); a.root.emit("focusin"); b.root.emit("focusin");
  assert.equal(a.bubble.hidden, true); assert.equal(b.bubble.hidden, false);
});
test("release closes top-layer help, cancels pending work and removes every listener", () => {
  const e = environment(), x = e.make(); x.root.emit("focusin"); e.win.emit("scroll");
  x.binding.release(); x.binding.release(); e.runFrames(); e.runTimers();
  assert.equal(x.bubble.hidden, true); assert.equal(e.win.count(), 0); assert.equal(x.root.count(), 0);
  assert.equal(e.frames.size, 0); assert.equal(e.timers.size, 0); assert.ok(e.observers.every(o => o.items.size === 0));
});
test("release before a delayed show cannot reopen an abandoned tooltip", () => {
  const e = environment(), x = e.make(); x.root.emit("pointerenter", {}); x.binding.release(); e.runTimers();
  assert.equal(x.bubble.shows, 0);
});
test("a detached trigger releases its observers on the next positioning check", () => {
  const e = environment(), x = e.make(); x.root.emit("focusin"); x.root.isConnected = false; e.win.emit("scroll"); e.runFrames();
  assert.equal(x.bubble.hidden, true); assert.equal(e.win.count(), 0); assert.equal(x.root.count(), 0);
});
test("background controls made inert by a modal never show help", () => {
  const e = environment(), x = e.make(); x.root.inert = true; x.root.emit("focusin"); assert.equal(x.bubble.hidden, true);
});
test("touch does not create a sticky hover bubble", () => {
  const e = environment(), x = e.make(); x.root.emit("pointerenter", { pointerType: "touch" }); e.runTimers();
  assert.equal(x.bubble.hidden, true); assert.equal(e.timers.size, 0);
});
test("missing nodes are a harmless no-op and ordinary keys do not close help", () => {
  const e = environment(); e.bind(null, null).release(); const x = e.make(); x.root.emit("focusin");
  e.win.emit("keydown", { key: "Tab" }); assert.equal(x.bubble.hidden, false);
});
test("existing controls receive the description without losing their original hints", () => {
  const e = environment(), attributes = new Map([["aria-describedby", "existing-hint"]]);
  const control = { getAttribute: key => attributes.get(key), setAttribute: (key, value) => attributes.set(key, value), removeAttribute: key => attributes.delete(key) };
  const x = e.make(undefined, [control]); assert.equal(attributes.get("aria-describedby"), "existing-hint controlled-help");
  attributes.set("aria-describedby", attributes.get("aria-describedby") + " new-hint");
  x.binding.release(); assert.equal(attributes.get("aria-describedby"), "existing-hint new-hint");
});
test("a pointer action dismisses help without blocking the underlying action", () => {
  const e = environment(), x = e.make(); x.root.emit("focusin"); e.win.emit("pointerdown");
  assert.equal(x.bubble.hidden, true);
});
test("scrolling an anchor out of view dismisses help rather than leaving it floating", () => {
  const e = environment(), x = e.make(); x.root.emit("focusin"); x.trigger.rect = { left: 100, top: -30, bottom: -12, width: 18, height: 18 };
  e.win.emit("scroll"); e.runFrames(); assert.equal(x.bubble.hidden, true);
});
