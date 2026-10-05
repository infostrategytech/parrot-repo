/* Typing before the page is live.

   The first paint is a prerendered copy of the page; when the live page
   takes over (a moment later, or several seconds on a slow network) every
   field is replaced by a new one, and the page moves the focus to its
   heading. Anything typed or autofilled into the first copy would be lost,
   and the caret taken away from someone mid-word.

   This keeps what each field with an id held, and which one had the focus.
   The moment a field with the same id replaces it, the text, the focus and
   the caret move across in the browser, with no wait for the server, so a
   key pressed during the swap still lands. The live field then asks for the
   text once, on its first render (auroraEarly.take), to tell the page.

   Pressing Sign in (or Enter) before the page is live would make the browser
   submit the form itself and reload the page, losing everything again. That
   submit is held, and sent once the live fields have taken over.

   "Live" is the first live field asking for its text, or, for a page with
   none, a few seconds after the connection starts (auroraEarly.connected,
   from Parrot.SharedComponents.lib.module.js). From then on nothing is
   recorded or held, so nothing leaks to fields on later pages. */
(() => {
  const kept = new Map();
  let focused = null;
  let live = false;
  let observer = null;
  // The action of a form whose submit was held until the page is live, and the form itself.
  let heldSubmit = null;
  let heldForm = null;

  const isField = (el) =>
    (el instanceof HTMLInputElement && !["hidden", "checkbox", "radio", "file", "submit", "button"].includes(el.type))
    || el instanceof HTMLTextAreaElement;

  const caretOf = (el) => {
    try { return [el.selectionStart, el.selectionEnd]; } catch { return [null, null]; } // email fields have no selection API
  };

  const placeCaret = (el, start, end) => {
    if (start === null) return;
    try { el.setSelectionRange(start, end); } catch { /* no selection API */ }
  };

  // edited: typed or filled into (not just clicked into), so it wins over a
  // prefill the live field starts with.
  const keep = (el, edited) => {
    if (live || !isField(el) || !el.id) return;
    const entry = kept.get(el.id);
    if (entry && entry.el !== el) return; // already moved across to the live field
    const [start, end] = caretOf(el);
    kept.set(el.id, { el, value: el.value, start, end, edited: edited || !!entry?.edited, hadFocus: false, into: null });
    watch();
  };

  // A field that replaced one typed into: give it the text, focus and caret now.
  const carryOver = () => {
    for (const [id, entry] of kept) {
      if (entry.into || entry.el.isConnected) continue;
      const next = document.getElementById(id);
      if (!isField(next) || next === entry.el) continue;
      entry.into = next;
      // What the person typed wins over the value the live field starts with.
      if (entry.edited && next.value !== entry.value) {
        next.value = entry.value;
        // Tell the live page at once, the way typing would: it hears this
        // before anything sent later, such as a held submit.
        next.dispatchEvent(new Event("input", { bubbles: true }));
      }
      // Only the field focused last gets the caret back.
      entry.hadFocus = focused === entry.el;
      if (entry.hadFocus) {
        next.focus();
        placeCaret(next, entry.start, entry.end);
      }
    }
  };

  const watch = () => {
    if (observer || !document.body) return;
    observer = new MutationObserver(carryOver);
    observer.observe(document.body, { childList: true, subtree: true });
  };

  const stopIfDone = () => {
    if (live && kept.size === 0 && observer) { observer.disconnect(); observer = null; }
  };

  // Sent once, after every live field on the page has had its turn to take over.
  const sendHeldSubmit = (delay) => {
    if (heldSubmit === null) return;
    const action = heldSubmit;
    heldSubmit = null;
    setTimeout(() => {
      const form = [...document.forms].find((f) => (f.getAttribute("action") ?? "") === action) ?? document.forms[0];
      form?.requestSubmit();
    }, delay);
  };

  const goLive = () => {
    if (live) return;
    live = true;
    carryOver();
    // Text typed into fields that were never replaced belongs to the live page already.
    for (const [id, entry] of kept) if (!entry.into) kept.delete(id);
    stopIfDone();
  };

  document.addEventListener("input", (e) => keep(e.target, true), true);
  document.addEventListener("change", (e) => keep(e.target, true), true);
  document.addEventListener("focusin", (e) => {
    if (live || !isField(e.target)) return;
    focused = e.target;
    // Clicked into, even before typing: the live field gets the caret too.
    keep(e.target, false);
  }, true);
  document.addEventListener("submit", (e) => {
    if (live || !(e.target instanceof HTMLFormElement)) return;
    e.preventDefault();
    heldSubmit = e.target.getAttribute("action") ?? "";
    heldForm = e.target;
    watch();
  }, true);

  window.auroraEarly = {
    take(element) {
      goLive();
      sendHeldSubmit(250);
      const entry = element && element.id ? kept.get(element.id) : null;
      if (!entry || entry.into !== element) return null;
      kept.delete(element.id);
      stopIfDone();
      // The page moves the focus to its heading when it goes live, sometimes
      // just after this: give it back, for a moment, but only from the heading
      // or nowhere, never from something the person moved to.
      if (entry.hadFocus) {
        const giveBack = () => {
          const now = document.activeElement;
          if (now === element || !element.isConnected) return;
          if (now && now !== document.body && now.tagName !== "H1") return;
          element.focus();
          placeCaret(element, entry.start, entry.end);
        };
        giveBack();
        requestAnimationFrame(giveBack);
        setTimeout(giveBack, 150);
        setTimeout(giveBack, 500);
      }
      return element.value || null;
    },

    // The connection has started; the live page renders a moment later. For a
    // page whose fields never ask (none from the design system), go live once
    // the held form has been replaced, or at the latest after 15 seconds.
    connected() {
      const started = Date.now();
      const tick = () => {
        if (live) return;
        if (heldForm && heldForm.isConnected && Date.now() - started < 15000) { setTimeout(tick, 500); return; }
        goLive();
        sendHeldSubmit(250);
      };
      setTimeout(tick, 3000);
    },
  };
})();
