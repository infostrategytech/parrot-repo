/* Blazor's JavaScript initializer for this library: loaded and called by
   blazor.web.js itself. Tells early-input.js when the live connection has
   started, so a page with no design-system fields still stops holding early
   typing and submits. */
export function afterServerStarted() {
  window.auroraEarly?.connected();
}
