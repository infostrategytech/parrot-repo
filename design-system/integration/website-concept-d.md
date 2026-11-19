# Concept D website handoff

Frontend-only update based on main `b3843378` (19 November 2026), retaining the team's merged Knowledge integration.

## Included

- The approved Concept D presentation at `/`, `/register` and `/login`.
- Product film, business-knowledge teaching illustration, work examples, human control, setup, FAQ, closing and footer.
- Public About, Contact, Privacy, Terms and Data deletion routes.
- Search/share metadata, public sitemap, responsive public-page shell and narrow navigation spacing.
- Progressive confirm-password/strength presentation; existing validation, registration/login clients and real Google/Facebook SSO endpoint routing remain.
- Opt-in shared avatar accessories/greeting/pointer control and the existing note glyph required by the film. Defaults preserve existing avatar consumers.
- Controlled component/motion checks and local production-page checks.

The reset control, reset service, demo providers, fake credentials/data, alternative landing concepts and unrelated Inbox/Tasks/mobile changes are excluded. Product-story examples are fictional marketing illustrations, not live workspace records or executable app controls. The production layout does not reference the local reset component or fake sign-in support.

## Existing API behaviour

No backend, database, auth-client contract, API payload or endpoint change. Homepage opens the existing sign-up UI; `/login` still signs in. Main's checkout/payment public-route guards are retained. Real signup, verification, SSO and connected-channel behaviour must still be checked in staging against the actual API. Public illustrations are not evidence of backend capability.

## Verification

Release build and local production packaging pass. Controlled checks cover shared selectors, human-control examples, avatar defaults, motion lifecycle, film sequencing and sound controls. HTTP checks run against the packaged Production host, without DemoMode or fake providers; they verify public routes, titles, canonical/share metadata, one main heading, section anchors, static assets/sprite glyphs, compression and reset-button absence. Figma token and lint checks pass without regenerating owned values or changing the baseline.

Example commands (from the branch root):

```text
dotnet build Parrot.Web/Parrot.Web/Parrot.Web.csproj -c Release --artifacts-path artifacts/website-build -p:DirectoryBuildTargetsPath=<absolute-path>/tools/ReadOnlyDesignTokens.targets
dotnet run --project tools/WebLandingFeatureChecks -p:ParrotWebBin=<absolute-artifacts-path>/website-build/bin/Parrot.Web/release/
node tools/web-avatar-teaching-scene-check.mjs
node tools/web-landing-product-film-check.mjs
node tools/web-landing-launch-check.mjs
npm run check --prefix design-system/tools
```

The HTTP guard defaults to localhost:5369; WEB_PREVIEW_URL can select its other explicitly allowed local ports. Supply normal local API configuration when running the host; never commit environment settings or credentials. These checks do not submit real account creation, change an account or send a customer message.

## Before public release

- Replace the deliberately retained `+234 800 000 0000` placeholder with the confirmed public phone number. Support inbox is confirmed as `support@yarahq.com`.
- Obtain approved Privacy/Terms/Data deletion content. Current bodies are starter copy, not legal sign-off.
- Complete real-API signup/email verification/SSO and advertised channel-capability checks in staging.
- Finish the latest phone-width, keyboard/accessibility and real-device/performance review. Existing unrelated Razor/unused-field warnings are not addressed here.
- App-store links remain unpublished; the website explicitly says the mobile app is coming soon.

No deployment or merge is approved by a local build or by pushing this review branch. Sitemap/robots directives are crawl guidance, not access control.
