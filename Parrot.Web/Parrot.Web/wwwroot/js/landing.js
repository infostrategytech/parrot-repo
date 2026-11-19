// The signed-out pages' top bar (designer, 17 November 2026). The page's <main> scrolls, not the window.
// It marks the page once it has scrolled (the bar turns frosted) and once the form's button has gone under it (the
// bar offers Sign in and Get started); it also scrolls back to the form, or down to a section.
const reduced = () => matchMedia('(prefers-reduced-motion: reduce)').matches;

export function watch(header) {
  const main = header?.closest('main');
  if (!main) return { dispose() {} };
  const form = main.querySelector('[data-landing-form]');
  const scrolled = () => main.toggleAttribute('data-scrolled', main.scrollTop > 4);
  main.addEventListener('scroll', scrolled, { passive: true });
  scrolled();
  let observer = null;
  if (form) {
    observer = new IntersectionObserver(([entry]) => {
      main.toggleAttribute('data-past-form', !entry.isIntersecting && entry.boundingClientRect.top < (entry.rootBounds?.top ?? 0));
    }, { root: main, rootMargin: `-${header.offsetHeight}px 0px 0px 0px` });
    // The moment the main button goes under the bar is the moment the action is out of reach.
    observer.observe(form.querySelector('button[type=submit]') ?? form);
  } else {
    main.setAttribute('data-past-form', '');
  }
  return {
    dispose() {
      main.removeEventListener('scroll', scrolled);
      observer?.disconnect();
    },
  };
}

export function toForm(header) {
  const main = header?.closest('main');
  if (!main) return;
  main.scrollTo({ top: 0, behavior: reduced() ? 'auto' : 'smooth' });
  const field = main.querySelector('[data-landing-form] input');
  setTimeout(() => field?.focus({ preventScroll: true }), reduced() ? 0 : 450);
}

export function toSection(header, id) {
  const target = header?.closest('main')?.querySelector(`#${CSS.escape(id)}`);
  if (!target) return false;
  target.scrollIntoView({ behavior: reduced() ? 'auto' : 'smooth', block: 'start' });
  return true;
}

// Whether the visitor asked for less motion, so a page can keep still what would otherwise change on its own.
export function prefersStill() { return reduced(); }

// A new page starts at its top. The page's <main> is kept when Sign In and Sign Up swap (they share the layout),
// so without this a visitor who scrolled to the end of one lands at the end of the next.
export function toTop(header) {
  header?.closest('main')?.scrollTo({ top: 0, behavior: 'instant' });
}
