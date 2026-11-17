// The only JavaScript in the app. Everything here is something the browser
// exposes and Blazor does not: focus order, media queries, scrolling and
// storage. Nothing about how the product behaves lives in this file.
//
// Loaded as a module via IJSObjectReference, so each export is reached by name
// and there is nothing on window.

const FOCUSABLE = [
  "a[href]",
  "button:not([disabled])",
  "input:not([disabled])",
  "select:not([disabled])",
  "textarea:not([disabled])",
  '[tabindex]:not([tabindex="-1"])'
].join(",");

// ── Focus trap ──────────────────────────────────────────────────────────────
// A dialog that lets Tab wander behind it is not a dialog. React's version
// focused the card and closed on Escape but never trapped or restored focus,
// so tabbing walked into the page underneath and closing left focus nowhere.

// escapeTarget/escapeMethod: an optional .NET callback for Escape. It is
// called only for Escape, never for other keys, so typing in a dialog's
// fields does not cost a server round trip per keystroke (on Blazor Server a
// per-key handler redraws the dialog and can overwrite the field mid-typing).
// The person's own time zone (an IANA id such as "Africa/Lagos"), so "today" is their today.
export function timeZone() {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || null; } catch { return null; }
}

export function trapFocus(container, escapeTarget, escapeMethod) {
  // The dialog can be gone by the time this runs (its page was left, or the
  // session ended, between render and this call). Nothing to trap then; an
  // error here would end the whole circuit.
  if (!container) return { release() {} };

  const previouslyFocused = document.activeElement;

  function focusable() {
    return Array.from(container.querySelectorAll(FOCUSABLE)).filter(
      (el) => el.offsetParent !== null || el === container
    );
  }

  function onKeyDown(event) {
    // A confirmation can sit inside a side panel. Let the innermost dialog own
    // its Escape/Tab keys, so dismissing it never dismisses the customer's panel too.
    const innerDialog = event.target.closest?.('[role="dialog"]');
    if (event.defaultPrevented || (innerDialog && innerDialog !== container && container.contains(innerDialog))) return;
    if (event.key === "Escape") {
      if (escapeTarget && escapeMethod) escapeTarget.invokeMethodAsync(escapeMethod);
      return;
    }
    if (event.key !== "Tab") return;

    const items = focusable();
    if (items.length === 0) {
      event.preventDefault();
      container.focus();
      return;
    }

    const first = items[0];
    const last = items[items.length - 1];
    const active = document.activeElement;

    // Wrap in both directions. Without this the browser walks out of the
    // dialog and into the page behind it, which is invisible to a mouse user
    // and completely disorienting to anyone else.
    if (event.shiftKey && (active === first || active === container)) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && active === last) {
      event.preventDefault();
      first.focus();
    }
  }

  container.addEventListener("keydown", onKeyDown);

  return {
    release() {
      container.removeEventListener("keydown", onKeyDown);
      // Put focus back where the person left it. Skip it if the element has
      // gone from the document, which happens when the dialog deleted the row
      // that opened it.
      if (previouslyFocused && document.contains(previouslyFocused)) {
        previouslyFocused.focus();
      }
    }
  };
}

// Content-modal environment only. Existing Dialogs keep their current behaviour until reviewed.
// Leases preserve author-supplied inert/overflow and remain safe when content modals are stacked.
const modalInertLeases = new WeakMap();
const modalScrollLeases = new WeakMap();
export function holdModalEnvironment(container) {
  if (!container?.isConnected) return { release() {} };
  const inert = new Set();
  const scrollers = new Set([document.documentElement, document.body]);
  for (let branch = container; branch?.parentElement; branch = branch.parentElement) {
    const parent = branch.parentElement;
    for (const sibling of parent.children) {
      if (sibling === branch || ['SCRIPT', 'STYLE', 'LINK'].includes(sibling.tagName)) continue;
      inert.add(sibling);
    }
    if (parent === document.body) break;
    if (/(auto|scroll)/.test(getComputedStyle(parent).overflowY)) scrollers.add(parent);
  }
  for (const element of inert) {
    let lease = modalInertLeases.get(element);
    if (!lease) {
      lease = { count: 0, original: element.getAttribute('inert') };
      modalInertLeases.set(element, lease);
    }
    lease.count++;
    element.setAttribute('inert', '');
  }
  for (const element of scrollers) {
    if (!element) continue;
    let lease = modalScrollLeases.get(element);
    if (!lease) {
      lease = { count: 0, value: element.style.getPropertyValue('overflow'), priority: element.style.getPropertyPriority('overflow') };
      modalScrollLeases.set(element, lease);
    }
    lease.count++;
    element.style.setProperty('overflow', 'hidden');
  }
  let released = false;
  return {
    release() {
      if (released) return;
      released = true;
      for (const element of inert) {
        const lease = modalInertLeases.get(element);
        if (!lease || --lease.count > 0) continue;
        if (lease.original === null) element.removeAttribute('inert');
        else element.setAttribute('inert', lease.original);
        modalInertLeases.delete(element);
      }
      for (const element of scrollers) {
        const lease = element && modalScrollLeases.get(element);
        if (!lease || --lease.count > 0) continue;
        if (lease.value) element.style.setProperty('overflow', lease.value, lease.priority);
        else element.style.removeProperty('overflow');
        modalScrollLeases.delete(element);
      }
    }
  };
}

// Content-modal scrollbar alignment. Keep the reserved native gutter inside the existing end padding,
// so fields share the header/footer edge both before and after the body needs to scroll.
// A content modal stays centred and eases to its new height when its content grows or shrinks (designer, 8 November
// 2026: "grow to accommodate while staying at the center … in a subtle way and not jump"). Each change is caught
// before it's painted: the window is held at the height it had, then animated to the height it now needs, so top and
// bottom move out (or in) together. Content that animates itself (a group sliding open) is followed frame by frame.
export function smoothModalHeight(surface) {
  if (!surface?.isConnected) return { release() {} };
  const reduced = typeof matchMedia === "function" ? matchMedia("(prefers-reduced-motion: reduce)") : null;
  // The height last shown: content that appears all at once has already been laid out by the time it is noticed.
  let released = false, timer = null, last = surface.offsetHeight;
  const parts = () => surface.querySelectorAll(":scope > .content-modal__header, :scope > .content-modal__body > *, :scope > .content-modal__footer");
  function settle() {
    if (released) return;
    clearTimeout(timer); timer = null;
    surface.classList.remove("is-resizing");
    surface.style.removeProperty("height");
    last = surface.offsetHeight;
  }
  function resize() {
    if (released) return;
    if (!surface.isConnected) { release(); return; }
    // Where it is now (mid-animation included), then the height its content needs, measured before any paint.
    const current = surface.classList.contains("is-resizing") ? parseFloat(getComputedStyle(surface).height) : last;
    surface.style.transition = "none";
    surface.style.height = "auto";
    const next = surface.offsetHeight;
    if (!Number.isFinite(current) || Math.abs(next - current) < 1 || reduced?.matches) {
      surface.style.removeProperty("transition");
      settle();
      return;
    }
    last = next;
    surface.style.height = `${current}px`;
    void surface.offsetHeight;
    surface.style.removeProperty("transition");
    surface.classList.add("is-resizing");
    surface.style.height = `${next}px`;
    clearTimeout(timer);
    timer = setTimeout(settle, 600);
  }
  const onEnd = (event) => { if (event.target === surface && event.propertyName === "height") settle(); };
  const sizes = typeof ResizeObserver === "function" ? new ResizeObserver(() => resize()) : null;
  const watch = () => { sizes?.disconnect(); parts().forEach((part) => sizes?.observe(part)); };
  // New content (another step, a section added) is watched as it arrives, and resized for at once.
  const changes = typeof MutationObserver === "function" ? new MutationObserver(() => { watch(); resize(); }) : null;
  function release() {
    if (released) return;
    released = true;
    clearTimeout(timer);
    sizes?.disconnect();
    changes?.disconnect();
    surface.removeEventListener("transitionend", onEnd);
    window.removeEventListener("resize", resize);
    surface.classList.remove("is-resizing");
    surface.style.removeProperty("height");
    surface.style.removeProperty("transition");
  }
  watch();
  changes?.observe(surface, { childList: true, subtree: true });
  surface.addEventListener("transitionend", onEnd);
  window.addEventListener("resize", resize);
  return { release };
}

export function balanceModalGutter(element) {
  if (!element?.isConnected) return { release() {} };
  const property = "--ds-content-modal-scrollbar";
  const original = element.style.getPropertyValue(property);
  const priority = element.style.getPropertyPriority(property);
  let released = false, frame = null, last = null;
  function measure() {
    frame = null;
    if (released) return;
    if (!element.isConnected) { release(); return; }
    // This region has no border. offsetWidth minus clientWidth is the actual classic/reserved
    // scrollbar width; overlay scrollbars consume no layout width and correctly report zero.
    const width = Math.max(0, element.offsetWidth - element.clientWidth);
    if (width === last) return;
    last = width;
    element.style.setProperty(property, `${width}px`);
  }
  function schedule() { if (!released && frame === null) frame = requestAnimationFrame(measure); }
  const observer = typeof ResizeObserver === "function" ? new ResizeObserver(schedule) : null;
  function release() {
    if (released) return;
    released = true;
    if (frame !== null) cancelAnimationFrame(frame);
    observer?.disconnect();
    window.removeEventListener("resize", schedule);
    if (original) element.style.setProperty(property, original, priority);
    else element.style.removeProperty(property);
  }
  measure();
  if (!released) { observer?.observe(element); window.addEventListener("resize", schedule); }
  return { release };
}

// ── Media queries ───────────────────────────────────────────────────────────
// The navigation rail collapses itself below a width. A resize listener would
// fire on every pixel; a media query fires twice in the life of the app.

export function observeMedia(query, dotNetRef, method) {
  const list = window.matchMedia(query);
  const notify = (event) => dotNetRef.invokeMethodAsync(method, event.matches);

  list.addEventListener("change", notify);
  // Report the state at subscribe time too, so the caller never has to guess.
  dotNetRef.invokeMethodAsync(method, list.matches);

  return {
    release() {
      list.removeEventListener("change", notify);
    }
  };
}


// ── Keyboard shortcuts ──────────────────────────────────────────────────────
// Ctrl+K (⌘K on a Mac) focuses the search it is bound to. Handled here so a
// keystroke anywhere on the page costs no server round trip.

export function bindFocusShortcut(element, key) {
  if (!element) return { release() {} };
  const wanted = String(key).toLowerCase();
  const onKeyDown = (event) => {
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === wanted) {
      event.preventDefault();
      element.focus();
      element.select?.();
    }
  };
  document.addEventListener("keydown", onKeyDown);
  return {
    release() {
      document.removeEventListener("keydown", onKeyDown);
    }
  };
}

// Non-modal drawers do not trap focus, but Escape should still dismiss them
// regardless of where focus currently sits on the page.
export function bindEscape(dotNetRef, method, owner) {
  const onKeyDown = event => {
    if (event.key !== "Escape") return;
    // A contextual drawer must not close underneath a child viewer, form or menu.
    if (owner && (event.defaultPrevented || event.target?.closest?.('[role="dialog"], [role="menu"], [role="listbox"]'))) return;
    dotNetRef.invokeMethodAsync(method);
  };
  document.addEventListener("keydown", onKeyDown);
  return {
    release() {
      document.removeEventListener("keydown", onKeyDown);
    }
  };
}

export function rememberFocus() {
  const trigger = document.activeElement;
  return { release() { if (trigger?.isConnected) trigger.focus({ preventScroll: true }); } };
}

export function focusWhenInteractive(element) {
  return new Promise(resolve => {
    if (!element?.isConnected) { resolve(false); return; }
    let timer;
    const observer = new MutationObserver(tryFocus);
    function finish(focused) { observer.disconnect(); clearTimeout(timer); resolve(focused); }
    function tryFocus() {
      if (!element.isConnected) { finish(false); return; }
      if (element.closest('[inert]')) return;
      element.focus({ preventScroll: true });
      finish(document.activeElement === element);
    }
    // Blazor Server can run an earlier render's callback while the opening batch is still travelling.
    // Observe the actual browser state; never remove inert ourselves or move focus into a closed drawer.
    observer.observe(element, { attributes: true, attributeFilter: ['inert'] });
    timer = setTimeout(() => finish(false), 1000);
    tryFocus();
  });
}


// ── Text areas ──────────────────────────────────────────────────────────────

/* Stop Enter from adding a newline to a textarea, so the component's own
   keydown handler can send instead. Blazor cannot cancel a key conditionally
   from C#, so this lives here. Shift+Enter still breaks the line, and Enter
   that confirms an IME composition is left alone. */
export function submitOnEnter(element) {
  const onKeyDown = event => {
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing) event.preventDefault();
  };
  element?.addEventListener("keydown", onKeyDown);

  return {
    release() {
      element?.removeEventListener("keydown", onKeyDown);
    }
  };
}

/* Replace a field's text from the server without re-rendering its value.
   A server-rendered value attribute races the keyboard: a render that lands
   mid-keystroke writes back an older copy of the text. The field is left
   uncontrolled and only text that did not come from typing is pushed. */
/* What was typed or autofilled into this field's prerendered copy, now in
   this field with the focus and caret (early-input.js), or null. */
export function takeEarly(element) {
  return window.auroraEarly?.take(element) ?? null;
}

/* Keep only digits, up to max, in what the field shows now: a correction made
   on the current text, not on an older copy from the server, so digits typed
   while the correction was on its way are kept. The caret stays at the end. */
export function keepDigits(element, max) {
  if (!element) return;
  const digits = element.value.replace(/\D/g, "").slice(0, max);
  if (element.value !== digits) element.value = digits;
}

export function setValue(element, value, focus) {
  // An amount put in by the page reads as one typed would: 58000 shows as 58,000.
  if (element?.dataset.numericInput === "decimal" && value) value = groupAmount(value);
  if (element && element.value !== value) element.value = value;
  element?.__auroraGrow?.();
  // Text put in for the person to carry on with (a draft, a saved reply, an
  // emoji): hand them the field, caret at the end.
  if (focus && value && element) {
    element.focus();
    element.setSelectionRange(value.length, value.length);
  }
}

/* An amount with its thousands grouped, as people read money (designer, 28 October 2026): 58000 → 58,000,
   100000.5 → 100,000.5. Anything but digits and one point is dropped; at most two places after the point. */
function groupAmount(text) {
  const [whole, ...rest] = String(text).split(".");
  const digits = whole.replace(/[^0-9]/g, "").replace(/^0+(?=[0-9])/, "");
  const grouped = digits.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  return rest.length ? `${grouped}.${rest.join("").replace(/[^0-9]/g, "").slice(0, 2)}` : grouped;
}

/* A pasted amount, read the way it's written in captions and chats (designer, 28 October 2026): "58k",
   "1.2m", "₦58,000", "N58,000", "NGN 58,000", "#8,500", "58,000 naira". Null when it isn't an amount, so
   "58k only" or "about 5k" are refused as before. Only pasting is read this way: typing stays digits only. */
function readPastedAmount(text) {
  const plain = String(text).trim().toLowerCase().replace(/\s+/g, "")
    .replace(/^(?:₦|ngn|n|#)/, "").replace(/(?:naira|ngn)$/, "");
  const match = plain.match(/^([0-9][0-9,]*(?:\.[0-9]+)?|\.[0-9]+)([km])?$/);
  if (!match) return null;
  const written = match[1].replace(/,/g, "");
  if (!match[2]) {
    const [whole, places = ""] = written.split(".");
    return places.length > 2 ? Number(written).toFixed(2) : written;
  }
  const amount = Number(written) * (match[2] === "k" ? 1e3 : 1e6);
  if (!Number.isFinite(amount)) return null;
  return (Math.round(amount * 100) / 100).toFixed(2).replace(/\.00$/, "").replace(/(\.\d)0$/, "$1");
}

/* Numeric fields opt in. Reject the entire invalid insertion, never turn -500 or 500abc into 500.
   Run before Blazor's delegated input listener; normal text/auth fields never attach these handlers.
   Amounts (decimal) also group their thousands as they're typed, and read a pasted 58k or ₦58,000. */
export function bindNumberInput(element, target) {
  if (!element) return { release() {} };
  const amount = element.dataset.numericInput === "decimal";
  const limit = element.maxLength;
  // A grouped amount may be wider than its digit limit. Native maxlength counts commas too, which can
  // otherwise trap a formatted large value; enforce the digit limit locally instead.
  if (element.dataset.numericInput === "decimal" && limit >= 0) element.removeAttribute("maxlength");
  // A phone number: digits and what people write one with (+234 803 555 1234, (0803) 555-1234), never letters.
  const kinds = { integer: /^[0-9]*$/, phone: /^[0-9+() -]*$/, decimal: /^[0-9,]*(?:\.[0-9]{0,2})?$/ };
  const valid = value => (kinds[element.dataset.numericInput] ?? kinds.decimal).test(value)
    && (limit < 0 || value.replace(/[,.]/g, "").length <= limit);
  let accepted = element.value;
  let caret = element.selectionStart ?? accepted.length;
  let composing = false;
  const reject = () => target.invokeMethodAsync("NumberRejectedAsync", accepted).catch(() => {});
  const proposed = text => element.value.slice(0, element.selectionStart ?? element.value.length)
    + text + element.value.slice(element.selectionEnd ?? element.value.length);
  // Groups the amount in place, keeping the caret beside the same digit it was beside.
  const regroup = () => {
    const shown = element.value;
    const next = groupAmount(shown);
    if (next === shown) return;
    const at = element.selectionStart ?? shown.length;
    const kept = shown.slice(0, at).replace(/[^0-9.]/g, "").length;
    element.value = next;
    let position = 0;
    for (let seen = 0; position < next.length && seen < kept; position++) if (/[0-9.]/.test(next[position])) seen++;
    element.setSelectionRange(position, position);
  };
  // Puts text in place of the selection as if typed, so the field's own input handling runs (and Blazor hears it).
  const putIn = (text, start, end) => {
    element.value = element.value.slice(0, start) + text + element.value.slice(end);
    element.setSelectionRange(start + text.length, start + text.length);
    element.dispatchEvent(new Event("input", { bubbles: true }));
  };
  const before = event => {
    accepted = element.value; caret = element.selectionStart ?? accepted.length;
    // Deleting next to a comma deletes the digit beside it: the comma alone would only come straight back.
    if (amount && (event.inputType === "deleteContentBackward" || event.inputType === "deleteContentForward")
      && element.selectionStart === element.selectionEnd) {
      const back = event.inputType === "deleteContentBackward";
      const comma = back ? caret - 1 : caret;
      if (accepted[comma] === ",") {
        event.preventDefault();
        const digit = back ? comma - 1 : comma + 1;
        if (digit < 0 || digit >= accepted.length) return;
        const from = Math.min(comma, digit);
        element.value = accepted.slice(0, from) + accepted.slice(Math.max(comma, digit) + 1);
        element.setSelectionRange(from, from);
        element.dispatchEvent(new Event("input", { bubbles: true }));
        return;
      }
    }
    if (event.isComposing || !event.inputType?.startsWith("insert") || event.data == null) return;
    if (!valid(proposed(event.data))) { event.preventDefault(); reject(); }
  };
  const paste = event => {
    const text = event.clipboardData?.getData("text");
    if (text == null) return;
    accepted = element.value; caret = element.selectionStart ?? accepted.length;
    // An amount pasted the way it's written ("58k", "₦58,000"): put in as the number it means.
    const read = amount ? readPastedAmount(text) : null;
    if (read !== null) {
      event.preventDefault();
      const start = element.selectionStart ?? accepted.length, end = element.selectionEnd ?? start;
      if (!valid(proposed(read))) { reject(); return; }
      putIn(read, start, end);
      return;
    }
    if (!valid(proposed(text))) { event.preventDefault(); reject(); }
  };
  const input = event => {
    if (composing || event.isComposing) { event.stopImmediatePropagation(); return; }
    // Old drafts can contain invalid text. Let people delete it rather than trapping them in that value.
    if (valid(element.value) || event.inputType?.startsWith("delete") && element.value.length < accepted.length) {
      if (amount && valid(element.value)) regroup();
      accepted = element.value; caret = element.selectionStart ?? accepted.length; return;
    }
    element.value = accepted;
    element.setSelectionRange(caret, caret);
    event.stopImmediatePropagation(); reject();
  };
  const startComposition = () => { composing = true; };
  const endComposition = () => { composing = false; element.dispatchEvent(new Event("input", { bubbles: true })); };
  // An amount already in the field (a saved price) shows grouped from the start; the value is the same number.
  if (amount && valid(element.value)) { element.value = groupAmount(element.value); accepted = element.value; }
  element.addEventListener("beforeinput", before);
  element.addEventListener("paste", paste);
  element.addEventListener("input", input, true);
  element.addEventListener("compositionstart", startComposition);
  element.addEventListener("compositionend", endComposition);
  return { release() {
    element.removeEventListener("beforeinput", before);
    element.removeEventListener("paste", paste);
    element.removeEventListener("input", input, true);
    element.removeEventListener("compositionstart", startComposition);
    element.removeEventListener("compositionend", endComposition);
  } };
}

/* A textarea that grows with its text, upwards when it sits at the bottom of
   its container, until the stylesheet's max-height; past that it scrolls. */
export function autoGrow(element) {
  if (!element) return { release() {} };
  const grow = () => {
    element.style.height = "auto";
    const style = getComputedStyle(element);
    const edges = parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
    element.style.height = `${element.scrollHeight + edges}px`;
  };
  element.__auroraGrow = grow;
  element.addEventListener("input", grow);
  grow();

  return {
    release() {
      element.removeEventListener("input", grow);
      delete element.__auroraGrow;
    }
  };
}

/* Browser credential managers may fill after Blazor has rendered and without
   a normal keystroke. CSS raises an animation when the autofill pseudo-class
   becomes active; replaying the standard input event keeps the bound value in
   sync without teaching product code about a particular browser. */
export function bindAutofill(element) {
  if (!(element instanceof Element)) return { release() {} };

  const isAutofilled = () => {
    try { if (element.matches(":autofill")) return true; } catch { /* Browser has no standard selector yet. */ }
    try { return element.matches(":-webkit-autofill"); } catch { return false; }
  };
  const sync = () => {
    if (isAutofilled()) element.dispatchEvent(new Event("input", { bubbles: true }));
  };
  const onAnimationStart = () => queueMicrotask(sync);

  element.addEventListener("animationstart", onAnimationStart);
  // Catch credentials the browser restored before the interactive component
  // attached its listener. The animation path handles later fills.
  requestAnimationFrame(() => requestAnimationFrame(sync));

  return {
    release() {
      element.removeEventListener("animationstart", onAnimationStart);
    }
  };
}


/* Put text in at the caret (or over a selection), as typing it would, and
   hand back the field's new text. The caret survives the field losing focus
   to a toolbar button, so an emoji lands where the person was typing. */
export function insertAtCaret(element, text) {
  if (!element) return null;
  const start = element.selectionStart ?? element.value.length;
  const end = element.selectionEnd ?? start;
  element.focus();
  element.setRangeText(text, start, end, "end");
  element.__auroraGrow?.();
  return element.value;
}

/* Selects a piece of a field's text (a sentence to change, say), with the
   focus, and brings it into view. False when the text isn't there. */
export function selectText(element, text) {
  if (!element || !text) return false;
  const start = element.value.indexOf(text);
  if (start < 0) return false;
  element.focus();
  element.setSelectionRange(start, start + text.length);
  element.scrollIntoView({ block: "center", behavior: "smooth" });
  return true;
}

// ── Scrolling ───────────────────────────────────────────────────────────────

// A footer floating over a scrolling area (a chat's reply box over its
// thread) measures the actual first visible card, rather than guessing a fixed
// resting height. Only this opt-in chat follows the end; other scroll surfaces
// (including native mobile) keep their existing behaviour.
export function reserveUnder(scroller, footer, property) {
  if (!(scroller instanceof Element) || !(footer instanceof Element)) return { release() {} };
  let pinned = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 48;
  let adjusting = false;
  let released = false;
  let frame = 0;
  const move = () => { if (!released && pinned) scroller.scrollTop = scroller.scrollHeight; };
  const onScroll = () => {
    if (!adjusting) pinned = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 48;
  };
  const onWheel = event => { if (event.deltaY < 0) pinned = false; };
  const update = () => {
    if (released) return;
    const first = [...footer.children].find(child => child.getBoundingClientRect().height > 0);
    const top = first?.getBoundingClientRect().top ?? footer.getBoundingClientRect().top;
    // Read a resolved length, not a custom property's unevaluated calc() token expression.
    const gap = (parseFloat(getComputedStyle(footer).rowGap) || 8) * 2;
    const reserve = Math.max(0, scroller.getBoundingClientRect().bottom - top + gap);
    adjusting = true;
    if (Math.abs(parseFloat(scroller.style.getPropertyValue(property)) - reserve) > 0.5
        || !scroller.style.getPropertyValue(property)) scroller.style.setProperty(property, reserve + "px");
    move();
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(() => { move(); adjusting = false; });
  };
  const controller = {
    scrollToLatest(force) {
      if (force) pinned = true;
      update();
    }
  };
  scroller.__auroraReservedEnd = controller;
  scroller.addEventListener("scroll", onScroll, { passive: true });
  scroller.addEventListener("wheel", onWheel, { passive: true });
  const observer = new ResizeObserver(update);
  observer.observe(footer);
  observer.observe(scroller);
  if (scroller.firstElementChild) observer.observe(scroller.firstElementChild);
  update();
  return {
    release() {
      released = true;
      cancelAnimationFrame(frame);
      observer.disconnect();
      scroller.removeEventListener("scroll", onScroll);
      scroller.removeEventListener("wheel", onWheel);
      if (scroller.__auroraReservedEnd === controller) delete scroller.__auroraReservedEnd;
      scroller.style.removeProperty(property);
    }
  };
}

export function scrollToBottom(element, force = true) {
  if (!element) return;
  if (element.__auroraReservedEnd) {
    element.__auroraReservedEnd.scrollToLatest(force);
    return;
  }
  const moveToLatest = () => { element.scrollTop = element.scrollHeight; };
  // Two frames let Blazor finish the new thread's layout before the jump.
  // Attachments may increase the thread again after their images load, so
  // keep the newest message in view when that happens too.
  requestAnimationFrame(() => {
    moveToLatest();
    requestAnimationFrame(moveToLatest);
    element.querySelectorAll("img").forEach(image => {
      if (!image.complete) image.addEventListener("load", moveToLatest, { once: true });
    });
  });
  followEnd(element, moveToLatest);
}

/* While the reader is at the newest message, keep them there when the region
   itself gets shorter: a smaller window, quick replies appearing under it, a
   composer growing as they type. Without this the newest message slid out of
   view below the fold. Someone who has scrolled up to read is left alone. */
function followEnd(element, moveToLatest) {
  if (element.__auroraFollowEnd) return;
  let atEnd = true;
  let height = element.clientHeight;
  element.addEventListener("scroll", () => {
    // A resize moves the scroll position too (the browser keeps what was in
    // view). That is not the reader scrolling up, so it does not count.
    if (element.clientHeight !== height) return;
    atEnd = element.scrollHeight - element.scrollTop - element.clientHeight < 24;
  }, { passive: true });
  element.__auroraFollowEnd = new ResizeObserver(() => {
    height = element.clientHeight;
    if (atEnd) moveToLatest();
  });
  element.__auroraFollowEnd.observe(element);
}

/* Bring an element to the top of the scrolling region.

   Used when a section of the setup accordion opens: the one that closes is
   above the one that opens, so the panel you asked for lands wherever the
   collapse left it — often below the fold on a long card. This lifts it back
   to the top instead of leaving the reader to find it. scroll-margin-top on
   the section keeps it clear of the edge. */
/* The rail's tooltips are placed against the window (position: fixed), so a
   scrolling list cannot clip them. CSS cannot know where an item is on
   screen, so this tells each one as the pointer or the focus reaches it:
   the middle of its height, and just past the rail's right edge. */
export function followRailTooltips(rail) {
  if (!rail || rail.__auroraRailTips) return;
  rail.__auroraRailTips = true;
  const place = (event) => {
    const item = event.target.closest?.("[data-tooltip]");
    if (!item || !rail.contains(item)) return;
    const box = item.getBoundingClientRect();
    const edge = rail.getBoundingClientRect().right;
    item.style.setProperty("--ds-tip-top", `${box.top + box.height / 2}px`);
    item.style.setProperty("--ds-tip-left", `${edge + 8}px`);
  };
  rail.addEventListener("pointerover", place, { passive: true });
  rail.addEventListener("focusin", place);
}

/** Drops any text selection (a double-click selects a word). */
export function clearSelection() {
  window.getSelection()?.removeAllRanges();
}

/**
 * Opens a menu upward when there is no room for it below, inside whatever
 * clips it (a scrolling list) or the window, and there is room above. Marks it
 * placed either way; it stays hidden until then.
 */
export function placeMenu(menu) {
  if (!menu) return;
  const box = menu.getBoundingClientRect();
  let top = 0, bottom = window.innerHeight;
  for (let el = menu.parentElement?.parentElement; el && el !== document.body; el = el.parentElement) {
    if (/(auto|scroll|hidden|clip)/.test(getComputedStyle(el).overflowY)) {
      const clip = el.getBoundingClientRect();
      top = Math.max(top, clip.top); bottom = Math.min(bottom, clip.bottom);
      break;
    }
  }
  const trigger = menu.parentElement.getBoundingClientRect();
  const margin = 8;
  if (box.bottom > bottom - margin && trigger.top - top > box.height + margin) menu.dataset.place = "above";
  else delete menu.dataset.place;
  menu.dataset.placed = "";
}

// ── Floating menus ──────────────────────────────────────────────────────────
// A menu inside a dialog or a side panel was cut off by the panel's own
// scrolling, and the dialog card's transform traps position: fixed as well
// (designer, 21 October 2026: a popover must stay a popover, never push the
// form). The browser's top layer (the popover attribute) draws the menu above
// everything while it stays where it is in the document, so clicks, focus and
// the dialog's focus trap behave as before. The menu sits against its trigger:
// below it, or above when there is more room there; kept on screen; following
// the trigger when the window resizes or the menu's content changes size.
// Escape closes the menu, not the dialog under it.
export function floatMenu(layer, anchor, menu, alignEnd, matchWidth, beside) {
  if (!layer || !anchor || !menu) return;
  // A browser without the top layer still shows the menu, placed the same way.
  if (typeof layer.showPopover !== "function") layer.removeAttribute("popover");
  else if (!layer.matches(":popover-open")) {
    try { layer.showPopover(); } catch { return; }
  }
  // A menu its stylesheet anchors to the right ("right: 0; left: auto") keeps
  // that edge, as it did before it floated: read before any placing.
  let end = !!alignEnd;
  if (!end && typeof menu.computedStyleMap === "function") {
    try { end = String(menu.computedStyleMap().get("left")) === "auto"; } catch { /* keep the start edge */ }
  }
  const gap = 4, margin = 8, least = 160;
  let frame = 0;
  // The trigger itself, when the anchor is a wrapper around it (an ActionMenu, a Popover's span).
  const trigger = anchor.matches("[aria-haspopup]") ? anchor : anchor.querySelector("[aria-haspopup]") ?? anchor;
  const place = () => {
    frame = 0;
    if (!layer.isConnected) { stop(); return; }
    const a = trigger.getBoundingClientRect();
    // As wide as the field at least (a select), never narrower than its own width.
    if (matchWidth) menu.style.minWidth = `${Math.round(a.width)}px`;
    menu.style.maxHeight = "";
    const box = menu.getBoundingClientRect();
    // Beside the trigger (from the rail): to its right, its top level with the trigger's, or its foot level with
    // the trigger's when there's more room above (a trigger low in the rail); kept on screen.
    if (beside) {
      const room = window.innerHeight - margin * 2;
      if (box.height > room) menu.style.maxHeight = `${Math.round(room)}px`;
      const height = Math.min(box.height, room);
      const up = window.innerHeight - a.top < a.bottom;
      const top = Math.max(margin, Math.min(up ? a.bottom - height : a.top, window.innerHeight - margin - height));
      // Clear of the rail itself, not only the item: from the rail's edge when the trigger sits in one.
      const edge = trigger.closest("aside")?.getBoundingClientRect().right ?? a.right;
      const left = Math.min(edge + margin, window.innerWidth - margin - box.width);
      menu.style.top = `${Math.round(top)}px`;
      menu.style.left = `${Math.round(Math.max(margin, left))}px`;
      menu.dataset.float = "beside";
      menu.dataset.placed = "";
      return;
    }
    const below = window.innerHeight - a.bottom - gap - margin;
    const above = a.top - gap - margin;
    // Below the trigger when it fits, or when there is more room below than above.
    const down = box.height <= below || below >= above;
    const room = Math.max(down ? below : above, least);
    if (box.height > room) menu.style.maxHeight = `${Math.round(room)}px`;
    const height = Math.min(box.height, room);
    const top = down ? a.bottom + gap : a.top - gap - height;
    // From the trigger's start edge, or its end edge; switched when that side would run off the screen.
    let left = end ? a.right - box.width : a.left;
    if (!end && left + box.width > window.innerWidth - margin) left = a.right - box.width;
    if (end && left < margin) left = a.left;
    left = Math.max(margin, Math.min(left, window.innerWidth - margin - box.width));
    menu.style.top = `${Math.round(top)}px`;
    menu.style.left = `${Math.round(left)}px`;
    menu.dataset.float = down ? "below" : "above";
    menu.dataset.placed = "";
  };
  const later = () => { if (!frame) frame = requestAnimationFrame(place); };
  // Escape closes the open menu first, wherever focus is (the trigger, the menu, a field of the form
  // under it), and goes no further, so a dialog under the menu stays open.
  const onKey = (event) => {
    if (!layer.isConnected) { stop(); return; }
    if (event.key !== "Escape") return;
    event.stopPropagation();
    event.preventDefault();
    layer.querySelector("[data-float-dismiss]")?.click();
  };
  const watch = typeof ResizeObserver === "function" ? new ResizeObserver(later) : null;
  function stop() {
    window.removeEventListener("resize", later);
    window.removeEventListener("scroll", later, true);
    document.removeEventListener("keydown", onKey, true);
    watch?.disconnect();
  }
  place();
  // A long list opens at the choice already made ("6pm" among the day's times), not at its top (designer,
  // 7 November 2026). Only the menu scrolls, never the page.
  const chosen = menu.querySelector('[aria-checked="true"], [aria-selected="true"]');
  if (chosen && menu.scrollHeight > menu.clientHeight) {
    menu.scrollTop = Math.max(0, chosen.offsetTop - (menu.clientHeight - chosen.offsetHeight) / 2);
  }
  window.addEventListener("resize", later);
  window.addEventListener("scroll", later, true);
  document.addEventListener("keydown", onKey, true);
  watch?.observe(menu);
}

export function scrollIntoView(element, block) {
  if (!element) return;
  // One frame later, so the panel has been laid out before we measure it.
  requestAnimationFrame(() => {
    element.scrollIntoView({ behavior: "smooth", block: block || "start" });
  });
}

// ── Storage ─────────────────────────────────────────────────────────────────
// Every access is guarded: storage throws rather than returning null in a
// private window, and a theme preference is never worth an unhandled error.

export function readStorage(key) {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function writeStorage(key, value) {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Nothing to do and nothing worth telling anyone.
  }
}

export function removeStorage(key) {
  try {
    localStorage.removeItem(key);
  } catch {
    // As above.
  }
}

// ── Theme ───────────────────────────────────────────────────────────────────
// The attribute is set before first paint by the inline script in index.html.
// This only keeps it in step once the app is running.

export function setTheme(theme) {
  if (theme) {
    document.documentElement.dataset.theme = theme;
  } else {
    delete document.documentElement.dataset.theme;
  }
}

export function getTheme() {
  return document.documentElement.dataset.theme ?? "";
}

// ── Clicking for the browser ────────────────────────────────────────────────
// A file picker can only be opened by a real click on a file input. The input
// itself is hidden, because no styling makes a native one match the product,
// so the visible Button forwards its click here.

export function clickElement(element) {
  element?.click();
}

// ── Page visibility ─────────────────────────────────────────────────────────
// A background tab is not being read, so what arrives in it is still unread.

export function isPageVisible() {
  return document.visibilityState === "visible";
}

// ── Clipboard ───────────────────────────────────────────────────────────────
// Guarded: the clipboard API throws without a secure context or a user
// gesture, and failing to copy a webhook URL is not worth an unhandled error.

export async function copyText(value) {
  try {
    await navigator.clipboard.writeText(value);
    return true;
  } catch {
    // The value is on screen and selectable; that is the fallback.
    return false;
  }
}

// No network/storage: security codes are offered as a file only after an explicit user action.
export function saveTextFile(value, fileName) {
  let url;
  let link;
  try {
    url = URL.createObjectURL(new Blob([value], { type: "text/plain;charset=utf-8" }));
    link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    return true;
  } catch {
    return false;
  } finally {
    link?.remove();
    if (url) setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}

export function focusWithin(container, id) {
  const target = document.getElementById(id);
  if (!target || !container?.contains(target) || target.disabled) return false;
  target.focus({ preventScroll: true });
  if (document.activeElement !== target) return false;
  target.scrollIntoView({ block: "nearest", inline: "nearest" });
  return true;
}

export async function downloadFile(url, fileName) {
  try {
    // Same-origin credentials allow protected yára media without ever
    // forwarding session cookies to an external channel URL.
    const response = await fetch(url, { credentials: "same-origin" });
    if (!response.ok) return false;
    const blobUrl = URL.createObjectURL(await response.blob());
    const link = document.createElement("a");
    link.href = blobUrl;
    link.download = fileName || "image";
    link.hidden = true;
    document.body.appendChild(link);
    link.click();
    link.remove();
    requestAnimationFrame(() => URL.revokeObjectURL(blobUrl));
    return true;
  } catch {
    return false;
  }
}

// ── Loading a script on first use ───────────────────────────────────────────
// A heavy library only one screen needs (Chart.js, for the dashboard) should
// not sit in front of every page's first paint. The first caller injects the
// tag; everyone after awaits the same promise.

const scripts = new Map();

export function ensureScript(url) {
  if (!scripts.has(url)) {
    scripts.set(url, new Promise((resolve, reject) => {
      const tag = document.createElement("script");
      tag.src = url;
      tag.async = true;
      tag.onload = () => resolve();
      tag.onerror = () => {
        scripts.delete(url);
        tag.remove();
        reject(new Error(`Could not load ${url}`));
      };
      document.head.appendChild(tag);
    }));
  }
  return scripts.get(url);
}
