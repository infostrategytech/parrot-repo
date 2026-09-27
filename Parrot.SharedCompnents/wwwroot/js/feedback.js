// Sound and haptics for messages only: a reply leaving, a customer writing
// in, and a reply that failed to send. Actions elsewhere (buttons, switches,
// screens, toasts) are deliberately silent. Every sound is synthesised with
// Web Audio, so there are no files to download: soft sine/triangle voices,
// short attacks and quick decays, mixed low. Nothing loops or nags.
//
// Haptics use navigator.vibrate, which Android browsers honour. iOS Safari
// does not expose vibration to web pages, so iPhones get sound only.
//
// People switch it off in Settings → Notifications; the choice is kept in
// localStorage. Blazor reaches this through window.parrotFeedback.
(() => {
  const STORAGE_KEY = "parrot-feedback";
  const MASTER_GAIN = 0.2;
  const REPEAT_GUARD_MS = 45;


  let ctx = null;
  let master = null;
  let enabled = readEnabled();
  const lastPlayed = new Map();

  function readEnabled() {
    try { return localStorage.getItem(STORAGE_KEY) !== "off"; } catch { return true; }
  }

  function audio() {
    if (!ctx) {
      const AudioContextClass = window.AudioContext || window.webkitAudioContext;
      if (!AudioContextClass) return null;
      ctx = new AudioContextClass();
      master = ctx.createGain();
      master.gain.value = MASTER_GAIN;
      // A gentle low-pass takes the edge off every voice at once.
      const soften = ctx.createBiquadFilter();
      soften.type = "lowpass";
      soften.frequency.value = 5200;
      master.connect(soften).connect(ctx.destination);
    }
    if (ctx.state === "suspended") ctx.resume();
    return ctx;
  }

  // One enveloped voice. Exponential ramps avoid clicks at either end.
  function tone({ freq, to, type = "sine", at = 0, dur = 0.08, gain = 0.5, attack = 0.005 }) {
    const c = audio();
    if (!c) return;
    const t = c.currentTime + at;
    const osc = c.createOscillator();
    const env = c.createGain();
    osc.type = type;
    osc.frequency.setValueAtTime(freq, t);
    if (to) osc.frequency.exponentialRampToValueAtTime(to, t + dur);
    env.gain.setValueAtTime(0.0001, t);
    env.gain.exponentialRampToValueAtTime(gain, t + attack);
    env.gain.exponentialRampToValueAtTime(0.0001, t + dur);
    osc.connect(env).connect(master);
    osc.start(t);
    osc.stop(t + dur + 0.03);
  }

  function buzz(pattern) {
    try { navigator.vibrate?.(pattern); } catch { /* not supported */ }
  }

  const sounds = {
    // Two soft, falling low notes, for a reply that could not be sent.
    error() {
      tone({ freq: 311.13, to: 293.66, dur: 0.12, gain: 0.42 });
      tone({ freq: 261.63, to: 233.08, at: 0.12, dur: 0.16, gain: 0.38 });
      buzz([30, 50, 30]);
    },

    // A light rising sweep as a reply leaves.
    send() {
      tone({ freq: 520, to: 1180, dur: 0.12, gain: 0.26 });
      tone({ freq: 1567.98, type: "triangle", at: 0.1, dur: 0.07, gain: 0.12 });
      buzz(10);
    },

    // A bubble for a new customer message: a pop and a bright answer.
    incoming() {
      tone({ freq: 880, to: 1318.51, dur: 0.07, gain: 0.3 });
      tone({ freq: 1760, type: "triangle", at: 0.075, dur: 0.12, gain: 0.16 });
      buzz([8, 30, 8]);
    },
  };

  function play(name, arg) {
    if (!enabled || !sounds[name]) return;
    // A sound that has only just played is not played again: repetition is
    // what makes feedback grating.
    const now = performance.now();
    const key = name;
    if (now - (lastPlayed.get(key) ?? -Infinity) < REPEAT_GUARD_MS) return;
    lastPlayed.set(key, now);
    try { sounds[name](arg); } catch { /* audio is a nicety; never break the page */ }
  }

  function setEnabled(on) {
    enabled = !!on;
    try { localStorage.setItem(STORAGE_KEY, enabled ? "on" : "off"); } catch { /* private mode */ }
    if (enabled) play("incoming"); // a preview of what they have switched on
  }

  window.parrotFeedback = { play, setEnabled, isEnabled: () => enabled };
})();
