import { tr } from '../i18n';
// Editor of dynamic keys: source, label template, presets and style rules.
import { useState } from 'react';
import { request } from '../net/client';
import { LucideGlyph } from '../lib/lucide';
import { showToast, useDeck } from '../state/store';
import type { Action, IconStyle, Key, LiveConfig, LiveRule } from '../types';
import { ColorField, Section, Segmented, Toggle } from './controls';
import { AppField, usePcInfo } from './ExtraEditors';
import { IconPicker } from './pickers';
import { LrParamField } from './LightroomEditor';
import { HttpRequestFields, HttpTest, LIVE_TEMPLATES, TemplatePicker, liveHttpDefault } from './HttpEditor';
import { Icon, type IconName } from '../ui/Icon';

type Edit = (fn: (k: Key) => void) => void;

const RED = '#b3261e';
const GREEN = '#1f7a4a';
const TWITCH = '#9146ff';

const lucide = (value: string, color = '#ffffff'): IconStyle => ({
  kind: 'lucide',
  value,
  color,
});
const NO_ICON: IconStyle = { kind: 'none', value: '' };

// ---- Presets ------------------------------------------------------------------------------

interface Preset {
  id: string;
  label: string;
  icon: IconName;
  apply: (k: Key) => void;
}

function base(
  k: Key,
  live: LiveConfig,
  text: string,
  opts: {
    icon?: IconStyle;
    size?: number;
    position?: Key['style']['label']['position'];
    bold?: boolean;
  } = {},
) {
  k.kind = 'dynamic';
  delete k.toggle;
  delete k.folderPageId;
  k.live = live;
  k.style.label = {
    ...k.style.label,
    text,
    size: opts.size ?? 14,
    position: opts.position ?? (opts.icon ? 'bottom' : 'center'),
    bold: opts.bold ?? !opts.icon,
  };
  k.style.icon = opts.icon ?? NO_ICON;
}

const rule = (when: LiveRule['when'], style: LiveRule['style'], value?: string): LiveRule => ({
  when,
  style,
  ...(value !== undefined ? { value } : {}),
});

const press = (k: Key, a: Action, long?: Action) => {
  k.onPress = a;
  if (long) k.onLongPress = long;
};

export const PRESETS: Preset[] = [
  {
    id: 'clock',
    label: tr('Orologio'),
    icon: 'clock',
    apply: (k) =>
      base(k, { source: 'clock', format: 'time', rules: [] }, '{text}', {
        size: 24,
      }),
  },
  {
    id: 'date',
    label: tr('Data'),
    icon: 'calendar',
    apply: (k) =>
      base(k, { source: 'clock', format: 'dayMonth', rules: [] }, '{text}', {
        size: 16,
      }),
  },
  {
    id: 'cpu',
    label: 'CPU',
    icon: 'cpu',
    apply: (k) => base(k, { source: 'cpu', rules: [rule('gt', { bgColor: RED }, '80')] }, 'CPU\n{text}', { size: 16 }),
  },
  {
    id: 'ram',
    label: 'RAM',
    icon: 'memory-stick',
    apply: (k) => base(k, { source: 'ram', rules: [rule('gt', { bgColor: RED }, '85')] }, 'RAM\n{text}', { size: 16 }),
  },
  {
    id: 'mic',
    label: tr('Microfono'),
    icon: 'mic',
    apply: (k) => {
      base(
        k,
        {
          source: 'mic',
          rules: [rule('true', { bgColor: RED, icon: lucide('MicOff') })],
        },
        '{text}',
        { icon: lucide('Mic') },
      );
      press(k, { type: 'media', command: 'micMute', mode: 'toggle' });
    },
  },
  {
    id: 'vol',
    label: 'Volume',
    icon: 'volume-2',
    apply: (k) => {
      base(
        k,
        {
          source: 'vol',
          rules: [rule('true', { bgColor: RED, icon: lucide('VolumeX') })],
        },
        '{text}',
        { icon: lucide('Volume2') },
      );
      press(k, { type: 'media', command: 'mute' });
    },
  },
  {
    id: 'appvol',
    label: tr('Volume app'),
    icon: 'app-window',
    apply: (k) => {
      base(
        k,
        {
          source: 'appvol:spotify',
          rules: [rule('true', { bgColor: RED, icon: lucide('VolumeX') })],
        },
        'spotify\n{text}',
        {
          icon: lucide('Music'),
        },
      );
      press(k, { type: 'media', command: 'appMute', app: 'spotify' });
    },
  },
  {
    id: 'output',
    label: tr('Uscita audio'),
    icon: 'headphones',
    apply: (k) => {
      base(k, { source: 'audio.out', rules: [] }, '{text}', {
        icon: lucide('Headphones'),
        size: 11,
      });
      press(k, { type: 'media', command: 'output', devices: [] });
    },
  },
  {
    id: 'weather',
    label: tr('Meteo'),
    icon: 'cloud-sun',
    apply: (k) =>
      base(k, { source: 'weather:', sourceIcon: true, rules: [] }, '{text}', {
        icon: { kind: 'emoji', value: '🌡️' },
        size: 18,
        bold: true,
      }),
  },
  {
    id: 'countdown',
    label: 'Timer',
    icon: 'hourglass',
    apply: (k) => makeTimerKey(k, 'countdown'),
  },
  {
    id: 'stopwatch',
    label: tr('Cronometro'),
    icon: 'timer',
    apply: (k) => makeTimerKey(k, 'stopwatch'),
  },
  {
    id: 'counter',
    label: tr('Contatore'),
    icon: 'hash',
    apply: (k) => {
      base(k, { source: 'counter:Contatore', rules: [] }, '{text}', {
        size: 28,
      });
      press(k, { type: 'counter', name: tr('Contatore'), op: 'inc', step: 1 }, { type: 'counter', name: tr('Contatore'), op: 'reset' });
    },
  },
  {
    id: 'obs.stream',
    label: tr('OBS diretta'),
    icon: 'radio',
    apply: (k) => {
      base(k, { source: 'obs.stream', rules: [rule('true', { bgColor: RED })] }, '{text}', { icon: lucide('Radio') });
      press(k, { type: 'obs', command: 'streamToggle' });
    },
  },
  {
    id: 'obs.rec',
    label: tr('OBS registrazione'),
    icon: 'circle-dot',
    apply: (k) => {
      base(k, { source: 'obs.rec', rules: [rule('true', { bgColor: RED })] }, '{text}', { icon: lucide('Circle') });
      press(k, { type: 'obs', command: 'recordToggle' });
    },
  },
  {
    id: 'lr.exposure',
    label: tr('Lightroom esposizione'),
    icon: 'aperture',
    apply: (k) => {
      base(k, { source: 'lr.param:Exposure', rules: [] }, '{sub}\n{text}', { size: 14 });
      press(
        k,
        { type: 'lightroom', command: 'inc', param: 'Exposure', step: 0.05 },
        { type: 'lightroom', command: 'reset', param: 'Exposure' },
      );
    },
  },
  {
    id: 'lr.rating',
    label: tr('Lightroom stelle'),
    icon: 'star',
    apply: (k) => base(k, { source: 'lr.rating', rules: [] }, '{text}', { size: 14 }),
  },
  {
    id: 'lr.flag',
    label: tr('Lightroom bandierina'),
    icon: 'flag',
    apply: (k) => {
      base(k, { source: 'lr.flag', rules: [rule('true', { bgColor: GREEN }), rule('lt', { bgColor: RED }, '0')] }, '{text}', {
        icon: lucide('Flag'),
        size: 12,
      });
      press(k, { type: 'lightroom', command: 'flag', flag: 'pick' }, { type: 'lightroom', command: 'flag', flag: 'none' });
    },
  },
  {
    id: 'discord.mute',
    label: tr('Discord muto'),
    icon: 'mic-off',
    apply: (k) => {
      base(k, { source: 'discord.mute', rules: [rule('true', { bgColor: RED, icon: lucide('MicOff') })] }, '{text}', {
        icon: lucide('Mic'),
      });
      press(k, { type: 'discord', command: 'toggleMute' });
    },
  },
  {
    id: 'discord.deaf',
    label: tr('Discord silenzia'),
    icon: 'headphone-off',
    apply: (k) => {
      base(k, { source: 'discord.deaf', rules: [rule('true', { bgColor: RED, icon: lucide('HeadphoneOff') })] }, '{text}', {
        icon: lucide('Headphones'),
      });
      press(k, { type: 'discord', command: 'toggleDeaf' });
    },
  },
  {
    id: 'slobs.stream',
    label: tr('Streamlabs diretta'),
    icon: 'radio',
    apply: (k) => {
      base(k, { source: 'slobs.stream', rules: [rule('true', { bgColor: RED })] }, '{text}', { icon: lucide('Radio') });
      press(k, { type: 'obs', app: 'slobs', command: 'streamToggle' });
    },
  },
  {
    id: 'slobs.rec',
    label: tr('Streamlabs registrazione'),
    icon: 'circle-dot',
    apply: (k) => {
      base(k, { source: 'slobs.rec', rules: [rule('true', { bgColor: RED })] }, '{text}', { icon: lucide('Circle') });
      press(k, { type: 'obs', app: 'slobs', command: 'recordToggle' });
    },
  },
  {
    id: 'slobs.scene',
    label: tr('Streamlabs scena'),
    icon: 'clapperboard',
    apply: (k) => base(k, { source: 'slobs.scene', rules: [] }, '{text}', { icon: lucide('Clapperboard'), size: 12 }),
  },
  {
    id: 'streamlabs.donation',
    label: tr('Ultima donazione'),
    icon: 'hand-coins',
    apply: (k) => base(k, { source: 'streamlabs.last:donation', rules: [] }, tr('{text}\n{sub}'), { size: 13 }),
  },
  {
    id: 'streamlabs.total',
    label: tr('Totale donazioni'),
    icon: 'coins',
    apply: (k) => {
      base(k, { source: 'streamlabs.total', rules: [] }, tr('Donazioni\n{text}'), { size: 14 });
      // Long press resets the total (a tap does nothing, so it is never reset by mistake).
      k.onLongPress = { type: 'streamlabs', command: 'resetTotal' };
    },
  },
  {
    id: 'twitch.live',
    label: tr('Twitch in diretta'),
    icon: 'radio',
    apply: (k) => base(k, { source: 'twitch.live', rules: [rule('true', { bgColor: TWITCH })] }, '{text}', { icon: lucide('Radio') }),
  },
  {
    id: 'twitch.viewers',
    label: tr('Spettatori'),
    icon: 'eye',
    apply: (k) => base(k, { source: 'twitch.viewers', rules: [] }, '{text}', { icon: lucide('Eye'), size: 18 }),
  },
  {
    id: 'twitch.followers',
    label: 'Follower',
    icon: 'heart',
    apply: (k) => base(k, { source: 'twitch.followers', rules: [] }, '{text}', { icon: lucide('Heart'), size: 18 }),
  },
  {
    id: 'twitch.lastfollower',
    label: tr('Ultimo follower'),
    icon: 'user-plus',
    apply: (k) => base(k, { source: 'twitch.last:follower', rules: [] }, 'Follower\n{text}', { size: 13 }),
  },
  {
    id: 'twitch.uptime',
    label: tr('Durata diretta'),
    icon: 'timer',
    apply: (k) =>
      base(k, { source: 'twitch.uptime', rules: [rule('true', { bgColor: TWITCH })] }, '{text}', { icon: lucide('Clock'), size: 18 }),
  },
  {
    id: 'twitch.clip',
    label: tr('Clip Twitch'),
    icon: 'scissors',
    apply: (k) => {
      base(k, { source: 'twitch.last:clip', rules: [] }, 'Clip', { icon: lucide('Scissors') });
      press(k, { type: 'twitch', command: 'clip' });
    },
  },
  {
    id: 'twitch.slow',
    label: 'Slow mode',
    icon: 'snail',
    apply: (k) => {
      base(k, { source: 'twitch.chat:slow', rules: [rule('true', { bgColor: TWITCH })] }, 'Slow\n{text}', { icon: lucide('Snail') });
      press(k, { type: 'twitch', command: 'chatMode', mode: 'slow', set: 'toggle', seconds: 30 });
    },
  },
  {
    id: 'twitch.subonly',
    label: tr('Solo sub'),
    icon: 'star',
    apply: (k) => {
      base(k, { source: 'twitch.chat:sub', rules: [rule('true', { bgColor: TWITCH })] }, tr('Solo sub\n{text}'), { icon: lucide('Star') });
      press(k, { type: 'twitch', command: 'chatMode', mode: 'sub', set: 'toggle' });
    },
  },
  {
    id: 'obs.scene',
    label: tr('OBS scena'),
    icon: 'clapperboard',
    apply: (k) =>
      base(k, { source: 'obs.scene', rules: [] }, '{text}', {
        icon: lucide('Clapperboard'),
        size: 12,
      }),
  },
];

/** For an action key: the preset that would show the state of its action, if any. */
export function presetForAction(a: Action | null): string | null {
  if (!a) return null;
  if (a.type === 'media') {
    if (a.command === 'micMute') return 'mic';
    if (a.command === 'mute') return 'vol';
    if (a.command === 'appMute' || a.command === 'appVolUp' || a.command === 'appVolDown') return 'appvol';
    if (a.command === 'output') return 'output';
  }
  if (a.type === 'obs' && (a.command === 'streamToggle' || a.command === 'streamStart' || a.command === 'streamStop'))
    return a.app === 'slobs' ? 'slobs.stream' : 'obs.stream';
  if (a.type === 'obs' && a.command === 'recordToggle') return a.app === 'slobs' ? 'slobs.rec' : 'obs.rec';
  if (a.type === 'twitch' && a.command === 'chatMode')
    return a.mode === 'slow' ? 'twitch.slow' : a.mode === 'sub' ? 'twitch.subonly' : null;
  if (a.type === 'twitch' && a.command === 'clip') return 'twitch.clip';
  if (a.type === 'timer') return a.mode === 'stopwatch' ? 'stopwatch' : 'countdown';
  if (a.type === 'counter') return 'counter';
  if (a.type === 'discord' && (a.command === 'toggleMute' || a.command === 'mute' || a.command === 'unmute')) return 'discord.mute';
  if (a.type === 'discord' && (a.command === 'toggleDeaf' || a.command === 'deaf' || a.command === 'undeaf')) return 'discord.deaf';
  return null;
}

/** Turns an action key into a dynamic one that shows the state of its own action. */
export function applyPresetKeepingAction(k: Key, presetId: string) {
  const onPress = k.onPress;
  const onLong = k.onLongPress;
  const label = k.style.label.text;
  PRESETS.find((p) => p.id === presetId)?.apply(k);
  if (onPress) k.onPress = onPress;
  if (onLong) k.onLongPress = onLong;
  if (!k.live) return;
  // Follow the action's target (app / counter / timer name).
  if (onPress?.type === 'media' && onPress.app) {
    k.live.source = `appvol:${onPress.app}`;
    k.style.label.text = `${label.trim() || onPress.app}\n{text}`;
  }
  if (onPress?.type === 'counter' || onPress?.type === 'timer') {
    k.live.source = `${onPress.type}:${onPress.name}`;
    // The preset's long-press reset must target the same counter/timer.
    const lp = k.onLongPress;
    if (!onLong && lp && lp.type === onPress.type) lp.name = onPress.name;
  }
}

/**
 * Called after the press action of a key changed. Counter and timer actions do nothing visible on a plain
 * action key, so the key becomes a dynamic one showing that counter/timer; a dynamic key that already showed
 * the old counter/timer follows its new name. Returns true if the key was turned into a dynamic one.
 */
export function syncLiveWithAction(k: Key, prev: Action | null): boolean {
  const a = k.onPress;
  if (a?.type !== 'counter' && a?.type !== 'timer') return false;
  // A newly chosen timer action gets its own timer, never the default shared name.
  if (a.type === 'timer' && prev?.type !== 'timer') {
    a.name = timerName(k);
    if (k.onLongPress?.type === 'timer') k.onLongPress.name = a.name;
  }
  const preset = presetForAction(a)!;
  if (k.kind === 'action') {
    if (prev?.type === a.type) return false; // the user turned the state off on purpose: respect it
    applyPresetKeepingAction(k, preset);
    return true;
  }
  if (k.kind !== 'dynamic' || !k.live) return false;
  const followed = prev && (prev.type === 'counter' || prev.type === 'timer') && k.live.source === `${prev.type}:${prev.name}`;
  if (!followed) return false;
  if (prev.type !== a.type || (a.type === 'timer' && prev.type === 'timer' && (prev.mode ?? 'countdown') !== (a.mode ?? 'countdown'))) {
    applyPresetKeepingAction(k, preset);
  } else k.live.source = `${a.type}:${a.name}`;
  // Keep a long-press reset pointed at the same counter/timer.
  const lp = k.onLongPress;
  if (lp && lp.type === prev.type && lp.name === prev.name && lp.type === a.type) lp.name = a.name;
  return false;
}

// ---- Timer keys ---------------------------------------------------------------------------

/**
 * Timers are shared on the PC by name, so every timer key gets its own name (the key id): two timer
 * keys never start together unless the user links them on purpose.
 */
export const timerName = (k: Key) => k.id;

/** A timer key: a dynamic key showing the timer that its own press action starts/pauses. */
export function isTimerKey(k: Key): boolean {
  const a = k.onPress;
  return k.kind === 'dynamic' && a?.type === 'timer' && k.live?.source === `timer:${a.name}`;
}

export const TIMER_RUNNING_COLOR = GREEN;
export const TIMER_DONE_COLOR = RED;

/** Turns a key into a timer key (countdown or stopwatch) with its own, unshared timer. */
export function makeTimerKey(k: Key, mode: 'countdown' | 'stopwatch', durationSec = 300) {
  const name = timerName(k);
  base(
    k,
    {
      source: `timer:${name}`,
      rules:
        mode === 'countdown'
          ? [rule('true', { bgColor: TIMER_RUNNING_COLOR }), rule('eq', { bgColor: TIMER_DONE_COLOR, labelText: tr('FINE') }, '0')]
          : [rule('true', { bgColor: TIMER_RUNNING_COLOR })],
    },
    '{text}',
    { size: 22 },
  );
  k.onPress = { type: 'timer', name, op: 'toggle', mode, ...(mode === 'countdown' ? { durationSec } : {}) };
  k.onLongPress = { type: 'timer', name, op: 'reset' };
}

/** Points every reference to timer `from` in a key (actions, shown source) to timer `to`. */
export function renameTimerInKey(k: Key, from: string, to: string) {
  for (const a of [k.onPress, k.onLongPress, k.onDoubleTap, k.toggle?.onAction, k.toggle?.offAction])
    if (a?.type === 'timer' && a.name === from) a.name = to;
  if (k.live?.source === `timer:${from}`) k.live.source = `timer:${to}`;
}

/** A copied/duplicated timer key gets its own timer (named after its new id), never the original's. */
export function retargetCopiedTimer(k: Key) {
  if (isTimerKey(k)) renameTimerInKey(k, (k.onPress as { name: string }).name, k.id);
  // HTTP dynamic keys: the source is named after the key, a copy polls on its own.
  if (k.live?.source.startsWith('http:')) k.live.source = `http:${k.id}`;
}

// ---- Sources ------------------------------------------------------------------------------

type SourceKind =
  | 'clock'
  | 'cpu'
  | 'ram'
  | 'vol'
  | 'mic'
  | 'audio.out'
  | 'app.fg'
  | 'appvol'
  | 'weather'
  | 'counter'
  | 'timer'
  | 'obs.stream'
  | 'obs.rec'
  | 'obs.scene'
  | 'obs.conn'
  | 'obs.mute'
  | 'slobs.stream'
  | 'slobs.rec'
  | 'slobs.replay'
  | 'slobs.scene'
  | 'slobs.mute'
  | 'slobs.conn'
  | 'streamlabs.last'
  | 'streamlabs.total'
  | 'streamlabs.conn'
  | 'http'
  | 'lr.param'
  | 'lr.rating'
  | 'lr.flag'
  | 'lr.label'
  | 'lr.module'
  | 'lr.conn'
  | 'twitch.live'
  | 'twitch.viewers'
  | 'twitch.followers'
  | 'twitch.subs'
  | 'twitch.uptime'
  | 'twitch.title'
  | 'twitch.last'
  | 'twitch.chat'
  | 'twitch.conn'
  | 'discord.mute'
  | 'discord.deaf'
  | 'discord.ptt'
  | 'discord.invol'
  | 'discord.outvol'
  | 'discord.channel'
  | 'discord.conn'
  | 'plugin';

function kindOf(source: string): SourceKind {
  const i = source.indexOf(':');
  return (i < 0 ? source : source.slice(0, i)) as SourceKind;
}

const SOURCE_GROUPS: [string, [SourceKind, string][]][] = [
  [
    tr('Tempo'),
    [
      ['clock', tr('Orologio / data')],
      ['timer', tr('Timer / cronometro')],
      ['counter', tr('Contatore')],
    ],
  ],
  [
    'PC',
    [
      ['cpu', tr('Uso CPU')],
      ['ram', tr('Uso RAM')],
      ['app.fg', tr('App in primo piano')],
    ],
  ],
  [
    'Audio',
    [
      ['vol', tr('Volume generale')],
      ['mic', tr('Microfono (muto / attivo)')],
      ['appvol', tr("Volume di un'app")],
      ['audio.out', tr('Dispositivo di uscita')],
    ],
  ],
  [
    'Internet',
    [
      ['weather', tr('Meteo')],
      ['http', tr('Richiesta HTTP (valore da un JSON, stato di un sito…)')],
    ],
  ],
  [
    'OBS Studio',
    [
      ['obs.stream', tr('Diretta')],
      ['obs.rec', tr('Registrazione')],
      ['obs.scene', tr('Scena attuale')],
      ['obs.mute', tr('Muto di una sorgente')],
      ['obs.conn', tr('Connessione a OBS')],
    ],
  ],
  [
    'Lightroom Classic',
    [
      ['lr.param', tr('Valore di un parametro (Sviluppo)')],
      ['lr.rating', tr('Stelle della foto')],
      ['lr.flag', tr('Bandierina della foto')],
      ['lr.label', tr('Etichetta colore')],
      ['lr.module', tr('Modulo attivo')],
      ['lr.conn', tr('Connessione a Lightroom')],
    ],
  ],
  [
    'Streamlabs Desktop',
    [
      ['slobs.stream', tr('Diretta')],
      ['slobs.rec', tr('Registrazione')],
      ['slobs.replay', 'Replay buffer'],
      ['slobs.scene', tr('Scena attuale')],
      ['slobs.mute', tr('Muto di una sorgente')],
      ['slobs.conn', tr('Connessione a Streamlabs Desktop')],
    ],
  ],
  [
    tr('Streamlabs (account)'),
    [
      ['streamlabs.last', tr('Ultima donazione / ultimo evento')],
      ['streamlabs.total', tr('Totale donazioni (da quando PadNook è aperto)')],
      ['streamlabs.conn', tr('Connessione agli eventi')],
    ],
  ],
  [
    'Twitch',
    [
      ['twitch.live', tr('In diretta')],
      ['twitch.viewers', tr('Spettatori')],
      ['twitch.followers', tr('Follower totali')],
      ['twitch.subs', tr('Abbonati totali')],
      ['twitch.uptime', tr('Durata della diretta')],
      ['twitch.title', tr('Titolo e categoria')],
      ['twitch.last', tr('Ultimo follower / sub / raid…')],
      ['twitch.chat', tr('Modalità chat')],
      ['twitch.conn', tr('Connessione a Twitch')],
    ],
  ],
  [
    'Discord',
    [
      ['discord.mute', tr('Microfono in muto')],
      ['discord.deaf', tr('Audio silenziato')],
      ['discord.ptt', 'Push-to-talk'],
      ['discord.invol', tr('Volume del microfono')],
      ['discord.outvol', tr('Volume in uscita')],
      ['discord.channel', tr('Canale vocale attuale')],
      ['discord.conn', tr('Connessione a Discord')],
    ],
  ],
  [tr('Altro'), [['plugin', tr('Sorgente di un plugin')]]],
];

/** What "on/true" means for each source, for the rules UI. */
const STATE_MEANING: Partial<Record<SourceKind, string>> = {
  vol: tr('audio in muto'),
  mic: tr('microfono silenziato'),
  appvol: tr('app in muto'),
  timer: tr('in corso'),
  'obs.stream': tr('in diretta'),
  'obs.rec': tr('in registrazione'),
  'obs.mute': tr('sorgente in muto'),
  'obs.conn': tr('OBS connesso'),
  http: tr('valore vero / risposta OK'),
  'lr.flag': tr('foto scelta'),
  'lr.conn': tr('Lightroom connesso'),
  'slobs.stream': tr('in diretta'),
  'slobs.rec': tr('in registrazione'),
  'slobs.replay': tr('replay attivo'),
  'slobs.mute': tr('sorgente in muto'),
  'slobs.conn': tr('Streamlabs connesso'),
  'streamlabs.conn': tr('eventi collegati'),
  'twitch.live': tr('in diretta'),
  'twitch.uptime': tr('in diretta'),
  'twitch.chat': tr('modalità attiva'),
  'twitch.conn': tr('Twitch connesso'),
  'discord.mute': tr('in muto'),
  'discord.deaf': tr('silenziato'),
  'discord.ptt': tr('push-to-talk attivo'),
  'discord.channel': tr('in un canale'),
  'discord.conn': tr('Discord collegato'),
};

function defaultSource(kind: SourceKind): string {
  switch (kind) {
    case 'appvol':
      return 'appvol:spotify';
    case 'weather':
      return 'weather:';
    case 'counter':
      return 'counter:Contatore';
    case 'obs.mute':
      return 'obs.mute:Mic/Aux';
    case 'slobs.mute':
      return 'slobs.mute:Mic/Aux';
    case 'lr.param':
      return 'lr.param:Exposure';
    case 'streamlabs.last':
      return 'streamlabs.last:donation';
    case 'twitch.last':
      return 'twitch.last:follower';
    case 'twitch.chat':
      return 'twitch.chat:slow';
    case 'plugin': {
      const p = useDeck.getState().plugins.find((x) => x.sources.length);
      return p ? `plugin:${p.id}/${p.sources[0].id}` : 'plugin:';
    }
    default:
      return kind;
  }
}

// ---- Editor -------------------------------------------------------------------------------

export function LiveSections({ k, edit }: { k: Key; edit: Edit }) {
  const live = k.live ?? { source: 'clock', rules: [] };
  const kind = kindOf(live.source);
  const editLive = (fn: (l: LiveConfig) => void) =>
    edit((x) => {
      x.live ??= { source: 'clock', rules: [] };
      fn(x.live);
    });
  const value = useDeck((s) => s.live[live.source]);

  return (
    <>
      <Section title={tr('Modelli pronti')}>
        <p className="muted small hint">{tr('Imposta sorgente, testo, colori e azione in un tocco; poi puoi personalizzare tutto.')}</p>
        <div className="preset-grid">
          {PRESETS.map((p) => (
            <button key={p.id} type="button" className="btn" onClick={() => edit(p.apply)}>
              <Icon name={p.icon} />
              {p.label}
            </button>
          ))}
        </div>
      </Section>

      <Section title={tr('Sorgente dei dati')}>
        <label className="field">
          <span>{tr('Mostra')}</span>
          <select
            className="input"
            value={kind}
            onChange={(e) => {
              const next = e.target.value as SourceKind;
              if (next === 'timer') edit((x) => makeTimerKey(x, 'countdown'));
              else if (next === 'http')
                edit((x) => {
                  x.live ??= { source: '', rules: [] };
                  x.live.source = `http:${x.id}`;
                  x.live.http ??= liveHttpDefault();
                });
              else editLive((l) => (l.source = defaultSource(next)));
            }}
          >
            {SOURCE_GROUPS.map(([g, opts]) => (
              <optgroup key={g} label={g}>
                {opts.map(([v, l]) => (
                  <option key={v} value={v}>
                    {l}
                  </option>
                ))}
              </optgroup>
            ))}
          </select>
        </label>
        {kind === 'http' ? (
          <HttpSource k={k} live={live} edit={edit} editLive={editLive} />
        ) : (
          <SourceParams live={live} kind={kind} editLive={editLive} />
        )}
        {(kind === 'weather' || kind === 'plugin') && (
          <Toggle
            label={tr("Usa l'icona fornita dalla sorgente")}
            checked={!!live.sourceIcon}
            onChange={(v) => editLive((l) => (l.sourceIcon = v))}
          />
        )}
        <p className="muted small hint">
          {tr(
            'Nel testo del tasto (scheda Aspetto) usa {0} per il valore, {1} per il numero e{2} {3} per il dettaglio',
            <code>{'{text}'}</code>,
            <code>{'{value}'}</code>,
            ' ',
            <code>{'{sub}'}</code>,
          )}
          {kind === 'weather' && (
            <>
              {tr(
                '; per il meteo anche {0} (min/max), {1}, {2},{3} {4}',
                <code>{'{range}'}</code>,
                <code>{'{humidity}'}</code>,
                <code>{'{feels}'}</code>,
                ' ',
                <code>{'{wind}'}</code>,
              )}
            </>
          )}
          {tr('. Vai a capo per scrivere su più righe.')}
        </p>
        {kind !== 'clock' && (
          <p className="muted small">{tr('Valore attuale: {0} {1}', <b>{value?.text ?? '—'}</b>, value?.sub ? ` · ${value.sub}` : '')}</p>
        )}
      </Section>

      <RulesSection live={live} kind={kind} editLive={editLive} />
    </>
  );
}

function SourceParams({ live, kind, editLive }: { live: LiveConfig; kind: SourceKind; editLive: (fn: (l: LiveConfig) => void) => void }) {
  const plugins = useDeck((s) => s.plugins);
  const info = usePcInfo();
  const arg = live.source.includes(':') ? live.source.slice(live.source.indexOf(':') + 1) : '';
  const setArg = (v: string) => editLive((l) => (l.source = `${kind}:${v}`));

  switch (kind) {
    case 'clock':
      return (
        <label className="field">
          <span>{tr('Formato')}</span>
          <select className="input" value={live.format ?? 'time'} onChange={(e) => editLive((l) => (l.format = e.target.value))}>
            <option value="time">{tr('Ora (14:05)')}</option>
            <option value="timeSec">{tr('Ora con secondi (14:05:09)')}</option>
            <option value="dateTime">{tr('Ora e data')}</option>
            <option value="date">{tr('Data (17/11/2026)')}</option>
            <option value="dayMonth">{tr('Giorno e mese (17 novembre)')}</option>
            <option value="weekday">{tr('Giorno della settimana')}</option>
          </select>
        </label>
      );
    case 'appvol':
      return <AppField label={tr('App (nome del processo)')} value={arg} source="sessions" onChange={setArg} />;
    case 'counter':
      return (
        <>
          <label className="field">
            <span>{tr('Nome del contatore')}</span>
            <input className="input" value={arg} onChange={(e) => setArg(e.target.value)} />
          </label>
          <p className="muted small hint">
            {tr("Usa lo stesso nome nell'azione «Contatore» del tasto (o di altri tasti) per comandarlo.")}
          </p>
        </>
      );
    case 'lr.param':
      return <LrParamField value={arg as never} onChange={(p) => setArg(p)} />;
    case 'streamlabs.last':
      return (
        <label className="field">
          <span>{tr('Ultimo')}</span>
          <select className="input" value={arg} onChange={(e) => setArg(e.target.value)}>
            <option value="donation">{tr('Donazione (o Super Chat)')}</option>
            <option value="event">{tr('Evento qualsiasi')}</option>
          </select>
        </label>
      );
    case 'obs.mute':
    case 'slobs.mute':
      return (
        <label className="field">
          <span>{tr('Sorgente audio di {0}', kind === 'slobs.mute' ? 'Streamlabs Desktop' : 'OBS')}</span>
          <input className="input" list="obs-inputs" value={arg} onChange={(e) => setArg(e.target.value)} />
          <datalist id="obs-inputs">
            {((kind === 'slobs.mute' ? info?.slobs : info?.obs)?.inputs ?? []).map((s) => (
              <option key={s} value={s} />
            ))}
          </datalist>
        </label>
      );
    case 'weather':
      return <WeatherPlace live={live} editLive={editLive} />;
    case 'twitch.last':
      return (
        <label className="field">
          <span>{tr('Ultimo')}</span>
          <select className="input" value={arg} onChange={(e) => setArg(e.target.value)}>
            <option value="follower">Follower</option>
            <option value="sub">{tr('Abbonamento (anche regalati e rinnovi)')}</option>
            <option value="raid">Raid</option>
            <option value="cheer">Bits</option>
            <option value="redeem">{tr('Riscatto punti canale')}</option>
            <option value="clip">{tr('Clip creata')}</option>
          </select>
        </label>
      );
    case 'twitch.chat':
      return (
        <label className="field">
          <span>{tr('Modalità')}</span>
          <select className="input" value={arg} onChange={(e) => setArg(e.target.value)}>
            <option value="slow">Slow mode</option>
            <option value="follower">{tr('Solo follower')}</option>
            <option value="sub">{tr('Solo abbonati')}</option>
            <option value="emote">{tr('Solo emote')}</option>
          </select>
        </label>
      );
    case 'plugin': {
      const options = plugins.flatMap((p) =>
        p.sources.map((s) => ({
          value: `${p.id}/${s.id}`,
          label: `${p.name}: ${s.name}`,
        })),
      );
      if (options.length === 0) return <p className="muted small hint">{tr('Nessun plugin con sorgenti installato.')}</p>;
      return (
        <label className="field">
          <span>{tr('Sorgente')}</span>
          <select className="input" value={arg} onChange={(e) => setArg(e.target.value)}>
            {!options.some((o) => o.value === arg) && <option value={arg}>{arg || tr('— scegli —')}</option>}
            {options.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </select>
        </label>
      );
    }
    default:
      return null;
  }
}

function HttpSource({
  k,
  live,
  edit,
  editLive,
}: {
  k: Key;
  live: LiveConfig;
  edit: Edit;
  editLive: (fn: (l: LiveConfig) => void) => void;
}) {
  const http = live.http ?? liveHttpDefault();
  const set = (next: typeof http) => editLive((l) => (l.http = next));
  return (
    <>
      <TemplatePicker
        templates={LIVE_TEMPLATES}
        onApply={(t, v) => {
          const r = t.build(v);
          edit((x) => {
            base(x, { source: `http:${x.id}`, http: r.http, rules: r.rules }, r.label, { size: 16 });
            if (r.onPress) x.onPress = r.onPress;
          });
        }}
      />
      <HttpRequestFields value={http} onChange={set} />
      <div className="field-row">
        <span>{tr('Aggiorna ogni (secondi)')}</span>
        <input
          className="input num"
          type="number"
          min={5}
          value={http.intervalSec}
          onChange={(e) => set({ ...http, intervalSec: Math.max(5, Number(e.target.value) || 30) })}
        />
      </div>
      <label className="field">
        <span>{tr('Percorso del valore nel JSON (vuoto = tutta la risposta)')}</span>
        <input
          className="input mono"
          placeholder="current.temperature · items[0].name · state"
          value={http.path ?? ''}
          spellCheck={false}
          onChange={(e) => set({ ...http, path: e.target.value })}
        />
      </label>
      <HttpTest spec={http} path={http.path ?? ''} />
      <p className="muted small hint">
        {tr(
          'La richiesta parte dal PC, solo mentre un dispositivo è collegato. Sul tasto: {0} = valore estratto,{1} {2} = codice HTTP.{3} {4}',
          <code>{'{text}'}</code>,
          ' ',
          <code>{'{sub}'}</code>,
          ' ',
          k.onPress ? '' : tr("Puoi anche dare al tasto un'azione (es. accendi lo Shelly) nella scheda Azione."),
        )}
      </p>
    </>
  );
}

function WeatherPlace({ live, editLive }: { live: LiveConfig; editLive: (fn: (l: LiveConfig) => void) => void }) {
  const [q, setQ] = useState('');
  const [busy, setBusy] = useState(false);
  const [results, setResults] = useState<{ name: string; region: string; lat: number; lon: number }[]>([]);
  const search = async () => {
    setBusy(true);
    try {
      const m = await request({ t: 'edit.weather.search', q });
      setResults(m.results ?? []);
      if (!m.results?.length) showToast(tr('Nessuna località trovata'), 'error');
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="field">
      <span className="muted small">
        {tr('Località:')} {live.place ? <b>{live.place}</b> : <span className="error-text">{tr('da scegliere')}</span>}
      </span>
      <form
        className="inline-row"
        onSubmit={(e) => {
          e.preventDefault();
          if (q.trim().length >= 2) void search();
        }}
      >
        <input className="input" placeholder={tr('Cerca una città…')} value={q} onChange={(e) => setQ(e.target.value)} />
        <button className="btn" type="submit" disabled={busy || q.trim().length < 2}>
          {busy ? '…' : tr('Cerca')}
        </button>
      </form>
      {results.length > 0 && (
        <div className="list">
          {results.map((r) => (
            <button
              key={`${r.lat},${r.lon}`}
              type="button"
              className="list-item"
              onClick={() => {
                editLive((l) => {
                  l.source = `weather:${r.lat},${r.lon}`;
                  l.place = r.name;
                });
                setResults([]);
              }}
            >
              <span>
                {r.name} <span className="muted small">{r.region}</span>
              </span>
            </button>
          ))}
        </div>
      )}
      <p className="muted small hint">
        {tr(
          'Dati meteo di{0} {1}{2} (CC BY 4.0), aggiornati ogni 15 minuti dal PC (serve Internet sul PC).',
          ' ',
          <a href="https://open-meteo.com/" target="_blank" rel="noreferrer">
            Open-Meteo.com
          </a>,
          ' ',
        )}
      </p>
    </div>
  );
}

// ---- Rules --------------------------------------------------------------------------------

function RulesSection({ live, kind, editLive }: { live: LiveConfig; kind: SourceKind; editLive: (fn: (l: LiveConfig) => void) => void }) {
  const meaning = STATE_MEANING[kind];
  const whenOptions: [LiveRule['when'], string][] = [
    ...(meaning
      ? ([
          ['true', tr('quando: {0}', meaning)],
          ['false', tr('quando: non {0}', meaning)],
        ] as [LiveRule['when'], string][])
      : []),
    ['gt', tr('valore maggiore di')],
    ['lt', tr('valore minore di')],
    ['eq', tr('valore uguale a')],
    ['contains', tr('testo contiene')],
    ...(kind === 'http' ? ([['status', tr('codice HTTP (200, 2xx, 4xx… · 0 = nessuna risposta)')]] as [LiveRule['when'], string][]) : []),
  ];
  const editRule = (i: number, fn: (r: LiveRule) => void) => editLive((l) => fn(l.rules[i]));
  return (
    <Section title={tr('Cambia aspetto in base allo stato')}>
      <p className="muted small hint">
        {tr('La prima regola vera viene applicata. Es.: rosso quando sei in diretta, icona barrata quando il microfono è muto.')}
      </p>
      {live.rules.map((r, i) => (
        <RuleEditor
          key={i}
          rule={r}
          options={whenOptions}
          onChange={(fn) => editRule(i, fn)}
          onRemove={() => editLive((l) => l.rules.splice(i, 1))}
          onUp={i > 0 ? () => editLive((l) => ([l.rules[i - 1], l.rules[i]] = [l.rules[i], l.rules[i - 1]])) : undefined}
        />
      ))}
      <button
        type="button"
        className="btn"
        onClick={() =>
          editLive((l) =>
            l.rules.push({
              when: meaning ? 'true' : 'gt',
              value: meaning ? undefined : '50',
              style: { bgColor: RED },
            }),
          )
        }
      >
        {tr('+ Aggiungi regola')}
      </button>
    </Section>
  );
}

function RuleEditor({
  rule: r,
  options,
  onChange,
  onRemove,
  onUp,
}: {
  rule: LiveRule;
  options: [LiveRule['when'], string][];
  onChange: (fn: (r: LiveRule) => void) => void;
  onRemove: () => void;
  onUp?: () => void;
}) {
  const [picking, setPicking] = useState(false);
  const needsValue = r.when !== 'true' && r.when !== 'false';
  const st = r.style;
  return (
    <div className="rule">
      <div className="rule-head">
        <select className="input" value={r.when} onChange={(e) => onChange((x) => (x.when = e.target.value as LiveRule['when']))}>
          {!options.some(([v]) => v === r.when) && <option value={r.when}>{r.when}</option>}
          {options.map(([v, l]) => (
            <option key={v} value={v}>
              {l}
            </option>
          ))}
        </select>
        {onUp && (
          <button type="button" className="btn mini" onClick={onUp} title={tr('Sposta su')}>
            ↑
          </button>
        )}
        <button type="button" className="btn mini" onClick={onRemove} title={tr('Elimina regola')}>
          ✕
        </button>
      </div>
      {needsValue && (
        <input
          className="input"
          placeholder={tr('valore')}
          value={r.value ?? ''}
          onChange={(e) => onChange((x) => (x.value = e.target.value))}
        />
      )}
      <Toggle
        label={tr('Colore di sfondo')}
        checked={st.bgColor != null}
        onChange={(v) => onChange((x) => (v ? (x.style.bgColor = RED) : delete x.style.bgColor))}
      />
      {st.bgColor != null && <ColorField value={st.bgColor} onChange={(c) => onChange((x) => (x.style.bgColor = c))} />}
      <label className="field">
        <span>{tr('Testo diverso (vuoto = invariato)')}</span>
        <input
          className="input"
          value={st.labelText ?? ''}
          onChange={(e) => onChange((x) => (x.style.labelText = e.target.value || undefined))}
        />
      </label>
      <Toggle
        label={tr('Icona diversa')}
        checked={!!st.icon}
        onChange={(v) => onChange((x) => (v ? (x.style.icon = lucide('CircleAlert')) : delete x.style.icon))}
      />
      {st.icon && (
        <>
          <Segmented
            value={st.icon.kind === 'emoji' ? 'emoji' : 'lucide'}
            options={[
              ['emoji', 'Emoji'],
              ['lucide', tr('Icona')],
            ]}
            onChange={(kind) => onChange((x) => (x.style.icon = kind === 'emoji' ? { kind: 'emoji', value: '🔴' } : lucide('CircleAlert')))}
          />
          {st.icon.kind === 'emoji' ? (
            <input
              className="input emoji-input wide"
              value={st.icon.value}
              onChange={(e) =>
                onChange(
                  (x) =>
                    (x.style.icon = {
                      ...x.style.icon!,
                      value: e.target.value,
                    }),
                )
              }
            />
          ) : (
            <div className="lucide-current">
              <span className="lucide-preview">
                <LucideGlyph name={st.icon.value} size={28} color={st.icon.color} />
              </span>
              <button type="button" className="btn" onClick={() => setPicking(true)}>
                {tr('Scegli icona…')}
              </button>
              <input
                type="color"
                value={st.icon.color ?? '#ffffff'}
                onChange={(e) =>
                  onChange(
                    (x) =>
                      (x.style.icon = {
                        ...x.style.icon!,
                        color: e.target.value,
                      }),
                  )
                }
              />
            </div>
          )}
        </>
      )}
      {picking && (
        <IconPicker
          onClose={() => setPicking(false)}
          onPick={(name) => {
            onChange(
              (x) =>
                (x.style.icon = {
                  ...(x.style.icon ?? { color: '#ffffff' }),
                  kind: 'lucide',
                  value: name,
                }),
            );
            setPicking(false);
          }}
        />
      )}
    </div>
  );
}
