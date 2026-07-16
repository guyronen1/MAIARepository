/**
 * Chart.js doesn't read CSS variables, so its grid lines / axis ticks / legend
 * text default to light-theme greys and render as a bright "table" grid on dark.
 * This reads the current theme's tokens off :root so charts match the app in
 * both themes. Charts call this at build time and rebuild on theme change.
 */
export interface ChartTheme {
  /** Axis ticks + legend text — secondary emphasis. */
  tick:       string;
  /** Primary axis labels (e.g. job names) — full emphasis. */
  tickStrong: string;
  /** Grid lines. */
  grid:       string;
  /** Subtler grid lines (e.g. inter-bucket separators). */
  gridSubtle: string;
}

export function readChartTheme(): ChartTheme {
  const fallback: ChartTheme = {
    tick: '#6b7280', tickStrong: '#374151', grid: '#e8ebf0', gridSubtle: '#f3f4f6',
  };
  if (typeof document === 'undefined') return fallback;
  const cs = getComputedStyle(document.documentElement);
  const v = (name: string, fb: string) => cs.getPropertyValue(name).trim() || fb;
  return {
    tick:       v('--text-muted',   fallback.tick),
    tickStrong: v('--text',         fallback.tickStrong),
    grid:       v('--border',       fallback.grid),
    gridSubtle: v('--border-light', fallback.gridSubtle),
  };
}
