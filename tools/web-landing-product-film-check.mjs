// The product film's clock, motion and sound, under controlled playback (18 November 2026). Not a substitute for
// watching and listening in a real browser.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../Parrot.Web/Parrot.Web/wwwroot/js/landing-product-film.js', import.meta.url), 'utf8');
const { bindFilm, frameAt, duration, chapters, posters, ending, buildMotionTracks, soundCues, cuesBetween, createFilmAudio, springEasing, progression, bar } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
let checks = 0;
function check(condition, label) { assert.ok(condition, label); checks++; }

// The clock.
check(frameAt(-1).scene === 0 && frameAt(duration + 1).progress === 1, 'The timeline clamps at both ends.');
for (const [index, time] of chapters.entries()) check(frameAt(time).scene === index, `Chapter ${index} starts where it says.`);
check(frameAt(ending - 1).beat === 0 && frameAt(ending).beat === 1, 'The closing lockup follows the last answer.');
check(posters.every((p, i) => frameAt(p).scene === i), 'Each chapter has a still inside it.');
check(frameAt(chapters[2] + 10).chapterProgress[1] === 1 && frameAt(chapters[2] + 10).chapterProgress[3] === 0, 'Chapter bars fill in order.');
check(duration <= 40000, 'The film stays under forty seconds.');
check(/^linear\(0, /.test(springEasing()) && springEasing().endsWith(', 1)'), 'The spring is a CSS linear() curve that lands on 1.');

// The motion: every actor, every width.
for (const width of [300, 390, 728, 1090]) {
  const composition = buildMotionTracks(width, width < 760 ? 700 : 640);
  check(composition.cardWidth <= width - 32, `Cards keep their gutter at ${width}px.`);
  for (const track of composition.tracks) {
    check(track.keyframes.every((k, i, a) => k.offset >= 0 && k.offset <= 1 && (!i || k.offset >= a[i - 1].offset)), `${track.name} has ordered keyframes at ${width}px.`);
    check(!JSON.stringify(track).match(/NaN|undefined|Infinity/), `${track.name} has finite values at ${width}px.`);
    check(track.keyframes.every(k => Object.keys(k).filter(n => n !== 'offset' && n !== 'easing').join() === Object.keys(track.keyframes[0]).filter(n => n !== 'offset').join()), `${track.name} animates the same properties in every frame.`);
  }
  const names = composition.tracks.map(t => t.name);
  for (const actor of ['kora', 'customer', 'inquiry', 'task', 'help', 'cursor', 'intro-title', 'end-title']) check(names.includes(actor), `${actor} is one persistent actor at ${width}px.`);
}
const tracks = Object.fromEntries(buildMotionTracks(1090, 640, { sizes: { task: { w: 440, h: 396 } }, taskHead: 72 }).tracks.map(t => [t.name, t.keyframes]));
check(tracks.task.some(k => k.height === '396px') && tracks.task.some(k => k.height === '72px'), 'The task opens to its measured height and docks back to its head.');
check(tracks.customer.filter(k => k.opacity === 1).at(-1).offset > chapters[4] / duration, 'Tunde stays the conversation header after the task closes.');
check(tracks.kora.filter(k => k.opacity === 1).length > 10, 'Kora stays on screen through the story.');
check(tracks.cursor.some(k => /scale\(1\.1\)/.test(k.transform)), 'The pointer presses.');
check(buildMotionTracks(1090, 640, {}, { spring: 'linear(0, 1)' }).tracks.some(t => t.keyframes.some(k => k.easing === 'linear(0, 1)')), 'Arrivals use the spring when the browser has one.');

// The score: effects and music on one clock.
check(soundCues.every((c, i) => c.time > 0 && c.time < duration && (!i || c.time >= soundCues[i - 1].time)), 'Sound cues are ordered and inside the film.');
check(cuesBetween(11970, 11980).some(c => c.kind === 'click') && cuesBetween(11980, 11990).every(c => c.kind !== 'click'), 'A cue fires once at its boundary.');
check(soundCues.filter(c => c.kind === 'pad').length === progression.length, 'Every bar of the score has its chord.');
check(soundCues.some(c => c.kind === 'arp') && soundCues.some(c => c.kind === 'kick'), 'The music builds: an arpeggio and a soft pulse join.');
check(progression.length * bar >= ending, 'The music reaches the closing lockup.');

// A fake Web Audio, enough to check opt-in, scheduling and cleanup.
let contexts = [];
const param = () => ({ value: 0, setValueAtTime() {}, linearRampToValueAtTime() {}, exponentialRampToValueAtTime() {}, cancelScheduledValues() {}, setTargetAtTime() {} });
const node = extra => ({ connect() {}, disconnect() {}, ...extra });
class FakeAudioContext {
  constructor() { this.currentTime = 0; this.sampleRate = 8000; this.state = 'suspended'; this.sources = []; contexts.push(this); }
  destination = {};
  resume() { this.state = 'running'; return Promise.resolve(); }
  close() { this.state = 'closed'; return Promise.resolve(); }
  createGain() { return node({ gain: param() }); }
  createDynamicsCompressor() { return node({ threshold: param(), ratio: param(), attack: param(), release: param() }); }
  createConvolver() { return node({}); }
  createBiquadFilter() { return node({ frequency: param(), Q: param() }); }
  createStereoPanner() { return node({ pan: param() }); }
  createBuffer(channels, length) { return { getChannelData: () => new Float32Array(length) }; }
  source(extra) { const s = node({ start() { s.started = true; }, stop() { s.stops = (s.stops ?? 0) + 1; }, ...extra }); this.sources.push(s); return s; }
  createOscillator() { return this.source({ frequency: param(), detune: param() }); }
  createBufferSource() { return this.source({}); }
}

function fixture({ reduced = false, saveData = false } = {}) {
  let now = 0, next = 0, intersect, frame;
  const handlers = new Map(), motionHandlers = new Map(), soundHandlers = new Map();
  const soundButton = { attributes: {}, setAttribute(k, v) { this.attributes[k] = v; }, addEventListener: (n, fn) => soundHandlers.set(n, fn), removeEventListener: n => soundHandlers.delete(n) };
  const motion = { matches: reduced, addEventListener: (name, fn) => motionHandlers.set(name, fn), removeEventListener: name => motionHandlers.delete(name) };
  const buttons = chapters.map(() => ({ attributes: {}, setAttribute(key, value) { this.attributes[key] = value; }, removeAttribute(key) { delete this.attributes[key]; } }));
  const animations = [];
  const actor = { animate() { const a = { paused: false, currentTime: 0, cancelled: false, pause() { this.paused = true; }, cancel() { this.cancelled = true; } }; animations.push(a); return a; } };
  const canvas = { clientWidth: 960, clientHeight: 640, querySelector: () => actor, querySelectorAll: () => [] };
  const element = { dataset: {}, styles: {}, style: { setProperty(key, value) { element.styles[key] = value; } }, querySelector: selector => selector === '.product-film__canvas' ? canvas : selector === '[data-film-sound]' ? soundButton : {}, querySelectorAll: () => buttons };
  const calls = [], soundCalls = [], hellos = [];
  const document = { hidden: false, addEventListener: (name, fn) => handlers.set(name, fn), removeEventListener: name => handlers.delete(name) };
  const connection = { saveData, addEventListener() {}, removeEventListener() {} };
  Object.assign(globalThis, {
    matchMedia: () => motion, document,
    IntersectionObserver: class { constructor(fn) { intersect = fn; } observe() {} disconnect() { intersect = null; } },
    ResizeObserver: class { observe() {} disconnect() {} },
    requestAnimationFrame: fn => { frame = fn; return ++next; },
    cancelAnimationFrame: () => { frame = null; }
  });
  Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { connection } });
  Object.defineProperty(globalThis, 'performance', { configurable: true, value: { now: () => now } });
  const player = bindFilm(element, { invokeMethodAsync: (name, ...values) => { if (name === 'SoundChanged') soundCalls.push(values); else if (name === 'KoraHello') hellos.push(values[0]); else calls.push(values); return Promise.resolve(); } });
  return {
    player, element, animations, buttons, calls, soundCalls, hellos, handlers, motionHandlers, soundHandlers,
    sound() { return soundHandlers.get('click')?.(); },
    visible(value) { intersect?.([{ isIntersecting: value }]); },
    hidden(value) { document.hidden = value; handlers.get('visibilitychange')?.(); },
    advance(ms) { for (let remaining = ms; remaining > 0 && frame;) { const step = Math.min(100, remaining); now += step; remaining -= step; const pending = frame; frame = null; pending(now); } }
  };
}

globalThis.AudioContext = FakeAudioContext;
let f = fixture(); f.visible(true); f.advance(13000);
check(contexts.length === 0, 'Silent autoplay never creates an audio context.');
await f.sound();
const joined = contexts[0].sources.length;
check(contexts.length === 1 && f.soundCalls.at(-1)[0] === true, 'Only pressing Sound turns sound on.');
check(joined > 0 && joined <= 24, 'Turning sound on mid-film picks up the bar\'s chord, without replaying past effects.');
f.advance(1100);
check(contexts[0].sources.length > joined, 'Effects and music follow the picture\'s clock.');
f.player.toggle();
check(contexts[0].sources.every(s => s.stops >= 2), 'Pausing stops every sound.');
await f.sound(); const silent = contexts[0].sources.length; f.player.toggle(); f.advance(2000);
check(contexts[0].sources.length === silent && f.soundCalls.at(-1)[0] === false, 'Sound off stays silent.');
f.player.release();
check(contexts[0].state === 'closed' && f.soundHandlers.size === 0, 'Leaving the page closes audio and removes its listener.');
const score = createFilmAudio(FakeAudioContext); await score.enableFromGesture();
for (const cue of soundCues) score.play(cue);
check(contexts.at(-1).sources.every(s => s.started && s.stops === 1), 'Every voice is a finite one-shot with a scheduled stop.');
score.release();
delete globalThis.AudioContext;
f = fixture(); await f.sound();
check(f.soundCalls.at(-1)[0] === false && f.soundCalls.at(-1)[1] === true, 'A browser without audio is told so, and the film still plays.');
f.visible(true); check(f.element.dataset.playing === 'true', 'Missing audio doesn\'t stop the film.'); f.player.release();
let resumeAudio;
class PendingContext extends FakeAudioContext { resume() { return new Promise(resolve => { resumeAudio = () => { this.state = 'running'; resolve(); }; }); } }
globalThis.AudioContext = PendingContext;
f = fixture(); const pending = f.sound(); await f.sound(); resumeAudio(); await pending;
check(f.soundCalls.at(-1)[0] === false, 'A slow audio unlock can\'t undo a newer mute.'); f.player.release();
f = fixture(); const late = f.sound(); f.player.release(); resumeAudio(); await late;
check(f.soundCalls.length === 0, 'An unlock that finishes after leaving doesn\'t touch removed controls.');
delete globalThis.AudioContext;

// Playback.
f = fixture();
check(f.element.dataset.scene === '0' && f.calls.length === 0, 'Before it is in view the film waits on its first frame.');
f.visible(true);
check(f.element.dataset.playing === 'true', 'It plays when it comes into view.');
f.advance(13500);
check(f.element.dataset.scene === '2', 'It reaches the reply chapter on time.');
f.player.toggle(); const paused = f.element.styles['--ds-film-progress']; f.advance(1000);
check(f.element.styles['--ds-film-progress'] === paused && f.animations[0].paused, 'Pause holds the picture.');
f.visible(false); f.visible(true);
check(f.element.dataset.playing === 'false', 'Scrolling back doesn\'t override a pause.');
f.player.toggle(); f.hidden(true);
check(f.element.dataset.playing === 'false', 'A hidden tab pauses it.');
f.hidden(false);
check(f.element.dataset.playing === 'true', 'It resumes on return.');
f.player.seek(3);
check(f.element.dataset.scene === '3' && f.element.dataset.playing === 'true' && f.animations.every(a => a.currentTime === chapters[3]), 'A chapter plays from its start, every actor together.');
check(f.buttons[3].attributes['aria-current'] === 'step' && !f.buttons[2].attributes['aria-current'], 'The current chapter is announced.');
f.player.seek(99);
check(f.element.dataset.scene === '3', 'A chapter that doesn\'t exist changes nothing.');
f.player.replay();
check(f.element.dataset.scene === '0' && f.element.dataset.playing === 'true', 'Replay starts from the beginning.');
f.advance(duration);
check(f.element.dataset.beat === '1' && f.element.dataset.playing === 'false' && f.calls.at(-1)[1] === true, 'It plays once and ends on the lockup.');
check(f.hellos.at(-1) === true, 'Kora says hello on the closing card.');
f.visible(false); f.visible(true);
check(f.element.dataset.playing === 'false', 'A finished film doesn\'t loop on scroll.');
f.player.toggle();
check(f.element.dataset.playing === 'true' && f.element.dataset.scene === '0' && f.hellos.at(-1) === false, 'Play after the end starts again, ready to say hello again.');
f.player.release();
check(f.handlers.size === 0 && f.motionHandlers.size === 0 && f.animations.every(a => a.cancelled), 'Leaving releases listeners and animations.');
for (const options of [{ reduced: true }, { saveData: true }]) {
  f = fixture(options); f.visible(true);
  check(f.element.dataset.playing === 'false' && f.animations[0].currentTime === posters[0], 'Reduced motion or data saving shows the introduction, still.');
  f.player.seek(2);
  check(f.element.dataset.scene === '2' && f.element.dataset.playing === 'false', 'A chapter shows as a still.');
  f.player.toggle();
  check(f.element.dataset.playing === 'true', 'Play is still there for those who want it.');
  f.player.release();
}

// The page around it.
const layout = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Auth/AuthLayout.razor', import.meta.url), 'utf8');
const order = ['<LandingAgentIntroduction />', '<LandingBusinessKnowledge />', '<LandingWorkForward />', '<LandingHumanControl />', '<LandingHowItWorks />', '<LandingFaq />', '<LandingAgentClosing />'];
const dSections = layout.slice(layout.indexOf('<LandingAgentIntroduction />'));
let previous = -1;
for (const section of order) { const position = dSections.indexOf(section); check(position > previous, `${section} keeps its place.`); previous = position; }
const knowledge = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Landing/LandingBusinessKnowledge.razor', import.meta.url), 'utf8');
const knowledgeCss = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Landing/LandingBusinessKnowledge.razor.css', import.meta.url), 'utf8');
check(!dSections.includes('<LandingAgentWork />'), 'The old and new knowledge sections are not shown together.');
check(knowledge.includes('Your business.<br />Your way of working.'), 'The new section leads with the approved business-centred message.');
for (const feature of ['natural','review','update'])check(knowledge.includes(`new("${feature}"`), `The ${feature} benefit has its own preview and accessible description.`);
check(knowledge.includes('var choice = feature;')&&knowledge.includes('OnSelect="() => SelectFeature(choice.Id)"'), 'Each benefit selector captures its own choice, without the deferred-loop problem.');
const featureSelector = await readFile(new URL('../Parrot.SharedCompnents/DesignSystem/FeatureSelectorItem.razor', import.meta.url), 'utf8');
check(featureSelector.includes('aria-pressed=')&&knowledge.includes('aria-live="polite"')&&knowledge.includes('Controls="knowledge-feature-preview"'), 'Benefit controls expose selection and announce the changed preview.');
check(!/<button|<input|<select\b/.test(knowledge)&&knowledge.includes('<FeatureSelectorItem')&&knowledge.includes('<Typography')&&featureSelector.includes('<RowButton'), 'The new section uses the shared feature selector and existing row-button/text primitives.');
check(!/HttpClient|DemoConversations|DemoBusinessKnowledge|@inject|setInterval|setTimeout/.test(knowledge), 'Public examples do not access business records or auto-cycle.');
check(knowledgeCss.includes('prefers-reduced-motion')&&knowledgeCss.includes('@media (max-width: 760px)'), 'The example has a still mode and a single-column small-screen layout.');
check(knowledge.includes('Change a detail whenever your business changes.'), 'The section does not imply one-time teaching.');
check(!knowledge.includes('knowledge-examples-label')&&!knowledge.includes('See it in a reply'), 'The benefits replace the Delivery/Payment/Returns switcher.');
check(knowledge.includes('aria-hidden="true"')&&knowledge.includes('role="img"')&&knowledge.includes('<AvatarTeachingScene'), 'Teaching/review/edit artwork is illustrative, not a live app form.');
check(knowledgeCss.includes('grid-area: 1/1')&&!knowledgeCss.includes('infinite'), 'One steady scene frame uses finite transitions, not a cycling carousel.');
check(knowledgeCss.includes('[data-feature="update"]')&&knowledge.includes('Updated just now')&&knowledge.includes('2–4'), 'Updating details has an explicit finite edit/save illustration.');
check(knowledge.includes('FeatureSelectorTone.Teal')&&knowledge.includes('FeatureSelectorTone.Violet')&&knowledge.includes('FeatureSelectorTone.Amber'), 'The three benefit icons keep their original accents.');
const work = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Landing/LandingWorkForward.razor', import.meta.url), 'utf8');
const workCss = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Landing/LandingWorkForward.razor.css', import.meta.url), 'utf8');
check(!dSections.includes('<LandingAgentDay />'), 'The operational section is not duplicated by an older day section.');
check(work.includes('More than replies.<br />Work moves forward.'), 'The next section uses the approved outcome-led headline.');
for(const id of ['work-forward-orders','work-forward-bookings','work-forward-followups'])check(work.includes(`id="${id}"`), `The ${id} outcome has an accessible heading.`);
check(work.includes('2 × ₦18,500')&&work.includes('₦4,000')&&work.includes('₦41,000'), 'Illustrated order arithmetic remains consistent.');
check(work.includes('Time to be confirmed')&&work.includes('Confirm a suitable time with Kemi.'), 'A requested booking is not presented as a confirmed calendar reservation.');
check(work.includes('Adaeze passed this to Tolu')&&work.includes('Internal note · Adaeze'), 'The handoff carries both its new owner and internal context.');
check(work.includes('Each task stays connected to the customer conversation.'), 'Tasks are shown as connected work rather than another isolated inbox.');
check(!/<button|<input|<select\b|@inject|HttpClient|DemoTask|setInterval|@onclick/.test(work), 'The public illustrations contain no fake interactive controls or workspace-service access.');
check(work.includes('<StatusBadge')&&work.includes('<PetalAvatar')&&work.includes('<Typography'), 'The new task illustrations reuse shared components.');
check(workCss.includes('@media (max-width: 760px)')&&!/min-height|animation:|position: absolute/.test(workCss), 'Cards stack naturally without fixed-height filler, autoplay or fragile absolute positioning.');
const film = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Landing/LandingProductFilm.razor', import.meta.url), 'utf8');
check(film.includes('Introducing <span class="product-film__brand">yára</span>'), 'The film introduces yára.');
check(!/<button|<input|<select\b/.test(film), 'Controls are the shared components.');
check(!/HttpClient|DemoConversations|DemoTask/.test(film + source), 'The public film never fetches workspace data.');
check(film.includes('You answered Kora') && !film.includes('Question task'), 'A question Kora can\'t answer stays in the chat, answered.');
check(!film.includes('product-film__workspace') && film.includes('data-film-actor="task"') && film.includes('Internal note'), 'Cards from the app, not the whole app.');
check(film.includes('data-film-sound="true"') && film.includes('The film works without sound'), 'Sound is optional and says so.');
check(!/border-left/.test(await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Landing/LandingProductFilm.razor.css', import.meta.url), 'utf8')), 'No coloured edge down one side of a quote.');
const intro = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Landing/LandingAgentIntroduction.razor', import.meta.url), 'utf8');
check(intro.includes('Too many chats.<br /> Too much to keep track of.'), 'The section keeps its approved headline.');
// The story (designer, 18 November 2026): the scattered chats hold long enough to read, it's your yára inbox, your
// say comes before the task, the order lives inside a task, every kind of avatar shows, and it ends on a call.
const story = Object.fromEntries(buildMotionTracks(1090, 640).tracks.map(t => [t.name, t.keyframes]));
const firstShown = name => story[name].find(k => k.opacity === 1)?.offset * duration;
check(story['chaos-title'].filter(k => k.opacity === 1).at(-1).offset * duration - firstShown('chaos-title') >= 2400, 'The scattered chats hold long enough to read.');
check(firstShown('help') < firstShown('task'), 'Your say comes before the task.');
check(film.includes('Your yára inbox') && film.includes('CadenceMark'), 'The inbox is clearly the yára inbox.');
check(film.includes('New task from Kora') && film.includes('The order'), 'The order sits inside a task.');
check(film.includes('PresenceAvatarDirection.Fig') && new Set(film.match(/PresenceAvatarDirection\.\w+(?=, PresenceAvatarTone)/g)).size === 4, 'Four teammates, four different shapes, the triangle among them.');
check(!/RiArrowRightLine/.test(film.slice(film.indexOf('data-film-cta'), film.indexOf('product-film__controls'))), 'The closing button has no arrow.');
check(film.includes('product-film__ring-fill') && film.includes('product-film__now'), 'The controls name the chapter and ring the play button with progress.');
check(film.includes('Get started with <span class="product-film__brand">yára</span> today.') && film.includes('data-film-cta'), 'It ends on a call to get started, with a real button.');
console.log(`${checks} landing product-film checks passed.`);
