// Generates Parrot.SharedCompnents/wwwroot/css/tokens.css from the Figma
// variable export in design-system/variables. This is the single source of
// truth for colour, spacing, radius, icon size and type.
//
//   node design-system/tools/build-design-tokens.mjs [--out <path>]
//
// The export is three files: one Primitives collection (mode-independent) and
// the Semantic collection in Light and Dark. Semantic leaf names already match
// what Figma Dev Mode shows, so a variable name copied from Figma maps to a
// CSS custom property by replacing "/" with "-" and nothing else:
//
//   colors/text/primary  ->  --colors-text-primary
//   icon size/icon-sm    ->  --icon-size-icon-sm
//
// Light tokens sit on :root so components resolve without a theme attribute;
// dark overrides them under [data-theme="aurora-dark"].
//
// A semantic token that Figma aliases to a primitive is emitted as a
// reference to it, so the link survives into CSS. See primitiveRef below.
import fs from "node:fs";
import path from "node:path";
import { REPO, VARIABLES, TOKENS_CSS, rel } from "./paths.mjs";

const outFlag = process.argv.indexOf("--out");
const OUT = outFlag !== -1 && process.argv[outFlag + 1] ? path.resolve(REPO, process.argv[outFlag + 1]) : TOKENS_CSS;

const load = (p) => JSON.parse(fs.readFileSync(path.join(VARIABLES, p), "utf8"));

const primitives = load("Primitives/Default.tokens.json");
const semanticLight = load("Semantic/Light Mode.tokens.json");
const semanticDark = load("Semantic/Dark Mode.tokens.json");

function flatten(node, prefix, out) {
  for (const [key, value] of Object.entries(node)) {
    if (!value || typeof value !== "object" || key.startsWith("$")) continue;
    const p = prefix ? `${prefix}.${key}` : key;
    if ("$value" in value) out[p] = { value: value.$value, type: value.$type, alias: value.$extensions?.["com.figma.aliasData"] };
    else flatten(value, p, out);
  }
  return out;
}

// "colors.text.primary"  -> "--colors-text-primary"
// "icon size.icon-sm"    -> "--icon-size-icon-sm"
// Primitives are namespaced so they can't collide with a semantic token.
const cssName = (token, isPrimitive) =>
  "--" +
  (isPrimitive ? `p.${token}` : token)
    .toLowerCase()
    .replace(/\s+-\s+(\d+)/g, "-a$1") // a trailing " - 72" means an alpha variant
    .replace(/[.\s]+/g, "-")
    .replace(/[^a-z0-9-]/g, "");

// Figma exports colour as { colorSpace, components: [r,g,b], alpha, hex }.
// hex drops alpha, so read components.
function toCss(value) {
  if (!value || typeof value !== "object" || !Array.isArray(value.components)) return null;
  const [r, g, b] = value.components.map((c) => Math.round(c * 255));
  const a = value.alpha ?? 1;
  return a >= 1
    ? `#${[r, g, b].map((v) => v.toString(16).padStart(2, "0")).join("")}`
    : `rgb(${r} ${g} ${b} / ${+a.toFixed(4)})`;
}

// Everything on a pixel scale gets px. Weights, opacities and the raw
// primitive number ramp stay unitless.
const DIMENSIONAL = /^(radius|spacing|icon size)\.|^typography\.(size|leading)\./;

// Figma calls the family "Geist" while @fontsource-variable registers the
// bundled variable face as "Geist Variable". Keep the design variable as the
// source of truth and translate only at the CSS/runtime boundary.
const RUNTIME_FONT_FAMILY = new Map([["Geist", "Geist Variable"]]);

// A semantic token that Figma aliases to a primitive is written as a
// reference to it rather than as its value, so a primitive change carries
// through to every semantic token bound to it, in both modes, without the
// semantic export having to change. One rule for every type: the primitive
// holds the raw value, the semantic token points at it and adds whatever unit
// its role needs.
//
//   colour         --colors-brand-primary: var(--p-color-primary-500)
//   dimension      --spacing-md: calc(var(--p-numbers-12) * 1px)
//   weight         --typography-font-weight-medium: var(--p-weight-medium)
//   family         --typography-font-family-base: var(--p-font-sans)
//
// Number primitives stay unitless because components already use them that
// way (calc(var(--p-numbers-14) * 1px)); the unit belongs to the role. The
// export's own resolved value is kept for anything that is not an alias.
const primitiveFlat = flatten(primitives, "", {});
const aliasWarnings = [];

function primitiveRef(token, alias, value, type) {
  if (alias?.targetVariableSetName !== "Primitives" || !alias.targetVariableName) return null;
  const target = alias.targetVariableName.split("/").join(".");
  const prim = primitiveFlat[target];
  if (!prim || prim.type !== type) {
    aliasWarnings.push(`${token} -> ${alias.targetVariableName}: no ${type} primitive by that name, kept the literal`);
    return null;
  }
  const ref = `var(${cssName(target, true)})`;

  if (type === "color") {
    if (toCss(prim.value) == null) return null;
    // An alias carries its target's alpha. If the two disagree the export is
    // inconsistent, and the literal is the safer thing to ship.
    if ((prim.value.alpha ?? 1) !== (value?.alpha ?? 1)) {
      aliasWarnings.push(`${token} -> ${alias.targetVariableName}: alpha differs from the primitive, kept the literal`);
      return null;
    }
    return ref;
  }
  if (typeof prim.value === "number") return DIMENSIONAL.test(token) ? `calc(${ref} * 1px)` : ref;
  if (typeof prim.value === "string") return ref;
  return null;
}

// A semantic token can also alias another SEMANTIC token rather than a
// primitive — colors/text/on-secondary points at colors/brand/secondary-
// foreground. Figma exports that as the reference string "{colors.brand.
// secondary-foreground}" instead of a resolved value, so it never reaches
// toCss() and the token used to be dropped without a word.
//
// Point at the other custom property rather than inlining its value, for the
// same reason primitive aliases do: the relationship is the design decision,
// and it keeps working when the target changes. Forward references are fine —
// custom properties resolve where they are used, not where they are declared.
function semanticRef(token, value, flat) {
  if (typeof value !== "string" || !value.startsWith("{") || !value.endsWith("}")) return null;
  const target = value.slice(1, -1);
  if (!(target in flat)) {
    aliasWarnings.push(`${token} -> ${target}: no semantic token by that name, dropped`);
    return null;
  }
  return `var(${cssName(target, false)})`;
}

function emit(source, isPrimitive) {
  const flat = flatten(source, "", {});
  const lines = [];
  const seen = new Set();
  const dropped = [];

  for (const [token, { value, type, alias }] of Object.entries(flat)) {
    let css = isPrimitive ? null : primitiveRef(token, alias, value, type) ?? semanticRef(token, value, flat);
    if (css != null) {
      // referenced, nothing more to do
    } else if (type === "color") css = toCss(value);
    else if (typeof value === "number") css = DIMENSIONAL.test(token) ? `${value}px` : `${value}`;
    else if (typeof value === "string") {
      const font = RUNTIME_FONT_FAMILY.get(value) ?? value;
      // ^font\. is the primitive family ramp (font/sans). It needs the same
      // quoting and runtime name as the semantic family that points at it.
      css = /font-family|\.font\.|^font\./i.test(token) ? `"${font}"` : value;
    }
    // A token Figma defines but we cannot express is a hole in the system that
    // nothing else would catch: the CSS stays valid, check-tokens still passes
    // because no component references the missing name yet, and the first
    // symptom is a var() that resolves to nothing months later. Collect them
    // and fail the build instead.
    if (css == null) {
      dropped.push(`${token} (${type ?? "unknown type"}): ${JSON.stringify(value)}`);
      continue;
    }

    const name = cssName(token, isPrimitive);
    if (seen.has(name)) continue;
    seen.add(name);
    lines.push(`  ${name}: ${css};`);
  }

  if (dropped.length) {
    console.error(`build-design-tokens  ${dropped.length} token(s) in the export could not be emitted:\n`);
    for (const d of dropped) console.error(`  ${d}`);
    console.error("\nEvery token Figma exports must land in tokens.css. Fix the export or teach");
    console.error("the generator this value shape — do not ship a silently incomplete token set.");
    process.exit(1);
  }

  return lines;
}

const primitiveLines = emit(primitives, true);
const lightLines = emit(semanticLight, false);
const darkLines = emit(semanticDark, false);

fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(
  OUT,
  `/* Generated by design-system/tools/build-design-tokens.mjs — do not edit by hand.
   Source: design-system/variables (Figma export: Primitives + Semantic Light/Dark).

   A Figma variable name maps to a CSS custom property by replacing "/" with
   "-": colors/text/primary is --colors-text-primary. Primitives are prefixed
   --p- and are the same in both modes, so never style with them directly —
   a primitive cannot respond to dark mode.

   A semantic token that Figma aliases to a primitive points at it --
   var(--p-...), or calc(var(--p-numbers-...) * 1px) for sizes -- so changing
   the primitive changes every semantic token bound to it, in both modes. */

:root {
  color-scheme: light;

  /* Primitives — the raw ramps. Identical in both modes by design. */
${primitiveLines.join("\n")}

  /* Semantic — light */
${lightLines.join("\n")}
}

:root[data-theme="aurora-dark"] {
  color-scheme: dark;

${darkLines.join("\n")}
}
`
);

for (const w of aliasWarnings) console.warn(`build-design-tokens  warning: ${w}`);

console.log(
  `tokens.css  ${primitiveLines.length} primitives, ${lightLines.length} semantic light, ${darkLines.length} semantic dark  ->  ${rel(OUT)}`
);
