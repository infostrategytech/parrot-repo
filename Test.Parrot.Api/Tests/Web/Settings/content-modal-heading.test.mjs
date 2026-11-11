import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const css = readFileSync(new URL("../../../../Parrot.SharedCompnents/DesignSystem/ContentModal.razor.css", import.meta.url), "utf8");
const component = readFileSync(new URL("../../../../Parrot.SharedCompnents/DesignSystem/ContentModal.razor", import.meta.url), "utf8");
const invitation = readFileSync(new URL("../../../../Parrot.Web/Parrot.Web/Components/Pages/Dashboard/Team/InviteModal.razor", import.meta.url), "utf8");

test("the shared content-modal heading owns the tighter semantic-token gap by default", () => {
  const rule = css.match(/\.content-modal__heading\s*\{([^}]+)\}/)?.[1];
  assert.ok(rule, "The shared heading rule must exist.");
  assert.match(rule, /gap:\s*var\(--spacing-xs\)/);
  assert.equal([...css.matchAll(/\.content-modal__heading\s*\{/g)].length, 1);
});
test("there is no invitation-only compact API or CSS modifier", () => {
  for (const source of [css, component, invitation]) assert.doesNotMatch(source, /CompactHeading|compact-heading/);
});
test("the shared heading still resets default text margins", () => {
  assert.match(css, /\.content-modal__heading\s+::deep\s+h2,\s*\.content-modal__heading\s+::deep\s+p\s*\{\s*margin:\s*var\(--spacing-none\)/);
});
