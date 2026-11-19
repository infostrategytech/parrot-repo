// Local HTTP/source launch guard, not a real-API auth test or a visual/performance audit.
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {get} from 'node:http';

const base = process.env.WEB_PREVIEW_URL ?? 'http://localhost:5369';
assert.match(base, /^http:\/\/localhost:(5279|5283|5369)$/);
let checks = 0;
const check = (value, why) => { assert.ok(value, why); checks++; };
const decode = text => text.replace(/&#x([\da-f]+);/gi, (_, hex) => String.fromCodePoint(parseInt(hex, 16)))
  .replace(/&#(\d+);/g, (_, num) => String.fromCodePoint(Number(num))).replace(/&amp;/g, '&').replace(/&quot;/g, '"');
const load = async path => {
  const response = await fetch(base + path);
  check(response.status === 200, `${path} loads successfully.`);
  return decode(await response.text());
};
const meta = (html, key) => [...html.matchAll(/<meta\b[^>]*>/gi)].find(m => m[0].includes(`name="${key}"`) || m[0].includes(`property="${key}"`))?.[0].match(/content="([^"]*)"/)?.[1];
const canonical = html => html.match(/<link\b[^>]*rel="canonical"[^>]*href="([^"]*)"/)?.[1];
const title = html => html.match(/<title[^>]*>(.*?)<\/title>/s)?.[1];

for (const path of ['/', '/?concept=d', '/login', '/register', '/about', '/contact', '/privacy', '/terms', '/data-deletion']) {
  const html = await load(path);
  const route = new URL(base + path).pathname;
  check(meta(html, 'description')?.length > 40, `${path} has a useful search description.`);
  check(canonical(html) === 'https://yarahq.com' + route, `${path} canonical excludes local hosts and comparison queries.`);
  check(meta(html, 'og:url') === canonical(html), `${path} shares its canonical address.`);
  check(meta(html, 'og:title') === title(html), `${path} has consistent search/share titles.`);
  check(meta(html, 'og:image') === 'https://yarahq.com/brand/omniai-logo-512.png', `${path} uses the approved, existing brand image.`);
  check(meta(html, 'twitter:card') === 'summary', `${path} has a social card.`);
  check((html.match(/<h1\b/gi) ?? []).length === 1, `${path} has exactly one main heading.`);
  const indexable = path !== '/?concept=d' && !['/login', '/register'].includes(route);
  check(meta(html, 'robots') === (indexable ? 'index,follow' : 'noindex,follow'), `${path} indexes only the intended public pages.`);
  if (['/', '/login', '/register'].includes(path) || path === '/?concept=d') {
    for (const section of ['agent-hero', 'agent-introduction', 'business-knowledge', 'work-forward', 'human-control', 'landing-how', 'landing-faq', 'agent-closing']) check(html.includes(`class="${section}`), `${path} retains ${section}.`);
    check(html.includes('Mobile app coming soon') && !html.includes('>Download app</span>'), 'Unpublished apps do not offer a dead download button.');
    check(!html.includes('Orders, payments and questions that need a person become tasks'), 'Old question-task marketing copy is gone.');
    const ids = new Set([...html.matchAll(/\bid="([^"]+)"/gi)].map(m => m[1]));
    for (const [, href] of html.matchAll(/<a\b[^>]*\bhref="([^"]+)"/gi)) {
      const url = new URL(href, base + path);
      if (url.origin === new URL(base).origin && url.hash && ['/', '/login', '/register'].includes(url.pathname)) check(ids.has(decodeURIComponent(url.hash.slice(1))), `${href} points at a rendered section.`);
    }
  }
}
for (const concept of ['a', 'b', 'c']) {
  const html = await load(`/?concept=${concept}`);
  check(html.includes('is-concept-d'), `Old ${concept} comparison URLs cannot expose a draft website in production.`);
  check(meta(html, 'robots') === 'noindex,follow', 'Old comparison URLs do not become duplicate search results.');
}
const png = await fetch(base + '/brand/omniai-logo-512.png');
check(png.ok && png.headers.get('content-type')?.includes('image/png'), 'The social image is served as a real PNG.');
const home = await load('/');
const assets = new Set([...home.matchAll(/(?:href|src)="([^"]+\.(?:css|js)(?:\?[^"]*)?)"/g)].map(m => new URL(m[1], base).href));
for (const asset of assets) {
  check(new URL(asset).origin === base && (await fetch(asset)).ok, `The local page asset ${new URL(asset).pathname} loads.`);
}
const spriteDocuments = new Map();
const glyphs = new Set([...home.matchAll(/<use\b[^>]*href="([^"]+)"/g)].map(m => m[1]));
for (const glyph of glyphs) {
  const url = new URL(glyph, base);
  const symbol = url.hash.slice(1); url.hash = '';
  check(url.origin === base && !!symbol, 'Illustrated icons use local shared sprites.');
  if (!spriteDocuments.has(url.href)) {
    const response = await fetch(url);
    check(response.ok, 'The versioned icon sprite is served.');
    spriteDocuments.set(url.href, await response.text());
  }
  check(spriteDocuments.get(url.href).includes(`id="${symbol}"`), `The packaged sprite contains ${symbol}.`);
}
for (const asset of ['/js/landing.js', '/js/landing-product-film.js', '/_content/Parrot.SharedComponents/js/avatar-teaching-scene.js']) check((await fetch(base + asset)).ok, `${asset} is available for interactive motion.`);
const robots = await load('/robots.txt');
check(robots.includes('Sitemap: https://yarahq.com/sitemap.xml') && robots.includes('Disallow: /dashboard'), 'Robots points at the public sitemap, not application pages.');
const sitemap = await load('/sitemap.xml');
const locations = [...sitemap.matchAll(/<loc>(.*?)<\/loc>/g)].map(m => m[1]);
check(locations.length === 6 && locations.every(url => url.startsWith('https://yarahq.com/') && !/[?#]/.test(url)), 'The sitemap contains six stable public URLs only.');
check(!locations.some(url => /login|register|dashboard/.test(url)), 'Auth/app URLs stay out of the sitemap.');

const source = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Landing/LandingHumanControl.razor', import.meta.url), 'utf8');
const css = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Landing/LandingHumanControl.razor.css', import.meta.url), 'utf8');
check(!/HttpClient|@inject|Demo|@onclick|<button|<input/.test(source), 'The public illustration has no services, data access or pretend interactive controls.');
check(css.includes('@media (max-width: 760px)') && !/position: absolute|min-height|animation:|infinite/.test(css), 'Human control stacks naturally without fixed-height filler or extra autoplay.');
check(css.includes('var(--colors-brand-primary-foreground)') && css.includes('var(--colors-accent-amber-subtle)'), 'Bubbles use their dedicated token pairs, including amber attention.');
const headerCss = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Landing/LandingHeader.razor.css', import.meta.url), 'utf8');
check(headerCss.includes('.landing-header__start ::deep .ds-button { padding-inline: var(--spacing-sm); }'), 'Small-screen navigation uses scoped token spacing.');
const layoutSource = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Layout/Auth/AuthLayout.razor', import.meta.url), 'utf8');
check(!/DemoReset|LocalDemo|LandingConceptC|LandingDemo|StoryRotation/.test(layoutSource), 'The production layout contains neither a reset tool nor draft comparison components.');
check(!/Reset demo|Reset local designer demo/i.test(home), 'The rendered production homepage has no demo reset control.');
for (const name of ['Login','Register']) {
  const auth = await readFile(new URL(`../Parrot.Web/Parrot.Web/Components/Pages/Auth/${name}/${name}.razor`, import.meta.url),'utf8');
  check(!/DemoMode|DemoLoginService|Demo\.Enabled|demo-token|demo-user/.test(auth), `${name} uses real auth contracts, not fake sign-in details.`);
  check(auth.includes('/api/auth/sso/google') && auth.includes('/api/auth/sso/facebook'), `${name} retains existing real SSO endpoint routing.`);
}
const routes = await readFile(new URL('../Parrot.Web/Parrot.Web/Components/Routes.razor', import.meta.url),'utf8');
check(routes.includes('|| IsCheckoutPath()') && routes.includes('StartsWith("/checkout/"'), 'Latest main’s public checkout route guards are preserved.');
const transfer = await new Promise((resolve, reject) => {
  get(base + '/', {headers: {'Accept-Encoding': 'gzip'}}, response => {
    let bytes = 0;
    response.on('data', chunk => bytes += chunk.length);
    response.on('end', () => resolve({status: response.statusCode, encoding: response.headers['content-encoding'], bytes}));
  }).on('error', reject);
});
check(transfer.status === 200 && transfer.encoding === 'gzip', 'Public HTML is served successfully with compression.');
check(transfer.bytes < 100 * 1024, 'The compressed public HTML stays below 100 KiB; this is not a whole-page performance score.');
console.log(`Local HTML transfer: ${transfer.bytes} bytes (gzip).`);
console.log(`${checks} local website launch checks passed. Real signup, legal approval and final visual/performance checks remain separate gates.`);
