// The browser's own notifications for yára: its permission, asking for it, and showing one while yára is open
// in another tab. Showing them when yára is closed needs the server (web push); everything here is real in
// every mode.
export function permission() {
  return "Notification" in window ? Notification.permission : "unsupported";
}

export async function ask() {
  if (!("Notification" in window)) return "unsupported";
  try { return await Notification.requestPermission(); } catch { return Notification.permission; }
}

// Browsers only show the permission prompt when it's asked for in the click itself: asked after a trip to
// the server, Firefox and Safari ignore it and Chrome may hide it behind an address-bar icon. So the switch
// asks here, in the click, and the page collects the answer with pending().
export function armAsk(element) {
  if (!element || element.__armed) return;
  element.__armed = true;
  element.addEventListener("click", () => {
    if ("Notification" in window && Notification.permission === "default") {
      element.__ask = Notification.requestPermission().catch(() => Notification.permission);
    }
  }, true);
}

export async function pending(element) {
  const asked = element && element.__ask;
  if (element) element.__ask = null;
  return asked ? await asked : permission();
}

// Away from yára: its tab is hidden (another tab, or the window minimised). A visible window shows the pop-up.
export function away() {
  return document.visibilityState !== "visible";
}

// One notification from the browser. The words are the generic headline only, so nothing private shows on a
// lock screen. Clicking it brings yára forward and opens what it's about.
export function show(title, body, tag, href) {
  if (!("Notification" in window) || Notification.permission !== "granted") return false;
  try {
    const note = new Notification(title, { body, tag, icon: "favicon.svg" });
    note.onclick = () => {
      window.focus();
      if (href && window.Blazor) window.Blazor.navigateTo(href);
      note.close();
    };
    return true;
  } catch {
    return false;
  }
}
