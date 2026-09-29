// Where everything the design-system tooling reads and writes lives. Every
// script imports this, so a project that moves is fixed here, once.
//
// Anchored on this file rather than on the working directory: MSBuild runs
// the scripts from a different cwd than npm does, and path.resolve() would
// quietly read the wrong folder in one of them.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const REPO = fileURLToPath(new URL("../../", import.meta.url));
export const DESIGN_SYSTEM = path.join(REPO, "design-system");

// The Figma export: one Primitives collection and the Semantic collection in
// Light and Dark. The single source of truth for colour, spacing, radius,
// icon size and type.
export const VARIABLES = path.join(DESIGN_SYSTEM, "variables");
export const FIGMA_SNAPSHOTS = path.join(DESIGN_SYSTEM, "figma-snapshots");

// The shared Razor library both apps consume: tokens, text styles, components.
export const SHARED = path.join(REPO, "Parrot.SharedCompnents");
export const TOKENS_CSS = path.join(SHARED, "wwwroot/css/tokens.css");
export const TYPOGRAPHY_CSS = path.join(SHARED, "wwwroot/css/typography.css");
export const FONTS_CSS = path.join(SHARED, "wwwroot/css/fonts.css");
export const TEXT_STYLE_CS = path.join(SHARED, "DesignSystem/TextStyle.g.cs");
export const TEXT_STYLE_NAMESPACE = "Parrot.SharedComponents.DesignSystem";

// Stylesheets that sit on :root. Anything they declare is available to every
// stylesheet, because custom properties inherit.
export const GLOBAL_CSS = [
  path.join(SHARED, "wwwroot/css/app.css"),
  path.join(SHARED, "wwwroot/css/cadence.css"),
  path.join(REPO, "Parrot.Web/Parrot.Web/wwwroot/app.css"),
];

// Everything the guardrails check: the design system, and the web app built on
// it. Each entry is a project folder; its wwwroot/ is where a root-relative
// url() resolves.
export const SCANNED = [
  SHARED,
  path.join(REPO, "Parrot.Web/Parrot.Web"),
  path.join(REPO, "Parrot.Web/Parrot.Web.Client"),
];

// Never read: build output, packages, and vendored third-party files.
export const SKIP_DIRS = new Set(["bin", "obj", "node_modules", "lib"]);

export function walk(dir, out = []) {
  if (!fs.existsSync(dir)) return out;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (SKIP_DIRS.has(entry.name)) continue;
    const p = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(p, out);
    else out.push(p);
  }
  return out;
}

// The project folder a file belongs to, for resolving "/x" urls.
export const projectOf = (file) => SCANNED.find((root) => file.startsWith(root + path.sep)) ?? SHARED;

export const rel = (file) => path.relative(REPO, file).replaceAll("\\", "/");
