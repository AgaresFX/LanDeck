import { create } from 'zustand';
import type { ConnState } from '../net/socket';
import type { DeviceInfo, LiveValue, Orientation, Page, PcInfo, PluginInfo, Profile } from '../types';

export type AuthState = 'unknown' | 'pairing' | 'ok';
export type PinMode = null | 'unlock' | 'set';

export interface DeckState {
  conn: ConnState;
  auth: AuthState;
  pairError: string | null;
  pairBusy: boolean;
  justPaired: boolean;

  deviceId: string | null;
  deviceName: string;
  defaultProfileId: string | null;
  profiles: Profile[];
  rev: number;
  /** keyId -> ON, for toggle keys (shared by all devices). */
  toggles: Record<string, boolean>;
  /** Live source values (dynamic keys); `at` = local receive time, for timers. */
  live: Record<string, LiveValue & { at?: number }>;
  /** keyId -> flash requested by an event rule (n restarts the animation). */
  flashes: Record<string, { color: string; ms: number; n: number }>;
  /** App in front on the PC (for automatic profiles). */
  fgApp: string | null;
  plugins: PluginInfo[];
  pluginErrors: string[];
  /** Editor only: lists from the PC. */
  pcInfo: PcInfo | null;
  /** Profile to return to when the automatic switch ends; null = not auto-switched. */
  autoBaseProfileId: string | null;

  profileId: string | null;
  /** Stack of page ids; empty = profile's first page. Folders push, "back" pops. */
  pageStack: string[];

  hasPin: boolean;
  unlocked: boolean;
  editing: boolean;
  selectedKeyId: string | null;
  /** Editor only: edit the other orientation's grid than the one of this screen. */
  previewOrientation: Orientation | null;
  /** A key was copied (the clipboard itself lives in localStorage). */
  hasClipboard: boolean;
  pinMode: PinMode;
  pinError: string | null;
  devices: DeviceInfo[];

  menuOpen: boolean;
  keepAwake: 'off' | 'waiting' | 'on';
  homeCode: { code: string; expiresAt: number } | null;
  toast: { id: number; text: string; kind: 'info' | 'error' } | null;
}

export const useDeck = create<DeckState>(() => ({
  conn: 'closed',
  auth: 'unknown',
  pairError: null,
  pairBusy: false,
  justPaired: false,
  deviceId: null,
  deviceName: '',
  defaultProfileId: null,
  profiles: [],
  rev: 0,
  toggles: {},
  live: {},
  flashes: {},
  fgApp: null,
  plugins: [],
  pluginErrors: [],
  pcInfo: null,
  autoBaseProfileId: null,
  profileId: null,
  pageStack: [],
  hasPin: false,
  unlocked: false,
  editing: false,
  selectedKeyId: null,
  previewOrientation: null,
  hasClipboard: (() => {
    try {
      return !!localStorage.getItem('padnook.clipboard');
    } catch {
      return false;
    }
  })(),
  pinMode: null,
  pinError: null,
  devices: [],
  menuOpen: false,
  keepAwake: 'off',
  homeCode: null,
  toast: null,
}));

// ---- Selectors ---------------------------------------------------------------------------

export function currentProfile(s: DeckState): Profile | null {
  return s.profiles.find((p) => p.id === s.profileId) ?? s.profiles[0] ?? null;
}

export function homePage(p: Profile): Page | null {
  return p.pages.find((pg) => pg.kind !== 'folder') ?? p.pages[0] ?? null;
}

export function currentPage(s: DeckState): Page | null {
  const p = currentProfile(s);
  if (!p) return null;
  for (let i = s.pageStack.length - 1; i >= 0; i--) {
    const pg = p.pages.find((x) => x.id === s.pageStack[i]);
    if (pg) return pg;
  }
  return homePage(p);
}

export const useCurrentProfile = () => useDeck(currentProfile);
export const useCurrentPage = () => useDeck(currentPage);

// ---- Toast -------------------------------------------------------------------------------

let toastSeq = 0;
let toastTimer: number | undefined;

export function showToast(text: string, kind: 'info' | 'error' = 'info', ms = 2500) {
  const id = ++toastSeq;
  useDeck.setState({ toast: { id, text, kind } });
  clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => {
    if (useDeck.getState().toast?.id === id) useDeck.setState({ toast: null });
  }, ms);
}
