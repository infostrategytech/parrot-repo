// Voice for the product's talking surfaces: the agent speaking, the person
// speaking back, a voice note, and the live level that makes the mascot glow.
//
// Today this uses the browser's own speech (speechSynthesis for the agent's
// voice, SpeechRecognition for the person's words). ElevenLabs replaces it
// later behind the same calls: speak / listen / record / level. The page never
// talks to a browser API directly.

const Recognition = globalThis.SpeechRecognition || globalThis.webkitSpeechRecognition;

/** What this browser can do, so the page can offer only what works. */
export function capabilities() {
  return {
    speak: "speechSynthesis" in globalThis,
    listen: !!Recognition,
    microphone: !!navigator.mediaDevices?.getUserMedia,
    record: "MediaRecorder" in globalThis
  };
}

// ── Level ───────────────────────────────────────────────────────────────────
// The level is written straight onto an element as --level (0 to 1), many
// times a second, without a render. Element can be a list: the orb and the
// light round the surface move together.

function setLevel(element, value) {
  for (const target of [].concat(element)) {
    if (target instanceof Element) target.style.setProperty("--level", value.toFixed(3));
  }
}

/** The microphone's level on element until released. Rejects if refused. */
export async function micLevel(element) {
  const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } });
  const context = new AudioContext();
  const analyser = context.createAnalyser();
  analyser.fftSize = 512;
  context.createMediaStreamSource(stream).connect(analyser);
  const samples = new Uint8Array(analyser.fftSize);
  let smooth = 0, frame = 0, running = true;
  const tick = () => {
    if (!running) return;
    analyser.getByteTimeDomainData(samples);
    let sum = 0;
    for (const v of samples) { const x = (v - 128) / 128; sum += x * x; }
    const target = Math.min(1, Math.sqrt(sum / samples.length) * 5);
    smooth += (target - smooth) * 0.35;
    setLevel(element, smooth);
    frame = requestAnimationFrame(tick);
  };
  tick();
  return {
    stream,
    release() {
      running = false;
      cancelAnimationFrame(frame);
      stream.getTracks().forEach((track) => track.stop());
      context.close();
      setLevel(element, 0);
    }
  };
}

// ── Speaking ────────────────────────────────────────────────────────────────

// The agent has one voice, a woman's, the same for every line. Women's
// voices by name across Chrome, Edge, Windows and macOS, best first: a
// Nigerian English voice where the browser has one (Edge's Ezinne), then
// British, then American. A name that marks a man's voice is never chosen.
const WOMEN = [
  "Microsoft Ezinne", "Google UK English Female", "Microsoft Sonia", "Microsoft Libby", "Microsoft Maisie",
  "Microsoft Hazel", "Microsoft Susan", "Serena", "Kate", "Stephanie", "Fiona", "Moira", "Tessa", "Karen",
  "Microsoft Aria", "Microsoft Jenny", "Microsoft Michelle", "Microsoft Zira", "Samantha", "Victoria", "Allison",
  "Ava", "Susan", "Zoe", "Google US English"
];
// Whole words only: "Female" must not match "male".
const MEN = /\b(male|man|david|mark|george|ryan|guy|daniel|alex|fred|thomas|james|oliver|arthur|aaron|gordon|tom|rishi|abeo|brian|eric|roger|christopher|steffan|william|lee|liam|connor|elliot|ethan|noah|andrew|davis|jason|tony)\b/i;

// The browser lists its voices a moment after the page loads. Speaking
// before then falls back to the system's default voice, which on Windows is
// a man's; so wait for the list (briefly) before the first line.
function voicesReady() {
  const now = speechSynthesis.getVoices();
  if (now.length || typeof speechSynthesis.addEventListener !== "function") return Promise.resolve(now);
  return new Promise((resolve) => {
    const ready = () => { speechSynthesis.removeEventListener("voiceschanged", ready); resolve(speechSynthesis.getVoices()); };
    speechSynthesis.addEventListener("voiceschanged", ready);
    setTimeout(ready, 1500);
  });
}

let agentVoice = null;
async function womansVoice() {
  if (agentVoice) return agentVoice;
  const voices = (await voicesReady()).filter((v) => v.lang?.toLowerCase().startsWith("en") && !MEN.test(v.name));
  agentVoice = WOMEN.map((name) => voices.find((v) => v.name.includes(name))).find(Boolean)
    ?? voices.find((v) => /female|woman/i.test(v.name))
    ?? null;
  return agentVoice;
}
if ("speechSynthesis" in globalThis) womansVoice();

// How fast speech runs, for voices that do not say where they are: about 15
// characters a second at the rate used here.
const RATE = 1.03;
// How long the voice may take to start before the connection is taken to be too poor for voice.
const STALL_MS = 3000;
const CHARS_PER_SECOND = 15 * RATE;

/**
 * Says text aloud, then calls dotnet[done]. As each word is spoken:
 * - captions: every element inside with data-at (a character position in
 *   text) gets data-on once the voice reaches that position, so a transcript
 *   can type out in step with the voice;
 * - element: its --level kicks, so a visual pulses on the words themselves.
 * Word positions come from the voice's own boundary events where it sends
 * them, and from an estimate of its pace where it does not, timed from when
 * the voice actually starts. said() tells how far it got (to cut a transcript
 * short when interrupted). If the voice has not started within STALL_MS (a
 * network voice on a poor connection), it gives up and calls
 * dotnet[stalled] instead of dotnet[done], so the page can carry on in text.
 */
export async function speak(element, text, dotnet, done, captions, stalled) {
  const voice = "speechSynthesis" in globalThis ? await womansVoice() : null;
  const marks = captions instanceof Element ? [...captions.querySelectorAll("[data-at]")] : [];
  const starts = [...text.matchAll(/\S+/g)].map((m) => m.index);
  let finished = false, boundaries = false, started = false, said = -1, word = 0, kick = 0, smooth = 0, frame = 0;
  let begun = performance.now();
  let stall = 0;

  const reach = (index) => {
    if (index <= said) return;
    said = index;
    while (word < starts.length && starts[word] <= said) { word++; kick = 1; }
    for (const mark of marks) if (!mark.hasAttribute("data-on") && Number(mark.dataset.at) <= said) mark.setAttribute("data-on", "");
  };
  // Where the voice has got to keeps time on its own clock, so the words
  // keep pace with the voice even when the screen is slow to draw; frames
  // only smooth the visual's pulse.
  const pace = setInterval(() => {
    if (started && !boundaries) reach(Math.floor(((performance.now() - begun) / 1000) * CHARS_PER_SECOND));
  }, 50);
  let last = begun;
  const tick = (now) => {
    if (finished) return;
    const frames = Math.min(6, (now - last) / 16.7);
    last = now;
    kick *= Math.pow(0.9, frames);
    smooth += (Math.min(1, 0.08 + kick * 0.85) - smooth) * Math.min(1, 0.35 * frames);
    setLevel(element, smooth);
    frame = requestAnimationFrame(tick);
  };
  frame = requestAnimationFrame(tick);

  const stop = () => { finished = true; clearInterval(pace); clearTimeout(stall); cancelAnimationFrame(frame); setLevel(element, 0); };
  const finish = () => {
    if (finished) return;
    reach(text.length);
    stop();
    dotnet.invokeMethodAsync(done).catch(() => {});
  };
  const handle = (cancel) => ({ cancel() { cancel(); stop(); }, said() { return Math.max(0, said); } });

  if (!("speechSynthesis" in globalThis)) {
    started = true;
    const timer = setTimeout(finish, Math.min(12000, 900 + (text.length / CHARS_PER_SECOND) * 1000));
    return handle(() => clearTimeout(timer));
  }
  speechSynthesis.cancel();
  const utterance = new SpeechSynthesisUtterance(text);
  if (voice) {
    utterance.voice = voice;
    utterance.lang = voice.lang;
  }
  utterance.rate = RATE;
  const begin = () => {
    if (started) return;
    started = true;
    begun = performance.now();
    clearTimeout(stall);
  };
  utterance.onstart = begin;
  utterance.onboundary = (event) => {
    begin();
    if (event.name && event.name !== "word") return;
    boundaries = true;
    reach(event.charIndex);
  };
  utterance.onend = finish;
  utterance.onerror = finish;
  // Some browsers never fire onend when the tab is hidden: a long safety net.
  const safety = setTimeout(finish, 2500 + text.length * 120);
  utterance.addEventListener("end", () => clearTimeout(safety));
  if (stalled) {
    stall = setTimeout(() => {
      if (started || finished) return;
      clearTimeout(safety);
      speechSynthesis.cancel();
      stop();
      dotnet.invokeMethodAsync(stalled).catch(() => {});
    }, STALL_MS);
  }
  speechSynthesis.speak(utterance);
  return handle(() => { clearTimeout(safety); speechSynthesis.cancel(); });
}

// ── The connection ──────────────────────────────────────────────────────────
// Voice needs a good connection (the agent's voice and the person's words both
// travel). "offline", "poor" (2G, or the person has asked to save data) or "ok".

function networkNow() {
  const connection = navigator.connection;
  if (!navigator.onLine) return "offline";
  if (connection && (connection.saveData || /(^|-)2g$/.test(connection.effectiveType || ""))) return "poor";
  return "ok";
}

export function networkState() { return networkNow(); }

/** Calls dotnet[method](state) when the connection changes between offline, poor and ok. */
export function watchNetwork(dotnet, method) {
  let last = networkNow();
  const changed = () => {
    const now = networkNow();
    if (now === last) return;
    last = now;
    dotnet.invokeMethodAsync(method, now).catch(() => {});
  };
  addEventListener("online", changed);
  addEventListener("offline", changed);
  navigator.connection?.addEventListener?.("change", changed);
  return {
    dispose() {
      removeEventListener("online", changed);
      removeEventListener("offline", changed);
      navigator.connection?.removeEventListener?.("change", changed);
    }
  };
}

// ── Listening ───────────────────────────────────────────────────────────────

let activeListener = null;

// A pause this long after some words ends the person's turn: long enough
// for a pause to think, mid-sentence. (A Done button can end it sooner.)
const QUIET_MS = 1400;

/**
 * Listens for one reply. Calls dotnet[interim](text) as words arrive and
 * dotnet[final](text) once the person stops, or dotnet[failed](reason).
 */
export function listen(dotnet, interim, final, failed) {
  let settled = false;
  let recognition = null;
  let quiet = 0;
  const settle = (method, value) => {
    if (settled) return;
    settled = true;
    clearInterval(quiet);
    activeListener = null;
    dotnet.invokeMethodAsync(method, value).catch(() => {});
  };
  const handle = {
    inject(text) { recognition?.abort(); settle(final, text); },
    hear(text) { if (!settled) dotnet.invokeMethodAsync(interim, text).catch(() => {}); },
    stop() { recognition ? recognition.stop() : settle(final, ""); },
    abort() { settled = true; clearInterval(quiet); activeListener = null; recognition?.abort(); }
  };
  activeListener = handle;
  if (!Recognition) return handle;

  recognition = new Recognition();
  recognition.lang = "en-GB";
  recognition.interimResults = true;
  recognition.continuous = false;
  let heard = "";
  let lastWord = 0;
  recognition.onresult = (event) => {
    let text = "";
    for (const result of event.results) text += result[0].transcript;
    heard = text.trim();
    lastWord = performance.now();
    dotnet.invokeMethodAsync(interim, heard).catch(() => {});
  };
  // The browser can take a second or more to decide someone has finished.
  // Once they have said something and gone quiet for a beat, end the turn.
  quiet = setInterval(() => {
    if (heard && performance.now() - lastWord > QUIET_MS) recognition.stop();
  }, 150);
  recognition.onerror = (event) => settle(failed, event.error || "error");
  recognition.onend = () => settle(heard ? final : failed, heard || "no-speech");
  try { recognition.start(); } catch (error) { settle(failed, String(error)); }
  return handle;
}

// For demos and automated checks without a microphone: answer as if spoken.
globalThis.auroraVoiceSay = (text) => { if (!activeListener) return false; activeListener.inject(text); return true; };
// The same, as words still arriving (an interim result), for checking interruptions.
globalThis.auroraVoiceHear = (text) => { if (!activeListener) return false; activeListener.hear(text); return true; };

// ── Voice notes ─────────────────────────────────────────────────────────────

/**
 * Records a voice note, as a messaging app does: the level shows on element
 * while it records, the audio is kept (in this browser only, as a blob URL,
 * until the API takes uploads) and its words are transcribed at the same
 * time. stop() resolves with the transcript, the length in seconds, the
 * audio's URL and its waveform (peaks, 0 to 1); cancel() throws it away.
 */
export async function recordNote(element) {
  const mic = await micLevel(element);
  const started = performance.now();
  // The audio, for playing back in the conversation.
  const chunks = [];
  let recorder = null;
  try {
    recorder = new MediaRecorder(mic.stream);
    recorder.ondataavailable = (event) => { if (event.data.size) chunks.push(event.data); };
    recorder.start();
  } catch { recorder = null; }
  // The waveform: the level, sampled as it records.
  const levels = [];
  const sampler = setInterval(() => levels.push(Number.parseFloat(element?.style?.getPropertyValue("--level")) || 0), 100);
  let transcript = "";
  let recognition = null;
  if (Recognition) {
    recognition = new Recognition();
    recognition.lang = "en-GB";
    recognition.interimResults = true;
    recognition.continuous = true;
    recognition.onresult = (event) => {
      let text = "";
      for (const result of event.results) text += result[0].transcript;
      transcript = text.trim();
    };
    recognition.onend = () => { if (recognition) try { recognition.start(); } catch { /* stopped */ } };
    try { recognition.start(); } catch { recognition = null; }
  }
  let injected = null;
  const note = {
    inject(text) { injected = text; },
    async stop() {
      const seconds = Math.max(1, Math.round((performance.now() - started) / 1000));
      clearInterval(sampler);
      const r = recognition; recognition = null;
      if (r) { r.onend = null; r.stop(); await new Promise((ok) => setTimeout(ok, 350)); }
      let url = null;
      if (recorder && recorder.state !== "inactive") {
        await new Promise((ok) => { recorder.onstop = ok; recorder.stop(); });
        if (chunks.length) url = URL.createObjectURL(new Blob(chunks, { type: recorder.mimeType || "audio/webm" }));
      }
      mic.release();
      activeNote = null;
      return { transcript: injected ?? transcript, seconds, url, peaks: peaksOf(levels) };
    },
    cancel() {
      clearInterval(sampler);
      const r = recognition; recognition = null;
      if (r) { r.onend = null; r.abort(); }
      try { if (recorder && recorder.state !== "inactive") recorder.stop(); } catch { /* already stopped */ }
      mic.release();
      activeNote = null;
    }
  };
  activeNote = note;
  return note;
}

// A waveform of 32 bars from the sampled levels: the loudest moment in each
// slice, scaled so the loudest bar is full height, never quite flat.
function peaksOf(levels, bars = 32) {
  if (!levels.length) return Array(bars).fill(0.15);
  const out = [];
  for (let i = 0; i < bars; i++) {
    const from = Math.floor((i * levels.length) / bars), to = Math.max(from + 1, Math.floor(((i + 1) * levels.length) / bars));
    out.push(Math.max(...levels.slice(from, to)));
  }
  const top = Math.max(...out, 0.01);
  return out.map((p) => Math.round(Math.max(0.15, p / top) * 100) / 100);
}

/**
 * Plays a voice note: host gets --progress (0 to 1) as it plays, and
 * dotnet[changed](playing) on play, pause and end. seconds is the note's
 * known length (a recorded blob often reports no duration of its own).
 */
export function bindPlayer(audio, host, seconds, dotnet, changed) {
  const length = () => (Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : seconds) || 1;
  const update = () => host?.style.setProperty("--progress", Math.min(1, audio.currentTime / length()).toFixed(3));
  const tell = (playing) => dotnet.invokeMethodAsync(changed, playing).catch(() => {});
  const onPlay = () => tell(true);
  const onPause = () => tell(false);
  const onEnded = () => { audio.currentTime = 0; update(); tell(false); };
  audio.addEventListener("timeupdate", update);
  audio.addEventListener("play", onPlay);
  audio.addEventListener("pause", onPause);
  audio.addEventListener("ended", onEnded);
  return {
    toggle() { if (audio.paused) audio.play().catch(() => tell(false)); else audio.pause(); },
    dispose() {
      audio.pause();
      audio.removeEventListener("timeupdate", update);
      audio.removeEventListener("play", onPlay);
      audio.removeEventListener("pause", onPause);
      audio.removeEventListener("ended", onEnded);
    }
  };
}

let activeNote = null;
// For demos and automated checks: what the voice note "said".
globalThis.auroraVoiceNoteSay = (text) => { if (!activeNote) return false; activeNote.inject(text); return true; };

// ── Microphone permission ───────────────────────────────────────────────────
// On desktop the browser's prompt is small and easy to miss, and the
// microphone can be blocked in three places: the site, the browser, or the
// computer itself. These let the page say which, and how to fix it.

/** granted | prompt | denied, or unknown where the browser will not say. */
export async function micPermission() {
  try { return (await navigator.permissions.query({ name: "microphone" })).state; }
  catch { return "unknown"; }
}

/** Calls dotnet[method](state) when the site's microphone permission changes. */
export async function watchMicPermission(dotnet, method) {
  try {
    const status = await navigator.permissions.query({ name: "microphone" });
    const changed = () => dotnet.invokeMethodAsync(method, status.state).catch(() => {});
    status.addEventListener("change", changed);
    return { dispose() { status.removeEventListener("change", changed); } };
  } catch {
    return { dispose() {} };
  }
}

/**
 * Asks for the microphone (showing the browser's prompt if it has not been
 * answered) and lets it go straight away. granted | denied (by the person or
 * the site) | system (the computer blocks the browser) | none (no microphone)
 * | busy (another app has it) | error.
 */
export async function requestMic() {
  if (!navigator.mediaDevices?.getUserMedia) return "none";
  try {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    stream.getTracks().forEach((track) => track.stop());
    return "granted";
  } catch (error) {
    switch (error?.name) {
      case "NotAllowedError":
      case "SecurityError":
        return /system/i.test(error.message ?? "") ? "system" : "denied";
      case "NotFoundError":
      case "OverconstrainedError":
        return "none";
      case "NotReadableError":
      case "AbortError":
        return "busy";
      default:
        return "error";
    }
  }
}

/** Calls dotnet[method](visible) when the tab is hidden or shown again. */
export function watchVisibility(dotnet, method) {
  const changed = () => dotnet.invokeMethodAsync(method, !document.hidden).catch(() => {});
  document.addEventListener("visibilitychange", changed);
  return { dispose() { document.removeEventListener("visibilitychange", changed); } };
}

/**
 * Ctrl+D (⌘D on a Mac) toggles mute, as in Google Meet. Ctrl+Shift+M would
 * open Chrome's profile menu. The browser's own use of Ctrl+D (bookmark) is
 * held back only while voice is on.
 */
export function bindMuteShortcut(dotnet, method) {
  const pressed = (event) => {
    if (!(event.ctrlKey || event.metaKey) || event.shiftKey || event.altKey || event.key.toLowerCase() !== "d") return;
    event.preventDefault();
    dotnet.invokeMethodAsync(method).catch(() => {});
  };
  document.addEventListener("keydown", pressed, true);
  return { dispose() { document.removeEventListener("keydown", pressed, true); } };
}
