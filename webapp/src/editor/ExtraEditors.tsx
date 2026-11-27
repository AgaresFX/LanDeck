import { tr } from '../i18n';
// Editors for the audio, soundboard, system, window, counter/timer, OBS and plugin actions.
import { useEffect, useId } from 'react';
import { refreshPcInfo } from '../net/client';
import { useDeck } from '../state/store';
import type {
  Action,
  CounterAction,
  LaunchAction,
  MediaAction,
  MediaCommand,
  ObsAction,
  ObsCommand,
  PluginAction,
  PluginField,
  SoundAction,
  SystemAction,
  SystemCommand,
  TimerAction,
  WindowAction,
  WindowCommand,
} from '../types';
import { Segmented, Toggle } from './controls';
import { PathInput } from './PathInput';
import { isDesktop } from '../lib/desktop';
import { Icon } from '../ui/Icon';

type Change = (a: Action) => void;

/** Loads the lists from the PC the first time an editor needs them. */
export function usePcInfo() {
  const info = useDeck((s) => s.pcInfo);
  const conn = useDeck((s) => s.conn);
  useEffect(() => {
    if (!info && conn === 'open') void refreshPcInfo();
  }, [info, conn]);
  return info;
}

/**
 * Like usePcInfo, but for a connection status (OBS, Streamlabs Desktop): reloads it when the
 * editor opens and every few seconds while it says "not connected", so it never stays stale.
 */
export function usePcInfoWatch(connected: (info: NonNullable<ReturnType<typeof usePcInfo>>) => boolean | undefined) {
  const info = usePcInfo();
  const conn = useDeck((s) => s.conn);
  const ok = info ? !!connected(info) : false;
  useEffect(() => {
    if (conn === 'open') void refreshPcInfo(true);
  }, [conn]);
  useEffect(() => {
    if (ok || conn !== 'open') return;
    const t = window.setInterval(() => void refreshPcInfo(true), 4000);
    return () => window.clearInterval(t);
  }, [ok, conn]);
  return info;
}

function Select<T extends string>({
  label,
  value,
  groups,
  onChange,
}: {
  label: string;
  value: T;
  groups: [string, [T, string][]][];
  onChange: (v: T) => void;
}) {
  return (
    <label className="field">
      <span>{label}</span>
      <select className="input" value={value} onChange={(e) => onChange(e.target.value as T)}>
        {groups.map(([g, opts]) =>
          g ? (
            <optgroup key={g} label={g}>
              {opts.map(([v, l]) => (
                <option key={v} value={v}>
                  {l}
                </option>
              ))}
            </optgroup>
          ) : (
            opts.map(([v, l]) => (
              <option key={v} value={v}>
                {l}
              </option>
            ))
          ),
        )}
      </select>
    </label>
  );
}

function Range({
  label,
  value,
  min,
  max,
  unit = '%',
  onChange,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  unit?: string;
  onChange: (v: number) => void;
}) {
  return (
    <label className="field">
      <span>
        {label}: {value}
        {unit}
      </span>
      <input type="range" min={min} max={max} value={value} onChange={(e) => onChange(Number(e.target.value))} />
    </label>
  );
}

/** Process name with suggestions from the PC (apps with audio or windows). */
export function AppField({
  label,
  value,
  onChange,
  source,
  placeholder = 'spotify',
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  source: 'sessions' | 'apps';
  placeholder?: string;
}) {
  const info = usePcInfo();
  const id = useId();
  const list = source === 'sessions' ? [...(info?.sessions ?? []), ...(info?.apps ?? [])] : (info?.apps ?? []);
  return (
    <label className="field">
      <span>{label}</span>
      <div className="inline-row">
        <input className="input mono" list={id} value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} />
        <button type="button" className="btn" onClick={() => void refreshPcInfo()} title={tr("Aggiorna l'elenco delle app aperte sul PC")}>
          ↻
        </button>
      </div>
      <datalist id={id}>
        {[...new Set(list)].map((a) => (
          <option key={a} value={a} />
        ))}
      </datalist>
      {source === 'sessions' && isDesktop && <AudioAppPicker value={value} onChange={onChange} />}
    </label>
  );
}

/** Desktop editor: the apps playing audio on the PC, with icon and volume, to pick with a click. */
function AudioAppPicker({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const apps = usePcInfo()?.audioApps ?? [];
  if (apps.length === 0)
    return <p className="muted small hint">{tr('Nessuna app sta riproducendo audio adesso: avviane una e premi ↻.')}</p>;
  const current = value
    .trim()
    .toLowerCase()
    .replace(/\.exe$/, '');
  return (
    <div className="audio-apps">
      {apps.map((a) => (
        <button
          key={a.name}
          type="button"
          className={`audio-app${a.name === current ? ' on' : ''}${a.muted ? ' muted' : ''}`}
          onClick={(e) => {
            e.preventDefault();
            onChange(a.name);
          }}
          title={a.muted ? tr('{0} (muto)', a.name) : a.name}
        >
          {a.icon ? (
            <img src={a.icon} alt="" />
          ) : (
            <span className="audio-app-noicon">
              <Icon name="volume-2" />
            </span>
          )}
          <span className="audio-app-name">{a.name}</span>
          <span className="audio-app-bar">
            <span style={{ width: `${a.volume ?? 0}%` }} />
          </span>
          <span className="audio-app-vol">{a.muted ? tr('muto') : a.volume !== null ? `${a.volume}%` : ''}</span>
        </button>
      ))}
    </div>
  );
}

// ---- Media / audio ------------------------------------------------------------------------

export const MEDIA_GROUPS: [string, [MediaCommand, string][]][] = [
  [
    tr('Riproduzione'),
    [
      ['playPause', tr('Play / Pausa')],
      ['next', tr('Traccia successiva')],
      ['prev', tr('Traccia precedente')],
      ['stop', 'Stop'],
    ],
  ],
  [
    tr('Volume generale'),
    [
      ['volUp', tr('Volume su')],
      ['volDown', tr('Volume giù')],
      ['volSet', tr('Imposta volume')],
      ['mute', tr('Muto (attiva/disattiva)')],
    ],
  ],
  [
    tr('Volume di una singola app'),
    [
      ['appVolUp', tr('Volume app su')],
      ['appVolDown', tr('Volume app giù')],
      ['appVolSet', tr('Imposta volume app')],
      ['appMute', tr('Muto app (attiva/disattiva)')],
    ],
  ],
  [tr('Microfono'), [['micMute', tr('Muto microfono')]]],
  [tr('Dispositivo di uscita'), [['output', tr('Cambia uscita audio (cuffie, casse…)')]]],
];

export function MediaEditor({ value, onChange }: { value: MediaAction; onChange: Change }) {
  const c = value.command;
  const isApp = c.startsWith('app');
  return (
    <>
      <Select label={tr('Comando')} value={c} groups={MEDIA_GROUPS} onChange={(command) => onChange({ ...value, command })} />
      {isApp && (
        <AppField
          label={tr('App (nome del processo)')}
          value={value.app ?? ''}
          source="sessions"
          onChange={(app) => onChange({ ...value, app })}
        />
      )}
      {isApp && <p className="muted small hint">{tr("L'app deve aver già riprodotto un suono per comparire nel mixer di Windows.")}</p>}
      {(c === 'volUp' || c === 'volDown' || c === 'appVolUp' || c === 'appVolDown') && (
        <Range label={tr('Passo')} value={value.step ?? 2} min={1} max={20} onChange={(step) => onChange({ ...value, step })} />
      )}
      {(c === 'volSet' || c === 'appVolSet') && (
        <Range label="Volume" value={value.level ?? 50} min={0} max={100} onChange={(level) => onChange({ ...value, level })} />
      )}
      {c === 'micMute' && (
        <>
          <Select
            label={tr('Modalità')}
            value={value.mode ?? 'toggle'}
            groups={[
              [
                '',
                [
                  ['toggle', tr('Attiva/disattiva a ogni pressione')],
                  ['on', tr('Silenzia sempre')],
                  ['off', tr('Riattiva sempre')],
                ],
              ],
            ]}
            onChange={(mode) => onChange({ ...value, mode })}
          />
          <p className="muted small hint">{tr('Agisce sul microfono predefinito di Windows (quello delle comunicazioni).')}</p>
        </>
      )}
      {c === 'output' && <OutputPicker value={value} onChange={onChange} />}
    </>
  );
}

function OutputPicker({ value, onChange }: { value: MediaAction; onChange: Change }) {
  const info = usePcInfo();
  const chosen = value.devices ?? [];
  const outputs = info?.outputs ?? [];
  // Chosen devices that are not connected right now stay visible.
  const all = [
    ...outputs.map((o) => ({ id: o.id, name: o.name, online: true })),
    ...chosen.filter((d) => !outputs.some((o) => o.id === d.id)).map((d) => ({ ...d, online: false })),
  ];
  const toggle = (d: { id: string; name: string }, on: boolean) =>
    onChange({ ...value, devices: on ? [...chosen, { id: d.id, name: d.name }] : chosen.filter((x) => x.id !== d.id) });
  return (
    <div className="field">
      <span className="muted small">{tr('Dispositivi')}</span>
      <div className="check-list">
        {all.map((d) => (
          <Toggle
            key={d.id}
            label={`${d.name}${d.online ? '' : tr(' (non collegato)')}${outputs.find((o) => o.id === d.id)?.isDefault ? tr(' · in uso') : ''}`}
            checked={chosen.some((x) => x.id === d.id)}
            onChange={(on) => toggle(d, on)}
          />
        ))}
        {all.length === 0 && <span className="muted small">{tr('Caricamento dei dispositivi…')}</span>}
      </div>
      <p className="muted small hint">
        {tr('Uno solo: il tasto passa sempre a quel dispositivo. Più di uno: ogni pressione passa al successivo (es. cuffie ↔ casse).')}
      </p>
      <button type="button" className="btn" onClick={() => void refreshPcInfo()}>
        {tr('↻ Aggiorna elenco')}
      </button>
    </div>
  );
}

// ---- Soundboard ---------------------------------------------------------------------------

export function SoundEditor({ value, onChange }: { value: SoundAction; onChange: Change }) {
  const stop = value.command === 'stopAll';
  return (
    <>
      <Select
        label={tr('Comando')}
        value={value.command ?? 'play'}
        groups={[
          [
            '',
            [
              ['play', tr('Riproduci un file audio')],
              ['stopAll', tr('Ferma tutti i suoni')],
            ],
          ],
        ]}
        onChange={(command) => onChange({ ...value, command })}
      />
      {!stop && (
        <>
          <label className="field">
            <span>{tr('File audio sul PC (mp3, wav, wma, m4a)')}</span>
            <PathInput
              kind="sound"
              placeholder={isDesktop ? undefined : tr('%USERPROFILE%\\Music\\applauso.mp3')}
              value={value.file}
              onChange={(file) => onChange({ ...value, file })}
            />
          </label>
          {!isDesktop && (
            <p className="muted small hint">{tr('Su Windows: Maiusc + clic destro sul file → «Copia come percorso», poi incolla qui.')}</p>
          )}
          <Range label="Volume" value={value.volume ?? 100} min={0} max={100} onChange={(volume) => onChange({ ...value, volume })} />
          <Select
            label={tr('Se il suono è già in riproduzione')}
            value={value.mode ?? 'restart'}
            groups={[
              [
                '',
                [
                  ['restart', tr('Ricomincia dall’inizio')],
                  ['toggle', tr('Fermalo')],
                  ['overlap', tr('Suona un’altra copia sopra')],
                ],
              ],
            ]}
            onChange={(mode) => onChange({ ...value, mode })}
          />
        </>
      )}
    </>
  );
}

// ---- System -------------------------------------------------------------------------------

export const SYSTEM_GROUPS: [string, [SystemCommand, string][]][] = [
  [
    tr('Alimentazione'),
    [
      ['lock', tr('Blocca il PC')],
      ['sleep', tr('Sospensione')],
      ['hibernate', tr('Ibernazione')],
      ['shutdown', tr('Spegni')],
      ['restart', tr('Riavvia')],
      ['cancelShutdown', tr('Annulla spegnimento programmato')],
      ['logoff', tr('Disconnetti utente')],
    ],
  ],
  [
    'Screenshot',
    [
      ['screenshot', tr('Screenshot (salva / copia)')],
      ['snip', tr('Cattura un’area (Strumento di cattura)')],
    ],
  ],
  [
    tr('Desktop virtuali'),
    [
      ['desktopNext', tr('Desktop successivo')],
      ['desktopPrev', tr('Desktop precedente')],
      ['desktopNew', tr('Nuovo desktop')],
      ['desktopClose', tr('Chiudi desktop attuale')],
      ['taskView', tr('Visualizzazione attività')],
      ['showDesktop', tr('Mostra desktop')],
    ],
  ],
  [
    tr('Luminosità dello schermo'),
    [
      ['brightnessUp', tr('Luminosità su')],
      ['brightnessDown', tr('Luminosità giù')],
      ['brightnessSet', tr('Imposta luminosità')],
    ],
  ],
];

const DANGEROUS = new Set<SystemCommand>(['shutdown', 'restart', 'logoff', 'sleep', 'hibernate']);

export function systemDefault(command: SystemCommand): SystemAction {
  return { type: 'system', command, confirm: DANGEROUS.has(command) || undefined };
}

export function SystemEditor({ value, onChange }: { value: SystemAction; onChange: Change }) {
  const c = value.command;
  const info = usePcInfo();
  return (
    <>
      <Select
        label={tr('Comando')}
        value={c}
        groups={SYSTEM_GROUPS}
        onChange={(command) => onChange({ ...systemDefault(command), ...pick(value, command) })}
      />
      {(DANGEROUS.has(c) || c === 'lock') && (
        <Toggle
          label={tr('Chiedi conferma sul dispositivo')}
          checked={!!value.confirm}
          onChange={(confirm) => onChange({ ...value, confirm })}
        />
      )}
      {(c === 'shutdown' || c === 'restart') && (
        <label className="field">
          <span>{tr('Ritardo (secondi, 0 = subito)')}</span>
          <input
            className="input num"
            type="number"
            inputMode="numeric"
            min={0}
            max={86400}
            value={value.delaySec ?? 0}
            onChange={(e) => onChange({ ...value, delaySec: Math.max(0, Math.min(86400, Number(e.target.value) || 0)) })}
          />
        </label>
      )}
      {c === 'screenshot' && (
        <>
          <Select
            label={tr('Cosa catturare')}
            value={value.area ?? 'all'}
            groups={[
              [
                '',
                [
                  ['all', tr('Tutti gli schermi')],
                  ['primary', tr('Schermo principale')],
                  ['window', tr('Finestra attiva')],
                ],
              ],
            ]}
            onChange={(area) => onChange({ ...value, area })}
          />
          <Select
            label={tr('Dove')}
            value={value.target ?? 'file'}
            groups={[
              [
                '',
                [
                  ['file', tr('Salva come file PNG')],
                  ['clipboard', tr('Copia negli appunti')],
                  ['both', tr('Salva e copia')],
                ],
              ],
            ]}
            onChange={(target) => onChange({ ...value, target })}
          />
          {value.target !== 'clipboard' && (
            <label className="field">
              <span>{tr('Cartella (vuota = predefinita)')}</span>
              <PathInput
                kind="folder"
                placeholder={info?.screenshotFolder ?? '%USERPROFILE%\\Pictures\\PadNook'}
                value={value.folder ?? ''}
                onChange={(folder) => onChange({ ...value, folder })}
              />
            </label>
          )}
        </>
      )}
      {(c === 'brightnessUp' || c === 'brightnessDown') && (
        <Range label={tr('Passo')} value={value.step ?? 10} min={1} max={50} onChange={(step) => onChange({ ...value, step })} />
      )}
      {c === 'brightnessSet' && (
        <Range label={tr('Luminosità')} value={value.level ?? 50} min={0} max={100} onChange={(level) => onChange({ ...value, level })} />
      )}
      {c.startsWith('brightness') && (
        <p className="muted small hint">
          {tr("Monitor esterni: serve l'opzione «DDC/CI» attiva nel menu del monitor. Portatili: usa lo schermo integrato.")}
        </p>
      )}
    </>
  );
}

function pick(a: SystemAction, command: SystemCommand): Partial<SystemAction> {
  // Keep the options that still make sense for the new command.
  const keep: Partial<SystemAction> = {};
  if (command === 'screenshot') Object.assign(keep, { area: a.area, target: a.target, folder: a.folder });
  return keep;
}

// ---- Windows ------------------------------------------------------------------------------

export const WINDOW_GROUPS: [string, [WindowCommand, string][]][] = [
  [
    '',
    [
      ['focus', tr('Porta in primo piano')],
      ['maximize', tr('Massimizza')],
      ['minimize', tr('Riduci a icona')],
      ['restore', tr('Ripristina')],
      ['toggleMax', tr('Massimizza / ripristina')],
      ['snap', tr('Sposta in una zona dello schermo')],
      ['nextMonitor', tr('Sposta sullo schermo successivo')],
      ['topmost', tr('Sempre in primo piano (attiva/disattiva)')],
      ['close', tr('Chiudi')],
    ],
  ],
];

const REGIONS: [string, string][] = [
  ['left', tr('Metà sinistra')],
  ['right', tr('Metà destra')],
  ['top', tr('Metà superiore')],
  ['bottom', tr('Metà inferiore')],
  ['topLeft', tr('Quarto in alto a sinistra')],
  ['topRight', tr('Quarto in alto a destra')],
  ['bottomLeft', tr('Quarto in basso a sinistra')],
  ['bottomRight', tr('Quarto in basso a destra')],
  ['leftThird', tr('Terzo sinistro')],
  ['centerThird', tr('Terzo centrale')],
  ['rightThird', tr('Terzo destro')],
  ['center', tr('Al centro')],
];

export function WindowEditor({ value, onChange }: { value: WindowAction; onChange: Change }) {
  const c = value.command;
  return (
    <>
      <Select label={tr('Comando')} value={c} groups={WINDOW_GROUPS} onChange={(command) => onChange({ ...value, command })} />
      {c === 'snap' && (
        <Select
          label={tr('Zona')}
          value={value.region ?? 'left'}
          groups={[['', REGIONS]]}
          onChange={(region) => onChange({ ...value, region })}
        />
      )}
      <AppField
        label={c === 'focus' ? tr('App da portare in primo piano') : tr('App (vuoto = finestra attiva sul PC)')}
        value={value.app ?? ''}
        source="apps"
        placeholder={c === 'focus' ? 'chrome' : tr('finestra attiva')}
        onChange={(app) => onChange({ ...value, app })}
      />
      {c === 'focus' && (
        <label className="field">
          <span>{tr('Se non è aperta, avvia (facoltativo)')}</span>
          <PathInput
            kind="program"
            placeholder={tr('C:\\Programmi\\App\\app.exe')}
            value={value.launch ?? ''}
            onChange={(launch) => onChange({ ...value, launch })}
          />
        </label>
      )}
    </>
  );
}

// ---- Launch: behaviour when already open --------------------------------------------------

const IF_OPEN: [NonNullable<LaunchAction['ifOpen']>, string][] = [
  ['new', tr('Apri un’altra volta')],
  ['focus', tr('Porta in primo piano')],
  ['close', tr('Chiudi (premi per aprire, ripremi per chiudere)')],
];

export function LaunchIfOpenFields({ value, onChange }: { value: LaunchAction; onChange: Change }) {
  const mode = value.ifOpen ?? 'new';
  const exe = value.target.trim().replace(/^"|"$/g, '');
  const isExe = /\.exe$/i.test(exe);
  const guess = isExe ? (exe.split(/[\\/]/).pop() ?? '').replace(/\.exe$/i, '') : '';
  return (
    <>
      <Select
        label={tr('Se è già aperto')}
        value={mode}
        groups={[['', IF_OPEN]]}
        onChange={(ifOpen) => onChange({ ...value, ifOpen: ifOpen === 'new' ? undefined : ifOpen })}
      />
      {mode !== 'new' && (
        <>
          <AppField
            label={isExe ? tr('App da controllare (facoltativo)') : tr('App da controllare')}
            value={value.app ?? ''}
            source="apps"
            placeholder={guess || tr('nome del processo, es. notepad')}
            onChange={(app) => onChange({ ...value, app: app || undefined })}
          />
          {!isExe && !value.app && (
            <p className="muted small hint">
              {tr('Per URL, cartelle, collegamenti o app dello Store indica il processo da controllare, altrimenti apre sempre.')}
            </p>
          )}
          {mode === 'close' && (
            <p className="muted small">
              {tr('Chiude tutte le finestre dell’app, come con la X: se c’è qualcosa da salvare te lo chiede.')}
            </p>
          )}
        </>
      )}
    </>
  );
}

// ---- Counters & timers --------------------------------------------------------------------

function NameField({ value, onChange, prefix }: { value: string; onChange: (v: string) => void; prefix: 'counter' | 'timer' }) {
  const live = useDeck((s) => s.live);
  const id = useId();
  const names = Object.keys(live)
    .filter((k) => k.startsWith(prefix + ':'))
    .map((k) => k.slice(prefix.length + 1));
  return (
    <label className="field">
      <span>
        {tr('Nome {0} (i tasti con lo stesso nome lo condividono)', prefix === 'counter' ? tr('del contatore') : tr('del timer'))}
      </span>
      <input className="input" list={id} value={value} onChange={(e) => onChange(e.target.value)} />
      <datalist id={id}>
        {names.map((n) => (
          <option key={n} value={n} />
        ))}
      </datalist>
    </label>
  );
}

export function CounterEditor({ value, onChange }: { value: CounterAction; onChange: Change }) {
  return (
    <>
      <NameField prefix="counter" value={value.name} onChange={(name) => onChange({ ...value, name })} />
      <Select
        label={tr('Operazione')}
        value={value.op}
        groups={[
          [
            '',
            [
              ['inc', tr('Aumenta')],
              ['dec', tr('Diminuisci')],
              ['reset', tr('Azzera')],
              ['set', tr('Imposta un valore')],
            ],
          ],
        ]}
        onChange={(op) => onChange({ ...value, op })}
      />
      {(value.op === 'inc' || value.op === 'dec') && (
        <label className="field">
          <span>{tr('Di quanto')}</span>
          <input
            className="input num"
            type="number"
            value={value.step ?? 1}
            onChange={(e) => onChange({ ...value, step: Number(e.target.value) || 1 })}
          />
        </label>
      )}
      {value.op === 'set' && (
        <label className="field">
          <span>{tr('Valore')}</span>
          <input
            className="input num"
            type="number"
            value={value.value ?? 0}
            onChange={(e) => onChange({ ...value, value: Number(e.target.value) || 0 })}
          />
        </label>
      )}
    </>
  );
}

export function TimerEditor({ value, onChange }: { value: TimerAction; onChange: Change }) {
  const dur = value.durationSec ?? 300;
  const setDur = (m: number, s: number) => onChange({ ...value, durationSec: Math.max(1, m * 60 + s) });
  return (
    <>
      <NameField prefix="timer" value={value.name} onChange={(name) => onChange({ ...value, name })} />
      <Select
        label={tr('Operazione')}
        value={value.op}
        groups={[
          [
            '',
            [
              ['toggle', tr('Avvia / pausa')],
              ['start', tr('Avvia')],
              ['pause', tr('Pausa')],
              ['reset', tr('Azzera')],
            ],
          ],
        ]}
        onChange={(op) => onChange({ ...value, op })}
      />
      <Select
        label={tr('Tipo')}
        value={value.mode ?? 'countdown'}
        groups={[
          [
            '',
            [
              ['countdown', tr('Conto alla rovescia')],
              ['stopwatch', tr('Cronometro')],
            ],
          ],
        ]}
        onChange={(mode) => onChange({ ...value, mode })}
      />
      {(value.mode ?? 'countdown') === 'countdown' && (
        <div className="field-row">
          <span>{tr('Durata')}</span>
          <div className="inline-row">
            <input
              className="input num"
              type="number"
              min={0}
              value={Math.floor(dur / 60)}
              onChange={(e) => setDur(Math.max(0, Number(e.target.value) || 0), dur % 60)}
            />
            <span className="muted small">min</span>
            <input
              className="input num"
              type="number"
              min={0}
              max={59}
              value={dur % 60}
              onChange={(e) => setDur(Math.floor(dur / 60), Math.max(0, Math.min(59, Number(e.target.value) || 0)))}
            />
            <span className="muted small">s</span>
          </div>
        </div>
      )}
    </>
  );
}

// ---- OBS ----------------------------------------------------------------------------------

export const OBS_GROUPS: [string, [ObsCommand, string][]][] = [
  [
    tr('Diretta'),
    [
      ['streamToggle', tr('Avvia / ferma la diretta')],
      ['streamStart', tr('Avvia la diretta')],
      ['streamStop', tr('Ferma la diretta')],
    ],
  ],
  [
    tr('Registrazione'),
    [
      ['recordToggle', tr('Avvia / ferma la registrazione')],
      ['recordPause', tr('Pausa / riprendi la registrazione')],
      ['replaySave', tr('Salva il replay buffer')],
      ['replayToggle', tr('Avvia / ferma il replay buffer')],
    ],
  ],
  [
    tr('Altro'),
    [
      ['scene', tr('Cambia scena')],
      ['muteToggle', tr('Muto di una sorgente audio')],
      ['virtualCam', tr('Videocamera virtuale (attiva/disattiva)')],
    ],
  ],
];

export function ObsEditor({ value, onChange }: { value: ObsAction; onChange: Change }) {
  const slobs = value.app === 'slobs';
  const info = usePcInfoWatch((i) => (slobs ? i.slobs : i.obs)?.connected);
  const obs = slobs ? info?.slobs : info?.obs;
  const sceneId = useId();
  const inputId = useId();
  return (
    <>
      <div>
        <Segmented
          value={slobs ? 'slobs' : 'obs'}
          options={[
            ['obs', 'OBS Studio'],
            ['slobs', 'Streamlabs Desktop'],
          ]}
          onChange={(app) => onChange({ ...value, app: app === 'slobs' ? 'slobs' : undefined })}
        />
      </div>
      <Select label={tr('Comando')} value={value.command} groups={OBS_GROUPS} onChange={(command) => onChange({ ...value, command })} />
      {slobs && (value.command === 'recordPause' || value.command === 'virtualCam') && (
        <p className="small error-text">{tr('Streamlabs Desktop non permette questo comando dalla sua API.')}</p>
      )}
      {value.command === 'scene' && (
        <label className="field">
          <span>{tr('Scena')}</span>
          <input
            className="input"
            list={sceneId}
            value={value.scene ?? ''}
            onChange={(e) => onChange({ ...value, scene: e.target.value })}
          />
          <datalist id={sceneId}>
            {(obs?.scenes ?? []).map((s) => (
              <option key={s} value={s} />
            ))}
          </datalist>
        </label>
      )}
      {value.command === 'muteToggle' && (
        <label className="field">
          <span>{tr('Sorgente audio')}</span>
          <input
            className="input"
            list={inputId}
            value={value.input ?? ''}
            onChange={(e) => onChange({ ...value, input: e.target.value })}
          />
          <datalist id={inputId}>
            {(obs?.inputs ?? []).map((s) => (
              <option key={s} value={s} />
            ))}
          </datalist>
        </label>
      )}
      <p className={`muted small hint${obs && !obs.connected ? ' error-text' : ''}`}>
        {slobs
          ? obs?.connected
            ? tr('Streamlabs Desktop è connesso.')
            : tr(
                'Streamlabs Desktop non connesso{0}. Impostazioni nel pannello del profilo → Streamlabs Desktop.',
                obs?.error ? `: ${obs.error}` : '',
              )
          : obs?.connected
            ? tr('OBS è connesso.')
            : tr(
                'OBS non connesso{0}. In OBS: Strumenti → Impostazioni server WebSocket → Abilita. La password va inserita in PadNook (pannello del layout → OBS Studio).',
                obs?.error ? `: ${obs.error}` : '',
              )}
      </p>
    </>
  );
}

// ---- Plugins ------------------------------------------------------------------------------

export function pluginDefault(pluginId: string, actionId: string): PluginAction {
  const def = useDeck
    .getState()
    .plugins.find((p) => p.id === pluginId)
    ?.actions.find((a) => a.id === actionId);
  const fields: PluginAction['fields'] = {};
  for (const f of def?.fields ?? []) if (f.default !== undefined) fields[f.id] = f.default;
  return { type: 'plugin', plugin: pluginId, action: actionId, fields };
}

export function PluginEditor({ value, onChange }: { value: PluginAction; onChange: Change }) {
  const plugins = useDeck((s) => s.plugins);
  const plugin = plugins.find((p) => p.id === value.plugin);
  const def = plugin?.actions.find((a) => a.id === value.action);
  return (
    <>
      <label className="field">
        <span>{tr('Azione del plugin')}</span>
        <select
          className="input"
          value={`${value.plugin}/${value.action}`}
          onChange={(e) => {
            const [p, a] = e.target.value.split('/');
            onChange(pluginDefault(p, a));
          }}
        >
          {!def && <option value={`${value.plugin}/${value.action}`}>{`${value.plugin} / ${value.action} (non installato)`}</option>}
          {plugins
            .filter((p) => p.actions.length)
            .map((p) => (
              <optgroup key={p.id} label={p.name}>
                {p.actions.map((a) => (
                  <option key={a.id} value={`${p.id}/${a.id}`}>
                    {a.name}
                  </option>
                ))}
              </optgroup>
            ))}
        </select>
      </label>
      {def?.description && <p className="muted small hint">{def.description}</p>}
      {def?.fields.map((f) => (
        <PluginFieldInput
          key={f.id}
          field={f}
          value={value.fields[f.id] ?? f.default ?? ''}
          onChange={(v) => onChange({ ...value, fields: { ...value.fields, [f.id]: v } })}
        />
      ))}
    </>
  );
}

function PluginFieldInput({
  field,
  value,
  onChange,
}: {
  field: PluginField;
  value: string | number | boolean;
  onChange: (v: string | number | boolean) => void;
}) {
  const label = field.label ?? field.id;
  switch (field.type) {
    case 'checkbox':
      return <Toggle label={label} checked={value === true || value === 'true'} onChange={onChange} />;
    case 'number':
      return (
        <label className="field">
          <span>{label}</span>
          <input
            className="input num"
            type="number"
            min={field.min}
            max={field.max}
            value={Number(value) || 0}
            onChange={(e) => onChange(Number(e.target.value) || 0)}
          />
        </label>
      );
    case 'select':
      return (
        <label className="field">
          <span>{label}</span>
          <select className="input" value={String(value)} onChange={(e) => onChange(e.target.value)}>
            {(field.options ?? []).map(([v, l]) => (
              <option key={v} value={v}>
                {l}
              </option>
            ))}
          </select>
        </label>
      );
    case 'textarea':
      return (
        <label className="field">
          <span>{label}</span>
          <textarea
            className="input textarea"
            rows={3}
            value={String(value)}
            placeholder={field.placeholder}
            onChange={(e) => onChange(e.target.value)}
          />
        </label>
      );
    default:
      return (
        <label className="field">
          <span>{label}</span>
          {field.type === 'file' ? (
            <PathInput kind="file" value={String(value)} placeholder={field.placeholder} onChange={onChange} />
          ) : (
            <input className="input" value={String(value)} placeholder={field.placeholder} onChange={(e) => onChange(e.target.value)} />
          )}
        </label>
      );
  }
}
