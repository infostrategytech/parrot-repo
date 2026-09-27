// Sound and haptics. Every sound is synthesised with Web Audio, so there are
// no files to download, and they share one tonal family: soft sine/triangle
// voices on a C-major pentatonic, short attacks and quick decays, mixed low.
// The aim is feedback that feels good for a whole working day: an action
// answers the instant it is taken, success rises, trouble falls, and nothing
// loops, nags, or plays for its own sake.
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

  // C-major pentatonic from C5: the digits of a code climb it one step each.
  const SCALE = [523.25, 587.33, 659.25, 783.99, 880.0, 1046.5, 1174.66, 1318.51];

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
    // A soft, dry tick: a button acknowledging the press.
    tap() { tone({ freq: 1500, to: 900, dur: 0.028, gain: 0.28 }); buzz(5); },

    // Moving between screens: the tick plus a quiet body note.
    nav() {
      tone({ freq: 1400, to: 950, dur: 0.03, gain: 0.25 });
      tone({ freq: 659.25, type: "triangle", at: 0.012, dur: 0.07, gain: 0.12 });
      buzz(8);
    },

    // Switches: two ticks that step up for on, down for off.
    toggleOn() {
      tone({ freq: 740, type: "triangle", dur: 0.045, gain: 0.3 });
      tone({ freq: 1108.73, type: "triangle", at: 0.05, dur: 0.06, gain: 0.3 });
      buzz(10);
    },
    toggleOff() {
      tone({ freq: 1108.73, type: "triangle", dur: 0.045, gain: 0.26 });
      tone({ freq: 740, type: "triangle", at: 0.05, dur: 0.06, gain: 0.22 });
      buzz(10);
    },

    // A code digit, climbing the scale with each one typed.
    digit(index = 0) {
      const freq = SCALE[Math.max(0, Math.min(SCALE.length - 1, index))];
      tone({ freq, type: "triangle", dur: 0.07, gain: 0.26 });
      buzz(6);
    },

    // A rising major arpeggio with a faint octave shimmer on top.
    success() {
      [1046.5, 1318.51, 1567.98].forEach((freq, i) =>
        tone({ freq, type: "triangle", at: i * 0.065, dur: 0.24 - i * 0.03, gain: 0.34 - i * 0.06 }));
      tone({ freq: 3135.96, at: 0.13, dur: 0.18, gain: 0.05 });
      buzz([12, 40, 18]);
    },

    // Two soft, falling low notes: noticeable, never alarming.
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
    // A sound that has only just played is not played again (two clicks on one
    // press, a toast re-rendering): repetition is what makes feedback grating.
    const now = performance.now();
    const key = name === "digit" ? `digit${arg}` : name;
    if (now - (lastPlayed.get(key) ?? -Infinity) < REPEAT_GUARD_MS) return;
    lastPlayed.set(key, now);
    try { sounds[name](arg); } catch { /* audio is a nicety; never break the page */ }
  }

  function setEnabled(on) {
    enabled = !!on;
    try { localStorage.setItem(STORAGE_KEY, enabled ? "on" : "off"); } catch { /* private mode */ }
    if (enabled) play("toggleOn");
  }

  window.parrotFeedback = { play, setEnabled, isEnabled: () => enabled };

  // ── Wired from the page, so no component has to remember to ask ─────────

  document.addEventListener("click", (event) => {
    const target = event.target;
    if (!(target instanceof Element)) return;

    const toggle = target.closest('[role="switch"]:not([disabled])');
    if (toggle) {
      // The switch flips on the server's next render, so read where it is now.
      play(toggle.getAttribute("aria-checked") === "true" ? "toggleOff" : "toggleOn");
      return;
    }

    const navItem = target.closest(".nav-item:not([disabled])");
    if (navItem) {
      if (!navItem.classList.contains("is-active")) play("nav");
      return;
    }

    const button = target.closest("button:not([disabled]), .ds-button:not([disabled])");
    if (button && !button.matches(".toast__dismiss, .field__eye")) play("tap");
  }, true);

  // The code field: each digit climbs the scale.
  document.addEventListener("input", (event) => {
    const input = event.target;
    if (!(input instanceof HTMLInputElement) || input.autocomplete !== "one-time-code") return;
    const digits = input.value.replace(/\D/g, "").length;
    if (digits > 0 && event.inputType !== "deleteContentBackward") play("digit", digits - 1);
  }, true);

  // Toasts and inline errors are drawn by Blazor; their arrival is the cue.
  const observer = new MutationObserver((records) => {
    for (const record of records) {
      for (const node of record.addedNodes) {
        if (!(node instanceof Element)) continue;
        const toast = node.matches(".toast") ? node : node.querySelector?.(".toast");
        if (toast) {
          if (toast.classList.contains("toast--error")) play("error");
          else if (toast.classList.contains("toast--success")) play("success");
          else play("tap");
          continue;
        }
        if (node.matches(".panel__alert:not(.panel__alert--info), .field__error")
            || node.querySelector?.(".panel__alert:not(.panel__alert--info), .field__error")) {
          play("error");
        }
      }
    }
  });

  const start = () => observer.observe(document.body, { childList: true, subtree: true });
  if (document.body) start(); else document.addEventListener("DOMContentLoaded", start);
})();
