// Fails the build if a value that should come from a design token is typed in
// by hand, in the design system or any page built on it.
//
//   node design-system/tools/lint-design-system.mjs
//
// This is the same idea as the React linter, pointed somewhere useful. That one
// scanned src/design-system/ only — 1,220 lines — while styles.css rotted
// beside it, unread, to 2,194 hardcoded colours across 1,106 distinct values.
// Its own header warned that a design system dies "one #f4f4f5 at a time" while
// pointing away from the file doing the dying.
//
// The rebuild is greenfield, so the gate is set at zero on the first commit.
// That is a luxury that exists exactly once and expires the moment it is
// spent.
//
// Escape hatch: declare a custom property on the component (--ds-*) with a
// comment saying which token is missing, and use that. Locals are allowed
// precisely so the gaps are visible in one place instead of scattered.
import fs from "node:fs";
import path from "node:path";
import { DESIGN_SYSTEM, TOKENS_CSS, FONTS_CSS, SCANNED, walk, projectOf, rel } from "./paths.mjs";

// Generated from the Figma export; it is supposed to be full of raw values.
const GENERATED = new Set([TOKENS_CSS]);

// Third-party brand marks. WhatsApp green and Facebook blue are not ours to
// tokenise — they are those companies' colours, and they do not change in dark
// mode. Shared with the theme tooling so there is one list, not two.
const BRAND_LITERALS = JSON.parse(
  fs.readFileSync(path.join(DESIGN_SYSTEM, "tools/brand-literals.json"), "utf8")
).map((entry) => entry.value.toLowerCase());

// The sanctioned breakpoint scale. React had ten ad-hoc widths and no scale.
// 760 is the phone breakpoint: the inbox shows one pane at a time below it.
// 1024 is tablets (designer, 14 October 2026): below it, a tablet in portrait.
const BREAKPOINTS = [760, 1024, 1180, 1280, 1360, 1520];

// Values that carry no design decision, so tokenising them would be noise.
const NEUTRAL = new Set([
  "0", "1", "100%", "auto", "none", "normal", "inherit", "initial", "unset",
  "transparent", "currentcolor", "0%", "50%", "1turn", "infinite",
  // The standard reduced-motion idiom. Not a duration anyone designed: it is
  // "effectively zero, but not zero", because a real 0s cancels transitionend
  // events some components rely on.
  "0.01ms !important"
]);

const COLOUR = /#[0-9a-fA-F]{3,8}\b|\brgba?\s*\(|\bhsla?\s*\(/;
const LENGTH = /(?<![\w-])\d*\.?\d+(px|rem|em|ms|s)(?![\w-])/;

const problems = [];
const report = (file, line, message) =>
  problems.push(`${rel(file)}:${line}  ${message}`);

const files = SCANNED.flatMap((root) => walk(root));
const cssFiles = files.filter((f) => f.endsWith(".css") && !GENERATED.has(f));
const razorFiles = files.filter((f) => f.endsWith(".razor"));

// Blank out comments while keeping line numbers, so a comment that mentions an
// old hex (they are quoted all over this codebase to explain what changed)
// cannot trip the linter.
function blankComments(text) {
  return text.replace(/\/\*[\s\S]*?\*\//g, (match) =>
    match.replace(/[^\n]/g, " ")
  );
}

function isBrandLiteral(value) {
  return BRAND_LITERALS.some((literal) => value.toLowerCase().includes(literal));
}

// ── Scoped stylesheets may not target the document ──────────────────────────
// Blazor rewrites a scoped selector by appending the component's scope
// attribute, so ":root" in a .razor.css compiles to ":root[b-hash]". <html>
// carries no component's attribute, so the block matches nothing and every
// custom property in it is silently undefined — which is how a dialog's icon
// loses its size and a field loses its height while the build stays green.
//
// This cost an afternoon before it was understood. The rule exists so it
// cannot cost anyone one again.
for (const file of cssFiles.filter((f) => f.endsWith(".razor.css"))) {
  const source = blankComments(fs.readFileSync(file, "utf8"));
  source.split("\n").forEach((line, index) => {
    const match = line.match(/^\s*(:root|html|body)\b[^{]*\{/);
    if (match) {
      report(file, index + 1,
        `${match[1]} cannot be styled from a scoped stylesheet — it would compile to ` +
        `${match[1]}[b-hash] and match nothing. Put component locals on the component's ` +
        `own root element, or document-wide rules in wwwroot/css/app.css.`);
    }
  });
}

// ── Type comes from a text style, not from a component stylesheet ───────────
// A component that sets its own font-size and font-weight has rebuilt a text
// style by hand. It will look right on the day it is written and drift from
// Figma from then on, because nothing connects it back to the scale.
//
// This is how the React app ended up with 13px/510, 17px/650 and 10px/650 in
// its navigation rail — three sizes and three weights that appear nowhere in
// the 41 styles the design system defines.
//
// The escape hatch is a comment on the same line saying why, which is needed
// about once: the floating label on Input animates BETWEEN two steps of the
// scale, and a class swap cannot be transitioned.
const TYPE_PROPERTIES = /^\s*(font-size|font-weight|letter-spacing|font-family)\s*:/;

for (const file of cssFiles.filter((f) => f.endsWith(".razor.css"))) {
  const raw = fs.readFileSync(file, "utf8");
  raw.split("\n").forEach((line, index) => {
    if (!TYPE_PROPERTIES.test(line)) return;
    if (line.includes("text-style-exempt")) return;
    // font: inherit and font-weight: inherit are resets, not type decisions.
    if (/:\s*inherit\s*;/.test(line)) return;

    const property = line.trim().split(":")[0];
    report(file, index + 1,
      `${property} belongs to a text style, not to a component. Name one — ` +
      `<Typography TextStyle="..."> or TextStyle.X.ToClassName() — or mark the ` +
      `line "text-style-exempt: <reason>" if it genuinely has to animate.`);
  });
}

// ── CSS ─────────────────────────────────────────────────────────────────────
for (const file of cssFiles) {
  const raw = fs.readFileSync(file, "utf8");
  const source = blankComments(raw);
  const rawLines = raw.split("\n");
  const lines = source.split("\n");

  let depth = 0;
  // Whether the block we are inside has explained itself yet.
  const blockHasComment = [];

  lines.forEach((line, index) => {
    const lineNo = index + 1;
    const rawLine = rawLines[index];

    // Track block nesting so a --ds-* local can be tied to the comment above it.
    const opens = (line.match(/\{/g) ?? []).length;
    const closes = (line.match(/\}/g) ?? []).length;

    if (/\/\*/.test(rawLine)) blockHasComment[depth] = true;

    // Breakpoints.
    for (const media of line.matchAll(/@media[^{]*?\(\s*(?:min|max)-width\s*:\s*(\d+)px/g)) {
      const width = Number(media[1]);
      if (!BREAKPOINTS.includes(width)) {
        report(file, lineNo,
          `breakpoint ${width}px is not on the scale (${BREAKPOINTS.join(", ")})`);
      }
    }

    // Every declaration on the line, not just one at the start of it. A rule
    // written on a single line — `.x { background: #abc; }` — put its
    // declaration after the brace, so an anchored match never saw it and every
    // one-line rule in the codebase was unchecked.
    for (const declaration of line.matchAll(/(?:^|[{;])\s*(--[\w-]+|[a-z-]+)\s*:\s*([^;}]+)/g)) {
      const [, property, value] = declaration;
      const isLocal = property.startsWith("--ds-");
      const isCustomProperty = property.startsWith("--");

      // A --ds-* local only has to explain itself when it holds a RAW value.
      // One that resolves to var(--token) is plumbing — typography.css uses
      // them to vary a weight inside a grouped rule — and there is no gap to
      // document. The comment requirement exists to make gaps visible, not to
      // tax every local.
      const isGapMarker = isLocal && !value.includes("var(") &&
        (COLOUR.test(value) || LENGTH.test(value));

      if (isGapMarker && !blockHasComment[depth]) {
        report(file, lineNo,
          `${property} holds a raw value with no comment naming the missing token`);
      }

      if (COLOUR.test(value) && !isBrandLiteral(value)) {
        report(file, lineNo, `hardcoded colour in ${property}: ${value.trim()}`);
      }

      // Lengths are only acceptable inside a --ds-* local, which is the
      // sanctioned place to record a gap in the token set.
      if (!isLocal && LENGTH.test(value) && !NEUTRAL.has(value.trim().toLowerCase())) {
        // calc(var(--token) * 1px) is tokenised — the number is a unit
        // conversion, not a design value.
        const withoutCalc = value.replace(/calc\([^)]*var\(--[^)]+\)[^)]*\)/g, "");
        if (LENGTH.test(withoutCalc)) {
          report(file, lineNo, `hardcoded length in ${property}: ${value.trim()}`);
        }
      }

      if (isCustomProperty && !isLocal && !value.includes("var(")) {
        // A component-local that is not named --ds-* sidesteps the rules above.
        if (COLOUR.test(value) || LENGTH.test(value)) {
          report(file, lineNo,
            `${property} holds a raw value — component locals must be named --ds-*`);
        }
      }
    }

    for (let i = 0; i < opens; i++) {
      depth++;
      blockHasComment[depth] = /\/\*/.test(rawLine);
    }
    for (let i = 0; i < closes; i++) {
      blockHasComment[depth] = false;
      depth = Math.max(0, depth - 1);
    }
  });
}

// ── Razor markup ────────────────────────────────────────────────────────────
// Inline styles are how the rot gets in the back door: the React linter could
// not see them, and ThemePreviewSwitch.tsx alone carried nine raw values.
for (const file of razorFiles) {
  const source = blankComments(fs.readFileSync(file, "utf8"));
  source.split("\n").forEach((line, index) => {
    const lineNo = index + 1;

    for (const match of line.matchAll(/style\s*=\s*"([^"]*)"/g)) {
      const value = match[1];
      // A Razor expression is computed per instance — a width bound to a
      // parameter is layout, not a design value nobody reviewed.
      if (value.includes("@")) continue;
      if (COLOUR.test(value) || LENGTH.test(value)) {
        report(file, lineNo, `literal inline style: ${value}`);
      }
    }

    // A colour in markup is always wrong, expression or not.
    const outsideStyle = line.replace(/style\s*=\s*"[^"]*"/g, "");
    const colour = outsideStyle.match(/#[0-9a-fA-F]{6}\b/);
    if (colour && !isBrandLiteral(colour[0])) {
      report(file, lineNo, `hardcoded colour in markup: ${colour[0]}`);
    }
  });
}

// ── The font family the generator emits ─────────────────────────────────────
// Rename the family and every --typography-font-family-* token falls through
// to system-ui with no error anywhere. This is the only check for it.
const tokensCss = fs.readFileSync(TOKENS_CSS, "utf8");
const fontsCss = fs.readFileSync(FONTS_CSS, "utf8");

// Semantic families point at a primitive (var(--p-font-sans)), so the names
// themselves live on the primitives. Read both, so a literal family still counts.
const families = [...tokensCss.matchAll(/--(?:typography-font-family|p-font)-[\w-]+:\s*"([^"]+)"/g)]
  .map((m) => m[1]);
if (!families.length) {
  problems.push(`${rel(TOKENS_CSS)}  names no font family at all — the font check has nothing to verify`);
}

for (const family of new Set(families)) {
  // Geist Mono is named by the tokens but has never been installed, so the
  // code styles fall back to ui-monospace. Faithful to React; on the fix list.
  if (family === "Geist Mono") continue;
  const declared = new RegExp(`font-family:\\s*['"]${family}['"]`).test(fontsCss);
  if (!declared) {
    problems.push(
      `${rel(FONTS_CSS)}  does not declare the family "${family}" that tokens.css names — ` +
      `every type token would silently fall back to system-ui`
    );
  }
}

// ── Every url() has to point at a file that exists ──────────────────────────
// A relative url() resolves against the STYLESHEET, not the page. fonts.css
// lives in wwwroot/css/, so "fonts/geist.woff2" asked for wwwroot/css/fonts/
// and 404'd — and a missing @font-face src does not raise an error, it just
// falls back to the next family. The whole app rendered in system-ui for days
// while every check here passed.
for (const file of cssFiles) {
  const source = blankComments(fs.readFileSync(file, "utf8"));
  source.split("\n").forEach((line, index) => {
    for (const use of line.matchAll(/url\(\s*['"]?([^'")]+)['"]?\s*\)/g)) {
      const reference = use[1].trim();
      // Data URIs and absolute URLs are not ours to resolve.
      if (/^(data:|https?:|\/\/)/.test(reference)) continue;

      const target = reference.startsWith("/")
        ? path.join(projectOf(file), "wwwroot", reference.slice(1))
        : path.resolve(path.dirname(file), reference);

      if (!fs.existsSync(target)) {
        report(file, index + 1,
          `url(${reference}) does not resolve to a file. A relative url() is ` +
          `resolved against this stylesheet's folder, not the page, and a missing ` +
          `font or image fails silently.`);
      }
    }
  });
}

// ── Baseline ────────────────────────────────────────────────────────────────
// This repo had pages before it had the linter, so it starts with known debt.
// lint-baseline.json records it. Known problems are reported, never fatal;
// anything NOT in the baseline fails the build. The design system cannot get
// worse, and the debt only shrinks.
//
// Keyed on file + message, not line number, so an edit above a known problem
// does not turn it into a "new" one.
//
//   node design-system/tools/lint-design-system.mjs --update-baseline
//
// Run that after FIXING debt, to lock the lower count in. Never run it to make
// a new problem go away: fix the value, or declare a commented --ds-* local.
const BASELINE = path.join(DESIGN_SYSTEM, "tools/lint-baseline.json");
const keyOf = (problem) => problem.replace(/:\d+(\s{2})/, "$1");
const tally = (list) => list.reduce((m, p) => m.set(keyOf(p), (m.get(keyOf(p)) ?? 0) + 1), new Map());

if (process.argv.includes("--update-baseline")) {
  const sorted = Object.fromEntries([...tally(problems)].sort(([a], [b]) => a.localeCompare(b)));
  fs.writeFileSync(BASELINE, JSON.stringify(sorted, null, 2) + "\n");
  console.log(`lint-design-system  baseline written: ${problems.length} known problem(s)`);
  process.exit(0);
}

const baseline = fs.existsSync(BASELINE) ? JSON.parse(fs.readFileSync(BASELINE, "utf8")) : {};
const allowance = new Map(Object.entries(baseline));
const fresh = [];
for (const problem of problems) {
  const key = keyOf(problem);
  const left = allowance.get(key) ?? 0;
  if (left > 0) allowance.set(key, left - 1);
  else fresh.push(problem);
}
const known = problems.length - fresh.length;
const paidDown = [...allowance.values()].reduce((a, b) => a + b, 0);

// ── Result ──────────────────────────────────────────────────────────────────
if (fresh.length) {
  console.error(`lint-design-system: ${fresh.length} new problem(s)\n`);
  for (const problem of fresh) console.error("  " + problem);
  console.error(
    "\nEvery colour, space, radius and duration comes from a token. If one is " +
    "missing, declare a --ds-* local with a comment naming the gap."
  );
  process.exit(1);
}

console.log(
  `lint-design-system  ${cssFiles.length} stylesheets, ${razorFiles.length} components, 0 new hardcoded values` +
  (known ? ` (${known} known, in lint-baseline.json)` : "") +
  (paidDown ? ` — ${paidDown} fixed since the baseline; run --update-baseline to lock that in` : "")
);
