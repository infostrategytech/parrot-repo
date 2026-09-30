// Compares snapshots of what Figma Dev Mode actually reports against the
// tokens.css generated from the export, and reports any variable whose VALUE
// disagrees.
//
// check-tokens.mjs catches names that don't exist. This catches something
// worse: a name that resolves fine but to the wrong colour, because the export
// has drifted from the live Figma file. Nothing else in the build would
// notice — the CSS is perfectly valid, just wrong.
//
//   node design-system/tools/verify-figma-tokens.mjs              # every snapshot
//   node design-system/tools/verify-figma-tokens.mjs path.json    # just one
//
// To refresh or add one: select the frame in Figma, call the MCP tool
// get_variable_defs on it, and save the JSON under design-system/figma-snapshots/
// with $mode set to the mode the frame was displaying.
import fs from "node:fs";
import path from "node:path";
import { FIGMA_SNAPSHOTS, TOKENS_CSS } from "./paths.mjs";

const SNAPSHOT_DIR = FIGMA_SNAPSHOTS;
const TOKENS = TOKENS_CSS;

const snapshots = process.argv[2]
  ? [path.resolve(process.argv[2])]
  : fs.existsSync(SNAPSHOT_DIR)
    ? fs.readdirSync(SNAPSHOT_DIR).filter((f) => f.endsWith(".json")).map((f) => path.join(SNAPSHOT_DIR, f))
    : [];

if (!snapshots.length) {
  console.log("verify-figma-tokens  no snapshots found — skipping");
  process.exit(0);
}

const css = fs.readFileSync(TOKENS, "utf8");
const darkStart = css.indexOf('[data-theme="aurora-dark"]');

function declarationsFor(mode) {
  const block = mode === "dark" ? css.slice(darkStart) : css.slice(0, darkStart);
  const map = new Map(
    [...block.matchAll(/^\s*(--[a-z0-9-]+)\s*:\s*([^;]+);/gim)].map((m) => [m[1], m[2].trim()])
  );
  // Primitives live in the :root block only — they are mode-independent — so a
  // dark-mode check still has to be able to find them.
  for (const m of css.slice(0, darkStart).matchAll(/^\s*(--p-[a-z0-9-]+)\s*:\s*([^;]+);/gim)) {
    if (!map.has(m[1])) map.set(m[1], m[2].trim());
  }
  // A semantic token aliased to a primitive is declared as var(--p-...), or
  // calc(var(--p-numbers-...) * 1px) for sizes, so follow the reference to the
  // value Figma would report.
  for (const [name, value] of map) {
    let v = value;
    let unit = "";
    for (let hops = 0; hops < 5; hops++) {
      const sized = /^calc\(var\((--[a-z0-9-]+)\)\s*\*\s*1px\)$/i.exec(v);
      if (sized) unit = "px";
      const ref = sized ?? /^var\((--[a-z0-9-]+)\)$/i.exec(v);
      if (!ref || !map.has(ref[1])) break;
      v = map.get(ref[1]);
    }
    map.set(name, v + unit);
  }
  return map;
}

// Both sides reduce to #rrggbbaa so an 8-digit Figma hex and our
// rgb(r g b / a) compare equal instead of looking like a mismatch.
function norm(value) {
  const s = String(value).trim().toLowerCase();

  let m = /^#([0-9a-f]{3,8})$/.exec(s);
  if (m) {
    let h = m[1];
    if (h.length === 3 || h.length === 4) h = [...h].map((c) => c + c).join("");
    if (h.length === 6) h += "ff";
    return `#${h}`;
  }

  m = /^rgba?\(([^)]+)\)$/.exec(s);
  if (m) {
    // Modern syntax is space-separated with a slash before alpha
    // (rgb(255 255 255 / 0.85)); legacy is comma-separated. Split on all three.
    const parts = m[1].split(/[\s,/]+/).map((x) => x.trim()).filter(Boolean);
    const [r, g, b] = parts.slice(0, 3).map((v) => Math.round(parseFloat(v)));
    const a = parts[3] === undefined ? 1 : parseFloat(parts[3]);
    const hex = [r, g, b, Math.round(a * 255)].map((v) => v.toString(16).padStart(2, "0")).join("");
    return `#${hex}`;
  }

  const plain = s.replace(/px$/, "").replace(/^"|"$/g, "");
  // Runtime name registered by @fontsource-variable for Figma's Geist family.
  return plain === "geist variable" ? "geist" : plain;
}

let totalMissing = 0;
let totalMismatched = 0;

for (const file of snapshots) {
  const snapshot = JSON.parse(fs.readFileSync(file, "utf8"));
  const mode = snapshot.$mode === "dark" ? "dark" : "light";
  const declared = declarationsFor(mode);

  const missing = [];
  const mismatched = [];
  let checked = 0;

  for (const [figmaName, figmaValue] of Object.entries(snapshot)) {
    if (figmaName.startsWith("$")) continue;
    // Text styles are Font(...) descriptions, not variables.
    if (String(figmaValue).startsWith("Font(")) continue;

    const plain = "--" + figmaName.toLowerCase().replace(/[/\s]+/g, "-");
    // Primitives are namespaced --p- in tokens.css.
    const cssVar = declared.has(plain) ? plain : `--p-${plain.slice(2)}`;
    checked++;

    if (!declared.has(cssVar)) {
      missing.push({ figmaName, figmaValue });
      continue;
    }
    const ours = declared.get(cssVar);
    if (norm(ours) !== norm(figmaValue)) mismatched.push({ figmaName, figma: figmaValue, ours });
  }

  console.log(
    `verify-figma-tokens  ${path.basename(file)} (${mode}, node ${snapshot.$node ?? "?"}): ` +
      `${checked - missing.length - mismatched.length}/${checked} matched` +
      (missing.length ? `, ${missing.length} missing` : "") +
      (mismatched.length ? `, ${mismatched.length} mismatched` : "")
  );

  for (const m of missing) console.log(`    missing     ${m.figmaName}  (Figma: ${m.figmaValue})`);
  for (const m of mismatched) console.log(`    mismatched  ${m.figmaName}  Figma: ${m.figma}  ours: ${m.ours}`);

  totalMissing += missing.length;
  totalMismatched += mismatched.length;
}

if (totalMissing || totalMismatched) {
  console.log("\nRe-export the variables from Figma into design-system/variables, then rebuild.");
  // Reporting only: a stale snapshot must never break the build.
}
