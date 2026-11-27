import type { Key, Layout, Orientation, Page, Profile } from '../types';

export const DEFAULT_LAYOUT: Layout = {
  portrait: { rows: 4, cols: 3 },
  landscape: { rows: 3, cols: 5 },
  gapX: 12,
  gapY: 12,
  margin: { top: 16, right: 16, bottom: 16, left: 16 },
  keyShape: 'square',
  cornerRadius: 18,
  align: { h: 'center', v: 'center' },
  minKeySize: 56,
};

/** Profile defaults merged with the page overrides. */
export function resolveLayout(profile: Profile, page: Page): Layout {
  const b: Layout = { ...DEFAULT_LAYOUT, ...profile.layout };
  const o = page.layout ?? {};
  return {
    ...b,
    ...o,
    portrait: { ...DEFAULT_LAYOUT.portrait, ...b.portrait, ...o.portrait },
    landscape: { ...DEFAULT_LAYOUT.landscape, ...b.landscape, ...o.landscape },
    margin: { ...DEFAULT_LAYOUT.margin, ...b.margin, ...o.margin },
    align: { ...DEFAULT_LAYOUT.align, ...b.align, ...o.align },
  };
}

export interface GridMetrics {
  rows: number;
  cols: number;
  cellW: number;
  cellH: number;
  width: number;
  height: number;
  scrollX: boolean;
  scrollY: boolean;
}

/** Cell size for the available area; never below minKeySize (the grid scrolls instead). */
export function computeGrid(layout: Layout, orientation: Orientation, availW: number, availH: number): GridMetrics {
  const { rows, cols } = layout[orientation];
  const innerW = Math.max(0, availW - layout.margin.left - layout.margin.right);
  const innerH = Math.max(0, availH - layout.margin.top - layout.margin.bottom);
  let cellW = (innerW - layout.gapX * (cols - 1)) / cols;
  let cellH = (innerH - layout.gapY * (rows - 1)) / rows;
  const min = layout.minKeySize;

  if (layout.keyShape === 'square') {
    const s = Math.max(min, Math.min(cellW, cellH));
    cellW = cellH = s;
  } else {
    cellW = Math.max(min, cellW);
    cellH = Math.max(min, cellH);
  }
  cellW = Math.floor(cellW);
  cellH = Math.floor(cellH);

  const width = cols * cellW + layout.gapX * (cols - 1);
  const height = rows * cellH + layout.gapY * (rows - 1);
  return { rows, cols, cellW, cellH, width, height, scrollX: width > innerW + 0.5, scrollY: height > innerH + 0.5 };
}

export interface Placement {
  key: Key;
  r: number;
  c: number;
  w: number;
  h: number;
}

/**
 * Places keys on the grid: keys with an explicit position for this orientation first,
 * then the others flow into the first free cells. Keys that do not fit are returned as hidden.
 */
export function placeKeys(keys: Key[], rows: number, cols: number, orientation: Orientation) {
  const occ = new Uint8Array(rows * cols);
  const fits = (r: number, c: number, w: number, h: number) => {
    if (r < 0 || c < 0 || r + h > rows || c + w > cols) return false;
    for (let y = r; y < r + h; y++) for (let x = c; x < c + w; x++) if (occ[y * cols + x]) return false;
    return true;
  };
  const mark = (r: number, c: number, w: number, h: number) => {
    for (let y = r; y < r + h; y++) for (let x = c; x < c + w; x++) occ[y * cols + x] = 1;
  };

  const placed: Placement[] = [];
  const hidden: Key[] = [];
  const auto: Key[] = [];

  for (const key of keys) {
    const w = Math.min(Math.max(1, key.span?.w ?? 1), cols);
    const h = Math.min(Math.max(1, key.span?.h ?? 1), rows);
    const p = key.pos?.[orientation];
    if (p && fits(p.r, p.c, w, h)) {
      mark(p.r, p.c, w, h);
      placed.push({ key, r: p.r, c: p.c, w, h });
    } else {
      auto.push(key);
    }
  }

  for (const key of auto) {
    const w = Math.min(Math.max(1, key.span?.w ?? 1), cols);
    const h = Math.min(Math.max(1, key.span?.h ?? 1), rows);
    let done = false;
    for (let r = 0; r < rows && !done; r++) {
      for (let c = 0; c < cols && !done; c++) {
        if (fits(r, c, w, h)) {
          mark(r, c, w, h);
          placed.push({ key, r, c, w, h });
          done = true;
        }
      }
    }
    if (!done) hidden.push(key);
  }

  const empty: { r: number; c: number }[] = [];
  for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) if (!occ[r * cols + c]) empty.push({ r, c });

  return { placed, hidden, empty };
}

export const flexAlign = (a: 'start' | 'center' | 'end') => (a === 'start' ? 'flex-start' : a === 'end' ? 'flex-end' : 'center');
