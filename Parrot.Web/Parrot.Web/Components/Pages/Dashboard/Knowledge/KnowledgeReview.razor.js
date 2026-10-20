// A found detail's card, swiped on a phone (designer, 20 October 2026): right, that's right; left, not right;
// up, later. The card follows the finger, tilting, with its stamp showing as it goes; past a third of its width
// (or a flick up) it flies off and the page decides; short of that, it settles back. A mouse is left to the
// buttons, and nothing starts on a button, a link or a box.
export function swipe(card, dotnet) {
  let start = null, dx = 0, dy = 0, at = 0;

  const reset = () => {
    card.style.transition = "";
    card.style.transform = "";
    card.style.removeProperty("--swipe");
    card.style.removeProperty("--lift");
  };

  const down = (event) => {
    if (event.pointerType === "mouse" || card.dataset.swipe !== "true") return;
    if (event.target.closest("button, a, textarea, input")) return;
    start = { x: event.clientX, y: event.clientY };
    at = performance.now();
    dx = dy = 0;
    card.setPointerCapture?.(event.pointerId);
    card.classList.add("is-dragging");
  };

  const move = (event) => {
    if (!start) return;
    dx = event.clientX - start.x;
    dy = event.clientY - start.y;
    const width = card.getBoundingClientRect().width || 1;
    const pull = Math.max(-1, Math.min(1, dx / (width * 0.35)));
    card.style.transform = `translate(${dx}px, ${Math.min(0, dy)}px) rotate(${pull * 12}deg)`;
    card.style.setProperty("--swipe", pull.toFixed(3));
    card.style.setProperty("--lift", Math.max(0, Math.min(1, -dy / 140)).toFixed(3));
  };

  const up = () => {
    if (!start) return;
    start = null;
    card.classList.remove("is-dragging");
    const rect = card.getBoundingClientRect();
    const quick = performance.now() - at < 260;
    let way = null;
    if (dx > rect.width / 3 || (quick && dx > 60)) way = "right";
    else if (dx < -rect.width / 3 || (quick && dx < -60)) way = "left";
    else if (dy < -140 || (quick && dy < -80)) way = "up";
    if (!way) {
      card.style.transition = "transform 200ms ease-out";
      card.style.transform = "";
      card.style.removeProperty("--swipe");
      card.style.removeProperty("--lift");
      setTimeout(() => { card.style.transition = ""; }, 220);
      return;
    }
    const off = way === "up" ? `translate(${dx}px, -120%)` : `translate(${way === "right" ? 150 : -150}%, ${Math.min(0, dy)}px) rotate(${way === "right" ? 18 : -18}deg)`;
    card.style.transition = "transform 200ms ease-in, opacity 200ms ease-in";
    card.style.transform = off;
    card.style.opacity = "0";
    setTimeout(() => dotnet.invokeMethodAsync("SwipedAsync", way).catch(() => {}), 190);
  };

  card.addEventListener("pointerdown", down);
  card.addEventListener("pointermove", move);
  card.addEventListener("pointerup", up);
  card.addEventListener("pointercancel", () => { start = null; card.classList.remove("is-dragging"); reset(); });

  return {
    dispose() {
      card.removeEventListener("pointerdown", down);
      card.removeEventListener("pointermove", move);
      card.removeEventListener("pointerup", up);
    },
  };
}
