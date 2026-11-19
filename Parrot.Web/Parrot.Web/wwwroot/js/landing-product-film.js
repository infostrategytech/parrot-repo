// The product film on Concept D (designer, 18 November 2026). One clock drives persistent actors, so what is on
// screen in one moment carries into the next: the scattered chats gather into yára, the inbox rows rise out of
// them, a row becomes the conversation's header and its preview the first bubble, Kora travels with every reply
// and settles into the card that asks for your answer, and the order arrives as a task to act on. Sampling
// paused Web Animations keeps seeking, pausing and resizing exact. A public illustration only: no API calls, live
// records, microphone or synthetic user input.

export const duration = 37800;
// Where each chapter starts, and a readable still for each one (reduced motion, data saver).
export const chapters = Object.freeze([0, 8400, 12900, 17300, 23700]);
export const posters = Object.freeze([7000, 11300, 16200, 23300, 31100]);
// The closing call: Kora and the cast return around "Get started with yára today".
export const ending = 32800;
// Kora lands on the closing card and says hello with its press-and-hold turn.
export const helloAt = 33700;
const clamp = (value, low, high) => Math.max(low, Math.min(high, value));
export function frameAt(time) {
  const elapsed = clamp(time, 0, duration);
  return {
    scene: chapters.findLastIndex(start => elapsed >= start),
    beat: elapsed >= ending ? 1 : 0,
    progress: elapsed / duration,
    chapterProgress: chapters.map((start, i) => clamp((elapsed - start) / ((chapters[i + 1] ?? duration) - start), 0, 1))
  };
}

export const ease = Object.freeze({
  out: 'cubic-bezier(.16, 1, .3, 1)',     // arrivals: fast, then a long soft landing
  inOut: 'cubic-bezier(.65, 0, .35, 1)',  // carries from one place to another
  in: 'cubic-bezier(.55, 0, .75, .2)',    // exits pick up speed as they leave
  glide: 'cubic-bezier(.3, .75, .15, 1)', // the pointer: a hand that knows where it is going
  pop: 'cubic-bezier(.34, 1.36, .64, 1)'  // a spring for browsers without linear()
});
// A damped spring sampled into CSS linear(): a small, quick overshoot that settles, like a physical card.
export function springEasing(damping = .68, stiffness = 11, samples = 36) {
  const wd = stiffness * Math.sqrt(1 - damping ** 2), points = [];
  for (let i = 0; i <= samples; i++) {
    const t = i / samples;
    points.push(+(1 - Math.exp(-damping * stiffness * t) * (Math.cos(wd * t) + (damping * stiffness / wd) * Math.sin(wd * t))).toFixed(4));
  }
  points[samples] = 1;
  return `linear(${points.join(', ')})`;
}

// The stage: chat cards in a column, captions to its left (above it on a phone), controls along the bottom.
export function layoutFor(width, height) {
  const w = Math.max(280, width), h = Math.max(560, height), compact = w < 760;
  const cw = compact ? Math.min(w - 32, 420) : Math.min(440, Math.round(w * .42));
  const cx = compact ? w / 2 : Math.round(w * .655), x = Math.round(cx - cw / 2);
  const captionX = compact ? 16 : Math.max(32, Math.round(w * .07));
  return { w, h, compact, cw, x, captionX, captionWidth: compact ? w - 32 : Math.max(200, Math.min(320, x - captionX - 64)), top: compact ? 132 : 96, floor: h - 92 };
}

const FACE = 88, POINTER = 22, RING = 48, AI_GAP = 36, CONTROLS = 76;
// Where the chats land, as fractions of the stage, around the words in the middle.
// Where the chats land (centre, as fractions of the stage): three columns, so none overlaps another, with the
// middle one leaving room for the words. They arrive in turn across the columns, so the pile looks random.
const PINGS = [[.15, .1, -4], [.85, .17, 3], [.47, .06, -3], [.13, .53, 4], [.87, .61, -5], [.53, .89, 2], [.17, .31, 3], [.84, .83, -2], [.54, .27, -4], [.14, .76, -2], [.86, .38, 5], [.46, .71, 2]];
// A phone shows eight, in rows above and below the words, alternating sides.
const PHONE_PINGS = 8;
export const pingTimes = Object.freeze([250, 600, 900, 1160, 1390, 1600, 1790, 1960, 2120, 2270, 2410, 2540]);
// The cast: four teammates of different shapes, the inner pair first, in a gentle smile-shaped arc around Kora.
const CAST_SLOTS = [-1, 1, -2, 2];
const r1 = v => Math.round(v * 10) / 10, r3 = v => Math.round(v * 1000) / 1000;

export function buildMotionTracks(width, height, metrics = {}, options = {}) {
  const L = layoutFor(width, height), { w, h, compact, cw, x, captionX } = L;
  const spring = options.spring ?? ease.pop, sizes = metrics.sizes ?? {};
  const size = (name, fw, fh) => sizes[name] ?? { w: fw, h: fh };
  const tracks = [];
  // Each move goes from wherever the actor is to new values between two times. Holds fall out of the gaps.
  function track(name, initial, moves) {
    const css = s => {
      const k = {};
      if ('x' in s) k.transform = `translate3d(${r1(s.x)}px, ${r1(s.y)}px, 0) scale(${r3(s.s ?? 1)})${'r' in s ? ` rotate(${r3(s.r)}deg)` : ''}`;
      if ('o' in s) k.opacity = r3(s.o);
      if ('b' in s) k.filter = `blur(${r3(s.b)}px)`;
      if ('h' in s) k.height = `${Math.round(s.h)}px`;
      if ('clip' in s) k.clipPath = `inset(0 ${r3(s.clip)}% 0 0)`;
      return k;
    };
    let state = { ...initial }, last = 0;
    const frames = [{ ...css(state), offset: 0 }];
    for (const [t0, t1, values, easing = ease.out] of moves) {
      if (t0 < last || t1 <= t0) throw new Error(`${name}: moves overlap at ${t0}`);
      frames.push({ ...css(state), offset: t0 / duration, easing });
      for (const key of Object.keys(values)) if (!(key in state)) throw new Error(`${name}: ${key} has no starting value`);
      state = { ...state, ...values };
      frames.push({ ...css(state), offset: t1 / duration });
      last = t1;
    }
    frames.push({ ...css(state), offset: 1 });
    tracks.push({ name, keyframes: frames });
  }
  const face = (cx, cy, s = 1) => ({ x: cx - FACE / 2, y: cy - FACE / 2, s });
  const point = (cx, cy) => ({ x: cx - POINTER / 2, y: cy - POINTER / 2 });
  const middle = groupHeight => Math.max(16, (h - CONTROLS - groupHeight) / 2);

  // Act one: chats everywhere. They pile up around the words, then gather into yára.
  const chaosTitle = size('chaos-title', w, 58), chaosLine = size('chaos-line', w, 28);
  const chaosY = middle(chaosTitle.h + 12 + chaosLine.h), chaosCy = chaosY + (chaosTitle.h + 12 + chaosLine.h) / 2;
  track('chaos-title', { x: 0, y: chaosY + 18, s: 1, o: 0, b: 10 }, [[700, 1400, { y: chaosY, o: 1, b: 0 }], [4000, 4450, { y: chaosY - 20, o: 0, b: 8 }, ease.in]]);
  track('chaos-line', { x: 0, y: chaosY + chaosTitle.h + 26, s: 1, o: 0, b: 8 }, [[1150, 1800, { y: chaosY + chaosTitle.h + 12, o: 1, b: 0 }], [4050, 4500, { y: chaosY + chaosTitle.h - 6, o: 0, b: 8 }, ease.in]]);
  const words = { top: chaosY - 20, bottom: chaosY + chaosTitle.h + 12 + chaosLine.h + 20, half: Math.min(w / 2 - 24, 280) };
  const spots = PINGS.map(([fx, fy], i) => {
    const p = size(`ping-${i}`, 200, 40);
    if (compact) {
      if (i >= PHONE_PINGS) return null;
      const above = i % 2 === 0, row = Math.floor(i / 2), left = (row + (above ? 0 : 1)) % 2 === 0;
      const from = above ? 16 : words.bottom, to = above ? words.top - p.h : h - CONTROLS - 8 - p.h;
      return { x: left ? 16 : w - 16 - p.w, y: from + row * Math.max(p.h + 8, (to - from) / 3), p };
    }
    return { x: clamp(fx * w - p.w / 2, 12, w - p.w - 12), y: clamp(fy * (h - CONTROLS) - p.h / 2, 12, h - CONTROLS - p.h), p };
  });
  // Belt and braces for unusual sizes: nudge apart any two that still touch, and anything sitting on the words.
  const placed = spots.filter(Boolean), margin = 8;
  for (let pass = 0; pass < 24; pass++) for (const a of placed) {
    const crossesWords = a.x < w / 2 + words.half && a.x + a.p.w > w / 2 - words.half && a.y < words.bottom && a.y + a.p.h > words.top;
    if (crossesWords) a.y = a.y + a.p.h / 2 < (words.top + words.bottom) / 2 ? words.top - a.p.h : words.bottom;
    for (const b of placed) {
      if (a === b) continue;
      const dx = Math.min(a.x + a.p.w, b.x + b.p.w) - Math.max(a.x, b.x) + margin, dy = Math.min(a.y + a.p.h, b.y + b.p.h) - Math.max(a.y, b.y) + margin;
      if (dx > 0 && dy > 0) { const down = a.y >= b.y ? 1 : -1; a.y += down * dy / 2; b.y -= down * dy / 2; }
    }
    for (const c of placed) c.y = clamp(c.y, 8, h - CONTROLS - c.p.h);
  }
  PINGS.forEach(([, , tilt], i) => {
    const spot = spots[i], t = pingTimes[i];
    if (!spot) { track(`ping-${i}`, { x: 0, y: 0, o: 0 }, []); return; }
    const { p } = spot, cx = spot.x + p.w / 2, cy = spot.y + p.h / 2;
    track(`ping-${i}`, { x: cx - p.w / 2, y: cy - p.h / 2 + 12, s: .7, o: 0, r: tilt, b: 0 }, [
      [t, t + 520, { y: cy - p.h / 2, s: 1, o: 1, r: tilt * .4 }, spring],
      [t + 520, 4300 + i * 25, { y: cy - p.h / 2 - 6, r: tilt * .2 }, ease.inOut],
      [4300 + i * 25, 4800, { x: w / 2 - p.w / 2, y: chaosCy - p.h / 2, s: .2, o: 0, r: 0, b: 6 }, ease.in]
    ]);
  });

  // yára arrives: Kora, the cast in an arc, the name.
  const introTitle = size('intro-title', w, 58), introLine = size('intro-line', w, 28);
  // Kora's block: the face, with the cast beside it.
  // Kora leads, a little larger than life on the title cards; the cast stands on the same floor as Kora.
  const hero = compact ? 1 : 1.2, block = FACE * hero;
  const introTop = middle(block + 24 + introTitle.h + 12 + introLine.h);
  const kora = [w / 2, introTop + block / 2], titleY = introTop + block + 24, lineY = titleY + introTitle.h + 12;
  track('light', { x: 0, y: 0, s: .9, o: .45 }, [
    [4650, 4950, { s: 1.25, o: 1 }], [4950, 6200, { s: 1, o: .6 }, ease.inOut], [32900, 33900, { s: 1.2, o: .95 }, ease.inOut]
  ]);
  track('intro-title', { x: 0, y: titleY + 18, s: 1, o: 0, b: 10 }, [[4950, 5650, { y: titleY, o: 1, b: 0 }], [7700, 8150, { y: titleY - 24, o: 0, b: 8 }, ease.in]]);
  track('intro-line', { x: 0, y: lineY + 14, s: 1, o: 0, b: 8 }, [[5250, 5900, { y: lineY, o: 1, b: 0 }], [7750, 8200, { y: lineY - 20, o: 0, b: 8 }, ease.in]]);
  const gap = compact ? 66 : 104, mini = compact ? .5 : .8;
  const endTitle = size('end-title', w, 58), cta = size('cta', 220, 48);
  const endTop = middle(block + 24 + endTitle.h + 28 + cta.h), endKora = [w / 2, endTop + block / 2];
  const arc = (centre, slot) => {
    return [centre[0] + slot * gap + Math.sign(slot) * gap * .15, centre[1] + block / 2 - FACE * mini / 2];
  };
  CAST_SLOTS.forEach((slot, i) => {
    const home = arc(kora, slot), away = [home[0] + Math.sign(slot) * 60, home[1] - 30], last = arc(endKora, slot);
    track(`cast-${i}`, { ...face(...kora, .2), o: 0, r: Math.sign(slot) * 10 }, [
      [5050 + i * 70, 5700 + i * 70, { ...face(...home, mini), o: 1, r: 0 }, spring],
      [5800 + i * 70, 7600, { y: home[1] - FACE / 2 + (i % 2 ? -5 : 5) }, ease.inOut],
      [7700 + i * 30, 8200 + i * 30, { ...face(...away, mini * .7), o: 0 }, ease.in],
      [33300, 33320, { ...face(...endKora, .2) }],
      [33450 + i * 70, 34100 + i * 70, { ...face(...last, mini), o: 1 }, spring]
    ]);
  });

  // The inbox: rows rise out of the gathered chats, a new one arrives on top.
  const top = compact ? Math.max(L.top, 48 + Math.max(...[1, 2, 3, 4].map(i => size(`caption-${i}`, L.captionWidth, 72).h))) : L.top;
  const headY = top, headH = size('inbox-head', cw, 28).h, rowH = size('customer', cw, 68).h;
  const rowsY = headY + headH + 14, rowY = k => rowsY + k * (rowH + 10);
  track('inbox-head', { x, y: headY + 10, o: 0 }, [[8300, 8850, { y: headY, o: 1 }], [12180, 12480, { y: headY - 8, o: 0 }, ease.in]]);
  for (let i = 1; i <= 3; i++) track(`row-${i}`, { x, y: chaosCy - rowH / 2, s: .7, o: 0, b: 0 }, [
    [8400 + (i - 1) * 90, 9100 + (i - 1) * 90, { y: rowY(i - 1), s: 1, o: 1 }, spring],
    [10200, 10750, { y: rowY(i) }, spring],
    [12180 + (i - 1) * 50, 12630 + (i - 1) * 50, { y: rowY(i) + 28, o: 0, b: 4 }, ease.in]
  ]);

  // The conversation, built down the column from measured heights so nothing overlaps.
  const inquiry = size('inquiry', 290, 48), reply = size('reply', 300, 72), followup = size('followup', 330, 48);
  const answered = size('answered', 170, 28), resolution = size('resolution', 320, 96), order = size('order-message', 240, 48);
  const y1 = headY + rowH + 20, y2 = y1 + inquiry.h + 12, y3 = y2 + reply.h + 12, y4 = y3 + followup.h + 12;
  const helpY = y1 + followup.h + 12, resY = helpY + answered.h + 14, taskY = y1 + order.h + 12;
  const aiX = width => x + cw - AI_GAP - width, ai = y => [x + cw - 14, y + 14];
  const dimmed = { s: .96, o: .22, b: 3 }, lit = { s: 1, o: 1, b: 0 }, scrolled = y => ({ y: y - 70, o: 0 });
  const recede = { s: .94, o: 0, b: 6 };

  track('customer', { x, y: rowY(0) - 24, s: .97, o: 0, b: 0 }, [
    [10250, 10850, { y: rowY(0), s: 1, o: 1 }, spring],
    [11830, 11960, { s: 1.012 }], [11980, 12060, { s: .985 }], [12060, 12180, { s: 1 }],
    [12180, 12860, { y: headY }, ease.inOut],
    [26900, 27480, dimmed], [31950, 32450, lit],
    [32800, 33400, recede, ease.in]
  ]);
  track('customer-meta', { o: 1 }, [[12180, 12400, { o: 0 }]]);
  track('customer-sub', { o: 0 }, [[12500, 12850, { o: 1 }]]);
  // The row's preview is the first bubble itself, drawn at the preview's size until the chat opens.
  const snippet = metrics.snippet ?? { left: 68, top: 38 }, pad = metrics.inquiryPad ?? { x: 16, y: 12 }, small = .875;
  const sx = x + snippet.left - pad.x * small, sy = rowY(0) + snippet.top - pad.y * small;
  track('inquiry', { x: sx, y: sy - 24, s: small, o: 0 }, [
    [10250, 10850, { y: sy, o: 1 }, spring],
    [12210, 12960, { x, y: y1, s: 1 }, ease.inOut],
    [17300, 17900, scrolled(y1), ease.inOut]
  ]);
  track('inquiry-bg', { o: 0 }, [[12360, 12900, { o: 1 }]]);
  const typing = (name, y, tin, tout) => {
    const t = size(name, 64, 40);
    track(name, { x: aiX(t.w), y, s: .6, o: 0 }, [[tin, tin + 450, { s: 1, o: 1 }, spring], [tout, tout + 220, { s: .85, o: 0 }, ease.in]]);
  };
  typing('typing-0', y2, 13050, 14300);
  track('reply', { x: aiX(reply.w), y: y2 + 8, s: .9, o: 0 }, [[14400, 15000, { y: y2, s: 1, o: 1 }, spring], [17300, 17900, scrolled(y2), ease.inOut]]);
  track('reply-source', { x: 0, y: 4, o: 0 }, [[14900, 15350, { y: 0, o: 1 }]]);
  track('followup', { x, y: y3 + 10, s: .94, o: 0 }, [
    [15800, 16400, { y: y3, s: 1, o: 1 }, spring], [17300, 17950, { y: y1 }, ease.inOut], [23700, 24300, scrolled(y1), ease.inOut]
  ]);
  typing('typing-1', y4, 16500, 17300);

  // Your say: Kora isn't sure, so it asks you, and replies with your answer.
  const hHB = size('help', cw, 220).h, shift = metrics.fieldShift ?? 66, hHA = hHB - shift;
  track('help', { x, y: helpY + 16, s: .96, o: 0, h: hHA }, [
    [17950, 18600, { y: helpY, s: 1, o: 1 }, spring], [19550, 20150, { h: hHB }], [22300, 22800, { y: helpY - 8, s: .97, o: 0 }, ease.in]
  ]);
  track('help-field', { x: 0, y: 6, o: 0 }, [[19650, 20100, { y: 0, o: 1 }]]);
  track('help-actions', { x: 0, y: -shift, o: 1 }, [[19550, 20150, { y: 0 }]]);
  track('actions-a', { o: 1 }, [[19550, 19750, { o: 0 }]]);
  track('actions-b', { o: 0 }, [[19850, 20150, { o: 1 }]]);
  track('answer-pill', { x: 0, y: 0, s: 1 }, [[19400, 19480, { s: .93 }], [19480, 19600, { s: 1 }]]);
  track('send-pill', { x: 0, y: 0, s: 1 }, [[22100, 22180, { s: .93 }], [22180, 22300, { s: 1 }]]);
  track('field-text', { clip: 100 }, [[20150, 21450, { clip: 0 }, 'steps(38, end)']]);
  track('answered', { x: x + cw / 2 - answered.w / 2, y: helpY + 6, s: .96, o: 0 }, [[22550, 23000, { y: helpY, s: 1, o: 1 }], [23700, 24300, scrolled(helpY), ease.inOut]]);
  track('resolution', { x: aiX(resolution.w), y: resY + 10, s: .9, o: 0 }, [[22800, 23450, { y: resY, s: 1, o: 1 }, spring], [23700, 24300, scrolled(resY), ease.inOut]]);

  // The task: the order arrives as something to act on. It opens, takes a note, and docks back in the chat.
  track('order-message', { x, y: y1 + 10, s: .94, o: 0, b: 0 }, [[24050, 24650, { y: y1, s: 1, o: 1 }, spring], [26900, 27480, dimmed], [31950, 32450, lit], [32830, 33430, recede, ease.in]]);
  typing('typing-2', taskY, 24600, 25300);
  const hChip = metrics.taskHead ?? 76, hFull = size('task', cw, 430).h;
  const hA = hFull - ((metrics.noteH ?? 92) - (metrics.addNoteH ?? 34));
  const yTop = Math.round(clamp((top + L.floor) / 2 - hFull / 2, 16, Math.max(16, L.floor - hFull)));
  track('task', { x, y: taskY + 14, s: .94, o: 0, h: hChip }, [
    [25400, 26050, { y: taskY, s: 1, o: 1 }, spring],
    [26650, 26780, { s: 1.01 }], [26800, 26880, { s: .985 }],
    [26900, 27650, { y: yTop, s: 1, h: hA }],
    [29000, 29550, { h: hFull }],
    [31950, 32550, { y: taskY, h: hChip }, ease.inOut],
    [32860, 33460, { s: .94, o: 0 }, ease.in]
  ]);
  track('task-meta', { o: 1 }, [[26950, 27200, { o: 0 }], [32250, 32550, { o: 1 }]]);
  track('task-close', { x: 0, y: 0, s: 1, o: 0 }, [[27400, 27700, { o: 1 }], [31700, 31780, { s: .85 }], [31780, 31900, { s: 1 }], [31900, 32050, { o: 0 }]]);
  for (let j = 0; j < 3; j++) track(`task-row-${j}`, { x: 0, y: 10, o: 0 }, [[27000 + j * 70, 27500 + j * 70, { y: 0, o: 1 }]]);
  track('add-note', { x: 0, y: 10, s: 1, o: 0 }, [[27210, 27710, { y: 0, o: 1 }], [28850, 28930, { s: .95 }], [28930, 29060, { s: 1 }], [29060, 29250, { o: 0 }]]);
  track('note', { x: 0, y: 8, o: 0 }, [[29150, 29600, { y: 0, o: 1 }]]);
  track('note-text', { clip: 100 }, [[29600, 31000, { clip: 0 }, 'steps(46, end)']]);

  // Kora: the same face from the introduction to the closing call.
  const slot = metrics.helpSlot ?? { left: 20, top: 20 };
  track('kora', { ...face(...kora, .3), o: 0 }, [
    [4800, 5400, { ...face(...kora, hero), o: 1 }, spring],
    [5900, 7600, { y: kora[1] - FACE / 2 - 6 }, ease.inOut],
    [7900, 8700, face(x + cw - 12, headY + headH / 2, 24 / FACE), ease.inOut],
    [12350, 13050, face(...ai(y2), 28 / FACE), ease.inOut],
    [16400, 16900, face(...ai(y4), 28 / FACE), ease.inOut],
    [17700, 18300, face(x + slot.left + 12, helpY + slot.top + 12, 24 / FACE), ease.inOut],
    [22350, 22950, face(...ai(resY), 28 / FACE), ease.inOut],
    [24400, 24900, face(...ai(taskY), 28 / FACE), ease.inOut],
    // Its part done, Kora steps back as the task lands, and returns for the closing call.
    [25350, 25650, { o: 0 }, ease.in],
    [32900, 33700, { ...face(...endKora, hero), o: 1 }, ease.inOut]
  ]);
  const endTitleY = endTop + block + 24;
  track('end-title', { x: 0, y: endTitleY + 18, s: 1, o: 0, b: 10 }, [[33600, 34300, { y: endTitleY, o: 1, b: 0 }]]);
  track('cta', { x: w / 2 - cta.w / 2, y: endTitleY + endTitle.h + 40, s: 1, o: 0 }, [[34000, 34600, { y: endTitleY + endTitle.h + 28, o: 1 }]]);

  // Captions: the one idea of each chapter, beside the cards.
  const captionTimes = [[8900, 12500], [13100, 17000], [17500, 23300], [23900, 32500]];
  captionTimes.forEach(([tin, tout], i) => {
    const c = size(`caption-${i + 1}`, L.captionWidth, 72), y = compact ? 24 : Math.round(chaosCy - c.h / 2);
    track(`caption-${i + 1}`, { x: captionX, y: y + 16, s: 1, o: 0, b: 8 }, [[tin, tin + 650, { y, o: 1, b: 0 }], [tout, tout + 400, { y: y - 12, o: 0, b: 6 }, ease.in]]);
  });

  // The pointer, and the ring each press leaves.
  const at = (name, fallback, top) => { const p = metrics[name] ?? fallback; return [x + p.x, top + p.y]; };
  const t1 = [x + cw * .6, rowY(0) + rowH / 2], t2 = [x + cw * .55, taskY + hChip / 2];
  const t3 = at('addNote', { x: 96, y: hA - 40 }, yTop), t4 = at('close', { x: cw - 30, y: hChip / 2 }, yTop);
  const t5 = at('answer', { x: cw - 56, y: hHB - 30 }, helpY - shift), t6 = at('send', { x: cw - 50, y: hHB - 30 }, helpY);
  const press = t => [[t - 150, t - 40, { s: 1.5 }], [t, t + 80, { s: 1.1 }], [t + 80, t + 200, { s: 1.5 }]];
  track('cursor', { ...point(w * .9, h * .84), s: 1, o: 0 }, [
    [11000, 11200, { o: 1 }], [11200, 11800, point(...t1), ease.glide], ...press(11980),
    [12200, 12950, { ...point(t1[0] + 30, y1 + 60), s: 1, o: 0 }, ease.inOut],
    [18300, 18320, point(w * .9, h * .86)], [18500, 18700, { o: 1 }], [18700, 19250, point(...t5), ease.glide], ...press(19400),
    [19600, 20100, { ...point(t5[0] - 60, t5[1] + 36 + shift), s: 1 }, ease.glide],
    [21450, 21950, point(...t6), ease.glide], ...press(22100),
    [22300, 23050, { ...point(t6[0] + 30, t6[1] + 60), s: 1, o: 0 }, ease.inOut],
    [25700, 25720, point(w * .9, h * .86)], [25800, 26000, { o: 1 }], [26000, 26650, point(...t2), ease.glide], ...press(26800),
    [27000, 27600, { ...point(x + cw * .55, yTop + hChip / 2), s: 1 }, ease.glide],
    [28000, 28600, point(...t3), ease.glide], ...press(28850),
    [29100, 29700, { ...point(t3[0] + 70, t3[1] + 36), s: 1 }, ease.glide],
    [31000, 31550, point(...t4), ease.glide], ...press(31700),
    [31900, 32600, { ...point(t4[0] + 20, taskY + 40), s: 1, o: 0 }, ease.inOut]
  ]);
  const ring = ([cx, cy]) => ({ x: cx - RING / 2, y: cy - RING / 2 });
  const clicks = [[12000, t1], [19420, t5], [22120, t6], [26820, t2], [28870, t3], [31720, t4]];
  track('click', { x: 0, y: 0, s: .3, o: 0 }, clicks.flatMap(([t, target]) => [[t - 20, t, { ...ring(target), s: .3, o: .5 }], [t, t + 520, { s: 1.6, o: 0 }]]));
  return { cardWidth: cw, captionWidth: L.captionWidth, tracks };
}

// An original score, synthesized in the browser: no recordings, no licensed music. One bar is 2.4s (100 bpm).
export const bar = 2400;
const CHORDS = Object.freeze({
  Em9: [40, 55, 59, 62, 66], Gmaj9: [43, 59, 62, 66, 69], 'D/F#': [42, 57, 62, 64, 69],
  Cmaj9: [48, 55, 59, 62, 64], 'G/B': [47, 55, 59, 62, 67], Dsus4: [38, 57, 62, 67, 69]
});
// Two bars of tension under the chats, the reveal, the story building, a breath, then home.
export const progression = Object.freeze(['Em9', 'Em9', 'Gmaj9', 'D/F#', 'Em9', 'Cmaj9', 'G/B', 'D/F#', 'Em9', 'Cmaj9', 'G/B', 'D/F#', 'Em9', 'Dsus4', 'Gmaj9']);
const ARP = [0, 1, 2, 3, 2, 1, 3, 2], MOTIF = [[0, 86], [300, 90], [600, 93], [900, 95], [1500, 93]];
function padCue(index, time, length) {
  // The chords carry the film from the first second: present under the chats, blooming at the reveal.
  const level = index === 0 ? .75 : index === 1 ? .9 : index === 2 || index === progression.length - 1 ? 1.1 : 1;
  return { time, kind: 'pad', chord: progression[index], length, level, bright: index < 2 ? 1000 : index < 5 ? 1250 : index < 13 ? 1700 : 1400 };
}
function musicCues() {
  const cues = [], last = progression.length - 1;
  progression.forEach((name, i) => {
    const t = i * bar, chord = CHORDS[name];
    cues.push(padCue(i, Math.max(1, t), (i === last ? duration - t : bar) / 1000));
    // The chats: a ticking that quickens and brightens into the reveal.
    if (i < 2) for (let k = 0; k < 16; k++) cues.push({ time: Math.max(1, t + k * 150), kind: 'tick', level: .006 + (i * 16 + k) * .0011, pan: k % 2 ? .3 : -.3 });
    if (i === 2 || i === last) MOTIF.forEach(([at, note]) => cues.push({ time: t + at, kind: 'bell', note, pan: (note - 90) / 12 }));
    if (i >= 3 && i <= 12) ARP.forEach((step, k) => cues.push({ time: t + k * 300, kind: 'arp', note: chord[1 + step] + 12, accent: k % 4 === 0, level: i < 5 ? .7 : 1, pan: k % 2 ? .35 : -.35 }));
    if (i >= 5 && i <= 12) {
      [0, 1200].forEach(at => cues.push({ time: t + at, kind: 'kick' }));
      [0, 900, 1200, 1800].forEach(at => cues.push({ time: t + at, kind: 'bass', note: chord[0] + 12 }));
    }
    if (i >= 7 && i <= 12) [0, 1, 2, 3].forEach(b => cues.push({ time: t + b * 600 + 300, kind: 'hat' }));
    if (i >= 10 && i <= 12) [600, 1800].forEach(at => cues.push({ time: t + at, kind: 'clap' }));
  });
  return cues;
}
function effectCues() {
  const pan = PINGS.map(([fx]) => (fx - .5) * 1.4);
  const keys = (from, to) => Array.from({ length: Math.floor((to - from) / 90) }, (_, n) => ({ time: from + n * 90 + (n * 37) % 23, kind: 'key', n }));
  return [
    ...pingTimes.map((time, n) => ({ time: time + 20, kind: 'pop', n: n % 6, pan: pan[n] })),
    { time: 3700, kind: 'riser' }, { time: 4800, kind: 'reveal' },
    ...CAST_SLOTS.map((slot, i) => ({ time: 5070 + i * 70, kind: 'bell', note: 81 + [0, 2, 4, 7, 9, 12, 14, 16][i], pan: slot / 5, level: .03 })),
    { time: 7700, kind: 'whoosh', pan: 0 },
    { time: 10260, kind: 'pop', n: 3 }, { time: 11980, kind: 'click' }, { time: 12200, kind: 'whoosh', pan: -.4, panTo: .1 },
    { time: 13060, kind: 'typing' }, { time: 14420, kind: 'reply' }, { time: 15820, kind: 'pop', n: 2, pan: -.2 }, { time: 16520, kind: 'typing' },
    { time: 17300, kind: 'whoosh', soft: true }, { time: 17980, kind: 'attention' },
    { time: 19400, kind: 'click' }, { time: 19560, kind: 'whoosh', soft: true }, ...keys(20150, 21450),
    { time: 22100, kind: 'click' }, { time: 22130, kind: 'send' }, { time: 22820, kind: 'reply' }, { time: 22950, kind: 'success' },
    { time: 23700, kind: 'whoosh', soft: true }, { time: 24070, kind: 'pop', n: 4, pan: -.2 }, { time: 24620, kind: 'typing' }, { time: 25450, kind: 'chime' },
    { time: 26800, kind: 'click' }, { time: 26920, kind: 'whoosh', pan: .2, panTo: -.2 }, { time: 28850, kind: 'click' }, ...keys(29600, 31000),
    { time: 31700, kind: 'click' }, { time: 31960, kind: 'whoosh', down: true }, { time: 32400, kind: 'riser', short: true },
    { time: 32800, kind: 'whoosh', soft: true }, { time: 33600, kind: 'finale' }
  ];
}
export const soundCues = Object.freeze([...effectCues(), ...musicCues()].sort((a, b) => a.time - b.time).map(c => Object.freeze(c)));
export const cuesBetween = (from, to) => soundCues.filter(c => c.time > from && c.time <= to);

const midi = n => 440 * 2 ** ((n - 69) / 12);
export function createFilmAudio(Context = globalThis.AudioContext ?? globalThis.webkitAudioContext) {
  let ctx, master, musicBus, padBus, effectsBus, send, noise, disposed = false;
  const active = new Set();
  function setup() {
    ctx = new Context();
    const limiter = ctx.createDynamicsCompressor();
    limiter.threshold.value = -14; limiter.ratio.value = 3; limiter.attack.value = .004; limiter.release.value = .25;
    limiter.connect(ctx.destination);
    master = ctx.createGain(); master.gain.value = .85; master.connect(limiter);
    // A soft stereo hall: decaying noise as the room's impulse, a different one in each ear.
    const reverb = ctx.createConvolver(), length = Math.floor(ctx.sampleRate * 3.2), room = ctx.createBuffer(2, length, ctx.sampleRate);
    for (let c = 0; c < 2; c++) { const d = room.getChannelData(c); for (let i = 0; i < length; i++) d[i] = (Math.random() * 2 - 1) * (1 - i / length) ** 3.4; }
    reverb.buffer = room;
    send = ctx.createGain(); send.gain.value = .34; send.connect(reverb); reverb.connect(master);
    musicBus = ctx.createGain(); musicBus.gain.value = .56; musicBus.connect(master);
    padBus = ctx.createGain(); padBus.gain.value = 1; padBus.connect(musicBus);
    effectsBus = ctx.createGain(); effectsBus.gain.value = .9; effectsBus.connect(master);
    noise = ctx.createBuffer(1, ctx.sampleRate, ctx.sampleRate);
    const n = noise.getChannelData(0); for (let i = 0; i < n.length; i++) n[i] = Math.random() * 2 - 1;
  }
  function envelope(param, t, attack, hold, release, level) {
    param.setValueAtTime(0, t);
    param.linearRampToValueAtTime(level, t + attack);
    if (hold) param.setValueAtTime(level, t + attack + hold);
    param.exponentialRampToValueAtTime(.0001, t + attack + hold + release);
    return t + attack + hold + release;
  }
  function route(gain, nodes, bus, wet, pan, panTo, t, end) {
    let tail = gain;
    if ((pan || panTo !== undefined) && ctx.createStereoPanner) {
      const p = ctx.createStereoPanner();
      p.pan.setValueAtTime(pan ?? 0, t);
      if (panTo !== undefined) p.pan.linearRampToValueAtTime(panTo, end);
      tail.connect(p); tail = p; nodes.push(p);
    }
    tail.connect(bus);
    if (wet) { const g = ctx.createGain(); g.gain.value = wet; tail.connect(g); g.connect(send); nodes.push(g); }
  }
  function run(sources, gain, nodes, t, end) {
    const entry = { sources, gain };
    active.add(entry);
    sources.forEach(s => { s.start(t); s.stop(end + .02); });
    sources[0].onended = () => { nodes.forEach(node => { try { node.disconnect(); } catch { /* Already gone. */ } }); active.delete(entry); };
  }
  function tone({ type = 'sine', freq, to, glide = .06, at = 0, attack = .005, hold = 0, release = .2, level = .2, bus = effectsBus, wet = .15, pan = 0, panTo, cutoff, detune = 0, fm, wobble }) {
    const t = ctx.currentTime + at, o = ctx.createOscillator(), g = ctx.createGain();
    o.type = type; o.detune.value = detune;
    o.frequency.setValueAtTime(freq, t);
    if (to) o.frequency.exponentialRampToValueAtTime(to, t + glide);
    const end = envelope(g.gain, t, attack, hold, release, level), sources = [o], nodes = [o, g];
    let head = o;
    if (fm) {
      // A second oscillator bending the first: a soft, glassy bell.
      const mod = ctx.createOscillator(), depth = ctx.createGain();
      mod.frequency.setValueAtTime(freq * fm.ratio, t);
      depth.gain.setValueAtTime(freq * fm.depth, t); depth.gain.exponentialRampToValueAtTime(1, t + fm.decay);
      mod.connect(depth); depth.connect(o.frequency); sources.push(mod); nodes.push(mod, depth);
    }
    if (cutoff) {
      const f = ctx.createBiquadFilter(); f.type = 'lowpass'; f.frequency.setValueAtTime(cutoff, t); head.connect(f); head = f; nodes.push(f);
      if (wobble) {
        // A slow breath in the chord's brightness, so a held pad never sits still.
        const lfo = ctx.createOscillator(), amount = ctx.createGain();
        lfo.frequency.setValueAtTime(wobble.rate, t); amount.gain.setValueAtTime(wobble.depth, t);
        lfo.connect(amount); amount.connect(f.frequency); sources.push(lfo); nodes.push(lfo, amount);
      }
    }
    head.connect(g); route(g, nodes, bus, wet, pan, panTo, t, end); run(sources, g, nodes, t, end);
  }
  function hiss({ at = 0, attack = .002, hold = 0, release = .05, level = .1, type = 'highpass', from = 3000, to, q = .7, bus = effectsBus, wet = .1, pan = 0, panTo }) {
    const t = ctx.currentTime + at, s = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain();
    s.buffer = noise; s.loop = true;
    f.type = type; f.Q.value = q; f.frequency.setValueAtTime(from, t);
    if (to) f.frequency.exponentialRampToValueAtTime(to, t + attack + hold + release);
    const end = envelope(g.gain, t, attack, hold, release, level), nodes = [s, f, g];
    s.connect(f); f.connect(g); route(g, nodes, bus, wet, pan, panTo, t, end); run([s], g, nodes, t, end);
  }
  const bell = (note, at, level, decay, pan = 0) => tone({ freq: midi(note), at, attack: .003, release: decay, level, wet: .4, pan, fm: { ratio: 3.5, depth: 1.1, decay: decay * .6 } });
  const sounds = {
    pop: c => { const f = 620 * 2 ** ((c.n ?? 0) * 2 / 12); tone({ freq: f * .8, to: f * 1.25, glide: .035, release: .13, level: .15, wet: .2, pan: c.pan }); tone({ type: 'triangle', freq: f * 2.5, at: .012, release: .07, level: .035, pan: c.pan }); },
    click: () => { hiss({ from: 2800, release: .016, level: .2, wet: .04 }); tone({ freq: 1500, to: 650, glide: .02, release: .03, level: .08, wet: 0 }); },
    whoosh: c => hiss({ type: 'bandpass', from: c.down ? 2600 : 380, to: c.down ? 420 : 2600, q: 1.1, attack: .16, release: .34, level: c.soft ? .055 : .1, wet: .28, pan: c.pan ?? (c.down ? .3 : -.3), panTo: c.panTo ?? (c.down ? -.3 : .3) }),
    riser: c => {
      const length = c.short ? .9 : 1.1;
      hiss({ type: 'bandpass', from: 220, to: 3600, q: 1.5, attack: length - .06, release: .06, level: c.short ? .07 : .12, wet: .35, pan: -.4, panTo: .4 });
      tone({ type: 'sawtooth', freq: 110, to: 440, glide: length, attack: length - .1, release: .08, level: c.short ? .012 : .022, cutoff: 1200, wet: .2 });
    },
    reveal: () => {
      tone({ freq: 70, to: 34, glide: 1.4, attack: .008, release: 1.9, level: .45, wet: .15 });
      hiss({ type: 'lowpass', from: 1400, to: 160, release: .7, level: .12, wet: .4 });
      [74, 78, 81, 86, 90].forEach((n, i) => bell(n, .04 + i * .05, .05, 2.6, (i - 2) * .25));
      [2637, 3136, 3951].forEach((f, i) => tone({ freq: f, at: .1 + i * .08, attack: .4, release: 2.4, level: .006, wet: .8, pan: (i - 1) * .5 }));
    },
    bell: c => bell(c.note, 0, c.level ?? .045, 1.4, c.pan),
    typing: () => [0, .11, .22].forEach((at, i) => tone({ freq: 1050 + i * 60, at, release: .05, level: .018, wet: .12, pan: .3 })),
    reply: () => { tone({ freq: 587, to: 880, glide: .08, release: .18, level: .11, wet: .22, pan: .25 }); bell(81, .07, .04, .8, .25); },
    chime: () => { bell(83, 0, .07, 1.5, -.15); bell(88, .09, .065, 1.7, .15); bell(95, .2, .025, 1.6, .3); },
    key: c => hiss({ type: 'bandpass', from: 3200 + (c.n * 397) % 1100, q: 2.2, release: .018 + (c.n % 3) * .004, level: .05 + (c.n % 4) * .006, wet: .04, pan: ((c.n * 7) % 5 - 2) * .06 }),
    attention: () => [81, 85, 88].forEach((n, i) => tone({ type: 'triangle', freq: midi(n), at: i * .075, release: .24, level: .065, wet: .22, pan: (i - 1) * .2 })),
    send: () => { hiss({ type: 'bandpass', from: 700, to: 3800, q: 1.2, attack: .05, release: .18, level: .08, wet: .15, pan: -.2, panTo: .3 }); tone({ freq: 880, to: 1320, glide: .08, at: .05, release: .12, level: .04, wet: .2 }); },
    success: () => [79, 83, 86, 91].forEach((n, i) => bell(n, i * .06, .045, 1.5, (i - 1.5) * .2)),
    finale: () => { tone({ freq: 98, to: 49, glide: 1.4, release: 2.6, level: .28, wet: .3 }); hiss({ type: 'lowpass', from: 900, to: 150, release: .6, level: .06, wet: .4 }); [79, 83, 86, 90, 93, 98].forEach((n, i) => bell(n, .04 + i * .08, .04, 2.8, (i - 2.5) * .18)); },
    pad: c => {
      const chord = CHORDS[c.chord], length = c.length, attack = Math.min(.9, length * .4), hold = Math.max(0, length - attack);
      chord.slice(1).forEach((n, k) => [-7, 7].forEach(detune => tone({ type: 'sawtooth', freq: midi(n), detune, attack, hold, release: 1.8, level: .021 * c.level, bus: padBus, wet: .5, cutoff: c.bright, pan: detune < 0 ? -.28 : .28, wobble: { rate: .15 + k * .03, depth: c.bright * .1 } })));
      tone({ freq: midi(chord[0]), attack: Math.min(.4, attack), hold, release: 1.2, level: .07 * c.level, bus: padBus, wet: 0 });
    },
    tick: c => hiss({ from: 6500, release: .022, level: c.level, bus: musicBus, wet: .12, pan: c.pan }),
    arp: c => tone({ type: 'triangle', freq: midi(c.note), release: .34, level: (c.accent ? .042 : .028) * c.level, bus: musicBus, wet: .42, cutoff: 3200, pan: c.pan }),
    bass: c => tone({ type: 'triangle', freq: midi(c.note), attack: .008, release: .34, level: .055, bus: musicBus, wet: 0, cutoff: 520 }),
    kick: () => { tone({ freq: 140, to: 45, glide: .1, attack: .002, release: .32, level: .3, bus: musicBus, wet: 0 }); hiss({ from: 4200, release: .008, level: .04, bus: musicBus, wet: 0 }); },
    hat: () => hiss({ from: 7500, release: .03, level: .022, bus: musicBus, wet: .06, pan: .2 }),
    clap: () => [0, .011, .023].forEach((at, i) => hiss({ type: 'bandpass', from: 1500, q: .9, at, release: i === 2 ? .14 : .012, level: .035, bus: musicBus, wet: .32, pan: -.1 }))
  };
  function stopAll() {
    if (!ctx) return;
    const now = ctx.currentTime;
    for (const { sources, gain } of active) {
      try { gain.gain.cancelScheduledValues(now); gain.gain.setTargetAtTime(0, now, .015); sources.forEach(s => s.stop(now + .08)); }
      catch { /* An already-ended voice has nothing left to stop. */ }
    }
    active.clear();
  }
  return {
    // Called synchronously by a native button listener, before the first await.
    async enableFromGesture() {
      if (disposed || !Context) return false;
      if (!ctx) setup();
      await ctx.resume();
      return !disposed && ctx.state === 'running';
    },
    play(cue) {
      if (disposed || ctx?.state !== 'running') return;
      sounds[cue.kind]?.(cue);
    },
    // Joining mid-bar (sound turned on, play after a pause): the bar's chord picks up at once, effects don't replay.
    catchUp(elapsed) {
      if (disposed || ctx?.state !== 'running') return;
      const index = Math.floor(elapsed / bar);
      if (index < 0 || index >= progression.length) return;
      const end = index === progression.length - 1 ? duration : (index + 1) * bar;
      if (end - elapsed > 500) sounds.pad(padCue(index, elapsed, (end - elapsed) / 1000));
    },
    stopAll,
    release() { disposed = true; stopAll(); if (ctx) ctx.close().catch(() => {}); }
  };
}

function measure(canvas, element) {
  const q = name => canvas.querySelector?.(`[data-film-actor="${name}"]`);
  const sizes = {};
  for (const node of canvas.querySelectorAll?.(':scope > [data-film-actor]') ?? []) sizes[node.dataset.filmActor] = { w: node.offsetWidth, h: node.offsetHeight };
  const cta = element.querySelector?.('[data-film-cta]');
  if (cta?.offsetWidth) sizes.cta = { w: cta.offsetWidth, h: cta.offsetHeight };
  const within = (child, parent) => {
    const c = child?.getBoundingClientRect?.(), p = parent?.getBoundingClientRect?.();
    return c && p && c.width ? { x: c.left - p.left + c.width / 2, y: c.top - p.top + c.height / 2, left: c.left - p.left, top: c.top - p.top } : undefined;
  };
  const task = q('task'), help = q('help'), inquiry = q('inquiry');
  const style = inquiry && globalThis.getComputedStyle?.(inquiry);
  const field = within(q('help-field'), help), actions = within(q('help-actions'), help);
  return {
    sizes,
    snippet: within(q('customer-sub'), q('customer')),
    inquiryPad: style ? { x: parseFloat(style.paddingLeft) || 16, y: parseFloat(style.paddingTop) || 12 } : undefined,
    taskHead: task?.querySelector?.('.product-film__task-head')?.offsetHeight,
    noteH: q('note')?.offsetHeight, addNoteH: q('add-note')?.offsetHeight,
    addNote: within(q('add-note'), task), close: within(q('task-close'), task),
    fieldShift: field && actions ? actions.top - field.top : undefined,
    answer: within(q('answer-pill'), help), send: within(q('send-pill'), help),
    helpSlot: within(help?.querySelector?.('.product-film__help-slot'), help)
  };
}

export function bindFilm(element, receiver) {
  const motion = matchMedia('(prefers-reduced-motion: reduce)'), connection = navigator.connection;
  const canvas = element.querySelector('.product-film__canvas');
  const buttons = [...element.querySelectorAll('.product-film__chapter')];
  const soundButton = element.querySelector('[data-film-sound]'), audio = createFilmAudio();
  const spring = globalThis.CSS?.supports?.('transition-timing-function', 'linear(0, 1)') ? springEasing() : ease.pop;
  const autoplay = () => !motion.matches && !connection?.saveData;
  // Without autoplay the film opens on its introduction, complete, rather than an empty first frame.
  let elapsed = autoplay() ? 0 : posters[0], last = 0, raf = 0, playing = false, visible = false, disposed = false;
  let userPaused = false, finished = false, manualPlay = false, scene = -1, beat = -1, animations = [];
  let soundOn = false, soundRequested = false, soundVersion = 0;
  const notify = () => receiver.invokeMethodAsync('PlaybackChanged', playing, finished).catch(() => {});
  let saidHello = false;
  const notifySound = unavailable => {
    if (disposed) return;
    soundButton?.setAttribute('aria-pressed', String(soundOn));
    receiver.invokeMethodAsync('SoundChanged', soundOn, unavailable).catch(() => {});
  };
  async function toggleSound() {
    const version = ++soundVersion;
    soundRequested = !soundRequested; audio.stopAll();
    if (!soundRequested) { soundOn = false; notifySound(false); return; }
    try {
      const enabled = await audio.enableFromGesture();
      if (disposed || version !== soundVersion) return;
      soundOn = enabled; soundRequested = enabled; notifySound(!enabled);
      if (soundOn && playing) audio.catchUp(elapsed);
    } catch {
      if (disposed || version !== soundVersion) return;
      soundOn = false; soundRequested = false; notifySound(true);
    }
  }
  soundButton?.addEventListener('click', toggleSound);
  function paint() {
    const f = frameAt(elapsed);
    if (scene !== f.scene) { scene = f.scene; element.dataset.scene = String(scene); buttons.forEach((b, i) => i === scene ? b.setAttribute('aria-current', 'step') : b.removeAttribute('aria-current')); }
    if (beat !== f.beat) { beat = f.beat; element.dataset.beat = String(beat); }
    element.style.setProperty('--ds-film-progress', String(f.progress));
    f.chapterProgress.forEach((p, i) => element.style.setProperty(`--ds-film-chapter-${i}`, String(r3(p))));
    animations.forEach(a => { a.currentTime = elapsed; });
    const hello = elapsed >= helloAt;
    if (hello !== saidHello) { saidHello = hello; receiver.invokeMethodAsync('KoraHello', hello).catch(() => {}); }
  }
  function compose() {
    animations.forEach(a => a.cancel());
    const L = layoutFor(canvas.clientWidth, canvas.clientHeight);
    element.style.setProperty('--ds-film-card-width', L.cw + 'px');
    element.style.setProperty('--ds-film-caption-width', L.captionWidth + 'px');
    const composition = buildMotionTracks(canvas.clientWidth, canvas.clientHeight, measure(canvas, element), { spring });
    animations = composition.tracks.flatMap(({ name, keyframes }) => {
      const node = name === 'cta' ? element.querySelector('[data-film-cta]') : canvas.querySelector(`[data-film-actor="${name}"]`);
      if (!node || typeof node.animate !== 'function') return [];
      const a = node.animate(keyframes, { duration, fill: 'both' }); a.pause(); return [a];
    });
    element.dataset.motionReady = 'true';
    paint();
  }
  function stop() { audio.stopAll(); if (!playing) return; playing = false; cancelAnimationFrame(raf); element.dataset.playing = 'false'; notify(); }
  function tick(now) {
    if (!playing || disposed) return;
    const previous = elapsed;
    elapsed = Math.min(duration, elapsed + Math.min(now - last, 200)); last = now; paint();
    if (soundOn) cuesBetween(previous, elapsed).forEach(c => audio.play(c));
    if (elapsed >= duration) { finished = true; element.dataset.finished = 'true'; stop(); notify(); return; }
    raf = requestAnimationFrame(tick);
  }
  function start() {
    if (playing || disposed || !visible || document.hidden) return;
    playing = true; element.dataset.playing = 'true'; last = performance.now(); raf = requestAnimationFrame(tick); notify();
    if (soundOn) audio.catchUp(elapsed);
  }
  function reconcile() {
    if (visible && !document.hidden && !userPaused && !finished && (manualPlay || autoplay())) start();
    else stop();
  }
  const observer = new IntersectionObserver(([e]) => { visible = e.isIntersecting; reconcile(); }, { threshold: .3 });
  const resize = new ResizeObserver(() => { if (!disposed) compose(); });
  compose(); resize.observe(canvas); observer.observe(element.querySelector('.product-film__screen'));
  document.fonts?.ready.then(() => { if (!disposed) compose(); });
  document.addEventListener('visibilitychange', reconcile); motion.addEventListener('change', reconcile); connection?.addEventListener('change', reconcile);
  element.dataset.playing = 'false';
  const restart = at => { audio.stopAll(); elapsed = at; finished = false; element.dataset.finished = 'false'; manualPlay = true; userPaused = false; paint(); if (playing && soundOn) audio.catchUp(elapsed); reconcile(); };
  return {
    toggle() { if (playing) { userPaused = true; stop(); } else if (finished) restart(0); else { manualPlay = true; userPaused = false; reconcile(); } },
    replay() { restart(0); },
    // A chapter plays from its start; without autoplay it shows that chapter's still instead.
    seek(index) {
      if (!Number.isInteger(index) || index < 0 || index >= chapters.length) return;
      if (!autoplay() && !manualPlay) { userPaused = true; finished = false; stop(); elapsed = posters[index]; paint(); return; }
      restart(chapters[index]);
    },
    async signup() { const { toForm } = await import('./landing.js'); if (!disposed) toForm(element); },
    release() { disposed = true; ++soundVersion; soundOn = false; stop(); audio.release(); soundButton?.removeEventListener('click', toggleSound); observer.disconnect(); resize.disconnect(); animations.forEach(a => a.cancel()); animations = []; document.removeEventListener('visibilitychange', reconcile); motion.removeEventListener('change', reconcile); connection?.removeEventListener('change', reconcile); }
  };
}
