// Instant feedback for the sidebar. Its items are buttons whose click goes to
// the server before anything changes on screen, so on a slow link there is a
// dead pause after the click. This answers in the browser: the clicked item
// takes the highlight at once, and if the new screen has not arrived after a
// beat, the top hairline runs and the old screen dims a little; if it is
// slower still, the brand's relay dots appear over the middle of the column.
// Everything clears the moment Blazor swaps the main column's content.
(() => {
  const DIM_AFTER_MS = 150;
  const DOTS_AFTER_MS = 400;
  const GIVE_UP_MS = 10000;
  const root = document.documentElement;

  let pending = null;   // the clicked item
  let dimTimer = 0;
  let dotsTimer = 0;
  let loader = null;
  let giveUpTimer = 0;
  let observer = null;

  // Built once, on first need: three dots in a small pill. It lives on the
  // body rather than inside the dimmed column so it is not dimmed with it.
  function dots() {
    if (!loader) {
      loader = document.createElement("div");
      loader.className = "nav-loader";
      loader.setAttribute("role", "status");
      loader.setAttribute("aria-label", "Loading");
      loader.innerHTML = "<span></span><span></span><span></span>";
      document.body.appendChild(loader);
    }
    return loader;
  }

  function showDots() {
    const main = document.querySelector(".app-main");
    if (!main) return;
    const box = main.getBoundingClientRect();
    const el = dots();
    el.style.setProperty("--nav-loader-x", `${box.left + box.width / 2}px`);
    el.style.setProperty("--nav-loader-y", `${Math.min(box.top + box.height / 2, window.innerHeight / 2)}px`);
    el.classList.add("is-on");
  }

  function clear() {
    clearTimeout(dimTimer);
    clearTimeout(dotsTimer);
    loader?.classList.remove("is-on");
    clearTimeout(giveUpTimer);
    observer?.disconnect();
    observer = null;
    pending?.classList.remove("is-pending");
    pending = null;
    root.classList.remove("is-navigating");
  }

  document.addEventListener("click", (event) => {
    const item = event.target.closest?.(".nav-item");
    // An item that opens something in place (the notifications bell) isn't a page change: nothing to wait for.
    if (!item || item.disabled || item.classList.contains("is-active") || item.hasAttribute("aria-haspopup")) return;
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey) return;

    clear();
    pending = item;
    item.classList.add("is-pending");
    dimTimer = setTimeout(() => root.classList.add("is-navigating"), DIM_AFTER_MS);
    dotsTimer = setTimeout(showDots, DOTS_AFTER_MS);
    giveUpTimer = setTimeout(clear, GIVE_UP_MS);

    // The new screen has arrived when the main column's children change, or when the column itself goes (Settings
    // has a layout of its own, so going there replaces the whole shell).
    const main = document.querySelector(".app-main");
    if (main) {
      observer = new MutationObserver((records) => {
        if (!main.isConnected || records.some((record) => record.target === main)) clear();
      });
      observer.observe(document.body, { childList: true, subtree: true });
    }
  }, true);

  // A reconnect or a full navigation leaves nothing to wait for.
  window.addEventListener("pagehide", clear);
})();
