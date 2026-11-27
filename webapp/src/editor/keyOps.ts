import { tr } from '../i18n';
// Key-level edits: drag & drop, copy / paste / duplicate.
import { updateProfile } from '../net/client';
import { placeKeys, resolveLayout, type Placement } from '../lib/layout';
import { newId, storage } from '../lib/platform';
import { currentPage, currentProfile, showToast, useDeck } from '../state/store';
import type { Cell, Key, Orientation, Page, Profile } from '../types';
import { retargetCopiedTimer } from './LiveEditor';
import { isSoundKey, isSoundpadPage } from './SoundpadEditor';

interface Rect {
  r: number;
  c: number;
  w: number;
  h: number;
}

const overlaps = (a: Rect, b: Rect) => a.r < b.r + b.h && b.r < a.r + a.h && a.c < b.c + b.w && b.c < a.c + a.w;

/**
 * Moves a key to a cell in the given orientation. If the target is taken by exactly one key,
 * the two keys swap places. All visible keys get an explicit position first, so that
 * auto-placed keys do not jump around after the move.
 */
export function moveKey(
  pageId: string,
  orientation: Orientation,
  placed: Placement[],
  keyId: string,
  target: Cell,
  rows: number,
  cols: number,
): boolean {
  const me = placed.find((p) => p.key.id === keyId);
  if (!me || (me.r === target.r && me.c === target.c)) return false;
  const dest: Rect = { r: target.r, c: target.c, w: me.w, h: me.h };
  if (dest.r < 0 || dest.c < 0 || dest.r + dest.h > rows || dest.c + dest.w > cols) return false;

  const others = placed.filter((p) => p.key.id !== keyId);
  const hit = others.filter((o) => overlaps(o, dest));
  let swap: Placement | null = null;
  if (hit.length > 1) return false;
  if (hit.length === 1) {
    swap = hit[0];
    // The other key goes where this key was: it must fit there without touching a third key.
    const back: Rect = { r: me.r, c: me.c, w: swap.w, h: swap.h };
    if (back.r + back.h > rows || back.c + back.w > cols) return false;
    if (overlaps(back, dest)) return false;
    if (others.some((o) => o !== swap && overlaps(o, back))) return false;
  }

  updateProfile((p) => {
    const page = p.pages.find((x) => x.id === pageId);
    if (!page) return;
    for (const pl of placed) {
      const k = page.keys.find((x) => x.id === pl.key.id);
      if (k) k.pos = { ...k.pos, [orientation]: { r: pl.r, c: pl.c } };
    }
    const k = page.keys.find((x) => x.id === keyId)!;
    k.pos[orientation] = { r: dest.r, c: dest.c };
    if (swap) {
      const o = page.keys.find((x) => x.id === swap!.key.id)!;
      o.pos[orientation] = { r: me.r, c: me.c };
    }
  });
  return true;
}

// ---- Clipboard ----------------------------------------------------------------------------

const CLIP_KEY = 'padnook.clipboard';

/** A copied key, with the pages of its folder tree (if it is a folder). */
interface Clip {
  key: Key;
  pages: Page[];
}

function collectFolder(profile: Profile, key: Key, acc: Page[] = []): Page[] {
  if (key.kind !== 'folder' || !key.folderPageId) return acc;
  const page = profile.pages.find((p) => p.id === key.folderPageId);
  if (!page || acc.includes(page)) return acc;
  acc.push(page);
  for (const k of page.keys) collectFolder(profile, k, acc);
  return acc;
}

/** Fresh ids for the key, its folder pages and the keys inside them. */
function cloneClip(clip: Clip): Clip {
  const copy: Clip = structuredClone(clip);
  const ids = new Map<string, string>();
  for (const pg of copy.pages) {
    const id = newId('pg');
    ids.set(pg.id, id);
    pg.id = id;
  }
  const fix = (k: Key) => {
    k.id = newId('k');
    retargetCopiedTimer(k);
    if (k.folderPageId) k.folderPageId = ids.get(k.folderPageId) ?? k.folderPageId;
  };
  fix(copy.key);
  for (const pg of copy.pages) pg.keys.forEach(fix);
  return copy;
}

export function getClipboard(): Clip | null {
  try {
    return JSON.parse(storage.get(CLIP_KEY) ?? 'null');
  } catch {
    return null;
  }
}

export function copyKey(keyId: string) {
  const s = useDeck.getState();
  const profile = currentProfile(s);
  const key = currentPage(s)?.keys.find((k) => k.id === keyId);
  if (!profile || !key) return;
  const clip: Clip = { key: structuredClone(key), pages: structuredClone(collectFolder(profile, key)) };
  storage.set(CLIP_KEY, JSON.stringify(clip));
  useDeck.setState({ hasClipboard: true });
  showToast(tr('Tasto copiato'));
}

/** Free cell for a key of the given size in the current page/orientation, preferring `near`. */
function freeCell(orientation: Orientation, w: number, h: number, near?: Cell): Cell | null {
  const s = useDeck.getState();
  const profile = currentProfile(s);
  const page = currentPage(s);
  if (!profile || !page) return null;
  const { rows, cols } = resolveLayout(profile, page)[orientation];
  const { placed } = placeKeys(page.keys, rows, cols, orientation);
  const free = (r: number, c: number) => r + h <= rows && c + w <= cols && !placed.some((p) => overlaps(p, { r, c, w, h }));
  const start = near ? near.r * cols + near.c : 0;
  for (let i = 0; i < rows * cols; i++) {
    const idx = (start + i) % (rows * cols);
    const r = Math.floor(idx / cols);
    const c = idx % cols;
    if (free(r, c)) return { r, c };
  }
  return null;
}

function insertClip(clip: Clip, orientation: Orientation, near?: Cell) {
  const s = useDeck.getState();
  const page = currentPage(s);
  if (!page) return;
  if (isSoundpadPage(page) && !isSoundKey(clip.key)) {
    showToast(tr('In una pagina Soundpad puoi mettere solo tasti soundpad'), 'error');
    return;
  }
  const fresh = cloneClip(clip);
  const cell = freeCell(orientation, fresh.key.span.w, fresh.key.span.h, near);
  if (!cell) {
    showToast(tr('Non ci sono celle libere in questa pagina'), 'error');
    return;
  }
  // Explicit position only for this orientation; the other one is placed automatically.
  fresh.key.pos = { [orientation]: cell };
  updateProfile((p) => {
    p.pages.find((x) => x.id === page.id)?.keys.push(fresh.key);
    p.pages.push(...fresh.pages);
  });
  useDeck.setState({ selectedKeyId: fresh.key.id });
}

export function pasteKey(orientation: Orientation, at?: Cell) {
  const clip = getClipboard();
  if (!clip) {
    showToast(tr('Nessun tasto copiato'), 'error');
    return;
  }
  insertClip(clip, orientation, at);
}

export function duplicateKey(keyId: string, orientation: Orientation) {
  const s = useDeck.getState();
  const profile = currentProfile(s);
  const page = currentPage(s);
  const key = page?.keys.find((k) => k.id === keyId);
  if (!profile || !page || !key) return;
  const { rows, cols } = resolveLayout(profile, page)[orientation];
  const me = placeKeys(page.keys, rows, cols, orientation).placed.find((p) => p.key.id === keyId);
  insertClip({ key, pages: collectFolder(profile, key) }, orientation, me ? { r: me.r, c: me.c + me.w } : undefined);
}
