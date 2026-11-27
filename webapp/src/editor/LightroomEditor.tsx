import { tr } from '../i18n';
// Lightroom Classic: plugin panel (install, status), action editor and the shared parameter list.
import { useEffect, useState } from 'react';
import { refreshPcInfo, request } from '../net/client';
import { socket } from '../net/socket';
import { showToast, useDeck } from '../state/store';
import type { LightroomAction, LightroomCommand, LrParam, PcInfo } from '../types';
import { Section, Segmented } from './controls';
import { usePcInfo } from './ExtraEditors';

/** Develop parameters: Italian label, default step, unit (same list as the server). */
export const LR_PARAMS: [LrParam, string, number][] = [
  ['Exposure', tr('Esposizione'), 0.05],
  ['Contrast', tr('Contrasto'), 5],
  ['Highlights', tr('Luci'), 5],
  ['Shadows', tr('Ombre'), 5],
  ['Whites', tr('Bianchi'), 5],
  ['Blacks', tr('Neri'), 5],
  ['Temperature', tr('Temperatura'), 100],
  ['Tint', tr('Tinta'), 2],
  ['Vibrance', tr('Vividezza'), 5],
  ['Saturation', tr('Saturazione'), 5],
  ['Texture', 'Texture', 5],
  ['Clarity', tr('Chiarezza'), 5],
  ['Dehaze', tr('Foschia'), 5],
];
export const lrLabel = (p: string | undefined) => LR_PARAMS.find((x) => x[0] === p)?.[1] ?? p ?? '';
export const lrStep = (p: string | undefined) => LR_PARAMS.find((x) => x[0] === p)?.[2] ?? 5;

export function LrParamField({ value, onChange }: { value: LrParam | undefined; onChange: (p: LrParam) => void }) {
  return (
    <label className="field">
      <span>{tr('Parametro')}</span>
      <select className="input" value={value ?? 'Exposure'} onChange={(e) => onChange(e.target.value as LrParam)}>
        {LR_PARAMS.map(([v, l]) => (
          <option key={v} value={v}>
            {l}
          </option>
        ))}
      </select>
    </label>
  );
}

/** Lightroom status, refreshed every 2 s while shown (edit.info is fetched once and would stay stale). */
function useLightroomInfo() {
  const info = usePcInfo()?.lightroom;
  const conn = useDeck((s) => s.conn);
  useEffect(() => {
    if (conn !== 'open') return;
    let alive = true;
    const tick = async () => {
      try {
        const m = await request({ t: 'edit.lightroom.info' });
        const pc = useDeck.getState().pcInfo;
        if (alive && pc) useDeck.setState({ pcInfo: { ...pc, lightroom: m.lightroom as PcInfo['lightroom'] } });
      } catch {
        // older server or socket closed: keep the last status
      }
    };
    void tick();
    const id = setInterval(() => void tick(), 2000);
    return () => {
      alive = false;
      clearInterval(id);
    };
  }, [conn]);
  return info;
}

// ---- Panel --------------------------------------------------------------------------------

export function LightroomSection() {
  const info = useLightroomInfo();
  const [busy, setBusy] = useState(false);
  if (!info) return null;
  const install = async () => {
    setBusy(true);
    try {
      await request({ t: 'edit.lightroom.install' });
      showToast(tr('Plugin installato: riavvia Lightroom Classic'));
      void refreshPcInfo();
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };
  return (
    <Section title="Lightroom Classic">
      <p className={`muted small hint${info.installed && !info.connected ? ' error-text' : ''}`}>
        {info.connected
          ? tr('✓ Collegato a Lightroom Classic.')
          : info.installed
            ? tr('Plugin installato. Lightroom non connesso: aprilo (o riavvialo) e attendi qualche secondo.')
            : tr('Plugin non ancora installato.')}
        {info.error ? tr(' Ultimo errore: {0}', info.error) : ''}
      </p>
      <div className="button-row">
        <button type="button" className="btn primary" disabled={busy} onClick={() => void install()}>
          {info.installed ? tr('Reinstalla plugin Lightroom') : tr('Installa plugin Lightroom')}
        </button>
        <button type="button" className="btn" onClick={() => socket.send({ t: 'edit.lightroom.openFolder' })}>
          {tr('Apri cartella')}
        </button>
      </div>
      <p className="muted small hint">
        {tr(
          'Il plugin viene copiato in {0}, che Lightroom Classic carica da solo all’avvio. Se non compare in{1} {2}, premi «Aggiungi» lì e scegli quella cartella. Il collegamento resta sul PC (127.0.0.1, porte 58873 e 58874). Per un profilo già pronto: pannello Profilo → «+ Lightroom».',
          <code>{info.folder}</code>,
          ' ',
          <i>{tr('File → Gestione plug-in')}</i>,
        )}
      </p>
    </Section>
  );
}

// ---- Action -------------------------------------------------------------------------------

const GROUPS: [string, [LightroomCommand, string][]][] = [
  [
    tr('Sviluppo'),
    [
      ['inc', tr('Aumenta un parametro (+)')],
      ['dec', tr('Diminuisci un parametro (−)')],
      ['reset', tr('Azzera un parametro')],
      ['set', tr('Imposta un valore')],
      ['resetAll', tr('Azzera tutte le regolazioni')],
      ['autoTone', tr('Tono automatico')],
    ],
  ],
  [
    tr('Impostazioni'),
    [
      ['copy', tr('Copia impostazioni')],
      ['paste', tr('Incolla impostazioni')],
      ['sync', tr('Sincronizza (incolla su tutte le foto selezionate)')],
      ['preset', tr('Applica un preset')],
      ['beforeAfter', tr('Prima / dopo')],
      ['undo', tr('Annulla@@undo')],
      ['redo', tr('Ripeti')],
    ],
  ],
  [
    tr('Libreria'),
    [
      ['rating', tr('Stelle')],
      ['flag', tr('Bandierina')],
      ['label', tr('Etichetta colore')],
      ['next', tr('Foto successiva')],
      ['prev', tr('Foto precedente')],
      ['module', tr('Cambia modulo')],
    ],
  ],
];

export const lightroomDefault = (command: LightroomCommand = 'inc'): LightroomAction => {
  switch (command) {
    case 'inc':
    case 'dec':
      return { type: 'lightroom', command, param: 'Exposure', step: 0.05 };
    case 'reset':
      return { type: 'lightroom', command, param: 'Exposure' };
    case 'set':
      return { type: 'lightroom', command, param: 'Exposure', value: 0 };
    case 'rating':
      return { type: 'lightroom', command, rating: 5 };
    case 'flag':
      return { type: 'lightroom', command, flag: 'pick' };
    case 'label':
      return { type: 'lightroom', command, label: 'red' };
    case 'module':
      return { type: 'lightroom', command, module: 'develop' };
    case 'preset':
      return { type: 'lightroom', command, preset: '' };
    default:
      return { type: 'lightroom', command };
  }
};

function PresetField({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const [presets, setPresets] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const load = async () => {
    setBusy(true);
    try {
      const m = await request({ t: 'edit.lightroom.presets' });
      setPresets(m.presets ?? []);
      if (!m.presets?.length) showToast(tr('Nessun preset trovato'), 'error');
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };
  return (
    <label className="field">
      <span>{tr('Preset (nome esatto)')}</span>
      <div className="inline-row">
        <input className="input" list="lr-presets" value={value} onChange={(e) => onChange(e.target.value)} />
        <button type="button" className="btn" disabled={busy} onClick={() => void load()} title={tr('Leggi i preset da Lightroom')}>
          {busy ? '…' : tr('↻ Elenco')}
        </button>
      </div>
      <datalist id="lr-presets">
        {presets.map((p) => (
          <option key={p} value={p} />
        ))}
      </datalist>
    </label>
  );
}

export function LightroomActionEditor({ value, onChange }: { value: LightroomAction; onChange: (a: LightroomAction) => void }) {
  const info = useLightroomInfo();
  const c = value.command;
  return (
    <>
      <label className="field">
        <span>{tr('Comando')}</span>
        <select
          className="input"
          value={c}
          onChange={(e) => {
            const next = lightroomDefault(e.target.value as LightroomCommand);
            // Keep the chosen parameter when switching among +, −, reset and set.
            if (value.param && next.param) {
              next.param = value.param;
              if (next.step !== undefined) next.step = lrStep(value.param);
            }
            onChange(next);
          }}
        >
          {GROUPS.map(([g, opts]) => (
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
      {(c === 'inc' || c === 'dec' || c === 'reset' || c === 'set') && (
        <LrParamField
          value={value.param}
          onChange={(param) => onChange({ ...value, param, ...(value.step !== undefined ? { step: lrStep(param) } : {}) })}
        />
      )}
      {(c === 'inc' || c === 'dec') && (
        <div className="field-row">
          <span>{tr('Passo')}</span>
          <input
            className="input num"
            type="number"
            step="any"
            min={0}
            value={value.step ?? lrStep(value.param)}
            onChange={(e) => onChange({ ...value, step: Math.abs(Number(e.target.value)) || lrStep(value.param) })}
          />
        </div>
      )}
      {c === 'set' && (
        <div className="field-row">
          <span>{tr('Valore')}</span>
          <input
            className="input num"
            type="number"
            step="any"
            value={value.value ?? 0}
            onChange={(e) => onChange({ ...value, value: Number(e.target.value) || 0 })}
          />
        </div>
      )}
      {c === 'preset' && <PresetField value={value.preset ?? ''} onChange={(preset) => onChange({ ...value, preset })} />}
      {c === 'rating' && (
        <Segmented
          value={String(value.rating ?? 0)}
          options={[
            ['0', '0'],
            ['1', '★'],
            ['2', '★★'],
            ['3', '★★★'],
            ['4', '★★★★'],
            ['5', '★★★★★'],
          ]}
          onChange={(v) => onChange({ ...value, rating: Number(v) })}
        />
      )}
      {c === 'flag' && (
        <Segmented
          value={value.flag ?? 'pick'}
          options={[
            ['pick', tr('Scelta')],
            ['reject', tr('Rifiutata')],
            ['none', tr('Nessuna')],
          ]}
          onChange={(flag) => onChange({ ...value, flag })}
        />
      )}
      {c === 'label' && (
        <Segmented
          value={value.label ?? 'red'}
          options={[
            ['red', tr('Rosso')],
            ['yellow', tr('Giallo')],
            ['green', tr('Verde')],
            ['blue', tr('Blu')],
            ['purple', tr('Viola')],
            ['none', tr('Nessuna')],
          ]}
          onChange={(label) => onChange({ ...value, label })}
        />
      )}
      {c === 'module' && (
        <label className="field">
          <span>{tr('Modulo')}</span>
          <select
            className="input"
            value={value.module ?? 'develop'}
            onChange={(e) => onChange({ ...value, module: e.target.value as LightroomAction['module'] })}
          >
            <option value="library">{tr('Libreria')}</option>
            <option value="develop">{tr('Sviluppo')}</option>
            <option value="map">{tr('Mappa')}</option>
            <option value="book">{tr('Libro')}</option>
            <option value="slideshow">{tr('Presentazione')}</option>
            <option value="print">{tr('Stampa')}</option>
            <option value="web">Web</option>
          </select>
        </label>
      )}
      <p className={`muted small hint${info && !info.connected ? ' error-text' : ''}`}>
        {info?.connected
          ? tr('Lightroom Classic è collegato.')
          : tr('Lightroom non connesso: installa il plugin dal pannello del profilo → Lightroom Classic.')}
        {c === 'label' && tr(' Rosso…Viola alternano l’etichetta, come i tasti 6–9 in Lightroom.')}
      </p>
    </>
  );
}
