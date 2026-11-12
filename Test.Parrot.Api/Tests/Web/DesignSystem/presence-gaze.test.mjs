import test from 'node:test';
import assert from 'node:assert/strict';
import { bindPresence, springStep, feel, lid, POSES, align, outline, voiceAt, turnAt, eyeOnTurn, pupilOffset, WHITES, CELEBRATION, ATTENTION } from '../../../../Parrot.SharedCompnents/wwwroot/js/presence-avatar.js';

test('the happy turn goes right round: the eyes slide across, round the edge, and come home', () => {
    assert.equal(turnAt(-1).spin, 0); assert.equal(turnAt(CELEBRATION.length + 1).spin, 0);
    const mid = turnAt(CELEBRATION.start + CELEBRATION.turn / 2);
    assert.ok(Math.abs(mid.spin - Math.PI) < 1e-9 && mid.arc > 0.99);
    const end = turnAt(CELEBRATION.start + CELEBRATION.turn);
    assert.ok(Math.abs(end.spin - 2 * Math.PI) < 1e-9 && Math.abs(end.arc) < 1e-9);
    for (const x of [49, 79]) {
        const home = eyeOnTurn(0, x, 64, 40), round = eyeOnTurn(2 * Math.PI, x, 64, 40);
        assert.ok(Math.abs(home.dx) < 1e-9 && Math.abs(home.facing - 1) < 1e-9);
        assert.ok(Math.abs(round.dx) < 1e-9 && Math.abs(round.facing - 1) < 1e-9);
        assert.ok(eyeOnTurn(Math.PI, x, 64, 40).facing < 0); // round the back: hidden
    }
});
test('a pupil looks around inside its white and never leaves it', () => {
    for (const [gx, gy] of [[10, 0], [-10, 9], [9.5, -9], [-7.5, 7.5], [0, 0]]) assert.ok(Math.hypot(...pupilOffset(gx, gy)) <= WHITES.reach + 1e-9);
    assert.ok(13 - 7 >= WHITES.reach); // white radius less pupil radius, in PresenceAvatar.razor
});

test('busy states look where the work is: thinking up and away, working down, needing help down and aside', () => {
    assert.ok(POSES.thinking.gx >= 6 && POSES.thinking.gy <= -6);
    assert.ok(POSES.working.gy >= 5);
    assert.ok(POSES.blocked.gx <= -4 && POSES.blocked.gy >= 5);
});
test('speech comes in phrases with pauses, never a steady bob', () => {
    const voice = { phraseEnd: 0, pauseEnd: 0, next: 0, syllables: [] };
    const levels = [];
    for (let t = 0; t < 6; t += 1 / 60) levels.push(voiceAt(voice, t).level);
    assert.ok(Math.max(...levels) > 0.5 && Math.min(...levels) === 0);
    // There are quiet stretches (pauses between phrases) of at least a fifth of a second.
    let quiet = 0, longest = 0;
    for (const level of levels) { quiet = level < 0.02 ? quiet + 1 : 0; longest = Math.max(longest, quiet); }
    assert.ok(longest >= 12);
});

test('a new shape lines up with the old one point for point, and every in-between outline is closed and smooth', () => {
    const ring = (r, turn = 0) => Array.from({ length: 12 }, (_, i) => { const a = (i + turn) / 12 * 2 * Math.PI; return [64 + r * Math.cos(a), 64 + r * Math.sin(a)]; });
    const from = ring(40), to = ring(30, 5); // the same circle traced from a different starting point
    const lined = align(from, to);
    lined.forEach((p, i) => assert.ok(Math.hypot(p[0] - to[(i + 7) % 12][0], p[1] - to[(i + 7) % 12][1]) < 1e-9));
    const d = outline(from);
    assert.match(d, /^M[\d.]+ [\d.]+(Q[\d.]+ [\d.]+ [\d.]+ [\d.]+){12}Z$/);
});

// A tiny stand-in for the avatar's DOM: the parts the engine writes to, and the control around it.
function environment({ reduced = false, classes = [] } = {}) {
    const old = { matchMedia: globalThis.matchMedia, requestAnimationFrame: globalThis.requestAnimationFrame, cancelAnimationFrame: globalThis.cancelAnimationFrame, IntersectionObserver: globalThis.IntersectionObserver };
    const frames = new Map(), mediaEvents = new Map(); let next = 0;
    const media = { matches: reduced, addEventListener: (name, callback) => mediaEvents.set(name, callback), removeEventListener: name => mediaEvents.delete(name) };
    globalThis.matchMedia = () => media;
    globalThis.requestAnimationFrame = callback => { frames.set(++next, callback); return next; };
    globalThis.cancelAnimationFrame = id => frames.delete(id);
    globalThis.IntersectionObserver = undefined;
    const part = () => ({ style: { transform: '', opacity: '' } });
    const parts = { '.presence-avatar__character': part(), '.presence-avatar__gaze': part() };
    const many = { '.presence-avatar__pupil': [part(), part()], '.presence-avatar__happy': [part(), part()],
        '.presence-avatar__eye': [part(), part()], '.presence-avatar__eyeball': [part(), part()] };
    const attributes = new Map(), listeners = new Map(), heard = { level: null };
    const control = { addEventListener: (name, callback) => listeners.set(name, callback), removeEventListener: name => listeners.delete(name) };
    const element = {
        querySelector: s => parts[s] ?? null, querySelectorAll: s => many[s] ?? [], closest: () => control,
        classList: { contains: name => classes.includes(name) },
        // The voice module's --level, as the tests set it.
        style: { getPropertyValue: name => name === '--level' && heard.level !== null ? String(heard.level) : '' },
        setAttribute: (name, value) => attributes.set(name, value), removeAttribute: name => attributes.delete(name),
        getBoundingClientRect: () => ({ left: 0, top: 0, width: 100, height: 100 }),
    };
    let time = 1000;
    // The engine's clock (cues, hover) runs on the same fake time as the frames.
    old.performance = globalThis.performance; globalThis.performance = { now: () => time };
    return { element, parts, many, attributes, listeners, media, mediaEvents, frames, heard,
        run(ms = 1000) { for (let t = 0; t < ms; t += 16) { time += 16; for (const [id, callback] of [...frames]) { frames.delete(id); callback(time); } } },
        restore() { Object.assign(globalThis, old); } };
}

test('every state has a full pose, and a spring settles on its target', () => {
    for (const pose of Object.values(POSES))
        for (const key of ['open', 'wide', 'joy', 'sad', 'gx', 'gy', 'tilt', 'lift']) assert.equal(typeof pose[key], 'number');
    const s = { x: 0, v: 0 };
    for (let i = 0; i < 120; i++) springStep(s, 10, feel(0.4, 0.7), 1 / 60);
    assert.ok(Math.abs(s.x - 10) < 0.05 && Math.abs(s.v) < 0.5);
});
test('a blink closes fast, stays shut a beat and opens again', () => {
    assert.equal(lid(-1), 1); assert.equal(lid(1), 1);
    assert.ok(lid(0.085) < 0.1); assert.ok(lid(0.03) > lid(0.06)); assert.ok(lid(0.2) > lid(0.12));
});
test('a live avatar writes its own parts each frame and blends to a new state', () => {
    const env = environment(); const live = bindPresence(env.element, { state: 'idle' });
    try {
        assert.ok(env.attributes.has('data-live')); env.run(300);
        assert.match(env.parts['.presence-avatar__character'].style.transform, /translate\(.+\) rotate\(.+\) scale\(.+\)/);
        live.update('done'); env.run(1500);
        assert.ok(Number(env.many['.presence-avatar__happy'][0].style.opacity) > 0.95);
        assert.ok(Number(env.many['.presence-avatar__pupil'][0].style.opacity) < 0.05);
    } finally { live.release(); env.restore(); }
});
test('arriving at Done plays the happy turn once: the eyes go round, then settle home', () => {
    const env = environment(); const live = bindPresence(env.element, { state: 'idle' });
    try {
        env.run(300); assert.equal(env.many['.presence-avatar__eye'][0].style.transform, '');
        live.update('done'); env.run(420);
        assert.match(env.many['.presence-avatar__eye'][0].style.transform, /^translate\(-?[\d.]+px, 0px\) scale\(/);
        env.run(2000); assert.equal(env.many['.presence-avatar__eye'][0].style.transform, '');
    } finally { live.release(); env.restore(); }
});
test('the pupil looks inside the white while the white holds the expression', () => {
    const env = environment(); const live = bindPresence(env.element, { state: 'thinking' });
    try {
        env.run(1200);
        const [x, y] = env.many['.presence-avatar__pupil'][0].style.transform.match(/translate\((-?[\d.]+)px, (-?[\d.]+)px\)/).slice(1).map(Number);
        assert.ok(x > 2 && y < -2 && Math.hypot(x, y) <= WHITES.reach + 0.01); // up and away, inside the white
        assert.match(env.many['.presence-avatar__eyeball'][0].style.transform, /rotate\(.+\) scale\(.+\)/);
        const face = env.parts['.presence-avatar__gaze'].style.transform.match(/translate\((-?[\d.]+)px/)[1];
        assert.ok(Math.abs(Number(face)) < 6); // the face travels less than with solid eyes
    } finally { live.release(); env.restore(); }
});
test('speaking with a real voice: it moves on the words and is still in the pauses', () => {
    const height = env => Number(env.parts['.presence-avatar__character'].style.transform.match(/scale\([^,]+, ([\d.]+)\)/)[1]);
    const env = environment(); const live = bindPresence(env.element, { state: 'speaking' });
    try {
        // A word lands: the level kicks, and the body stretches up with it.
        env.heard.level = 0.9; env.run(120);
        const onWord = height(env);
        // A pause: the level rests at its hum; the body settles and stays settled, never babbling on its own.
        env.heard.level = 0.08; env.run(700);
        const settled = [];
        for (let i = 0; i < 20; i++) { env.heard.level = 0.08 + (i % 2) * 0.0001; env.run(50); settled.push(height(env)); }
        assert.ok(onWord > Math.max(...settled) + 0.02);
        assert.ok(Math.max(...settled) - Math.min(...settled) < 0.02); // breathing only
    } finally { live.release(); env.restore(); }
});
test('speaking, it holds your gaze rather than following the pointer', () => assert.equal(ATTENTION.speaking, 0));
test('with no voice playing (a scripted preview), speaking keeps its own rhythm', () => {
    const env = environment(); const live = bindPresence(env.element, { state: 'speaking' });
    try {
        const heights = [];
        for (let i = 0; i < 60; i++) { env.run(50); heights.push(Number(env.parts['.presence-avatar__character'].style.transform.match(/scale\([^,]+, ([\d.]+)\)/)[1])); }
        assert.ok(Math.max(...heights) - Math.min(...heights) > 0.04);
    } finally { live.release(); env.restore(); }
});
test('hover and press belong to the surrounding control; release clears everything and is idempotent', () => {
    const env = environment(); const live = bindPresence(env.element);
    try {
        for (const name of ['pointerenter', 'pointerleave', 'pointerdown', 'pointerup', 'pointercancel', 'click', 'contextmenu']) assert.ok(env.listeners.has(name));
        env.listeners.get('pointerenter')({ pointerType: 'mouse' }); env.run(400);
        live.release(); live.release();
        assert.equal(env.listeners.size, 0); assert.equal(env.mediaEvents.size, 0); assert.equal(env.attributes.size, 0);
        assert.equal(env.parts['.presence-avatar__character'].style.transform, '');
    } finally { live.release(); env.restore(); }
});
test('reduced motion keeps the still pose and stops a running avatar', () => {
    const still = environment({ reduced: true }); const none = bindPresence(still.element);
    try { still.run(200); assert.equal(still.frames.size, 0); assert.equal(still.parts['.presence-avatar__gaze'].style.transform, ''); }
    finally { none.release(); still.restore(); }
    const env = environment(); const live = bindPresence(env.element);
    try {
        env.run(200); env.media.matches = true; env.mediaEvents.get('change')();
        assert.equal(env.attributes.size, 0); assert.equal(env.parts['.presence-avatar__gaze'].style.transform, '');
    } finally { live.release(); env.restore(); }
});
