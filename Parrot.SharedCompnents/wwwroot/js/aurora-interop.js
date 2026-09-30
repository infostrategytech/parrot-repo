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
export function bindEscape(dotNetRef, method) {
  const onKeyDown = event => {
    if (event.key !== "Escape") return;
    dotNetRef.invokeMethodAsync(method);
  };
  document.addEventListener("keydown", onKeyDown);
  return {
    release() {
      document.removeEventListener("keydown", onKeyDown);
    }
  };
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
export function setValue(element, value, focus) {
  if (element && element.value !== value) element.value = value;
  element?.__auroraGrow?.();
  // Text put in for the person to carry on with (a draft, a saved reply, an
  // emoji): hand them the field, caret at the end.
  if (focus && value && element) {
    element.focus();
    element.setSelectionRange(value.length, value.length);
  }
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

// ── Scrolling ───────────────────────────────────────────────────────────────

// A footer floating over a scrolling area (a chat's reply box over its
// thread) covers whatever sits under it, so the area keeps room at the end
// for it: the room the design gives, plus however much taller the footer
// grows (an error above the reply box, say). The last line stays in view.
export function reserveUnder(scroller, footer, property) {
  if (!(scroller instanceof Element) || !(footer instanceof Element)) return { release() {} };
  const base = parseFloat(getComputedStyle(scroller).getPropertyValue(property)) || 0;
  let rest = null;
  const update = () => {
    const height = footer.getBoundingClientRect().height;
    // The footer's height the first time it is measured is its resting size.
    rest ??= height;
    const pinned = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 48;
    scroller.style.setProperty(property, Math.max(base, base + height - rest) + "px");
    if (pinned) scroller.scrollTop = scroller.scrollHeight;
  };
  const observer = new ResizeObserver(update);
  observer.observe(footer);
  return {
    release() {
      observer.disconnect();
      scroller.style.removeProperty(property);
    }
  };
}

export function scrollToBottom(element) {
  if (!element) return;
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
}

/* Bring an element to the top of the scrolling region.

   Used when a section of the setup accordion opens: the one that closes is
   above the one that opens, so the panel you asked for lands wherever the
   collapse left it — often below the fold on a long card. This lifts it back
   to the top instead of leaving the reader to find it. scroll-margin-top on
   the section keeps it clear of the edge. */
export function scrollIntoView(element) {
  if (!element) return;
  // One frame later, so the panel has been laid out before we measure it.
  requestAnimationFrame(() => {
    element.scrollIntoView({ behavior: "smooth", block: "start" });
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

// ── Clipboard ───────────────────────────────────────────────────────────────
// Guarded: the clipboard API throws without a secure context or a user
// gesture, and failing to copy a webhook URL is not worth an unhandled error.

export async function copyText(value) {
  try {
    await navigator.clipboard.writeText(value);
  } catch {
    // The value is on screen and selectable; that is the fallback.
  }
}

export async function downloadFile(url, fileName) {
  try {
    // Same-origin credentials allow protected OmniAI media without ever
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
