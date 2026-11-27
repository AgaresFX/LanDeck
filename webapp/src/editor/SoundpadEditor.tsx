import { tr } from '../i18n';
// Soundpad pages: pages that hold only sound keys, plus the audio routing settings (headphones + virtual mic).
import { useEffect, useState } from 'react';
import { request, updateProfile } from '../net/client';
import { newId } from '../lib/platform';
import { isDesktop } from '../lib/desktop';
import { showToast, useCurrentProfile, useDeck } from '../state/store';
import type { Cell, Key, NavigateAction, Orientation, Page, SoundAction } from '../types';
import { Section, Segmented, Slider, Toggle } from './controls';
import { PathInput } from './PathInput';

export const isSoundpadPage = (page: Page | null | undefined) => page?.kind === 'soundpad';

/** A key that can live on a soundpad page: a plain key that plays / stops sounds, or leaves the page. */
export function isSoundKey(k: Key) {
  return (
    k.kind === 'action' &&
    (k.onPress === null || k.onPress.type === 'sound' || k.onPress.type === 'navigate') &&
    !k.onLongPress &&
    !k.onDoubleTap
  );
}

/** "Indietro" key that leaves the soundpad page (to the Home page, or to a chosen page). */
function navKey(cell: Cell): Key {
  return {
    id: newId('k'),
    span: { w: 1, h: 1 },
    pos: { portrait: cell, landscape: cell },
    style: {
      label: { text: tr('Indietro'), size: 13, color: '#ffffff', position: 'bottom' },
      icon: { kind: 'lucide', value: 'ArrowLeft', color: '#ffffff' },
      bg: { color: '#1b1d24' },
    },
    kind: 'action',
    onPress: { type: 'navigate', target: 'home' },
    onLongPress: null,
  };
}

export function newSoundKey(orientation: Orientation, cell: Cell): Key {
  return {
    id: newId('k'),
    span: { w: 1, h: 1 },
    pos: { [orientation]: cell },
    style: {
      label: { text: tr('Suono'), size: 13, color: '#ffffff', position: 'bottom' },
      icon: { kind: 'lucide', value: 'Volume2', color: '#ffffff' },
      bg: { color: '#3b2a5c' },
    },
    kind: 'action',
    onPress: { type: 'sound', command: 'play', file: '', volume: 100, mode: 'restart' },
    onLongPress: null,
  };
}

export function addSoundpadPage() {
  const id = newId('pg');
  updateProfile((p) => {
    const n = p.pages.filter((x) => x.kind === 'soundpad').length + 1;
    p.pages.push({ id, name: n === 1 ? 'Soundpad' : `Soundpad ${n}`, kind: 'soundpad', keys: [navKey({ r: 0, c: 0 })] });
  });
  useDeck.setState({ pageStack: [id], selectedKeyId: null });
}

const fileTitle = (path: string) =>
  path
    .split(/[\\/]/)
    .pop()!
    .replace(/\.[^.]+$/, '')
    .replace(/[_-]+/g, ' ')
    .trim();

const DEFAULT_SOUND: SoundAction = { type: 'sound', command: 'play', file: '', volume: 100, mode: 'restart' };

// ---- Key editor ---------------------------------------------------------------------------

export function SoundKeySections({ k, edit }: { k: Key; edit: (fn: (key: Key) => void) => void }) {
  const profile = useCurrentProfile();
  const a: SoundAction = k.onPress?.type === 'sound' ? k.onPress : DEFAULT_SOUND;
  const nav: NavigateAction | null = k.onPress?.type === 'navigate' ? k.onPress : null;
  const stop = !nav && a.command === 'stopAll';
  const mode = nav ? 'nav' : stop ? 'stopAll' : 'play';
  const pages = (profile?.pages ?? []).filter((p) => p.kind !== 'folder');
  const set = (patch: Partial<SoundAction>) => edit((x) => (x.onPress = { ...a, ...patch }));

  const setFile = (file: string) =>
    edit((x) => {
      const prev = x.onPress?.type === 'sound' ? x.onPress.file : '';
      x.onPress = { ...a, file };
      // The label follows the file name while it was never changed by hand.
      const label = x.style.label.text;
      if (file && (label === '' || label === 'Suono' || (prev && label === fileTitle(prev)))) x.style.label.text = fileTitle(file);
    });

  const test = async () => {
    try {
      await request({ t: 'edit.sound.test', ...a });
    } catch (e) {
      showToast((e as Error).message, 'error', 4000);
    }
  };

  return (
    <>
      <Section title={tr('Tasto soundpad')}>
        <Segmented
          value={mode}
          options={[
            ['play', tr('Suono')],
            ['stopAll', tr('Ferma tutto')],
            ['nav', tr('Indietro')],
          ]}
          onChange={(v) =>
            edit((x) => {
              if (v === 'nav') {
                x.onPress = { type: 'navigate', target: 'home' };
                x.style.label.text = tr('Indietro');
                x.style.icon = { kind: 'lucide', value: 'ArrowLeft', color: '#ffffff' };
                x.style.bg.color = '#1b1d24';
                return;
              }
              x.onPress = { ...a, command: v };
              if (v === 'stopAll') {
                x.style.label.text = 'Stop';
                x.style.icon = { kind: 'lucide', value: 'Square', color: '#ffffff' };
                x.style.bg.color = '#5a2a2a';
              } else {
                x.style.label.text = a.file ? fileTitle(a.file) : tr('Suono');
                x.style.icon = { kind: 'lucide', value: 'Volume2', color: '#ffffff' };
                x.style.bg.color = '#3b2a5c';
              }
            })
          }
        />
      </Section>

      {nav && (
        <Section title={tr('Dove porta')}>
          <label className="field">
            <span>{tr('Pagina da aprire')}</span>
            <select
              className="input"
              value={nav.target === 'page' && nav.id ? nav.id : ''}
              onChange={(e) =>
                edit((x) => {
                  const id = e.target.value;
                  x.onPress = id ? { type: 'navigate', target: 'page', id } : { type: 'navigate', target: 'home' };
                  const name = pages.find((p) => p.id === id)?.name;
                  if (x.style.label.text === 'Indietro' || pages.some((p) => p.name === x.style.label.text))
                    x.style.label.text = name ?? tr('Indietro');
                })
              }
            >
              <option value="">{tr('Prima pagina (Home)')}</option>
              {pages.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </label>
        </Section>
      )}

      {mode === 'play' && (
        <Section title={tr('Suono')}>
          <label className="field">
            <span>{tr('File audio (mp3, wav, ogg, m4a, wma)')}</span>
            <PathInput
              kind="sound"
              placeholder={isDesktop ? tr('Sfoglia o trascina qui un file') : tr('%USERPROFILE%\\Music\\applauso.mp3')}
              value={a.file}
              onChange={setFile}
            />
          </label>
          <Slider
            label={tr('Volume del suono')}
            unit="%"
            value={a.volume ?? 100}
            min={0}
            max={100}
            onChange={(volume) => set({ volume })}
          />
          <label className="field">
            <span>{tr('Se lo premi mentre sta suonando')}</span>
            <select className="input" value={a.mode ?? 'restart'} onChange={(e) => set({ mode: e.target.value as SoundAction['mode'] })}>
              <option value="restart">{tr('Ricomincia dall’inizio')}</option>
              <option value="toggle">{tr('Fermalo')}</option>
              <option value="overlap">{tr('Suona un’altra copia sopra')}</option>
            </select>
          </label>
        </Section>
      )}

      {!nav && (
        <div className="inline-row">
          <button className="btn primary" disabled={!stop && !a.file} onClick={test}>
            {stop ? tr('Ferma tutti ora') : tr('Prova')}
          </button>
          {!stop && (
            <button className="btn" onClick={() => void request({ t: 'edit.sound.test', command: 'stopAll' }).catch(() => {})}>
              Stop
            </button>
          )}
        </div>
      )}
    </>
  );
}

// ---- Routing settings ---------------------------------------------------------------------

interface SoundpadSettings {
  cable: string;
  mic: string;
  toMic: boolean;
  passthrough: boolean;
  monitor: boolean;
  monitorVolume: number;
  micVolume: number;
}

interface SoundpadStatus {
  settings: SoundpadSettings;
  cableName: string | null;
  cableActive: boolean;
  micName: string | null;
  micActive: boolean;
  micError: string | null;
  installing: boolean;
  outputs: { id: string; name: string; cable: boolean }[];
  inputs: { id: string; name: string; cable: boolean }[];
}

export function SoundpadSettingsPanel() {
  const conn = useDeck((s) => s.conn);
  const [st, setSt] = useState<SoundpadStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [waitingInstall, setWaitingInstall] = useState(false);

  const load = async (t = 'edit.soundpad.get') => {
    try {
      setSt((await request({ t })) as unknown as SoundpadStatus);
    } catch (e) {
      showToast((e as Error).message, 'error');
    }
  };

  useEffect(() => {
    if (conn === 'open') void load();
  }, [conn]);

  // After starting the setup, look for the new devices every few seconds.
  useEffect(() => {
    if (!waitingInstall || st?.cableName) return;
    const id = window.setInterval(() => void load('edit.soundpad.refresh'), 4000);
    return () => window.clearInterval(id);
  }, [waitingInstall, st?.cableName]);

  if (!st) return <p className="muted small hint">{tr('Caricamento impostazioni audio…')}</p>;
  const s = st.settings;

  const save = async (patch: Partial<SoundpadSettings>) => {
    const settings = { ...s, ...patch };
    setSt({ ...st, settings });
    try {
      setSt((await request({ t: 'edit.soundpad.set', settings })) as unknown as SoundpadStatus);
    } catch (e) {
      showToast((e as Error).message, 'error');
    }
  };

  const install = async () => {
    setBusy(true);
    try {
      await request({ t: 'edit.soundpad.installCable' }, 120000);
      setWaitingInstall(true);
      showToast(tr('Conferma la richiesta di Windows e premi «Install Driver»'), 'info', 6000);
    } catch (e) {
      showToast((e as Error).message, 'error', 6000);
    } finally {
      setBusy(false);
    }
  };

  const realMics = st.inputs.filter((d) => !d.cable);

  return (
    <>
      <Section title={tr('Soundpad: dove si sentono i suoni')}>
        <p className="muted small hint">
          {tr(
            'I suoni partono nelle tue cuffie e, tramite un microfono virtuale, arrivano anche a chi ti sente su Discord insieme alla tua voce. Le tue cuffie e il tuo microfono restano quelli di sempre.',
          )}
        </p>

        {st.cableName ? (
          <div className="callout">
            <div>
              <b className="status-line">
                <span className="led ok" />
                {tr('Microfono virtuale pronto')}
              </b>
              <div className="muted small">{st.cableName}</div>
              {s.passthrough && (
                <div className="muted small">
                  {tr('Voce: {0}', st.micActive ? st.micName : (st.micError ?? tr('microfono non attivo')))}
                </div>
              )}
            </div>
          </div>
        ) : (
          <div className="callout">
            <div>
              <b>{tr('Serve un microfono virtuale')}</b>
              <div className="muted small">
                {tr(
                  'Per far sentire i suoni su Discord installo VB-CABLE, scaricandolo dal sito ufficiale. Windows ti chiederà i permessi di amministratore; poi premi «Install Driver». Dopo l’installazione è consigliato riavviare il PC.',
                )}
              </div>
              <div className="muted small">
                {tr(
                  'VB-CABLE è un software di{0} {1} , distribuito come donationware con la sua licenza: non fa parte di PadNook. Se lo usi, valuta di sostenerne l’autore.',
                  ' ',
                  <a href="https://vb-audio.com/Cable/" target="_blank" rel="noreferrer">
                    VB-Audio Software
                  </a>,
                )}
              </div>
            </div>
            <div className="inline-row">
              <button className="btn primary" disabled={busy || st.installing} onClick={install}>
                {busy ? tr('Scarico…') : tr('Installa VB-CABLE')}
              </button>
              <button className="btn" onClick={() => void load('edit.soundpad.refresh')}>
                {tr('Ricontrolla')}
              </button>
            </div>
            {waitingInstall && <div className="muted small">{tr('In attesa dell’installazione… controllo ogni pochi secondi.')}</div>}
          </div>
        )}
      </Section>

      <Section title={tr('Audio del soundpad')}>
        <Toggle label={tr('Senti i suoni nelle tue cuffie')} checked={s.monitor} onChange={(monitor) => save({ monitor })} />
        {s.monitor && (
          <Slider
            label={tr('Volume in cuffia')}
            unit="%"
            value={s.monitorVolume}
            min={0}
            max={100}
            onChange={(monitorVolume) => save({ monitorVolume })}
          />
        )}
        <Toggle label={tr('Fai sentire i suoni nel microfono (Discord)')} checked={s.toMic} onChange={(toMic) => save({ toMic })} />
        {s.toMic && (
          <Slider
            label={tr('Volume nel microfono')}
            unit="%"
            value={s.micVolume}
            min={0}
            max={100}
            onChange={(micVolume) => save({ micVolume })}
          />
        )}
        <Toggle
          label={tr('Manda anche la tua voce nel microfono virtuale')}
          checked={s.passthrough}
          onChange={(passthrough) => save({ passthrough })}
        />
        {s.passthrough && (
          <label className="field">
            <span>{tr('Il tuo microfono')}</span>
            <select className="input" value={s.mic} onChange={(e) => save({ mic: e.target.value })}>
              <option value="">{tr('Predefinito di Windows')}</option>
              {realMics.map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name}
                </option>
              ))}
            </select>
          </label>
        )}
        <label className="field">
          <span>{tr('Microfono virtuale (uscita verso cui mandare i suoni)')}</span>
          <select className="input" value={s.cable} onChange={(e) => save({ cable: e.target.value })}>
            <option value="">{tr('Automatico (VB-CABLE)')}</option>
            {st.outputs.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
          </select>
        </label>
      </Section>

      <Section title={tr('Su Discord')}>
        <ol className="small steps">
          <li>{tr('Impostazioni utente → {0}.', <b>{tr('Voce e video')}</b>)}</li>
          <li>{tr('{0}: scegli {1}.', <b>{tr('Dispositivo di input')}</b>, <b>CABLE Output (VB-Audio Virtual Cable)</b>)}</li>
          <li>{tr('{0}: lascia le tue cuffie.', <b>{tr('Dispositivo di output')}</b>)}</li>
        </ol>
        <p className="muted small hint">
          {tr(
            'La tua voce passa da PadNook: se chiudi PadNook, su Discord rimetti il tuo microfono come input. Se Discord taglia i suoni, abbassa la sensibilità o disattiva la soppressione del rumore.',
          )}
        </p>
      </Section>
    </>
  );
}
