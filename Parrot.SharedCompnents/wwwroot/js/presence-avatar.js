// Life for a reactive avatar (designer, 9 November 2026: "more reactive, react to hover, Apple-level motion").
// Every movement is a spring (response + damping, as motion tools describe them), so a new state, a hover or a
// press blends from wherever the face is now and can be interrupted at any moment, never cut or restarted.
// Cosmetic and local: no audio, network or recording. One shared frame loop drives every live avatar, pointer
// moves are read once per frame, avatars off screen rest, and reduced motion leaves the still pose from CSS.

// Each state's face, the same numbers as the still poses in PresenceAvatar.razor.css (128-unit drawing).
// Where the eyes look tells you what it's doing (designer, 9 November 2026: "should be obvious"): thinking looks
// up and away, working looks down at its work, needing help looks down and aside, waiting looks at you.
export const POSES = {
    idle: { open: 1, wide: 1, joy: 0, sad: 0, gx: 0, gy: 0, tilt: 0, lift: 0 },
    thinking: { open: 0.9, wide: 1, joy: 0, sad: 0, gx: 9.5, gy: -9, tilt: 6, lift: 0 },
    working: { open: 0.6, wide: 1, joy: 0, sad: 0, gx: 0, gy: 8, tilt: 0, lift: 0 },
    waiting: { open: 1.06, wide: 1.04, joy: 0, sad: 0, gx: -1, gy: 1, tilt: 6, lift: 0 },
    blocked: { open: 0.75, wide: 1, joy: 0, sad: 1, gx: -7.5, gy: 7.5, tilt: -5, lift: 0 },
    done: { open: 1, wide: 1, joy: 1, sad: 0, gx: 0, gy: 0, tilt: 0, lift: 0 },
    listening: { open: 1.14, wide: 1.08, joy: 0, sad: 0, gx: 0, gy: -1, tilt: -4, lift: 1 },
    speaking: { open: 1, wide: 1.03, joy: 0, sad: 0, gx: 0, gy: 0, tilt: 0, lift: 0 },
};
// How much each state looks at you rather than at its own work: busy states barely glance away from it.
// Speaking, it holds your gaze and doesn't follow the pointer (designer, 10 November 2026: looking around
// while talking made it seem not serious).
export const ATTENTION = { idle: 1, thinking: 0.15, working: 0.1, waiting: 1, blocked: 0.2, done: 0.8, listening: 1, speaking: 0 };

// Speaking: syllables in phrases with small pauses, each a soft bump of uneven strength, so the body talks with a
// natural rhythm instead of bobbing. Returns how loud it is now (0 to 1) and whether a stressed word just began.
export function voiceAt(voice, t) {
    let stressed = false;
    if (t >= voice.pauseEnd) { voice.phraseEnd = t + 1.3 + Math.random() * 1.3; voice.pauseEnd = voice.phraseEnd + 0.22 + Math.random() * 0.3; voice.next = t; }
    if (t < voice.phraseEnd && t >= voice.next) {
        const length = 0.14 + Math.random() * 0.12, strength = 0.35 + Math.random() * 0.65;
        voice.syllables.push({ at: t, length, strength });
        voice.next = t + length * (0.85 + Math.random() * 0.45);
        stressed = strength > 0.8;
    }
    voice.syllables = voice.syllables.filter(s => t - s.at < s.length);
    const level = voice.syllables.reduce((loud, s) => Math.max(loud, Math.sin(Math.PI * (t - s.at) / s.length) * s.strength), 0);
    return { level, stressed };
}
const newVoice = () => ({ phraseEnd: 0, pauseEnd: 0, next: 0, syllables: [] });

// A real voice (designer, 10 November 2026: "in sync with the voice, not bouncing on its own"). The voice
// module writes --level (0 to 1) onto the avatar: a kick on each word as the browser's voice says it, the
// sound itself from a streamed voice, the person's microphone while it listens. Its resting hum (about
// 0.08) is not speech, so loudness starts above it.
export const HEARD = { floor: 0.08, span: 0.8, onset: 0.22, ringGap: 0.65 };
export const heardLoudness = level => Math.max(0, Math.min(1, (level - HEARD.floor) / HEARD.span));

// A spring from its response (seconds to settle, roughly) and damping fraction (1 = no bounce).
export const feel = (response, damping) => ({ k: (2 * Math.PI / response) ** 2, c: 4 * Math.PI * damping / response });
const FEEL = {
    eyes: feel(0.16, 0.86),   // quick, like a real glance
    lids: feel(0.22, 0.9),
    face: feel(0.34, 0.82),   // open eyes melting into the happy arcs
    body: feel(0.42, 0.62),   // hops, squishes and leans overshoot, then settle
    pop: feel(0.5, 0.48),     // a new shape springing in
};
const CHANNELS = {
    open: 'lids', wide: 'lids', joy: 'face', sad: 'face', gx: 'eyes', gy: 'eyes',
    tilt: 'body', x: 'body', y: 'body', sx: 'body', sy: 'body', pop: 'pop',
};

// One step of a spring towards its target (semi-implicit Euler, in small slices so stiff springs stay stable).
export function springStep(s, target, f, dt) {
    for (let left = dt; left > 0; left -= 1 / 240) {
        const h = Math.min(left, 1 / 240);
        s.v += (-f.k * (s.x - target) - f.c * s.v) * h;
        s.x += s.v * h;
    }
    return s;
}

// A blink: a fast close, a beat shut, a slower open. 1 is open.
export function lid(elapsed) {
    if (elapsed < 0 || elapsed > 0.24) return 1;
    const ease = t => 1 - (1 - t) ** 3;
    if (elapsed < 0.07) return 1 - ease(elapsed / 0.07) * 0.92;
    if (elapsed < 0.1) return 0.08;
    return 0.08 + ease((elapsed - 0.1) / 0.14) * 0.92;
}

// Shape morphing (designer, 9 November 2026: "rotate and then cleanly morph, no sudden jump or gaps").
// Each outline is traced as the same number of evenly spaced points, turned the same way round and lined up
// with the old one, so every in-between frame is a whole, smooth shape: no gaps, no crossing, no jump.
const SAMPLES = 180;
const traced = new Map();

// Floating, in drawing units (the drawing is 128 across): where it rests above its shadow, how much higher it
// rises, the seconds a rise and fall take, and the degrees it sways. Was 4 / 5 / 3.6 / 1.4: too small to see on
// a card (designer, 12 November 2026).
const FLOAT = { rest: 7, rise: 11, period: 3.4, sway: 2 };
const area = points => points.reduce((sum, p, i) => { const q = points[(i + 1) % points.length]; return sum + p[0] * q[1] - q[0] * p[1]; }, 0) / 2;
export function trace(d) {
    if (traced.has(d)) return traced.get(d);
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', d);
    const length = path.getTotalLength();
    const points = Array.from({ length: SAMPLES }, (_, i) => { const p = path.getPointAtLength(length * i / SAMPLES); return [p.x, p.y]; });
    if (area(points) < 0) points.reverse();
    traced.set(d, points);
    return points;
}
// Line the new outline up with the old one: start where the pairs of points sit closest.
export function align(from, to) {
    const n = from.length;
    let best = 0, bestCost = Infinity;
    for (let k = 0; k < n; k++) {
        let cost = 0;
        for (let i = 0; i < n && cost < bestCost; i++) { const a = from[i], b = to[(i + k) % n]; cost += (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2; }
        if (cost < bestCost) { bestCost = cost; best = k; }
    }
    return from.map((_, i) => to[(i + best) % n]);
}
// A smooth closed outline through the points (curves through their midpoints, so no corners appear mid-morph).
export function outline(points) {
    const n = points.length, f = v => v.toFixed(2);
    const mid = i => { const a = points[i % n], b = points[(i + 1) % n]; return [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2]; };
    const start = mid(n - 1);
    let d = `M${f(start[0])} ${f(start[1])}`;
    for (let i = 0; i < n; i++) { const p = points[i], m = mid(i); d += `Q${f(p[0])} ${f(p[1])} ${f(m[0])} ${f(m[1])}`; }
    return d + 'Z';
}
const centre = points => points.reduce((c, p) => [c[0] + p[0] / points.length, c[1] + p[1] / points.length], [0, 0]);
const place = text => { const m = /translate\(\s*(-?[\d.]+)[\s,]+(-?[\d.]+)\s*\)/.exec(text ?? ''); return m ? [Number(m[1]), Number(m[2])] : [0, 0]; };
const TWIST = feel(0.5, 0.6);     // the turn: out and back, with a little overshoot
const RESHAPE = feel(0.42, 0.95); // the morph: eases in and lands without wobble

// White eyes (a study, designer, 10 November 2026): the pupil does most of the looking inside the white and the
// face travels less, the same shares as the still pose in CSS. The pupil never leaves the white.
export const WHITES = { face: 0.45, pupil: 0.55, reach: 5.5, pull: [9.5, 7.5] };
const EYE_X = [49, 79];
export function pupilOffset(gx, gy) {
    let x = gx * WHITES.pupil, y = gy * WHITES.pupil;
    const length = Math.hypot(x, y);
    if (length > WHITES.reach) { x *= WHITES.reach / length; y *= WHITES.reach / length; }
    return [x, y];
}

// The happy turn (designer, 10 November 2026: Done, or press and hold, "turn around and do some nice animation").
// It gathers itself, hops and turns right round as if on a turntable: the eyes slide across its front and round
// the side, the back passes, and they come back round the other side as it lands. Then two little bounces.
export const CELEBRATION = { start: 0.1, turn: 0.72, hop: 13, length: 1.7 };
export function turnAt(elapsed) {
    if (elapsed < 0 || elapsed > CELEBRATION.length) return { spin: 0, arc: 0, lean: 0 };
    const p = Math.max(0, Math.min(1, (elapsed - CELEBRATION.start) / CELEBRATION.turn));
    const eased = p < 0.5 ? 4 * p ** 3 : 1 - (-2 * p + 2) ** 3 / 2; // slow where the face shows, quick round the back
    return { spin: 2 * Math.PI * eased, arc: Math.sin(Math.PI * p), lean: Math.sin(2 * Math.PI * p) };
}
// Where an eye sits mid-turn: on a round body of this radius, it slides across, narrows and goes round the edge.
// Facing is 1 where the eye normally sits (drawn flat, facing you) and 0 as it goes round the edge.
export function eyeOnTurn(spin, x, axis, radius) {
    const home = Math.asin(Math.max(-0.95, Math.min(0.95, (x - axis) / radius))), angle = spin + home;
    return { dx: axis + radius * Math.sin(angle) - x, facing: Math.cos(angle) / Math.cos(home) };
}

// The shared frame loop and the one pointer every avatar looks at.
const live = new Set();
const pointer = { x: 0, y: 0, at: -Infinity };
let frame = 0, last = 0;
const now = () => performance.now() / 1000;
function tick(ms) {
    frame = 0;
    const t = ms / 1000, dt = last ? Math.min(0.05, t - last) : 1 / 60;
    last = t;
    for (const avatar of live) avatar.step(t, dt);
    if (live.size) frame = requestAnimationFrame(tick); else last = 0;
}
const wake = () => { if (!frame && live.size) frame = requestAnimationFrame(tick); };
const onMove = e => { if (e.pointerType === 'touch') return; pointer.x = e.clientX; pointer.y = e.clientY; pointer.at = now(); };
const onOut = e => { if (!e.relatedTarget) pointer.at = -Infinity; };
function listenGlobally(on) {
    const method = on ? 'addEventListener' : 'removeEventListener';
    globalThis.addEventListener && globalThis[method]('pointermove', onMove, { passive: true });
    globalThis.document?.[method]('pointerout', onOut);
}

export function bindPresence(element, options = {}) {
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    const one = s => element.querySelector(s), all = s => [...element.querySelectorAll(s)];
    const parts = {
        character: one('.presence-avatar__character'), gaze: one('.presence-avatar__gaze'),
        turn: one('.presence-avatar__turn'), body: one('.presence-avatar__body'), anchor: one('.presence-avatar__face-anchor'),
        pupils: all('.presence-avatar__pupil'), happy: all('.presence-avatar__happy'),
        eyes: all('.presence-avatar__eye'), eyeballs: all('.presence-avatar__eyeball'),
        shadow: one('.presence-avatar__shadow'), echoes: all('.presence-avatar__echo'), sparkles: all('.presence-avatar__sparkle'),
    };
    const floating = options.floating ?? element.classList?.contains('presence-avatar--floating') ?? false;
    const drift = Math.random() * 10; // so avatars side by side don't rise and fall in step
    // A shape change in flight: where it started, where it's going, and how far it has got.
    let morph = null, shape = null, face = null, pivot = [64, 64];
    const twist = { x: 0, v: 0 }, reshape = { x: 0, v: 0 };
    let watcherOfShape = null;
    const drawShape = (d, at) => {
        parts.body.setAttribute('d', d);
        parts.echoes.forEach(echo => echo.setAttribute('d', d));
        parts.anchor.setAttribute('transform', `translate(${at[0].toFixed(2)} ${at[1].toFixed(2)})`);
        watcherOfShape?.takeRecords(); // our own drawing is not a new choice
    };
    // Blazor has just swapped the outline and the face position, before the screen shows it: put the old look
    // back for this frame, then twist and morph to the new one. A second pick mid-way blends on from where it is.
    const shapeChanged = records => {
        if (!running || !parts.body || !parts.anchor) return;
        let oldD = null, oldFace = null, d = null;
        for (const r of records) {
            if (r.target === parts.body) { oldD ??= r.oldValue; d = parts.body.getAttribute('d'); }
            else if (r.target === parts.anchor) oldFace ??= r.oldValue;
        }
        const faceText = parts.anchor.getAttribute('transform');
        if (!d || (!morph && d === oldD)) return;
        try {
            const from = morph ? shape : trace(oldD), fromFace = morph ? face : place(oldFace ?? faceText);
            morph = { from, to: align(from, trace(d)), d, fromFace, toFace: place(faceText), faceText, at: now() };
            reshape.x = 0; reshape.v = 0;
            twist.v += 720;            // the turn (about 27°) starts at once; the shape follows a beat later
            shape = from; face = fromFace;
            drawShape(outline(from), fromFace);
        } catch { morph = null; } // a drawing that can't be traced just swaps, as before
    };
    const finishShape = () => {
        if (!morph) return;
        parts.body.setAttribute('d', morph.d);
        parts.echoes.forEach(echo => echo.setAttribute('d', morph.d));
        parts.anchor.setAttribute('transform', morph.faceText);
        watcherOfShape?.takeRecords();
        morph = null; shape = null; face = null;
    };
    // Hover and press belong to the whole control around it (a picker tile, the preview), not just the drawing.
    const target = element.closest?.('button, a, [role="button"]') ?? element;
    let state = POSES[options.state] ? options.state : 'idle';
    let hover = false, pressed = false, visible = true, running = false, disposed = false;
    let blinks = [], nextBlink = now() + 0.8 + Math.random() * 2, wander = { x: 0, y: 0, at: 0 };
    let voice = newVoice(), rings = [], ringTurn = 0, nextNudge = 0;
    // The real voice's level as last read, and whether one has been heard in this state (then it leads, pauses included).
    let lastHeard = NaN, heardInState = false, heardBefore = 0;
    const loudness = { x: 0, v: 0 };
    // Press and hold: after a moment it's happy to help, and arriving at Done it's pleased: the happy turn, happy
    // eyes, a soft glow of its own shape (green when done) and sparkles as it lands.
    let holdAt = null, holdFired = false, celebratedAt = -Infinity, swallowClick = false;
    let turnAxis = 64, turnRadius = 40;
    const cues = [];
    const queue = (kind, at, amount = 1) => { cues.push({ at, kind, amount }); cues.sort((a, b) => a.at - b.at); };
    const celebrate = t => {
        celebratedAt = t;
        // The turntable's centre and size come from this shape, in the face's own coordinates.
        try {
            const box = parts.body?.getBBox?.();
            if (box?.width) { turnAxis = box.x + box.width / 2 - place(parts.anchor?.getAttribute('transform'))[0]; turnRadius = box.width * 0.4; }
        } catch { /* keep the last measure */ }
        const landing = t + CELEBRATION.start + CELEBRATION.turn;
        [[0, 'gather', 1], [landing - t, 'land', 1], [landing - t + 0.22, 'hop', 0.5], [landing - t + 0.25, 'wiggle', 0.6], [landing - t + 0.5, 'hop', 0.3]]
            .forEach(([after, kind, amount]) => queue(kind, t + after, amount));
        if (parts.echoes.length) {
            rings.push({ el: parts.echoes[0], at: landing - 0.04, life: 0.9, peak: 0.42, grow: 0.34 });
            rings.push({ el: parts.echoes[1 % parts.echoes.length], at: landing + 0.16, life: 0.9, peak: 0.28, grow: 0.5 });
        }
    };
    const excite = t => {
        swallowClick = true; celebrate(t);
        try { globalThis.navigator?.vibrate?.(12); } catch { /* a phone without vibration just skips it */ }
    };

    const springs = {};
    const goals = () => {
        const p = POSES[state];
        return { ...p, x: 0, y: -p.lift * 2, sx: 1, sy: 1 + p.lift * 0.015, pop: 1 };
    };
    for (const [name, value] of Object.entries(goals())) if (name in CHANNELS) springs[name] = { x: value, v: 0 };

    const play = (kind, t, amount = 1) => {
        const s = springs;
        if (kind === 'pop') { s.pop.x = 0.78; s.pop.v = 0; blinks.push(t + 0.05); }
        else if (kind === 'hop') { s.y.v -= 70 * amount; s.sy.v += amount; s.sx.v -= 0.6 * amount; s.gy.v -= 30 * amount; }
        else if (kind === 'jump') { s.y.v -= 125 * amount; s.sy.v += 1.8 * amount; s.sx.v -= 1.1 * amount; }
        else if (kind === 'wiggle') { s.tilt.v += 320 * amount; }
        else if (kind === 'nod') { s.y.v += 45; s.gy.v += 30; }
        else if (kind === 'celebrate') celebrate(t);
        else if (kind === 'gather') { s.sy.v -= 1.2; s.sx.v += 0.7; s.y.v += 25; }
        else if (kind === 'land') { s.sy.v -= 1.6; s.sx.v += 1; s.y.v += 35; }
        else if (kind === 'shift') { s.sy.v -= 0.7; s.sx.v += 0.45; blinks.push(t); }
        // Noticing you: the eyes open a touch wider and glance up.
        else if (kind === 'perk') { s.open.v += 2.5; s.wide.v += 1.2; s.gy.v -= 35; s.sy.v += 0.5; }
    };

    const avatar = {
        step(t, dt) {
            if (!visible) return;
            while (cues.length && cues[0].at <= t) { const cue = cues.shift(); play(cue.kind, t, cue.amount); }
            const p = POSES[state], attention = ATTENTION[state];
            const sinceTurn = t - celebratedAt, excited = sinceTurn >= 0 && sinceTurn < CELEBRATION.length;
            if (pressed && holdAt !== null && !holdFired && t - holdAt > 0.42) { holdFired = true; excite(t); }
            // Waiting for you: an expectant little hop now and then.
            if (state === 'waiting' && t > nextNudge) { if (nextNudge) play('hop', t, 0.45); nextNudge = t + 2.6 + Math.random() * 1.6; }

            // Where to look: at the pointer anywhere on the page, or, when it's still, small natural glances.
            let lx = 0, ly = 0;
            if (t - pointer.at < 3.5) {
                const r = element.getBoundingClientRect();
                const dx = pointer.x - (r.left + r.width / 2), dy = pointer.y - (r.top + r.height / 2);
                const distance = Math.hypot(dx, dy) || 1, reach = Math.min(1, distance / Math.max(48, r.width * 1.6));
                lx = dx / distance * reach; ly = dy / distance * reach;
            } else {
                if (t > wander.at) {
                    const centre = Math.random() < 0.35;
                    wander = { x: centre ? 0 : Math.random() * 1.2 - 0.6, y: centre ? 0 : Math.random() * 0.7 - 0.4, at: t + 1.2 + Math.random() * 2.6 };
                }
                lx = wander.x * 0.7; ly = wander.y * 0.7;
            }

            const h = hover ? 1 : 0, press = pressed && !excited ? 1 : 0, g = goals();
            g.open = p.open + h * 0.1; g.wide = p.wide + h * 0.05;
            if (excited) { g.joy = 1; g.sad = 0; }
            // The eyes split the look between the face and the pupil, so they need a strong pull to read
            // clearly (designer, 10 November 2026: "not following my mouse"): the pupil reaches its white's edge.
            const pull = WHITES.pull;
            g.gx = Math.max(-10, Math.min(10, p.gx + lx * pull[0] * attention));
            g.gy = Math.max(-9.5, Math.min(9, p.gy + ly * pull[1] * attention));
            g.tilt = p.tilt + lx * 3 * attention; g.x = lx * 1.5 * attention;
            g.y += -h * 2.5 + press * 2; g.sx += -h * 0.012 + press * 0.07; g.sy += h * 0.025 - press * 0.09;
            for (const [name, s] of Object.entries(springs)) springStep(s, g[name], FEEL[CHANNELS[name]], dt);

            // Small layers on top of the springs: breathing, and each state's own rhythm.
            const s = springs, breathe = Math.sin(t * 2 * Math.PI / 4.2) * 0.01;
            let sx = s.sx.x - breathe * 0.4, sy = s.sy.x + breathe, y = s.y.x, tilt = s.tilt.x, gx = s.gx.x, gy = s.gy.x, wide = s.wide.x;
            // Floating: a slow rise and fall with a gentle sway, resting a little above its shadow.
            // Big enough to read as floating at a card's 48px (designer, 12 November 2026: "subtle, but obvious"):
            // it rests FLOAT.rest units up and rises FLOAT.rise more, eased, with the shadow answering below.
            let rise = 0;
            if (floating) {
                rise = (Math.sin((t + drift) * 2 * Math.PI / FLOAT.period) + 1) / 2 * FLOAT.rise;
                y -= FLOAT.rest + rise; tilt += Math.sin((t + drift) * 2 * Math.PI / 5.3) * FLOAT.sway;
            }
            // The happy turn: up in a soft arc, leaning into it, a touch narrower side-on, as if it had depth.
            const { spin, arc, lean } = turnAt(sinceTurn);
            y -= arc * CELEBRATION.hop; sy += arc * 0.04; sx = (sx - arc * 0.02) * (1 - 0.07 * Math.sin(spin) ** 2); tilt += lean * 4;
            // Speaking: the body talks in phrases, stretching up from its base on each syllable, the eyes
            // brightening on stressed words, and a soft ring of its own shape growing out on the strongest ones.
            // With a real voice, it moves only as the words are said and is still in its pauses; without one (a
            // scripted preview), it keeps its own speaking rhythm.
            const raw = Number.parseFloat(element.style?.getPropertyValue?.('--level'));
            if (Number.isFinite(raw) && raw !== lastHeard) { if (raw > HEARD.floor) heardInState = true; lastHeard = raw; }
            const heard = heardInState && Number.isFinite(lastHeard) ? heardLoudness(lastHeard) : 0;
            let spoken = { level: 0, stressed: false };
            if (state === 'speaking' && heardInState) {
                spoken = { level: heard, stressed: heard - heardBefore > HEARD.onset && heard > 0.5 }; // a word landing
            } else if (state === 'speaking') spoken = voiceAt(voice, t);
            heardBefore = heard;
            springStep(loudness, spoken.level, heardInState ? feel(0.07, 0.75) : feel(0.12, 0.7), dt);
            const loud = Math.max(0, loudness.x);
            sy += loud * 0.06; sx -= loud * 0.035; y -= loud * 1.4; wide += loud * 0.07;
            const ringGap = heardInState ? HEARD.ringGap : 0.4;
            if (spoken.stressed && (!rings.length || t - rings[rings.length - 1].at > ringGap) && parts.echoes.length) {
                rings.push({ el: parts.echoes[ringTurn++ % parts.echoes.length], at: t });
            }
            // Listening to a real voice: it leans in a touch and its eyes open a little as you speak.
            if (state === 'listening' && heardInState) { wide += heard * 0.05; y -= heard * 0.8; tilt -= heard * 1.5; }
            if (state === 'working') { const scan = Math.sin(t * 2 * Math.PI / 2.6); gx += scan * 4.5; tilt += scan * 1.5; }
            else if (state === 'thinking') { gx += Math.sin(t * 0.9) * 1; gy += Math.sin(t * 0.7) * 0.8; }
            else if (state === 'listening') { y += Math.sin(t * 2 * Math.PI / 2.4) * 0.9; }
            else if (state === 'waiting') { tilt += Math.sin(t * 2 * Math.PI / 5.2) * 2; }

            // Blinks at uneven gaps, now and then twice; never while smiling.
            if (t >= nextBlink) {
                blinks.push(t); if (Math.random() < 0.18) blinks.push(t + 0.3);
                nextBlink = t + 2.4 + Math.random() * 3.6;
            }
            blinks = blinks.filter(b => t - b < 0.3);
            const lids = blinks.reduce((least, b) => Math.min(least, lid(t - b)), 1);

            const joy = Math.max(0, Math.min(1, s.joy.x)), pop = s.pop.x;
            const sad = s.sad.x * 12;
            parts.character.style.transform = `translate(${s.x.x.toFixed(2)}px, ${y.toFixed(2)}px) rotate(${tilt.toFixed(2)}deg) scale(${(sx * pop).toFixed(4)}, ${(sy * pop).toFixed(4)})`;
            const faceX = gx * WHITES.face, faceY = gy * WHITES.face;
            parts.gaze.style.transform = `translate(${faceX.toFixed(2)}px, ${faceY.toFixed(2)}px)`;
            // The shadow answers the height: smaller and fainter as it rises or jumps, fuller as it lands.
            if (parts.shadow) {
                const height = Math.max(-3, -(y + FLOAT.rest));
                parts.shadow.style.transform = `translateX(${s.x.x.toFixed(2)}px) scale(${Math.max(0.5, Math.min(1.1, 1 - height * 0.035)).toFixed(3)}, 1)`;
                parts.shadow.style.opacity = Math.max(0.3, Math.min(1, 1 - height * 0.05)).toFixed(3);
            }
            // Rings: a speaking word (0.8s), or the happy turn's glow (its own life, size and strength).
            rings = rings.filter(ring => t - ring.at < (ring.life ?? 0.8));
            parts.echoes.forEach(echo => {
                const ring = rings.filter(r => r.el === echo && t >= r.at).pop();
                if (!ring) { echo.style.opacity = '0'; return; }
                const p = (t - ring.at) / (ring.life ?? 0.8);
                echo.style.opacity = ((ring.peak ?? 0.32) * (1 - p) ** 1.6).toFixed(3);
                echo.style.transform = `scale(${(1 + (ring.grow ?? 0.24) * (1 - (1 - p) ** 3)).toFixed(4)})`;
            });
            // Sparkles pop one after another as it lands.
            parts.sparkles.forEach((sparkle, i) => {
                const p = (sinceTurn - CELEBRATION.start - CELEBRATION.turn + 0.06 - i * 0.09) / 0.7;
                sparkle.style.transform = p > 0 && p < 1 ? `rotate(${(p * 90).toFixed(1)}deg) scale(${(Math.sin(Math.PI * p) ** 0.8).toFixed(3)})` : 'scale(0)';
            });
            // Mid-turn, each eye slides round the body; otherwise it stays put.
            parts.eyes.forEach((eye, i) => {
                if (!spin) { eye.style.transform = ''; eye.style.opacity = ''; return; }
                const x = (EYE_X[i] ?? 64) + faceX, at = eyeOnTurn(spin, x, turnAxis, turnRadius);
                eye.style.transform = `translate(${at.dx.toFixed(2)}px, 0px) scale(${Math.max(0.001, at.facing).toFixed(3)}, 1)`;
                eye.style.opacity = Math.max(0, Math.min(1, at.facing * 6)).toFixed(3);
            });
            // The white squints, widens, droops and smiles; the pupil looks around inside it.
            // A smiling eye doesn't blink: the blink fades out as the smile comes in.
            const blink = 1 - (1 - lids) * (1 - joy);
            const [px, py] = pupilOffset(gx, gy), lidded = Math.max(0.08, s.open.x * blink) * (1 - 0.22 * joy);
            parts.eyeballs.forEach((ball, i) => { ball.style.transform = `rotate(${(i ? sad : -sad).toFixed(2)}deg) scale(${wide.toFixed(3)}, ${lidded.toFixed(3)})`; });
            parts.pupils.forEach(pupil => {
                pupil.style.transform = `translate(${px.toFixed(2)}px, ${py.toFixed(2)}px) scale(${(1 - 0.5 * joy).toFixed(3)})`;
                pupil.style.opacity = (1 - joy).toFixed(3);
            });
            parts.happy.forEach(arc => {
                arc.style.opacity = joy.toFixed(3);
                arc.style.transform = `translateY(${((1 - joy) * 3).toFixed(2)}px) scale(${(0.6 + 0.4 * joy).toFixed(3)})`;
            });

            // A new shape: a quick twist out and back, and the outline reshapes a beat into it.
            springStep(twist, 0, TWIST, dt);
            let dip = 0;
            if (morph) {
                if (t - morph.at > 0.08) springStep(reshape, 1, RESHAPE, dt);
                const m = Math.max(0, Math.min(1, reshape.x));
                shape = morph.from.map((a, i) => { const b = morph.to[i]; return [a[0] + (b[0] - a[0]) * m, a[1] + (b[1] - a[1]) * m]; });
                face = [morph.fromFace[0] + (morph.toFace[0] - morph.fromFace[0]) * m, morph.fromFace[1] + (morph.toFace[1] - morph.fromFace[1]) * m];
                pivot = centre(shape);
                dip = Math.sin(Math.PI * m) * 0.06; // it gathers in slightly mid-change, then fills out
                if (reshape.x > 0.997 && Math.abs(reshape.v) < 0.05) finishShape(); else drawShape(outline(shape), face);
            }
            if (parts.turn) {
                const settled = !morph && Math.abs(twist.x) < 0.01 && Math.abs(twist.v) < 0.1;
                parts.turn.style.transformOrigin = `${pivot[0].toFixed(2)}px ${pivot[1].toFixed(2)}px`;
                parts.turn.style.transform = settled ? '' : `rotate(${twist.x.toFixed(2)}deg) scale(${(1 - dip).toFixed(4)})`;
            }
        },
    };

    const enter = e => { if (e.pointerType === 'touch') return; hover = true; queue('perk', now()); };
    const leave = () => { hover = false; pressed = false; holdAt = null; };
    const down = e => {
        pressed = true; holdAt = now(); holdFired = false; swallowClick = false;
        // A finger has no hover: look at where it touched.
        if (e.pointerType === 'touch') { pointer.x = e.clientX; pointer.y = e.clientY; pointer.at = now() - 2; }
    };
    const up = () => { pressed = false; holdAt = null; };
    // A long press was for fun, not a tap: it doesn't also open what the avatar sits in.
    const click = e => { if (swallowClick) { swallowClick = false; e.stopPropagation(); e.preventDefault(); } };
    const menu = e => { if (pressed || holdFired) e.preventDefault(); };
    const watcher = typeof IntersectionObserver === 'function'
        ? new IntersectionObserver(entries => { visible = entries[entries.length - 1].isIntersecting; }) : null;

    const clear = () => {
        for (const el of [parts.character, parts.gaze, parts.turn, parts.shadow, ...parts.echoes, ...parts.sparkles, ...parts.eyes, ...parts.eyeballs, ...parts.pupils, ...parts.happy]) {
            if (!el) continue;
            el.style.transform = ''; el.style.opacity = ''; el.style.transformOrigin = '';
        }
        element.removeAttribute('data-live');
    };
    const start = () => {
        if (running || disposed || reduced.matches || !parts.character || !parts.gaze) return;
        running = true; element.setAttribute('data-live', '');
        if (parts.body && parts.anchor && typeof MutationObserver === 'function') {
            watcherOfShape = new MutationObserver(shapeChanged);
            watcherOfShape.observe(parts.body, { attributes: true, attributeFilter: ['d'], attributeOldValue: true });
            watcherOfShape.observe(parts.anchor, { attributes: true, attributeFilter: ['transform'], attributeOldValue: true });
        }
        if (!live.size) listenGlobally(true);
        live.add(avatar); wake();
    };
    const stop = () => {
        if (!running) return;
        // Stopping mid-change lands on the new shape at once: the still avatar always shows what was chosen.
        finishShape(); watcherOfShape?.disconnect(); watcherOfShape = null;
        twist.x = twist.v = reshape.x = reshape.v = 0;
        running = false; live.delete(avatar); clear();
        // The last one out stops the shared loop, so the next avatar starts it afresh.
        if (!live.size) { listenGlobally(false); if (frame) globalThis.cancelAnimationFrame?.(frame); frame = 0; last = 0; }
    };
    const motionChanged = () => { if (reduced.matches) stop(); else start(); };

    target.addEventListener('pointerenter', enter);
    target.addEventListener('pointerleave', leave);
    target.addEventListener('pointerdown', down);
    target.addEventListener('pointerup', up);
    target.addEventListener('pointercancel', up);
    target.addEventListener('click', click, true);
    target.addEventListener('contextmenu', menu);
    reduced.addEventListener('change', motionChanged);
    watcher?.observe(element);
    start();

    return {
        update(next) {
            if (disposed || !POSES[next] || next === state) return;
            state = next;
            if (next === 'speaking') voice = newVoice();
            heardInState = false; heardBefore = 0;
            // Arriving at Done is a success: the happy turn. Every other state shifts its weight and blinks.
            if (next === 'done') queue('celebrate', now() + 0.05); else queue('shift', now());
        },
        cue(kind, delay = 0) {
            if (disposed) return;
            queue(kind, now() + Math.max(0, delay) / 1000);
        },
        release() {
            if (disposed) return;
            disposed = true; stop();
            target.removeEventListener('pointerenter', enter);
            target.removeEventListener('pointerleave', leave);
            target.removeEventListener('pointerdown', down);
            target.removeEventListener('pointerup', up);
            target.removeEventListener('pointercancel', up);
            target.removeEventListener('click', click, true);
            target.removeEventListener('contextmenu', menu);
            reduced.removeEventListener('change', motionChanged);
            watcher?.disconnect();
        },
    };
}
