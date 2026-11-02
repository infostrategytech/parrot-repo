// A voice conversation with the AI through ElevenLabs: the browser connects to
// a signed link the server got for the signed-in owner, ElevenLabs hears the
// person and speaks the AI's answers, and this reports what was said and who
// is talking. The level of whoever is talking is written as --level (0 to 1),
// as voice.js does, so the orb and the light round the panel move with it.

const LIBRARY = new URL("../lib/elevenlabs/elevenlabs-client-1.28.0.iife.js", import.meta.url).href;
let loading = null;

function library() {
  if (globalThis.ElevenLabsClient) return Promise.resolve(globalThis.ElevenLabsClient);
  loading ??= new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = LIBRARY;
    script.async = true;
    script.onload = () => resolve(globalThis.ElevenLabsClient);
    script.onerror = () => { loading = null; reject(new Error("The voice library could not be loaded.")); };
    document.head.append(script);
  });
  return loading;
}

/**
 * Starts the conversation. session: { signedUrl, dynamicVariables, firstMessage }.
 * names: the .NET methods for a message (who, text), a mode (speaking or
 * listening), the end (reason) and a failure (message); optionally opened(),
 * when the quiet start is over.
 * options.quietStart (ms): the call starts with the microphone muted, so the AI
 * hears a silent line and speaks first (a silence is its only cue to open). The
 * microphone comes on once it has finished its first words, or after this long.
 * Returns { mute(on), end() }.
 */
export async function converse(session, targets, dotnet, names, options = {}) {
  const { VoiceConversation } = await library();
  const elements = (Array.isArray(targets) ? targets : [targets]).filter((e) => e instanceof Element);
  let conversation = null;
  let frame = 0;
  let ending = false;
  const seen = new Set();
  let quiet = (options?.quietStart ?? 0) > 0;
  let agentSpoke = false;
  let quietTimer = 0;

  // If the page can no longer reach the server (the connection dropped, the server restarted,
  // the panel is gone), nobody can press End: the call ends itself rather than staying open.
  const call = (name, ...args) => dotnet.invokeMethodAsync(name, ...args).catch(() => end());
  // The quiet start is over: the microphone comes on and the page is told.
  const speakUp = () => {
    if (!quiet) return;
    quiet = false;
    clearTimeout(quietTimer);
    try { conversation?.setMicMuted(false); } catch { }
    if (names.opened) call(names.opened);
  };
  const stopLevel = () => {
    cancelAnimationFrame(frame);
    clearInterval(alive);
    clearTimeout(quietTimer);
    for (const element of elements) element.style.setProperty("--level", "0");
  };
  let alive = 0;

  // Ends the call however far it got: the library's own end first, then the socket itself,
  // in case the library's audio cleanup fails or hangs before it closes the connection.
  async function end() {
    if (ending) return;
    ending = true;
    stopLevel();
    window.removeEventListener("pagehide", end);
    if (!conversation) return;
    try {
      await Promise.race([conversation.endSession(), new Promise((resolve) => setTimeout(resolve, 3000))]);
    } catch { }
    finally {
      try { conversation.connection?.close?.(); } catch { }
    }
  }
  window.addEventListener("pagehide", end);

  conversation = await VoiceConversation.startSession({
    signedUrl: session.signedUrl,
    connectionType: "websocket",
    dynamicVariables: session.dynamicVariables ?? {},
    overrides: session.firstMessage ? { agent: { firstMessage: session.firstMessage } } : undefined,
    // Final transcripts only (tentative ones go to onDebug), each once: the greeting included.
    onMessage: ({ source, message, event_id }) => {
      const key = `${source}:${event_id ?? message}`;
      if (!message || seen.has(key)) return;
      seen.add(key);
      call(names.message, source === "user" ? "user" : "agent", message);
    },
    onModeChange: ({ mode }) => {
      call(names.mode, mode);
      if (mode === "speaking") agentSpoke = true;
      // Its first words are out: a moment for them to finish playing, so the speakers aren't heard as the owner.
      else if (quiet && agentSpoke) setTimeout(speakUp, 300);
    },
    onError: (message) => call(names.failed, String(message ?? "")),
    onDisconnect: (details) => {
      stopLevel();
      if (!ending) call(names.ended, String(details?.reason ?? ""));
    },
  });

  // Ended while it was still connecting (the page left, or the server went): close it now.
  if (ending) {
    ending = false;
    await end();
    throw new Error("The voice chat ended while it was starting.");
  }
  if (quiet) {
    conversation.setMicMuted(true);
    quietTimer = setTimeout(speakUp, options.quietStart);
  }
  // Every few seconds the page asks whether this call should still be on. No answer (the
  // server is gone) or "no" (the page ended it but the message never arrived) ends it here.
  alive = setInterval(() => {
    dotnet.invokeMethodAsync(names.alive).then((on) => { if (on === false) end(); }, () => end());
  }, 5000);

  const tick = () => {
    const level = Math.min(1, Math.max(conversation.getInputVolume?.() ?? 0, conversation.getOutputVolume?.() ?? 0));
    for (const element of elements) element.style.setProperty("--level", level.toFixed(3));
    frame = requestAnimationFrame(tick);
  };
  frame = requestAnimationFrame(tick);

  return {
    mute: (on) => {
      // The owner's own choice ends the quiet start.
      if (quiet) { quiet = false; clearTimeout(quietTimer); }
      conversation.setMicMuted(Boolean(on));
    },
    end,
  };
}
