// Whether text cut to a few lines (CSS line-clamp) has more to show, so "Show all" appears only when it does.
export function overflows(element, selector) {
  const target = selector ? element?.querySelector(selector) : element;
  return !!target && target.scrollHeight > target.clientHeight + 1;
}
