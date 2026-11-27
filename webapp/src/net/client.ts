import { tr } from '../i18n';
// Protocol logic: authentication, server messages, presses and edits.
import { desktopKey, isDesktop } from '../lib/desktop';
import { normApp } from '../lib/live';
import { loadLucide } from '../lib/lucide';
import { guessDeviceName, sessionStore, storage } from '../lib/platform';
import { currentProfile, homePage, showToast, useDeck } from '../state/store';
import type { Action, Key, Profile } from '../types';
import { socket, type ServerMsg } from './socket';
import { timerAlarm } from '../lib/alarm';

const TOKEN_KEY = 'padnook.token';
const CACHE_KEY = 'padnook.cache';
const PROFILE_KEY = 'padnook.profile';
const EDIT_KEY = 'padnook.editToken';
const AUTO_KEY = 'padnook.autoProfiles';

// ---- Request/response (messages with a rid) ----------------------------------------------

let ridSeq = 0;
const pending = new Map<string, { resolve: (m: ServerMsg) => void; reject: (e: Error) => void; timer: number }>();

/** Sends a message and waits for the reply with the same rid. */
export function request(msg: object, timeoutMs = 15000): Promise<ServerMsg> {
  return new Promise((resolve, reject) => {
    const rid = `r${++ridSeq}`;
    if (!socket.send({ ...msg, rid })) {
      reject(new Error(tr('Non connesso al PC')));
      return;
    }
    const timer = window.setTimeout(() => {
      pending.delete(rid);
      reject(new Error(tr('Il PC non risponde')));
    }, timeoutMs);
    pending.set(rid, { resolve, reject, timer });
  });
}

// Pairing link from the QR code: http://IP:PORT/#pair=123456[&name=PC]
let pendingPair: { code: string; name: string | null } | null = null;
// "#edit=1" (tray menu "Apri editor nel browser"): open edit mode as soon as we are connected.
let wantEdit = false;

function readHash(): boolean {
  const h = new URLSearchParams(location.hash.replace(/^#/, ''));
  const code = h.get('pair');
  if (h.get('edit') === '1') wantEdit = true;
  if (code) pendingPair = { code, name: h.get('name') };
  if (code || wantEdit) history.replaceState(null, '', location.pathname + location.search);
  return !!code;
}
readHash();

// The link can also be opened while the app is already loaded (only the hash changes).
window.addEventListener('hashchange', () => {
  if (readHash() && !storage.get(TOKEN_KEY) && pendingPair) {
    sendPair(pendingPair.code, pendingPair.name ?? guessDeviceName());
  }
  if (wantEdit && useDeck.getState().auth === 'ok' && socket.state === 'open') {
    wantEdit = false;
    openEditor();
  }
});

export function initClient() {
  // Show the last known layout immediately while the socket connects.
  if (storage.get(TOKEN_KEY)) {
    try {
      const cache = JSON.parse(storage.get(CACHE_KEY) ?? 'null');
      if (cache?.profiles?.length) {
        useDeck.setState({
          auth: 'ok',
          profiles: cache.profiles,
          rev: cache.rev ?? 0,
          deviceName: cache.deviceName ?? '',
          defaultProfileId: cache.defaultProfileId ?? null,
          profileId: pickProfile(cache.profiles, storage.get(PROFILE_KEY), cache.defaultProfileId),
        });
      }
    } catch {
      /* ignore */
    }
  }

  socket.onState((conn) => useDeck.setState({ conn }));
  socket.on(handleMessage);
  socket.start(onOpen);
}

function onOpen() {
  const token = storage.get(TOKEN_KEY);
  if (isDesktop) {
    socket.send({ t: 'hello', desktopKey: desktopKey() });
  } else if (token) {
    socket.send({ t: 'hello', token, editToken: sessionStore.get(EDIT_KEY) ?? undefined });
  } else if (pendingPair) {
    sendPair(pendingPair.code, pendingPair.name ?? guessDeviceName());
  } else {
    useDeck.setState({ auth: 'pairing' });
  }
}

export function sendPair(code: string, deviceName: string) {
  useDeck.setState({ pairBusy: true, pairError: null });
  if (!socket.send({ t: 'pair', code: code.replace(/\s/g, ''), deviceName })) {
    useDeck.setState({ pairBusy: false, pairError: tr('Server non raggiungibile') });
  }
}

function pickProfile(profiles: Profile[], ...candidates: (string | null | undefined)[]): string | null {
  for (const c of candidates) if (c && profiles.some((p) => p.id === c)) return c;
  return profiles[0]?.id ?? null;
}

let cacheTimer: number | undefined;
function saveCache() {
  clearTimeout(cacheTimer);
  cacheTimer = window.setTimeout(() => {
    const s = useDeck.getState();
    storage.set(
      CACHE_KEY,
      JSON.stringify({ profiles: s.profiles, rev: s.rev, deviceName: s.deviceName, defaultProfileId: s.defaultProfileId }),
    );
  }, 800);
}

function handleMessage(m: ServerMsg) {
  if (m.rid && pending.has(m.rid)) {
    const p = pending.get(m.rid)!;
    pending.delete(m.rid);
    clearTimeout(p.timer);
    if (m.t === 'error') p.reject(new Error(m.message ?? tr('Errore')));
    else p.resolve(m);
    if (m.t === 'error' || m.t === 'info' || m.t === 'weather.results') return;
  }
  const st = useDeck.getState();
  switch (m.t) {
    case 'paired':
      storage.set(TOKEN_KEY, m.token);
      pendingPair = null;
      useDeck.setState({ justPaired: true, pairBusy: false, pairError: null });
      break;

    case 'welcome': {
      const profiles: Profile[] = m.profiles ?? [];
      const keep = st.profileId && profiles.some((p) => p.id === st.profileId) ? st.profileId : null;
      const profileId = keep ?? pickProfile(profiles, m.defaultProfileId, storage.get(PROFILE_KEY));
      useDeck.setState({
        auth: 'ok',
        pairBusy: false,
        deviceId: m.deviceId,
        deviceName: m.deviceName,
        defaultProfileId: m.defaultProfileId ?? null,
        profiles,
        rev: m.rev,
        toggles: m.toggles ?? {},
        live: stamp(m.live ?? {}),
        fgApp: m.fg?.exe ?? null,
        plugins: m.plugins?.plugins ?? [],
        pluginErrors: m.plugins?.errors ?? [],
        profileId,
        pageStack: keep ? st.pageStack : [],
        hasPin: !!m.hasPin,
        unlocked: !!m.edit,
        // The desktop editor window is always in edit mode.
        editing: isDesktop ? !!m.edit : st.editing && !!m.edit,
      });
      if (!m.edit) sessionStore.set(EDIT_KEY, null);
      else socket.send({ t: 'edit.devices.list' }); // resumed edit session: refresh the device list
      if (JSON.stringify(profiles).includes('"kind":"lucide"')) void loadLucide();
      if (wantEdit) {
        wantEdit = false;
        openEditor();
      }
      // Re-send an edit made while offline.
      if (pendingProfile && m.edit) {
        applyLocalProfile(pendingProfile);
        scheduleFlush();
      }
      saveCache();
      applyAutoProfile();
      break;
    }

    case 'live': {
      const next = { ...st.live };
      const at = Date.now();
      for (const [k, v] of Object.entries(m.values ?? {})) {
        if (v === null) delete next[k];
        else next[k] = { ...(v as object), at };
      }
      useDeck.setState({ live: next });
      break;
    }

    case 'flash': {
      const keyId = String(m.keyId);
      const n = Date.now();
      const ms = Number(m.ms) || 3000;
      useDeck.setState((s) => ({ flashes: { ...s.flashes, [keyId]: { color: String(m.color ?? '#9146ff'), ms, n } } }));
      window.setTimeout(() => {
        useDeck.setState((s) => {
          if (s.flashes[keyId]?.n !== n) return {};
          const next = { ...s.flashes };
          delete next[keyId];
          return { flashes: next };
        });
      }, ms);
      break;
    }

    case 'timer.done':
      timerAlarm(String(m.title ?? 'Timer'));
      break;

    case 'fg':
      useDeck.setState({ fgApp: m.exe ?? null });
      applyAutoProfile();
      break;

    case 'plugins':
      useDeck.setState({ plugins: m.plugins ?? [], pluginErrors: m.errors ?? [] });
      break;

    case 'profile.changed': {
      const p: Profile = m.profile;
      const exists = st.profiles.some((x) => x.id === p.id);
      useDeck.setState({
        rev: m.rev,
        profiles: exists ? st.profiles.map((x) => (x.id === p.id ? p : x)) : [...st.profiles, p],
      });
      saveCache();
      applyAutoProfile();
      break;
    }

    case 'profile.deleted': {
      const profiles = st.profiles.filter((x) => x.id !== m.id);
      useDeck.setState({
        rev: m.rev,
        profiles,
        profileId: st.profileId === m.id ? pickProfile(profiles, st.defaultProfileId) : st.profileId,
        pageStack: st.profileId === m.id ? [] : st.pageStack,
      });
      saveCache();
      break;
    }

    case 'toggle.state':
      useDeck.setState((s) => ({ toggles: { ...s.toggles, [m.keyId]: !!m.on } }));
      break;

    case 'profile.saved':
      useDeck.setState({ rev: m.rev });
      break;

    case 'device.updated':
      useDeck.setState({ deviceName: m.deviceName, defaultProfileId: m.defaultProfileId ?? null });
      saveCache();
      break;

    case 'devices':
      useDeck.setState({ devices: m.devices ?? [] });
      break;

    case 'edit.unlocked':
      sessionStore.set(EDIT_KEY, m.editToken);
      useDeck.setState({ unlocked: true, editing: true, hasPin: true, pinMode: null, pinError: null, menuOpen: false });
      break;

    case 'edit.locked':
      sessionStore.set(EDIT_KEY, null);
      useDeck.setState({ unlocked: false, editing: false, selectedKeyId: null });
      break;

    case 'pair.code':
      useDeck.setState({ homeCode: { code: m.code, expiresAt: Date.now() + m.expiresInSec * 1000 } });
      break;

    case 'press.result':
      if (!m.ok) showToast(m.error ?? tr('Azione non riuscita'), 'error', 3500);
      break;

    case 'revoked':
      storage.set(TOKEN_KEY, null);
      storage.set(CACHE_KEY, null);
      sessionStore.set(EDIT_KEY, null);
      useDeck.setState({ auth: 'pairing', profiles: [], editing: false, unlocked: false });
      showToast(tr("L'accesso di questo dispositivo è stato revocato"), 'error', 5000);
      break;

    case 'error':
      handleError(m.code, m.message);
      break;
  }
}

function handleError(code: string, message: string) {
  switch (code) {
    case 'auth':
      if (isDesktop) {
        showToast(message, 'error');
        break;
      }
      storage.set(TOKEN_KEY, null);
      storage.set(CACHE_KEY, null);
      if (pendingPair) {
        sendPair(pendingPair.code, pendingPair.name ?? guessDeviceName());
      } else {
        useDeck.setState({ auth: 'pairing', profiles: [] });
      }
      break;
    case 'pairInvalid':
    case 'rateLimited':
      if (useDeck.getState().pinMode) useDeck.setState({ pinError: message });
      else {
        pendingPair = null;
        useDeck.setState({ auth: 'pairing', pairBusy: false, pairError: message });
      }
      break;
    case 'noPin':
      useDeck.setState({ hasPin: false, pinMode: 'set', pinError: null });
      break;
    case 'wrongPin':
    case 'invalidPin':
      useDeck.setState({ pinError: message });
      break;
    case 'locked':
      sessionStore.set(EDIT_KEY, null);
      useDeck.setState({ unlocked: false, editing: false });
      showToast(message, 'error');
      break;
    default:
      showToast(message || tr('Errore'), 'error', 3500);
  }
}

// ---- Use mode ----------------------------------------------------------------------------

export function selectProfile(id: string) {
  storage.set(PROFILE_KEY, id);
  // A manual choice ends the automatic switch.
  useDeck.setState({ profileId: id, pageStack: [], selectedKeyId: null, menuOpen: false, autoBaseProfileId: null });
}

function stamp(values: Record<string, object>) {
  const at = Date.now();
  return Object.fromEntries(Object.entries(values).map(([k, v]) => [k, { ...v, at }]));
}

// ---- Automatic profiles ------------------------------------------------------------------

export function autoProfilesEnabled(): boolean {
  return storage.get(AUTO_KEY) !== '0';
}

export function setAutoProfilesEnabled(on: boolean) {
  storage.set(AUTO_KEY, on ? '1' : '0');
  if (on) applyAutoProfile();
  else {
    const base = useDeck.getState().autoBaseProfileId;
    if (base) useDeck.setState({ profileId: base, pageStack: [], autoBaseProfileId: null });
  }
}

/** Shows the profile linked to the app in front on the PC; goes back when that app is left. */
function applyAutoProfile() {
  const st = useDeck.getState();
  if (!autoProfilesEnabled() || st.editing || !st.fgApp) return;
  const exe = normApp(st.fgApp);
  const match = st.profiles.find((p) => p.auto?.apps?.some((a) => normApp(a) === exe));
  if (match) {
    if (match.id === st.profileId) return;
    useDeck.setState({
      profileId: match.id,
      pageStack: [],
      selectedKeyId: null,
      autoBaseProfileId: st.autoBaseProfileId ?? st.profileId,
    });
  } else if (st.autoBaseProfileId) {
    const base = st.profiles.some((p) => p.id === st.autoBaseProfileId)
      ? st.autoBaseProfileId
      : pickProfile(st.profiles, storage.get(PROFILE_KEY), st.defaultProfileId);
    useDeck.setState({ profileId: base, pageStack: [], autoBaseProfileId: null });
  }
}

const CONFIRM_TEXT: Record<string, string> = {
  shutdown: tr('Spegnere il PC?'),
  restart: tr('Riavviare il PC?'),
  logoff: tr("Disconnettere l'utente dal PC?"),
  sleep: tr('Mettere in sospensione il PC?'),
  hibernate: tr('Ibernare il PC?'),
  lock: tr('Bloccare il PC?'),
};

function needsConfirm(action: Action | null): string | null {
  if (!action || !('confirm' in action) || !action.confirm) return null;
  return (action.type === 'system' && CONFIRM_TEXT[action.command]) || tr('Eseguire questa azione?');
}

/** Runs actions that live entirely in the webapp. Returns true if handled. */
function runLocal(action: Action | null): boolean {
  if (!action || action.type !== 'navigate') return false;
  const st = useDeck.getState();
  switch (action.target) {
    case 'back':
      useDeck.setState({ pageStack: st.pageStack.slice(0, -1) });
      break;
    case 'home':
      useDeck.setState({ pageStack: [] });
      break;
    case 'page':
      if (action.id) useDeck.setState({ pageStack: [action.id] });
      break;
    case 'profile':
      if (action.id && st.profiles.some((p) => p.id === action.id)) selectProfile(action.id);
      break;
  }
  return true;
}

/** Called on pointerdown (or long-press timeout). Returns false if the press could not be sent. */
export function pressKey(key: Key, pageId: string, gesture: 'press' | 'long' | 'double'): boolean {
  if (gesture === 'press' && key.kind === 'folder' && key.folderPageId) {
    useDeck.setState((s) => ({ pageStack: [...s.pageStack, key.folderPageId!] }));
    return true;
  }
  const st = useDeck.getState();
  if (gesture === 'press' && key.kind === 'toggle') {
    // The server owns the state; flip locally right away for instant feedback.
    const prev = !!st.toggles[key.id];
    const sent = socket.send({ t: 'press', profileId: st.profileId, pageId, keyId: key.id, gesture });
    if (sent) useDeck.setState({ toggles: { ...st.toggles, [key.id]: !prev } });
    return sent;
  }
  const action = gesture === 'long' ? key.onLongPress : gesture === 'double' ? key.onDoubleTap : key.onPress;
  if (!action) return true;
  if (runLocal(action)) return true;
  const question = needsConfirm(action);
  if (question) {
    if (socket.state !== 'open') return false;
    const profileId = st.profileId;
    // After the pointer events of this touch, so the key does not stay "pressed" behind the dialog.
    window.setTimeout(() => {
      if (confirm(question)) socket.send({ t: 'press', profileId, pageId, keyId: key.id, gesture });
    }, 60);
    return true;
  }
  return socket.send({ t: 'press', profileId: st.profileId, pageId, keyId: key.id, gesture });
}

/** Slider keys: sets the level (0..100) on the PC. */
export function sendSlider(key: Key, pageId: string, value: number): boolean {
  const st = useDeck.getState();
  return socket.send({ t: 'slider', profileId: st.profileId, pageId, keyId: key.id, value: Math.round(value * 10) / 10 });
}

/** Slider keys: a tap toggles mute (audio targets). */
export function tapSlider(key: Key, pageId: string): boolean {
  const st = useDeck.getState();
  return socket.send({ t: 'slider.tap', profileId: st.profileId, pageId, keyId: key.id });
}

export function uploadAuth() {
  return { token: storage.get(TOKEN_KEY), editToken: sessionStore.get(EDIT_KEY), desktopKey: desktopKey() };
}

export function requestHomeCode() {
  socket.send({ t: 'pair.code.request' });
}

// ---- Edit mode ---------------------------------------------------------------------------

/** Editor lists from the PC (audio devices, apps, OBS scenes). */
export async function refreshPcInfo(silent = false): Promise<void> {
  try {
    const m = await request({ t: 'edit.info' });
    const { t: _t, rid: _rid, ...info } = m;
    useDeck.setState({ pcInfo: info as never });
  } catch (e) {
    if (!silent) showToast((e as Error).message, 'error');
  }
}

export function openEditor() {
  const st = useDeck.getState();
  if (st.unlocked) useDeck.setState({ editing: true, menuOpen: false });
  else useDeck.setState({ pinMode: st.hasPin ? 'unlock' : 'set', pinError: null, menuOpen: false });
}

export function unlock(pin: string) {
  socket.send({ t: 'edit.unlock', pin });
}

export function setPin(pin: string) {
  socket.send({ t: 'edit.pin.set', pin });
}

export function lockEditor() {
  flushNow();
  socket.send({ t: 'edit.lock' });
  sessionStore.set(EDIT_KEY, null);
  useDeck.setState({ editing: false, unlocked: false, selectedKeyId: null });
}

export function closeEditor() {
  flushNow();
  useDeck.setState({ editing: false, selectedKeyId: null });
  applyAutoProfile();
}

export function revokeDevice(deviceId: string) {
  socket.send({ t: 'edit.device.revoke', deviceId });
}

export function setToggleState(keyId: string, on: boolean) {
  socket.send({ t: 'edit.toggle.set', keyId, on });
}

export function updateDevice(deviceId: string, patch: { name?: string; defaultProfileId?: string | null }) {
  socket.send({ t: 'edit.device.update', deviceId, ...patch });
}

let pendingProfile: Profile | null = null;
let flushTimer: number | undefined;

function applyLocalProfile(p: Profile) {
  useDeck.setState((s) => ({ profiles: s.profiles.map((x) => (x.id === p.id ? p : x)) }));
}

function scheduleFlush() {
  if (flushTimer !== undefined) return;
  // ~30 updates/s: live preview on other devices while dragging sliders.
  flushTimer = window.setTimeout(flushNow, 33);
}

function flushNow() {
  clearTimeout(flushTimer);
  flushTimer = undefined;
  if (!pendingProfile) return;
  if (socket.send({ t: 'edit.profile.put', profile: pendingProfile })) pendingProfile = null;
  else showToast(tr('Non connesso: la modifica verrà inviata alla riconnessione'), 'error');
}

/** Applies a change to the current profile locally and syncs it to the server. */
export function updateProfile(mutate: (p: Profile) => void) {
  const p = currentProfile(useDeck.getState());
  if (!p) return;
  const next: Profile = structuredClone(p);
  mutate(next);
  applyLocalProfile(next);
  pendingProfile = next;
  scheduleFlush();
  saveCache();
}

export function currentPageId(): string | null {
  const st = useDeck.getState();
  const p = currentProfile(st);
  if (!p) return null;
  for (let i = st.pageStack.length - 1; i >= 0; i--) if (p.pages.some((x) => x.id === st.pageStack[i])) return st.pageStack[i];
  return homePage(p)?.id ?? null;
}
