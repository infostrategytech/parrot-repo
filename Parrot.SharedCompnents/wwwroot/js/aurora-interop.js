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

export function trapFocus(container) {
  const previouslyFocused = document.activeElement;

  function focusable() {
    return Array.from(container.querySelectorAll(FOCUSABLE)).filter(
      (el) => el.offsetParent !== null || el === container
    );
  }

  function onKeyDown(event) {
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

// ── Composer ────────────────────────────────────────────────────────────────

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
   mid-keystroke writes back an older copy of the text. The composer leaves
   the field uncontrolled and only pushes text that did not come from typing. */
export function setValue(element, value) {
  if (element && element.value !== value) element.value = value;
}

// ── Scrolling ───────────────────────────────────────────────────────────────

export function scrollToBottom(element) {
  if (!element) return;
  // One frame later, so the newly rendered message is measured before we jump.
  requestAnimationFrame(() => {
    element.scrollTop = element.scrollHeight;
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
