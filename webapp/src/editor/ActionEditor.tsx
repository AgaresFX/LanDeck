import { tr } from '../i18n';
import { useEffect, useState } from 'react';
import { useCurrentProfile, useDeck } from '../state/store';
import type { Action } from '../types';
import { MacroEditor, MouseEditor, mouseDefault } from './MacroEditor';
import { DiscordActionEditor, discordDefault } from './DiscordEditor';
import {
  CounterEditor,
  LaunchIfOpenFields,
  MediaEditor,
  ObsEditor,
  PluginEditor,
  SoundEditor,
  SystemEditor,
  TimerEditor,
  WindowEditor,
  pluginDefault,
  systemDefault,
} from './ExtraEditors';
import { Segmented, Toggle } from './controls';
import { HttpEditor, httpActionDefault } from './HttpEditor';
import { TwitchActionEditor, twitchDefault } from './TwitchEditor';
import { StreamlabsActionEditor, streamlabsDefault } from './StreamlabsEditor';
import { LightroomActionEditor, lightroomDefault } from './LightroomEditor';
import { PathInput } from './PathInput';
import { KEY_GROUPS, KNOWN_KEYS, MODIFIERS, keyLabel } from './keys';

const TYPE_GROUPS: [string, [string, string][]][] = [
  [
    tr('Tastiera e mouse'),
    [
      ['hotkey', tr('Scorciatoia da tastiera')],
      ['text', tr('Scrivi un testo')],
      ['mouse', tr('Mouse (clic, movimento, rotellina)')],
      ['macro', tr('Macro (sequenza di passi)')],
    ],
  ],
  [
    tr('Programmi e sistema'),
    [
      ['launch', tr('Apri programma, file, cartella o URL')],
      ['window', tr('Gestione finestre')],
      ['system', tr('Sistema (blocco, spegnimento, screenshot, luminosità…)')],
      ['command', tr('Comando o script (cmd/PowerShell)')],
    ],
  ],
  [
    'Audio',
    [
      ['media', tr('Multimedia, volume e microfono')],
      ['sound', tr('Soundboard (riproduci un file audio)')],
    ],
  ],
  [
    tr('Contatori e tempo'),
    [
      ['counter', tr('Contatore')],
      ['timer', tr('Timer / cronometro')],
    ],
  ],
  [
    tr('App e servizi'),
    [
      ['obs', 'OBS Studio / Streamlabs Desktop'],
      ['streamlabs', tr('Streamlabs (alert personalizzato)')],
      ['twitch', tr('Twitch (clip, marker, chat, raid, sondaggi…)')],
      ['discord', tr('Discord (muto, silenzia, volume, canali vocali)')],
      ['lightroom', tr('Lightroom Classic (sviluppo, stelle, bandierine…)')],
      ['http', tr('Richiesta HTTP / webhook (Discord, Telegram, Home Assistant…)')],
      ['plugin', 'Plugin'],
    ],
  ],
  ['PadNook', [['navigate', tr('Navigazione (pagine e profili)')]]],
];

export const typeLabel = (type: string) =>
  TYPE_GROUPS.flatMap(([, items]) => items)
    .find(([v]) => v === type)?.[1]
    .replace(/ \(.*\)$/, '') ?? type;

// Inside a macro: no nested macros, and navigation runs only in the webapp.
const NESTED_EXCLUDED = new Set(['macro', 'navigate']);

function defaultFor(type: string): Action | null {
  switch (type) {
    case 'hotkey':
      return { type: 'hotkey', keys: [] };
    case 'launch':
      return { type: 'launch', target: '' };
    case 'media':
      return { type: 'media', command: 'playPause' };
    case 'navigate':
      return { type: 'navigate', target: 'back' };
    case 'text':
      return { type: 'text', text: '' };
    case 'command':
      return { type: 'command', shell: 'cmd', command: '', hidden: true };
    case 'macro':
      return { type: 'macro', steps: [{ action: mouseDefault(), delayMs: 50 }] };
    case 'mouse':
      return mouseDefault();
    case 'discord':
      return discordDefault();
    case 'sound':
      return { type: 'sound', command: 'play', file: '', volume: 100, mode: 'restart' };
    case 'system':
      return systemDefault('lock');
    case 'window':
      return { type: 'window', command: 'focus' };
    case 'counter':
      return { type: 'counter', name: tr('Contatore'), op: 'inc', step: 1 };
    case 'timer':
      return { type: 'timer', name: 'Timer', op: 'toggle', mode: 'countdown', durationSec: 300 };
    case 'obs':
      return { type: 'obs', command: 'streamToggle' };
    case 'http':
      return httpActionDefault();
    case 'twitch':
      return twitchDefault();
    case 'streamlabs':
      return streamlabsDefault();
    case 'lightroom':
      return lightroomDefault();
    case 'plugin': {
      const p = useDeck.getState().plugins.find((x) => x.actions.length);
      return p ? pluginDefault(p.id, p.actions[0].id) : { type: 'plugin', plugin: '', action: '', fields: {} };
    }
    default:
      return null;
  }
}

export function ActionEditor({
  value,
  onChange,
  nested = false,
  serverOnly = false,
}: {
  value: Action | null;
  onChange: (a: Action | null) => void;
  nested?: boolean;
  /** Run by the PC on its own (event rules): macros allowed, navigation (device-only) excluded. */
  serverOnly?: boolean;
}) {
  return (
    <div className="action-editor">
      <label className="field">
        <span>{tr('Tipo di azione')}</span>
        <select className="input" value={value?.type ?? ''} onChange={(e) => onChange(defaultFor(e.target.value))}>
          <option value="">{tr('Nessuna')}</option>
          {TYPE_GROUPS.map(([group, items]) => {
            const shown = items.filter(([v]) => (!nested || !NESTED_EXCLUDED.has(v)) && (!serverOnly || v !== 'navigate'));
            return (
              shown.length > 0 && (
                <optgroup key={group} label={group}>
                  {shown.map(([v, l]) => (
                    <option key={v} value={v}>
                      {l}
                    </option>
                  ))}
                </optgroup>
              )
            );
          })}
        </select>
      </label>

      {value?.type === 'text' && (
        <label className="field">
          <span>{tr('Testo da scrivere nella finestra attiva')}</span>
          <textarea
            className="input textarea"
            rows={4}
            value={value.text}
            onChange={(e) => onChange({ ...value, text: e.target.value })}
            placeholder={tr('Ciao! A capo e tabulazioni vengono premuti come Invio e Tab.')}
          />
        </label>
      )}

      {value?.type === 'command' && (
        <>
          <Segmented
            value={value.shell}
            options={[
              ['cmd', 'Prompt (cmd)'],
              ['powershell', 'PowerShell'],
            ]}
            onChange={(shell) => onChange({ ...value, shell })}
          />
          <label className="field">
            <span>{value.shell === 'powershell' ? tr('Script PowerShell') : tr('Comando')}</span>
            <textarea
              className="input textarea mono"
              rows={4}
              value={value.command}
              onChange={(e) => onChange({ ...value, command: e.target.value })}
              placeholder={
                value.shell === 'powershell' ? 'Get-Process | Sort-Object CPU -Descending | Select-Object -First 5' : 'shutdown /s /t 60'
              }
            />
          </label>
          <Toggle label={tr('Nascondi la finestra')} checked={value.hidden} onChange={(hidden) => onChange({ ...value, hidden })} />
          <label className="field">
            <span>{tr('Cartella di lavoro (facoltativa)')}</span>
            <PathInput kind="folder" placeholder="%USERPROFILE%" value={value.cwd ?? ''} onChange={(cwd) => onChange({ ...value, cwd })} />
          </label>
          <p className="muted small">
            {value.hidden
              ? tr('Il comando gira in background, senza finestra.')
              : tr('La finestra resta aperta alla fine, così puoi leggere il risultato.')}
          </p>
        </>
      )}

      {value?.type === 'macro' && <MacroEditor value={value} onChange={onChange} />}
      {value?.type === 'mouse' && <MouseEditor value={value} onChange={onChange} />}
      {value?.type === 'discord' && <DiscordActionEditor value={value} onChange={onChange} />}

      {value?.type === 'hotkey' && <HotkeyField keys={value.keys} onChange={(keys) => onChange({ ...value, keys })} />}

      {value?.type === 'launch' && (
        <>
          <label className="field">
            <span>{tr('Programma, file, cartella o URL')}</span>
            <PathInput
              kind="program"
              alsoFolder
              placeholder={tr('C:\\Programmi\\App\\app.exe  ·  https://…  ·  %USERPROFILE%\\Documenti')}
              value={value.target}
              onChange={(target) => onChange({ ...value, target })}
            />
          </label>
          <label className="field">
            <span>{tr('Argomenti (facoltativi)')}</span>
            <input className="input mono" value={value.args ?? ''} onChange={(e) => onChange({ ...value, args: e.target.value })} />
          </label>
          <LaunchIfOpenFields value={value} onChange={onChange} />
        </>
      )}

      {value?.type === 'media' && <MediaEditor value={value} onChange={onChange} />}
      {value?.type === 'sound' && <SoundEditor value={value} onChange={onChange} />}
      {value?.type === 'system' && <SystemEditor value={value} onChange={onChange} />}
      {value?.type === 'window' && <WindowEditor value={value} onChange={onChange} />}
      {value?.type === 'counter' && <CounterEditor value={value} onChange={onChange} />}
      {value?.type === 'timer' && <TimerEditor value={value} onChange={onChange} />}
      {value?.type === 'obs' && <ObsEditor value={value} onChange={onChange} />}
      {value?.type === 'http' && <HttpEditor value={value} onChange={onChange} />}
      {value?.type === 'twitch' && <TwitchActionEditor value={value} onChange={onChange} />}
      {value?.type === 'streamlabs' && <StreamlabsActionEditor value={value} onChange={onChange} />}
      {value?.type === 'lightroom' && <LightroomActionEditor value={value} onChange={onChange} />}
      {value?.type === 'plugin' &&
        (value.plugin ? (
          <PluginEditor value={value} onChange={onChange} />
        ) : (
          <p className="muted small hint">
            {tr("Nessun plugin installato: aggiungili nella cartella dei plugin (menu dell'icona → Plugin).")}
          </p>
        ))}

      {value?.type === 'navigate' && <NavigateField value={value} onChange={onChange} />}
    </div>
  );
}

function NavigateField({ value, onChange }: { value: Extract<Action, { type: 'navigate' }>; onChange: (a: Action) => void }) {
  const profiles = useDeck((s) => s.profiles);
  const profile = useCurrentProfile();
  const pages = profile?.pages.filter((p) => p.kind !== 'folder') ?? [];
  return (
    <>
      <label className="field">
        <span>{tr('Destinazione')}</span>
        <select
          className="input"
          value={value.target}
          onChange={(e) => onChange({ type: 'navigate', target: e.target.value as 'back' | 'home' | 'profile' | 'page' })}
        >
          <option value="back">{tr('Indietro')}</option>
          <option value="home">{tr('Pagina iniziale')}</option>
          <option value="page">{tr('Vai a una pagina')}</option>
          <option value="profile">{tr('Cambia profilo')}</option>
        </select>
      </label>
      {value.target === 'page' && (
        <label className="field">
          <span>{tr('Pagina')}</span>
          <select className="input" value={value.id ?? ''} onChange={(e) => onChange({ ...value, id: e.target.value })}>
            <option value="">{tr('— scegli —')}</option>
            {pages.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </label>
      )}
      {value.target === 'profile' && (
        <label className="field">
          <span>{tr('Profilo')}</span>
          <select className="input" value={value.id ?? ''} onChange={(e) => onChange({ ...value, id: e.target.value })}>
            <option value="">{tr('— scegli —')}</option>
            {profiles.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </label>
      )}
    </>
  );
}

function HotkeyField({ keys, onChange }: { keys: string[]; onChange: (k: string[]) => void }) {
  const [recording, setRecording] = useState(false);
  const mods = MODIFIERS.filter((m) => keys.includes(m));
  const main = keys.find((k) => !(MODIFIERS as readonly string[]).includes(k)) ?? '';

  const build = (m: readonly string[], k: string) => [...MODIFIERS.filter((x) => m.includes(x)), ...(k ? [k] : [])];

  useEffect(() => {
    if (!recording) return;
    const onKey = (e: KeyboardEvent) => {
      e.preventDefault();
      e.stopPropagation();
      if (['ControlLeft', 'ControlRight', 'ShiftLeft', 'ShiftRight', 'AltLeft', 'AltRight', 'MetaLeft', 'MetaRight'].includes(e.code))
        return; // wait for the main key
      const m: string[] = [];
      if (e.ctrlKey) m.push('Ctrl');
      if (e.altKey) m.push('Alt');
      if (e.shiftKey) m.push('Shift');
      if (e.metaKey) m.push('Win');
      onChange(build(m, KNOWN_KEYS.has(e.code) ? e.code : e.code || ''));
      setRecording(false);
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [recording]); // eslint-disable-line react-hooks/exhaustive-deps

  const summary = keys.length ? keys.map((k) => ((MODIFIERS as readonly string[]).includes(k) ? k : keyLabel(k))).join(' + ') : '—';

  return (
    <div className="hotkey">
      <div className="hotkey-summary">{summary}</div>
      <div className="chips">
        {MODIFIERS.map((m) => (
          <button
            key={m}
            type="button"
            className={`chip${mods.includes(m) ? ' on' : ''}`}
            onClick={() => onChange(build(mods.includes(m) ? mods.filter((x) => x !== m) : [...mods, m], main))}
          >
            {m}
          </button>
        ))}
      </div>
      <label className="field">
        <span>{tr('Tasto')}</span>
        <select className="input" value={main} onChange={(e) => onChange(build(mods, e.target.value))}>
          <option value="">{tr('— nessuno —')}</option>
          {KEY_GROUPS.map((g) => (
            <optgroup key={g.label} label={g.label}>
              {g.keys.map(([code, label]) => (
                <option key={code} value={code}>
                  {label}
                </option>
              ))}
            </optgroup>
          ))}
        </select>
      </label>
      <button type="button" className={`btn${recording ? ' primary' : ''}`} onClick={() => setRecording((r) => !r)}>
        {recording ? tr('Premi la combinazione… (clic per annullare)') : tr('Registra dalla tastiera')}
      </button>
      <p className="muted small">
        {tr('Alcune combinazioni (come Ctrl+Alt+Canc o Win+L) sono riservate da Windows e non possono essere inviate.')}
      </p>
    </div>
  );
}
