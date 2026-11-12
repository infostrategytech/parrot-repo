// Shared, non-interactive tooltips. No focus movement, click-away overlay or server round trip.
let activeTooltip = null;

export function bindTooltip(root, bubble) {
  if (!root || !bubble) return { release() {} };
  const trigger = root.querySelector(".ds-tooltip__trigger");
  if (!trigger) return { release() {} };

  // A wrapper description is not inherited by a nested button/link: associate the help with the
  // existing control too, preserving any field hints it already has. Informational marks use the wrapper.
  const described = [];
  for (const target of trigger.querySelectorAll?.("button, a[href], input, select, textarea, [tabindex]") ?? []) {
    const ids = (target.getAttribute("aria-describedby") || "").split(/\s+/).filter(Boolean);
    if (bubble.id && !ids.includes(bubble.id)) {
      target.setAttribute("aria-describedby", [...ids, bubble.id].join(" "));
      described.push(target);
    }
  }

  let disposed = false, visible = false, hovered = false, focused = false, suppressed = false;
  let timer = null, frame = null;
  const cancelTimer = () => { if (timer !== null) clearTimeout(timer); timer = null; };
  const cancelFrame = () => { if (frame !== null) cancelAnimationFrame(frame); frame = null; };

  function place() {
    frame = null;
    if (disposed || !visible) return;
    if (!root.isConnected || !bubble.isConnected) { release(); return; }
    const anchor = trigger.getBoundingClientRect();
    const box = bubble.getBoundingClientRect();
    const width = document.documentElement.clientWidth || window.innerWidth;
    const height = document.documentElement.clientHeight || window.innerHeight;
    if (anchor.bottom <= 0 || anchor.top >= height || anchor.left >= width || anchor.left + anchor.width <= 0) { hide(); return; }
    const gap = parseFloat(getComputedStyle(bubble).getPropertyValue("--ds-tooltip-gap")) || 8;
    const gutter = gap;
    const clamp = (value, min, max) => Math.max(min, Math.min(value, Math.max(min, max)));
    const above = anchor.top - box.height - gap >= gutter;
    const top = above ? anchor.top - box.height - gap : anchor.bottom + gap;
    const left = anchor.left + (anchor.width - box.width) / 2;
    bubble.style.top = `${Math.round(clamp(top, gutter, height - box.height - gutter))}px`;
    bubble.style.left = `${Math.round(clamp(left, gutter, width - box.width - gutter))}px`;
    bubble.dataset.side = above ? "above" : "below";
  }

  const later = () => { if (visible && frame === null) frame = requestAnimationFrame(place); };
  function hide() {
    cancelTimer(); cancelFrame();
    if (!visible) return;
    visible = false;
    try { bubble.hidePopover?.(); } catch { /* Already detached or closed. */ }
    bubble.hidden = true;
    window.removeEventListener("resize", later);
    window.removeEventListener("scroll", later, true);
    window.removeEventListener("keydown", onKey, true);
    window.removeEventListener("pointerdown", hide, true);
    observer?.disconnect();
    if (activeTooltip === hide) activeTooltip = null;
  }
  function show() {
    cancelTimer();
    if (disposed || suppressed || (!hovered && !focused) || !root.isConnected || root.closest("[inert]")) return;
    activeTooltip?.();
    activeTooltip = hide;
    visible = true;
    bubble.hidden = false;
    try { bubble.showPopover?.(); } catch { /* A basic fixed-position fallback remains readable. */ }
    place();
    window.addEventListener("resize", later);
    window.addEventListener("scroll", later, true);
    // Window capture dismisses help before the document-level menu/dialog Escape handlers.
    window.addEventListener("keydown", onKey, true);
    window.addEventListener("pointerdown", hide, true);
    observer?.observe(trigger); observer?.observe(bubble);
  }
  function onKey(event) {
    if (event.key !== "Escape" || !visible) return;
    suppressed = true;
    event.preventDefault(); event.stopImmediatePropagation();
    hide();
  }
  function enter(event) {
    if (event.pointerType === "touch") return;
    hovered = true; cancelTimer();
    if (!suppressed && !visible) timer = setTimeout(show, 350);
  }
  function leave(event) {
    if (root.contains(event.relatedTarget)) return;
    hovered = false;
    if (!focused) { suppressed = false; cancelTimer(); timer = setTimeout(hide, 120); }
  }
  function focus() { focused = true; cancelTimer(); show(); }
  function blur(event) {
    if (root.contains(event.relatedTarget)) return;
    focused = false; suppressed = false;
    if (!hovered) hide();
  }
  const observer = typeof ResizeObserver === "function" ? new ResizeObserver(later) : null;
  root.addEventListener("pointerenter", enter);
  root.addEventListener("pointerleave", leave);
  root.addEventListener("focusin", focus);
  root.addEventListener("focusout", blur);
  function release() {
    if (disposed) return;
    hide(); disposed = true; cancelTimer(); cancelFrame();
    root.removeEventListener("pointerenter", enter);
    root.removeEventListener("pointerleave", leave);
    root.removeEventListener("focusin", focus);
    root.removeEventListener("focusout", blur);
    for (const target of described) {
      const ids = (target.getAttribute("aria-describedby") || "").split(/\s+/).filter(id => id && id !== bubble.id);
      if (ids.length) target.setAttribute("aria-describedby", ids.join(" "));
      else target.removeAttribute("aria-describedby");
    }
    observer?.disconnect();
  }
  return { release };
}
