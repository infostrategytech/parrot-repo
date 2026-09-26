// Instant feedback for the sidebar. Its items are buttons whose click goes to
// the server before anything changes on screen, so on a slow link there is a
// dead pause after the click. This answers in the browser: the clicked item
// takes the highlight at once, and if the new screen has not arrived after a
// beat, the top hairline runs and the old screen dims a little. Everything
// clears the moment Blazor swaps the main column's content.
(() => {
  const DIM_AFTER_MS = 150;
  const GIVE_UP_MS = 10000;
  const root = document.documentElement;

  let pending = null;   // the clicked item
  let dimTimer = 0;
  let giveUpTimer = 0;
  let observer = null;

  function clear() {
    clearTimeout(dimTimer);
    clearTimeout(giveUpTimer);
    observer?.disconnect();
    observer = null;
    pending?.classList.remove("is-pending");
    pending = null;
    root.classList.remove("is-navigating");
  }

  document.addEventListener("click", (event) => {
    const item = event.target.closest?.(".nav-item");
    if (!item || item.disabled || item.classList.contains("is-active")) return;
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey) return;

    clear();
    pending = item;
    item.classList.add("is-pending");
    dimTimer = setTimeout(() => root.classList.add("is-navigating"), DIM_AFTER_MS);
    giveUpTimer = setTimeout(clear, GIVE_UP_MS);

    // The new screen has arrived when the main column's children change.
    const main = document.querySelector(".app-main");
    if (main) {
      observer = new MutationObserver(clear);
      observer.observe(main, { childList: true });
    }
  }, true);

  // A reconnect or a full navigation leaves nothing to wait for.
  window.addEventListener("pagehide", clear);
})();
