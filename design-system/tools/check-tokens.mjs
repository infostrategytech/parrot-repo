// Fails the build if a var(--something) names a custom property that nothing
// declares.
//
//   node design-system/tools/check-tokens.mjs
//
// This is the other half of lint-design-system.mjs. That one catches a value
// typed in by hand; this catches the opposite mistake — a token reference that
// looks right and resolves to nothing. CSS has no error for that: the property
// simply does not apply, and the component renders with a browser default that
// can look almost correct. A misspelt --colors-text-primry is invisible until
// someone notices the text is black instead of near-black.
import fs from "node:fs";
import path from "node:path";
import { DESIGN_SYSTEM, SHARED, TOKENS_CSS as TOKENS, GLOBAL_CSS, SCANNED, walk, rel } from "./paths.mjs";

if (!fs.existsSync(TOKENS)) {
  console.error("check-tokens: tokens.css is missing — run build-design-tokens.mjs first");
  process.exit(1);
}

const stripComments = (text) => text.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, " "));

// Everything the global stylesheets declare is available everywhere,
// because both sit on :root and custom properties inherit. tokens.css carries
// the design system; app.css carries the handful of locals Figma has no scale
// for yet, and a scoped stylesheet is entitled to use those.
const tokenNames = new Set(
  [TOKENS, ...GLOBAL_CSS]
    .filter((file) => fs.existsSync(file))
    .flatMap((file) => [...stripComments(fs.readFileSync(file, "utf8")).matchAll(/^\s*(--[\w-]+)\s*:/gm)])
    .map((match) => match[1])
);

// Blazor's scoped CSS rewrites selectors but never touches custom properties,
// so a local declared in one component's stylesheet does reach its children.
// Collect them per file and treat them as available to that file.
const problems = [];
const files = SCANNED.flatMap((root) => walk(root)).filter((f) => f.endsWith(".css") && f !== TOKENS);

for (const file of files) {
  const source = stripComments(fs.readFileSync(file, "utf8"));
  // A declaration can start at the beginning of a line or straight after a
  // { or ; — typography.css sets its weight local inline, on one line.
  const locals = new Set(
    [...source.matchAll(/(?:^|[{;])\s*(--[\w-]+)\s*:/gm)].map((m) => m[1])
  );
  // A component may also set a local per instance from its markup --
  // style="--ds-donut-value: @share.Percent" -- which its scoped stylesheet
  // then reads. Those count as declared for that stylesheet.
  const markup = file.endsWith(".razor.css") ? file.slice(0, -".css".length) : null;
  if (markup && fs.existsSync(markup)) {
    for (const style of fs.readFileSync(markup, "utf8").matchAll(/style\s*=\s*"([^"]*)"/g)) {
      for (const m of style[1].matchAll(/(--[\w-]+)\s*:/g)) locals.add(m[1]);
    }
  }

  source.split("\n").forEach((line, index) => {
    // var(--x, fallback) is safe by construction: the author has said what
    // should happen when it is absent. Only a bare reference has to resolve.
    for (const use of line.matchAll(/var\(\s*(--[\w-]+)\s*([,)])/g)) {
      const name = use[1];
      if (use[2] === "," || tokenNames.has(name) || locals.has(name)) continue;
      problems.push(`${rel(file)}:${index + 1}  ${name} is not declared anywhere`);
    }
  });
}

// ── Contracts ───────────────────────────────────────────────────────────────
// A handful of pairings that are correct by design rather than by accident,
// and would be easy to "tidy" into something that looks fine and reads wrong.
// Kept as data so a designer can amend them; React hardcoded eight of these
// into the script itself, four of which pointed at a generated file.
const contractsPath = path.join(DESIGN_SYSTEM, "tools/contracts.json");
if (fs.existsSync(contractsPath)) {
  const contracts = JSON.parse(fs.readFileSync(contractsPath, "utf8"));

  for (const contract of contracts) {
    const file = path.join(SHARED, contract.file);
    if (!fs.existsSync(file)) {
      problems.push(`contracts.json names ${contract.file}, which does not exist`);
      continue;
    }

    const source = stripComments(fs.readFileSync(file, "utf8"));
    // Everything between the selector and the end of its block.
    const block = source.match(
      new RegExp(contract.selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\s*\\{([^}]*)\\}")
    );

    if (!block) {
      problems.push(`${contract.file}  has no rule for ${contract.selector}`);
      continue;
    }

    for (const [property, token] of Object.entries(contract.require)) {
      const declared = new RegExp(`${property}\\s*:[^;]*var\\(\\s*${token}\\b`).test(block[1]);
      if (!declared) {
        problems.push(
          `${contract.file}  ${contract.selector} must set ${property} from var(${token})` +
          (contract.why ? ` — ${contract.why}` : "")
        );
      }
    }
  }
}

if (problems.length) {
  console.error(`check-tokens: ${problems.length} problem(s)\n`);
  for (const problem of problems) console.error("  " + problem);
  process.exit(1);
}

console.log(`check-tokens  ${tokenNames.size} tokens, ${files.length} stylesheets, every var() resolves`);
