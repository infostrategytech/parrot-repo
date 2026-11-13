import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import test from 'node:test';

const read = name => readFileSync(fileURLToPath(new URL(`../../../../${name}`, import.meta.url)), 'utf8');
const component = read('Parrot.SharedCompnents/DesignSystem/PresenceAvatar.razor');
const styles = read('Parrot.SharedCompnents/DesignSystem/PresenceAvatar.razor.css');
// The avatar playground is a local design page that stays out of production branches; its checks run where it is.
const optional = name => existsSync(fileURLToPath(new URL(`../../../../${name}`, import.meta.url))) ? read(name) : null;
const page = optional('Parrot.Web/Parrot.Web/Components/Pages/DesignSystem/AvatarPlayground.razor');
const layout = optional('Parrot.Web/Parrot.Web/Components/Pages/DesignSystem/AvatarPlayground.razor.css');
const playground = { skip: page === null && 'the playground is local only' };
const routes = read('Parrot.Web/Parrot.Web/Components/Routes.razor');
const chooser = read('Parrot.SharedCompnents/DesignSystem/AgentAvatarChooser.razor');
const chooserStyles = read('Parrot.SharedCompnents/DesignSystem/AgentAvatarChooser.razor.css');

test('reduced motion disables every animation without hiding the expression', () => {
  assert.match(styles, /@media \(prefers-reduced-motion: reduce\)[\s\S]*animation: none !important; transition: none !important;/);
  assert.doesNotMatch(styles.slice(styles.indexOf('@media (prefers-reduced-motion')), /display: none|opacity: 0/);
  assert.doesNotMatch(component, /<animate|MicLevelAsync|Task\.Delay|PeriodicTimer/);
});
test('pausing leaves final state badges visible rather than freezing their entrance', () => {
  assert.match(styles, /\.presence-avatar\.is-paused[^}]*animation: none !important/);
  assert.doesNotMatch(styles, /animation-play-state: paused/);
});
test('small conversation avatars do not bounce and completion only plays once', () => {
  // Match the state selector's specificity, so the small-size rule really wins.
  assert.match(styles, /\.presence-avatar\.presence-avatar--small \.presence-avatar__character \{ animation: none;/);
  assert.match(styles, /presence-done[^;]+ 1 both/);
  assert.match(styles, /presence-acknowledge[^;]+ 1 both/);
});
test('playground scrolls independently and stacks below the phone breakpoint', playground, () => {
  assert.match(layout, /height: 100dvh/); assert.match(layout, /overflow-y: auto/);
  assert.match(layout, /@media \(max-width: 760px\)[\s\S]*grid-template-columns: minmax\(0, 1fr\)/);
});
test('preview controls reuse shared components and show a textual status', playground, () => {
  assert.doesNotMatch(page + chooser, /<button|<input|<select/);
  assert.match(page, /<AgentAvatarChooser/); assert.match(chooser, /aria-label="Character colour"/);
  assert.match(page, /<ThemeToggle Inline/); assert.match(page, /aria-live="polite"/);
});
test('public access is limited to the exact playground path and local mode', playground, () => {
  assert.match(routes, /Demo.Enabled && new Uri\(Nav.Uri\).AbsolutePath.TrimEnd\('\/'\).Equals\("\/design-system\/avatar-playground", StringComparison.OrdinalIgnoreCase\)/);
  assert.match(page, /if \(!Demo.Enabled\)/);
});
test('character colours use semantic tokens rather than fixed brand hex or primitive ramps', () => {
  assert.doesNotMatch(styles, /#[0-9a-f]{3,8}\b|var\(--p-/i);
  for (const tone of ['violet', 'green', 'sky', 'amber']) assert.match(styles, new RegExp(`--colors-accent-${tone}-solid`));
  assert.match(styles, /--colors-brand-secondary/);
});
test('avatars are silhouette-and-eyes only, without shaded fills or accessories', playground, () => {
  assert.doesNotMatch(component, /<defs|<linearGradient|<filter|__orbit|__visor|__antenna|__leaves|__mouth/);
  assert.doesNotMatch(styles, /gradient\(|box-shadow|drop-shadow|__highlight/);
  assert.doesNotMatch(layout, /radial-gradient/);
});
test('eye placement and animated glances have separate transform layers', () => {
  assert.match(component, /class="presence-avatar__face-anchor" transform="@Style.FacePosition"/);
  assert.match(component, /class="presence-avatar__face"/);
  assert.match(styles, /data-state="thinking"\] \.presence-avatar__face/);
  assert.match(styles, /data-state="working"\] \.presence-avatar__face/);
});
test('every character is alive; the preview, selection and workspace examples float, the picker grid stays grounded', playground, () => {
  // Designer, 9 November 2026: "more reactive, react to hover too", then "let them float".
  const previews = [...page.matchAll(/<PresenceAvatar\b[^>]+\/>/g)].map(match => match[0]);
  assert.equal(previews.length, 4);
  assert.ok(previews.every(tag => /\bReactive Floating\b/.test(tag) && tag.includes('Paused="@(_paused || _conversation.IsOpen)"')));
  assert.match(chooser, /Size="PresenceAvatarSize.Medium" Reactive Decorative/);
  assert.doesNotMatch(chooser, /Floating/);
  // One living preview: a new state, shape or colour blends in rather than remounting it.
  assert.doesNotMatch(page, /<PresenceAvatar @key/);
});
test('every face has its still pose for each state, carried by the eyes alone', () => {
  for (const state of ['thinking', 'working', 'waiting', 'blocked', 'done', 'listening', 'speaking'])
    assert.match(styles, new RegExp(`\\[data-state="${state}"\\] \\{[^}]*--ds-pose-`));
  // A single brow was tried and dropped (designer, 9 November 2026: it read as unfinished).
  assert.doesNotMatch(component + styles, /__brow/);
  assert.match(component, /class="presence-avatar__happy"/);
  assert.match(styles, /\[data-live\][^{]*\{ animation: none !important; transition: none !important; \}/);
});
test('every bright colour has a fill, in the avatar and its swatch', () => {
  for (const tone of ['red', 'coral', 'orange', 'amber', 'yellow', 'lime', 'green', 'teal', 'sky', 'blue', 'indigo', 'violet', 'fuchsia', 'pink']) {
    assert.match(styles, new RegExp(`\\.presence-avatar--${tone} \\{ --ds-presence-fill: [^;]+; \\}`));
    assert.match(chooserStyles, new RegExp(`\\.avatar-swatch--${tone} \\{ --swatch: [^;]+; --swatch-ink: [^;]+; \\}`));
  }
});
test('every avatar has white eyes with a near-black pupil, the same in both themes; no solid-eye option', playground, () => {
  // Designer, 10 November 2026: white eyes approved, solid eyes retired.
  assert.doesNotMatch(page, /PresenceAvatarEyes/);
  assert.doesNotMatch(component, /PresenceAvatarEyes|Whites|presence-avatar--whites/);
  assert.match(component, /<circle class="presence-avatar__white" cx="49" cy="61" r="13" \/>/);
  assert.match(styles, /--ds-presence-white: var\(--colors-text-on-primary\)/);
  assert.match(styles, /\.presence-avatar__pupil \{\s*fill: var\(--ds-presence-pupil\)/);
  assert.doesNotMatch(styles, /presence-avatar--whites|--ds-presence-ink/);
  assert.match(styles, /\[data-live\][^{]*\.presence-avatar__eyeball/);
});
test('done is a success: a green badge, glow and sparkles from the feedback tokens', () => {
  assert.match(styles, /\[data-state="done"\] \.presence-avatar__badge circle \{ fill: var\(--colors-feedback-success\); \}/);
  assert.match(styles, /\[data-state="done"\] \.presence-avatar__badge path \{ stroke: var\(--colors-feedback-success-foreground\); \}/);
  assert.match(styles, /\[data-state="done"\] :is\(\.presence-avatar__echo, \.presence-avatar__sparkle\) \{ fill: var\(--colors-feedback-success\); \}/);
});
test('selection is announced and confirmation is limited to the playground', playground, () => {
  assert.match(chooser, /aria-pressed=/); assert.match(chooser, /aria-label=.*avatar/);
  assert.match(page, /Use this avatar/); assert.match(page, /Selected for this preview/);
  assert.match(page, /No live agent is created or changed/);
  assert.doesNotMatch(page + chooser, /@inject.*(?:AiAgentsState|HttpClient|IAgent|Store)|SaveAsync/);
});
test('picker choices wrap and have explicit keyboard focus and disabled presentation', () => {
  assert.match(chooserStyles, /:focus-visible/); assert.match(chooserStyles, /:disabled/);
  assert.match(chooserStyles, /max-width: 760px/); assert.match(chooserStyles, /flex-wrap: wrap/);
});

test('the chosen avatar opens an isolated Talk or Type example, without replacing the live flow', playground, () => {
  const example = read('Parrot.SharedCompnents/DesignSystem/AgentConversationExample.razor');
  assert.match(page, /Try Talk or Type/); assert.match(page, /<ContentModal Open/);
  assert.match(page, /<AgentConversationExample Appearance="_choice" Name="@DisplayName"/);
  assert.match(example, /Talk to me/); assert.match(example, />Type</);
  assert.match(example, /Microphone off; no recording or audio playback/);
  assert.doesNotMatch(example, /<VoiceOrb|<button|<textarea|<input/);
  assert.doesNotMatch(page, /Name="DisplayName"|Value="_name"/);
});

test('text example scrolls its thread, leaving the composer in its own grid row', () => {
  const css = read('Parrot.SharedCompnents/DesignSystem/AgentConversationExample.razor.css');
  assert.match(css, /grid-template-rows: minmax\(0, 1fr\) auto/);
  assert.match(css, /__thread[^}]*overflow-y: auto/);
});
