import { tr } from '../i18n';
// Structural edits: pages, folders, profiles.
import { selectProfile, updateProfile } from '../net/client';
import { socket } from '../net/socket';
import { DEFAULT_LAYOUT } from '../lib/layout';
import { newId } from '../lib/platform';
import { currentProfile, homePage, showToast, useDeck } from '../state/store';
import type { Action, Key, Page, Profile } from '../types';
import { sliderDefaults } from './SliderEditor';
import { defaultKeyStyle } from '../deck/DeckView';
import { makeTimerKey, retargetCopiedTimer } from './LiveEditor';

/** Ids of the folder pages reachable from a page (recursively), the page itself excluded. */
function folderTree(profile: Profile, pageId: string, acc = new Set<string>()): Set<string> {
  const page = profile.pages.find((p) => p.id === pageId);
  for (const k of page?.keys ?? []) {
    if (k.kind === 'folder' && k.folderPageId && !acc.has(k.folderPageId)) {
      acc.add(k.folderPageId);
      folderTree(profile, k.folderPageId, acc);
    }
  }
  return acc;
}

function removePages(p: Profile, ids: Set<string>) {
  p.pages = p.pages.filter((pg) => !ids.has(pg.id));
}

function backKey(): Key {
  return {
    id: newId('k'),
    span: { w: 1, h: 1 },
    pos: { portrait: { r: 0, c: 0 }, landscape: { r: 0, c: 0 } },
    style: {
      label: { text: tr('Indietro'), size: 13, color: '#ffffff', position: 'bottom' },
      icon: { kind: 'lucide', value: 'ArrowLeft', color: '#ffffff' },
      bg: { color: '#1b1d24' },
    },
    kind: 'action',
    onPress: { type: 'navigate', target: 'back' },
    onLongPress: null,
  };
}

// ---- Pages --------------------------------------------------------------------------------

export function addPage() {
  const id = newId('pg');
  updateProfile((p) => {
    const n = p.pages.filter((x) => x.kind !== 'folder').length + 1;
    p.pages.push({ id, name: tr('Pagina {0}', n), kind: 'page', keys: [] });
  });
  useDeck.setState({ pageStack: [id], selectedKeyId: null });
}

export function renamePage(pageId: string, name: string) {
  updateProfile((p) => {
    const pg = p.pages.find((x) => x.id === pageId);
    if (pg) pg.name = name;
  });
}

export function deletePage(pageId: string) {
  const p = currentProfile(useDeck.getState());
  if (!p) return;
  const page = p.pages.find((x) => x.id === pageId);
  if (!page) return;
  if (page.kind !== 'folder' && p.pages.filter((x) => x.kind !== 'folder').length <= 1) {
    showToast(tr("Non puoi eliminare l'ultima pagina"), 'error');
    return;
  }
  if (!confirm(tr('Eliminare la pagina «{0}» con tutti i suoi tasti e le sue cartelle?', page.name))) return;
  updateProfile((prof) => {
    const ids = folderTree(prof, pageId);
    ids.add(pageId);
    removePages(prof, ids);
    // Folder keys pointing to a removed page become empty keys.
    for (const pg of prof.pages)
      for (const k of pg.keys)
        if (k.kind === 'folder' && k.folderPageId && ids.has(k.folderPageId)) {
          k.kind = 'action';
          delete k.folderPageId;
        }
  });
  useDeck.setState({ pageStack: [], selectedKeyId: null });
}

export function movePage(pageId: string, delta: -1 | 1) {
  updateProfile((p) => {
    const i = p.pages.findIndex((x) => x.id === pageId);
    const order = p.pages.map((x, idx) => ({ x, idx })).filter((o) => o.x.kind !== 'folder');
    const pos = order.findIndex((o) => o.idx === i);
    const other = order[pos + delta];
    if (i < 0 || !other) return;
    [p.pages[i], p.pages[other.idx]] = [p.pages[other.idx], p.pages[i]];
  });
}

export function goToPage(pageId: string) {
  useDeck.setState({ pageStack: [pageId], selectedKeyId: null });
}

// ---- Folders ------------------------------------------------------------------------------

/**
 * Changes the key type. The look is reset to the defaults of the new type; the action is kept between
 * action, toggle and dynamic keys (toggle: it becomes the "turn ON" action).
 * Folder: creates its sub-page with a "back" key (removed again when leaving).
 */
export function setKeyKind(pageId: string, keyId: string, kind: Key['kind']) {
  updateProfile((p) => {
    const key = p.pages.find((x) => x.id === pageId)?.keys.find((x) => x.id === keyId);
    if (!key || key.kind === kind) return;

    if (key.kind === 'folder' && key.folderPageId) {
      const ids = folderTree(p, key.folderPageId);
      ids.add(key.folderPageId);
      removePages(p, ids);
      delete key.folderPageId;
    }
    if (key.kind === 'toggle' && key.toggle) {
      if (kind === 'action' || kind === 'dynamic') key.onPress = key.toggle.onAction;
      delete key.toggle;
    }

    delete key.live;
    delete key.slider;
    // A new type starts with a clean look (text, icon, colors): nothing carries over from the old type.
    key.style = defaultKeyStyle();
    if (kind === 'slider' || kind === 'folder') {
      key.onPress = null;
      key.onLongPress = null;
      delete key.longPressMs;
      delete key.onDoubleTap;
      delete key.doubleTapMs;
    }

    if (kind === 'slider') {
      key.kind = 'slider';
      key.slider = { target: 'vol' };
      const d = sliderDefaults(key.slider);
      key.style.label.text = d.label;
      key.style.icon = { kind: 'lucide', value: d.icon, color: '#ffffff' };
    } else if (kind === 'toggle') {
      key.toggle = { onAction: key.onPress, offAction: null, sameAction: true, onStyle: { bgColor: '#1f7a4a' } };
      key.onPress = null;
      key.kind = 'toggle';
    } else if (kind === 'action') {
      key.kind = 'action';
    } else if (kind === 'dynamic') {
      key.kind = 'dynamic';
      key.live = { source: 'clock', format: 'time', rules: [] };
      key.style.label.text = '{text}';
      key.style.label.position = 'center';
    } else {
      const folder: Page = {
        id: newId('pg'),
        name: tr('Cartella'),
        kind: 'folder',
        keys: [backKey()],
      };
      p.pages.push(folder);
      key.kind = 'folder';
      key.folderPageId = folder.id;
      key.style.label.text = tr('Cartella');
      key.style.icon = { kind: 'lucide', value: 'Folder', color: '#ffffff' };
    }
  });
}

/** "Timer" in the key type tiles: a dynamic key with its own countdown (or stopwatch). */
export function setTimerKind(pageId: string, keyId: string, mode: 'countdown' | 'stopwatch' = 'countdown') {
  setKeyKind(pageId, keyId, 'dynamic');
  updateProfile((p) => {
    const key = p.pages.find((x) => x.id === pageId)?.keys.find((x) => x.id === keyId);
    if (key) makeTimerKey(key, mode);
  });
}

export function openFolder(folderPageId: string) {
  useDeck.setState((s) => ({ pageStack: [...s.pageStack, folderPageId], selectedKeyId: null }));
}

export function deleteKey(pageId: string, keyId: string) {
  updateProfile((p) => {
    const page = p.pages.find((x) => x.id === pageId);
    const key = page?.keys.find((x) => x.id === keyId);
    if (!page || !key) return;
    if (key.kind === 'folder' && key.folderPageId) {
      const ids = folderTree(p, key.folderPageId);
      ids.add(key.folderPageId);
      removePages(p, ids);
    }
    page.keys = page.keys.filter((x) => x.id !== keyId);
  });
  useDeck.setState({ selectedKeyId: null });
}

/** Folder pages in the current navigation path, for the editor breadcrumb. */
export function breadcrumb(): Page[] {
  const s = useDeck.getState();
  const p = currentProfile(s);
  if (!p) return [];
  const pages = s.pageStack.map((id) => p.pages.find((x) => x.id === id)).filter(Boolean) as Page[];
  const first = pages[0]?.kind === 'folder' || pages.length === 0 ? homePage(p) : null;
  return first ? [first, ...pages] : pages;
}

// ---- Profiles -----------------------------------------------------------------------------

export function putNewProfile(profile: Profile) {
  if (!socket.send({ t: 'edit.profile.put', profile })) {
    showToast(tr('Non connesso al PC'), 'error');
    return;
  }
  useDeck.setState((s) => ({ profiles: [...s.profiles, profile] }));
  selectProfile(profile.id);
  useDeck.setState({ editing: true });
}

export function newProfile() {
  const base = currentProfile(useDeck.getState());
  const n = useDeck.getState().profiles.length + 1;
  putNewProfile({
    id: newId('p'),
    name: tr('Profilo {0}', n),
    layout: structuredClone(base?.layout ?? DEFAULT_LAYOUT),
    background: structuredClone(base?.background ?? { color: '#0e0f13' }),
    pages: [{ id: newId('pg'), name: 'Home', kind: 'page', keys: [] }],
  });
}

export function duplicateProfile() {
  const src = currentProfile(useDeck.getState());
  if (!src) return;
  putNewProfile(withFreshIds(src, tr('{0} (copia)', src.name)));
}

/** Deep copy with fresh ids; folder and navigation references are remapped. */
export function withFreshIds(src: Profile, name: string): Profile {
  const copy: Profile = structuredClone(src);
  copy.id = newId('p');
  copy.name = name;
  const pageIds = new Map<string, string>();
  for (const pg of copy.pages) {
    const id = newId('pg');
    pageIds.set(pg.id, id);
    pg.id = id;
  }
  const remapAction = (a: Action | null) => {
    if (a?.type === 'navigate' && a.target === 'page' && a.id) a.id = pageIds.get(a.id) ?? a.id;
  };
  for (const pg of copy.pages)
    for (const k of pg.keys) {
      k.id = newId('k');
      retargetCopiedTimer(k);
      if (k.folderPageId) k.folderPageId = pageIds.get(k.folderPageId);
      remapAction(k.onPress);
      remapAction(k.onLongPress);
      remapAction(k.onDoubleTap ?? null);
      remapAction(k.toggle?.onAction ?? null);
      remapAction(k.toggle?.offAction ?? null);
    }
  return copy;
}

export function deleteProfile() {
  const s = useDeck.getState();
  const p = currentProfile(s);
  if (!p) return;
  if (s.profiles.length <= 1) {
    showToast(tr("Non puoi eliminare l'ultimo profilo"), 'error');
    return;
  }
  if (!confirm(tr("Eliminare il profilo «{0}»? L'operazione non si può annullare.", p.name))) return;
  socket.send({ t: 'edit.profile.delete', id: p.id });
}
