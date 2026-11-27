import { tr } from '../i18n';
// Streamlabs: Streamlabs Desktop connection panel, Streamlabs account panel (tokens, event rules) and the action editor.
import { useEffect, useState } from 'react';
import { refreshPcInfo, request } from '../net/client';
import { showToast, useDeck } from '../state/store';
import type { EventRule, StreamEventKind, StreamlabsAction, StreamlabsInfo } from '../types';
import { Section, Segmented, Toggle } from './controls';
import { usePcInfo, usePcInfoWatch } from './ExtraEditors';
import { EventRulesEditor } from './TwitchEditor';

// ---- Streamlabs Desktop -------------------------------------------------------------------

export function SlobsSection() {
  const info = usePcInfoWatch((i) => !i.slobs?.enabled || i.slobs.connected)?.slobs;
  const [mode, setMode] = useState<'pipe' | 'ws' | null>(null);
  const [port, setPort] = useState<number | null>(null);
  const [token, setToken] = useState('');
  const [busy, setBusy] = useState(false);
  if (!info) return null;
  const m = mode ?? info.mode;
  const save = async (enabled = info.enabled, ok = tr('Impostazioni di Streamlabs Desktop salvate')) => {
    setBusy(true);
    try {
      await request({ t: 'edit.slobs.set', enabled, mode: m, port: port ?? info.port, ...(token ? { token } : {}) });
      setToken('');
      showToast(ok);
      window.setTimeout(() => void refreshPcInfo(), 1500);
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };
  return (
    <Section title="Streamlabs Desktop">
      <p className={`muted small hint${info.enabled && !info.connected ? ' error-text' : ''}`}>
        {info.connected
          ? tr('✓ Connesso a Streamlabs Desktop.')
          : info.enabled
            ? tr('Non connesso{0}.', info.error ? `: ${info.error}` : '')
            : tr('Integrazione disattivata.')}
      </p>
      <Toggle label={tr('Collegati a Streamlabs Desktop su questo PC')} checked={info.enabled} onChange={(v) => void save(v)} />
      <Segmented
        value={m}
        options={[
          ['pipe', tr('Automatico (consigliato)')],
          ['ws', tr('WebSocket con token')],
        ]}
        onChange={setMode}
      />
      {m === 'ws' && (
        <>
          <div className="field-row">
            <span>{tr('Porta')}</span>
            <input
              className="input num"
              type="number"
              value={port ?? info.port}
              onChange={(e) => setPort(Number(e.target.value) || 59650)}
            />
          </div>
          <label className="field">
            <span>Token {info.hasToken ? tr('(già salvato)') : ''}</span>
            <input className="input" type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} />
          </label>
        </>
      )}
      <button type="button" className="btn" disabled={busy} onClick={() => void save(info.enabled, tr('Riconnessione in corso…'))}>
        {tr('Salva e prova connessione')}
      </button>
      <p className="muted small hint">
        {tr(
          '{0}{1} Nelle azioni e nei tasti OBS scegli «Streamlabs Desktop» invece di «OBS Studio».',
          m === 'pipe'
            ? tr(
                'PadNook usa il canale locale di Streamlabs Desktop («named pipe», attivo di serie): basta aprire Streamlabs, nessun token.',
              )
            : tr(
                'In Streamlabs Desktop: Impostazioni → Remote Control → attiva i WebSocket e copia il token. La connessione resta sul PC (127.0.0.1).',
              ),
          ' ',
        )}
      </p>
    </Section>
  );
}

// ---- Streamlabs account -------------------------------------------------------------------

const SL_EVENTS: [StreamEventKind, string, string?][] = [
  ['donation', tr('Donazione'), tr('importo minimo')],
  ['follow', tr('Nuovo follower')],
  ['sub', tr('Abbonamento / membro')],
  ['cheer', 'Bits (Twitch)', tr('bits minimi')],
  ['raid', 'Raid (Twitch)', tr('spettatori minimi')],
  ['superchat', 'Super Chat (YouTube)', tr('importo minimo')],
];

async function streamlabsRequest(msg: object): Promise<StreamlabsInfo | null> {
  try {
    const m = await request(msg, 30000);
    const { t: _t, rid: _rid, ...info } = m;
    useDeck.setState((s) => (s.pcInfo ? { pcInfo: { ...s.pcInfo, streamlabs: info as unknown as StreamlabsInfo } } : {}));
    return info as unknown as StreamlabsInfo;
  } catch (e) {
    showToast((e as Error).message, 'error');
    return null;
  }
}

export function StreamlabsSection() {
  const info = usePcInfo()?.streamlabs;
  const [socketToken, setSocketToken] = useState('');
  const [accessToken, setAccessToken] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void streamlabsRequest({ t: 'edit.streamlabs.info' });
    const id = window.setInterval(() => void streamlabsRequest({ t: 'edit.streamlabs.info' }), 10000);
    return () => window.clearInterval(id);
  }, []);

  if (!info) return null;
  const save = async () => {
    setBusy(true);
    const r = await streamlabsRequest({ t: 'edit.streamlabs.set', socketToken, accessToken });
    setBusy(false);
    if (r) {
      setSocketToken('');
      setAccessToken('');
      showToast(tr('Token salvati sul PC'));
    }
  };
  const test = async () => {
    setBusy(true);
    const r = await streamlabsRequest({ t: 'edit.streamlabs.test' });
    setBusy(false);
    if (r?.test) showToast(r.test);
  };
  return (
    <Section title={tr('Streamlabs (alert e donazioni)')}>
      <p className={`muted small hint${info.hasSocketToken && !info.connected ? ' error-text' : ''}`}>
        {info.connected
          ? tr('✓ Eventi collegati (donazioni, follow, bits…).')
          : info.hasSocketToken
            ? tr('Non connesso{0}', info.error ? `: ${info.error}` : '…')
            : tr('Inserisci il Socket API Token per ricevere gli eventi.')}
      </p>
      <Toggle
        label={tr('Ricevi gli eventi di Streamlabs')}
        checked={info.enabled}
        onChange={(enabled) => void streamlabsRequest({ t: 'edit.streamlabs.set', enabled })}
      />
      <label className="field">
        <span>Socket API Token {info.hasSocketToken ? tr('(già salvato)') : ''}</span>
        <input className="input" type="password" autoComplete="off" value={socketToken} onChange={(e) => setSocketToken(e.target.value)} />
      </label>
      <label className="field">
        <span>{tr('Access Token (solo per gli alert personalizzati) {0}', info.hasAccessToken ? tr('(già salvato)') : '')}</span>
        <input className="input" type="password" autoComplete="off" value={accessToken} onChange={(e) => setAccessToken(e.target.value)} />
      </label>
      <div className="button-row">
        <button type="button" className="btn primary" disabled={busy || (!socketToken && !accessToken)} onClick={() => void save()}>
          {tr('Salva token')}
        </button>
        <button type="button" className="btn" disabled={busy} onClick={() => void test()}>
          {tr('Prova connessione')}
        </button>
        {(info.hasSocketToken || info.hasAccessToken) && (
          <button
            type="button"
            className="btn"
            disabled={busy}
            onClick={() =>
              window.confirm(tr('Cancellare i token di Streamlabs da questo PC?')) &&
              void streamlabsRequest({ t: 'edit.streamlabs.set', clearTokens: true })
            }
          >
            {tr('Cancella token')}
          </button>
        )}
      </div>
      <p className="muted small hint">
        {tr(
          "I token sono nella dashboard di Streamlabs (streamlabs.com → Impostazioni → API Settings → API Tokens). Restano cifrati sul PC: non arrivano ai telefoni e non finiscono nei profili esportati. Gli eventi Twitch arrivano anche da qui: se usi già l'integrazione Twitch, crea le regole in un solo posto per non averle doppie.",
        )}
      </p>
      {info.hasSocketToken && (
        <EventRulesEditor
          rules={info.rules}
          events={SL_EVENTS}
          onSave={async (rules: EventRule[]) => !!(await streamlabsRequest({ t: 'edit.streamlabs.set', rules }))}
        />
      )}
    </Section>
  );
}

// ---- Action -------------------------------------------------------------------------------

export const streamlabsDefault = (): StreamlabsAction => ({
  type: 'streamlabs',
  command: 'alert',
  alertType: 'donation',
  message: '',
  durationMs: 5000,
});

export function StreamlabsActionEditor({ value, onChange }: { value: StreamlabsAction; onChange: (a: StreamlabsAction) => void }) {
  const info = usePcInfo()?.streamlabs;
  const field = (k: 'message' | 'userMessage' | 'imageHref' | 'soundHref', label: string, placeholder = '') => (
    <label className="field">
      <span>{label}</span>
      <input
        className="input"
        placeholder={placeholder}
        value={value[k] ?? ''}
        onChange={(e) => onChange({ ...value, [k]: e.target.value })}
      />
    </label>
  );
  return (
    <>
      <Segmented
        value={value.command}
        options={[
          ['alert', tr('Alert personalizzato')],
          ['resetTotal', tr('Azzera totale donazioni')],
        ]}
        onChange={(command) => onChange(command === 'alert' ? streamlabsDefault() : { type: 'streamlabs', command })}
      />
      {value.command === 'alert' && (
        <>
          <label className="field">
            <span>{tr("Stile dell'alert")}</span>
            <select
              className="input"
              value={value.alertType ?? 'donation'}
              onChange={(e) => onChange({ ...value, alertType: e.target.value as StreamlabsAction['alertType'] })}
            >
              <option value="donation">{tr('Donazione')}</option>
              <option value="follow">Follow</option>
              <option value="subscription">{tr('Abbonamento')}</option>
              <option value="host">Host</option>
            </select>
          </label>
          {field('message', tr('Messaggio (*testo* evidenziato)'), tr('Grazie *a tutti*!'))}
          {field('userMessage', tr('Seconda riga (facoltativa)'))}
          {field('imageHref', tr('URL immagine/GIF (facoltativo)'), 'https://…')}
          {field('soundHref', tr('URL suono (facoltativo)'), 'https://…')}
          <div className="field-row">
            <span>{tr('Durata (secondi)')}</span>
            <input
              className="input num"
              type="number"
              min={1}
              max={60}
              value={Math.round((value.durationMs ?? 5000) / 1000)}
              onChange={(e) => onChange({ ...value, durationMs: Math.max(1, Number(e.target.value) || 5) * 1000 })}
            />
          </div>
          <p className={`muted small hint${info && !info.hasAccessToken ? ' error-text' : ''}`}>
            {info?.hasAccessToken
              ? tr(
                  'L’alert appare nell’Alert Box di Streamlabs. Variabili: {ora} {data} {contatore:Nome} e, nelle regole evento, {utente} {importo} {messaggio}.',
                )
              : tr('Serve l’Access Token di Streamlabs: inseriscilo nel pannello del profilo → Streamlabs.')}
          </p>
        </>
      )}
      {value.command === 'resetTotal' && (
        <p className="muted small hint">{tr('Azzera il totale delle donazioni ricevute da quando PadNook è aperto.')}</p>
      )}
    </>
  );
}
