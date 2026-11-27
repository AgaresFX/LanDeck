import { tr } from '../i18n';
// HTTP requests: the "Richiesta HTTP" action, HTTP dynamic keys, ready-made templates and the credentials
// stored (encrypted) on the PC. Secrets are referenced as {segreto:NAME}: the values never reach the profile.
import { useState } from 'react';
import { request } from '../net/client';
import { showToast, useDeck } from '../state/store';
import type { HttpAction, HttpRequestSpec, LiveHttpConfig, LiveRule, LiveValue } from '../types';
import { Section } from './controls';
import { usePcInfo } from './ExtraEditors';

const METHODS: HttpRequestSpec['method'][] = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD'];

export const httpActionDefault = (): HttpAction => ({ type: 'http', method: 'GET', url: '', bodyType: 'none', timeoutSec: 10 });
export const liveHttpDefault = (): LiveHttpConfig => ({
  method: 'GET',
  url: '',
  bodyType: 'none',
  timeoutSec: 10,
  intervalSec: 30,
  path: '',
});

// ---- Credentials --------------------------------------------------------------------------

function setSecretNames(names: string[]) {
  useDeck.setState((s) => (s.pcInfo ? { pcInfo: { ...s.pcInfo, secrets: names } } : {}));
}

export async function saveSecret(name: string, value: string) {
  const m = await request({ t: 'edit.secrets.set', name, value });
  setSecretNames(m.names ?? []);
}

/** Pannello del profilo: credentials kept only on this PC. */
export function SecretsSection() {
  const info = usePcInfo();
  const names = info?.secrets ?? [];
  const [name, setName] = useState('');
  const [value, setValue] = useState('');
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true);
    try {
      await saveSecret(name.trim(), value);
      showToast(tr('Credenziale «{0}» salvata sul PC', name.trim()));
      setName('');
      setValue('');
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };
  const remove = async (n: string) => {
    if (!window.confirm(tr('Eliminare la credenziale «{0}»? I tasti che la usano smetteranno di funzionare.', n))) return;
    try {
      const m = await request({ t: 'edit.secrets.delete', name: n });
      setSecretNames(m.names ?? []);
    } catch (e) {
      showToast((e as Error).message, 'error');
    }
  };
  if (!info) return null;
  return (
    <Section title={tr('Credenziali (solo su questo PC)')}>
      <p className="muted small hint">
        {tr(
          'Token, chiavi e URL segreti (webhook Discord, bot Telegram, Home Assistant…) restano cifrati sul PC con la protezione di Windows: non arrivano al telefono e non finiscono nei profili esportati. Nelle richieste HTTP scrivi {0} al loro posto.',
          <code>{'{segreto:Nome}'}</code>,
        )}
      </p>
      {names.map((n) => (
        <div key={n} className="secret-row">
          <code>{`{segreto:${n}}`}</code>
          <button type="button" className="btn mini" title={tr('Copia')} onClick={() => void copyText(`{segreto:${n}}`)}>
            {tr('Copia')}
          </button>
          <button type="button" className="btn mini" title={tr('Sostituisci il valore')} onClick={() => setName(n)}>
            {tr('Cambia')}
          </button>
          <button type="button" className="btn mini" title={tr('Elimina')} onClick={() => void remove(n)}>
            ✕
          </button>
        </div>
      ))}
      {names.length === 0 && <p className="muted small">{tr('Nessuna credenziale salvata.')}</p>}
      <div className="field-row">
        <span>{tr('Nome')}</span>
        <input className="input" value={name} placeholder={tr('es. Discord')} onChange={(e) => setName(e.target.value)} />
      </div>
      <label className="field">
        <span>
          {tr('Valore {0}', names.some((n) => n.toLowerCase() === name.trim().toLowerCase()) ? tr('(sostituisce quello salvato)') : '')}
        </span>
        <input className="input" type="password" autoComplete="off" value={value} onChange={(e) => setValue(e.target.value)} />
      </label>
      <button type="button" className="btn" disabled={busy || !name.trim() || !value} onClick={() => void save()}>
        {tr('Salva credenziale')}
      </button>
    </Section>
  );
}

async function copyText(text: string) {
  try {
    await navigator.clipboard.writeText(text);
    showToast(tr('Copiato: {0}', text));
  } catch {
    window.prompt(tr('Copia questo testo:'), text);
  }
}

// ---- Request fields -----------------------------------------------------------------------

function VarsHint() {
  const names = usePcInfo()?.secrets ?? [];
  return (
    <p className="muted small hint">
      {tr(
        'Variabili: {0} {1} {2} {3}{4} {5} {6}.{7}',
        <code>{'{ora}'}</code>,
        <code>{'{data}'}</code>,
        <code>{'{appunti}'}</code>,
        <code>{'{contatore:Nome}'}</code>,
        ' ',
        <code>{'{timer:Nome}'}</code>,
        <code>{'{segreto:Nome}'}</code>,
        ' ',
      )}
      {names.length > 0 ? (
        <>
          {tr('Credenziali salvate:{0}', ' ')}
          {names.map((n) => (
            <button key={n} type="button" className="btn mini" title={tr('Copia')} onClick={() => void copyText(`{segreto:${n}}`)}>
              {n}
            </button>
          ))}
        </>
      ) : (
        tr('Salva token e chiavi in «Credenziali» (pannello del profilo), mai direttamente qui: il profilo arriva anche al telefono.')
      )}
    </p>
  );
}

/** Warns when something that looks like a token is typed in clear text. */
function looksSecret(spec: HttpRequestSpec): boolean {
  const all = [spec.url, spec.body ?? '', ...(spec.headers ?? []).map((h) => h.value)].join(' ').replace(/\{segreto:[^}]*\}/g, '');
  return (
    /discord(app)?\.com\/api\/webhooks\/\d+\/[\w-]{20,}/i.test(all) ||
    /api\.telegram\.org\/bot\d+:[\w-]{20,}/i.test(all) ||
    /bearer\s+[\w.-]{20,}/i.test(all) ||
    /\btk_[a-z0-9]{20,}/i.test(all)
  );
}

export function HttpRequestFields<T extends HttpRequestSpec>({ value, onChange }: { value: T; onChange: (v: T) => void }) {
  const headers = value.headers ?? [];
  const bodyType = value.bodyType ?? 'none';
  const setHeaders = (h: { name: string; value: string }[]) => onChange({ ...value, headers: h });
  const hasBody = value.method !== 'GET' && value.method !== 'HEAD';
  return (
    <>
      <div className="inline-row">
        <select
          className="input http-method"
          value={value.method}
          onChange={(e) => onChange({ ...value, method: e.target.value as T['method'] })}
        >
          {METHODS.map((m) => (
            <option key={m}>{m}</option>
          ))}
        </select>
        <input
          className="input mono"
          placeholder="https://…"
          value={value.url}
          spellCheck={false}
          onChange={(e) => onChange({ ...value, url: e.target.value })}
        />
      </div>
      {looksSecret(value) && (
        <p className="small error-text">
          {tr(
            'Sembra che qui ci sia un token in chiaro: salvalo in «Credenziali» e scrivi {0} al suo posto.',
            <code>{'{segreto:Nome}'}</code>,
          )}
        </p>
      )}
      <div className="field">
        <span className="muted small">Header</span>
        {headers.map((h, i) => (
          <div key={i} className="inline-row">
            <input
              className="input mono"
              placeholder={tr('Nome')}
              value={h.name}
              onChange={(e) => setHeaders(headers.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))}
            />
            <input
              className="input mono"
              placeholder={tr('Valore')}
              value={h.value}
              onChange={(e) => setHeaders(headers.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)))}
            />
            <button type="button" className="btn mini" title={tr('Rimuovi')} onClick={() => setHeaders(headers.filter((_, j) => j !== i))}>
              ✕
            </button>
          </div>
        ))}
        <button type="button" className="btn mini" onClick={() => setHeaders([...headers, { name: '', value: '' }])}>
          + Header
        </button>
      </div>
      {hasBody && (
        <>
          <label className="field">
            <span>{tr('Corpo')}</span>
            <select
              className="input"
              value={bodyType}
              onChange={(e) => onChange({ ...value, bodyType: e.target.value as HttpRequestSpec['bodyType'] })}
            >
              <option value="none">{tr('Nessuno')}</option>
              <option value="json">JSON</option>
              <option value="text">{tr('Testo')}</option>
              <option value="form">{tr('Form (chiave=valore, uno per riga)')}</option>
            </select>
          </label>
          {bodyType !== 'none' && (
            <textarea
              className="input textarea mono"
              rows={5}
              spellCheck={false}
              value={value.body ?? ''}
              placeholder={
                bodyType === 'json'
                  ? tr('{ "testo": "Sono le {ora}" }')
                  : bodyType === 'form'
                    ? tr('chiave=valore')
                    : tr('Testo del messaggio')
              }
              onChange={(e) => onChange({ ...value, body: e.target.value })}
            />
          )}
        </>
      )}
      <div className="field-row">
        <span>{tr('Timeout (secondi)')}</span>
        <input
          className="input num"
          type="number"
          min={1}
          max={60}
          value={value.timeoutSec ?? 10}
          onChange={(e) => onChange({ ...value, timeoutSec: Math.max(1, Math.min(60, Number(e.target.value) || 10)) })}
        />
      </div>
      <VarsHint />
    </>
  );
}

// ---- Test ---------------------------------------------------------------------------------

interface TestResult {
  ok: boolean;
  status: number;
  ms?: number;
  error?: string;
  body?: string;
  contentType?: string;
  live?: LiveValue;
  pathFound?: boolean;
}

export function HttpTest({ spec, path }: { spec: HttpRequestSpec; path?: string }) {
  const [busy, setBusy] = useState(false);
  const [res, setRes] = useState<TestResult | null>(null);
  const run = async () => {
    if (
      spec.method !== 'GET' &&
      spec.method !== 'HEAD' &&
      !window.confirm(tr('La prova invia davvero la richiesta (es. pubblica il messaggio). Continuare?'))
    )
      return;
    setBusy(true);
    try {
      const { type: _t, ...req } = spec as HttpRequestSpec & { type?: string };
      setRes((await request({ t: 'edit.http.test', request: { ...req, path } }, 70000)) as unknown as TestResult);
    } catch (e) {
      setRes({ ok: false, status: 0, error: (e as Error).message });
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="field">
      <button type="button" className="btn" disabled={busy || !spec.url.trim()} onClick={() => void run()}>
        {busy ? tr('Invio…') : tr('Prova richiesta')}
      </button>
      {res && (
        <div className={`http-result${res.ok ? '' : ' bad'}`}>
          {res.error ? (
            <b>{res.error}</b>
          ) : (
            <>
              <b>
                HTTP {res.status} · {res.ms} ms
              </b>
              {path !== undefined && (
                <div>
                  {tr('Valore sul tasto: {0}', <b>{res.live?.text ?? '—'}</b>)}
                  {res.pathFound === false && (
                    <span className="error-text"> {tr('(percorso «{0}» non trovato nella risposta)', path)}</span>
                  )}
                </div>
              )}
              {res.body ? <pre>{res.body}</pre> : <div className="muted small">{tr('Risposta vuota')}</div>}
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ---- Templates ----------------------------------------------------------------------------

interface TField {
  id: string;
  label: string;
  placeholder?: string;
  default?: string;
  /** Stored as a credential: the field asks for the credential name and the value. */
  secret?: boolean;
  optional?: boolean;
  options?: [string, string][];
  hint?: string;
}

type Vals = Record<string, string>;

export interface ActionTemplate {
  id: string;
  label: string;
  hint: string;
  fields: TField[];
  build: (v: Vals) => HttpAction;
}

export interface LiveTemplate {
  id: string;
  label: string;
  hint: string;
  fields: TField[];
  build: (v: Vals) => { http: LiveHttpConfig; label: string; rules: LiveRule[]; onPress?: HttpAction };
}

const VAR_RE = /(\{(?:ora|data|appunti|contatore:[^{}]+|timer:[^{}]+|segreto:[^{}]+)\})/i;

/** URL-encodes the literal text, keeping {variables} (the PC encodes their values). */
const enc = (s: string) =>
  s
    .split(VAR_RE)
    .map((p, i) => (i % 2 ? p : encodeURIComponent(p)))
    .join('');

const trimSlash = (s: string) => s.trim().replace(/\/+$/, '');
const secretRef = (v: Vals, id: string) => `{segreto:${v[`${id}.name`]}}`;
const json = (o: unknown) => JSON.stringify(o, null, 2);
const RED = '#b3261e';
const GREEN = '#1f7a4a';
const offlineRule: LiveRule = { when: 'status', value: '0', style: { bgColor: '#555555', labelText: 'Offline' } };

const haFields: TField[] = [
  {
    id: 'base',
    label: tr('Indirizzo di Home Assistant'),
    default: 'http://homeassistant.local:8123',
    hint: tr('Porta 8123 (o 80 con Home Assistant OS dietro proxy).'),
  },
  {
    id: 'token',
    label: tr('Token di accesso a lunga durata'),
    secret: true,
    default: 'HomeAssistant',
    hint: tr('Home Assistant → il tuo profilo → Sicurezza → Token di accesso a lunga durata → Crea token.'),
  },
];
const haHeaders = (v: Vals) => [{ name: 'Authorization', value: `Bearer ${secretRef(v, 'token')}` }];

const shellyFields: TField[] = [
  { id: 'ip', label: tr('Indirizzo IP dello Shelly'), placeholder: '192.168.1.50' },
  {
    id: 'gen',
    label: tr('Generazione'),
    default: 'gen2',
    options: [
      ['gen2', 'Gen2 / Gen3 / Gen4 (Plus, Pro, Mini)'],
      ['gen1', 'Gen1 (Shelly 1, 2.5, Plug S…)'],
    ],
  },
  { id: 'ch', label: tr('Canale (relè)'), default: '0' },
];
const shellyCmd = (v: Vals, cmd: string): HttpAction => {
  const host = `http://${v.ip
    .trim()
    .replace(/^https?:\/\//, '')
    .replace(/\/+$/, '')}`;
  const ch = v.ch.trim() || '0';
  const url =
    v.gen === 'gen1'
      ? `${host}/relay/${ch}?turn=${cmd}`
      : cmd === 'toggle'
        ? `${host}/rpc/Switch.Toggle?id=${ch}`
        : `${host}/rpc/Switch.Set?id=${ch}&on=${cmd === 'on'}`;
  return { type: 'http', method: 'GET', url, bodyType: 'none', timeoutSec: 5 };
};

export const ACTION_TEMPLATES: ActionTemplate[] = [
  {
    id: 'discord',
    label: 'Discord (webhook)',
    hint: tr('Sul server Discord: Impostazioni canale → Integrazioni → Webhook → Nuovo webhook → Copia URL del webhook.'),
    fields: [
      { id: 'url', label: tr('URL del webhook'), secret: true, default: 'Discord' },
      { id: 'msg', label: tr('Messaggio (max 2000 caratteri)'), default: tr('Sono in diretta! ({ora})') },
      { id: 'user', label: tr('Nome del mittente'), optional: true, placeholder: 'PadNook' },
    ],
    build: (v) => ({
      type: 'http',
      method: 'POST',
      url: secretRef(v, 'url'),
      bodyType: 'json',
      body: json({ content: v.msg, ...(v.user?.trim() ? { username: v.user.trim() } : {}) }),
      timeoutSec: 10,
    }),
  },
  {
    id: 'ntfy',
    label: tr('ntfy.sh (notifica sul telefono)'),
    hint: tr(
      "Installa l'app ntfy e iscriviti allo stesso argomento: chi conosce il nome dell'argomento può leggerlo, sceglilo difficile da indovinare.",
    ),
    fields: [
      { id: 'server', label: 'Server', default: 'https://ntfy.sh' },
      { id: 'topic', label: tr('Argomento (topic)'), placeholder: 'padnook-mario-8k2q' },
      { id: 'title', label: tr('Titolo'), optional: true, default: 'PadNook' },
      { id: 'msg', label: tr('Messaggio'), default: tr('Promemoria delle {ora}') },
      {
        id: 'prio',
        label: tr('Priorità'),
        default: '3',
        options: [
          ['1', tr('Minima')],
          ['2', tr('Bassa')],
          ['3', tr('Normale')],
          ['4', tr('Alta')],
          ['5', tr('Urgente')],
        ],
      },
      { id: 'tags', label: tr('Tag / emoji (separati da virgola)'), optional: true, placeholder: 'bell,warning' },
      { id: 'token', label: tr('Token di accesso (solo argomenti protetti)'), secret: true, optional: true, default: 'ntfy' },
    ],
    build: (v) => {
      const q = [
        v.title?.trim() ? `title=${enc(v.title.trim())}` : '',
        v.prio && v.prio !== '3' ? `priority=${v.prio}` : '',
        v.tags?.trim() ? `tags=${enc(v.tags.replace(/\s/g, ''))}` : '',
      ].filter(Boolean);
      return {
        type: 'http',
        method: 'POST',
        url: `${trimSlash(v.server || 'https://ntfy.sh')}/${enc(v.topic.trim())}${q.length ? `?${q.join('&')}` : ''}`,
        headers: v['token.value'] || v['token.reuse'] ? [{ name: 'Authorization', value: `Bearer ${secretRef(v, 'token')}` }] : [],
        bodyType: 'text',
        body: v.msg,
        timeoutSec: 10,
      };
    },
  },
  {
    id: 'telegram',
    label: 'Telegram (bot)',
    hint: tr(
      'Crea il bot con @BotFather (/newbot) e copia il token. Scrivi un messaggio al bot, poi apri https://api.telegram.org/bot<TOKEN>/getUpdates per leggere il chat_id (anche negativo per i gruppi).',
    ),
    fields: [
      { id: 'token', label: tr('Token del bot'), secret: true, default: 'Telegram' },
      { id: 'chat', label: 'chat_id', placeholder: '123456789' },
      { id: 'msg', label: tr('Messaggio (max 4096 caratteri)'), default: tr('Ciao dalle {ora}') },
    ],
    build: (v) => ({
      type: 'http',
      method: 'POST',
      url: `https://api.telegram.org/bot${secretRef(v, 'token')}/sendMessage`,
      bodyType: 'json',
      body: json({ chat_id: /^-?\d+$/.test(v.chat.trim()) ? Number(v.chat.trim()) : v.chat.trim(), text: v.msg }),
      timeoutSec: 10,
    }),
  },
  {
    id: 'ha',
    label: tr('Home Assistant (servizio)'),
    hint: tr('Chiama un servizio, es. light.toggle, switch.turn_on, scene.turn_on, script.turn_on.'),
    fields: [
      ...haFields,
      { id: 'service', label: tr('Servizio (dominio.servizio)'), default: 'light.toggle' },
      { id: 'entity', label: tr('Entità'), placeholder: 'light.soggiorno' },
    ],
    build: (v) => {
      const [domain, service] = v.service.trim().split('.', 2);
      return {
        type: 'http',
        method: 'POST',
        url: `${trimSlash(v.base)}/api/services/${enc(domain ?? '')}/${enc(service ?? '')}`,
        headers: haHeaders(v),
        bodyType: 'json',
        body: json({ entity_id: v.entity.trim() }),
        timeoutSec: 10,
      };
    },
  },
  {
    id: 'shelly',
    label: tr('Shelly (relè / presa)'),
    hint: tr(
      'Comando diretto in rete locale (API HTTP dello Shelly, senza cloud). Se hai attivato la password sullo Shelly servono gli header di autenticazione.',
    ),
    fields: [
      ...shellyFields,
      {
        id: 'cmd',
        label: tr('Comando'),
        default: 'toggle',
        options: [
          ['toggle', tr('Accendi / spegni')],
          ['on', tr('Accendi')],
          ['off', tr('Spegni')],
        ],
      },
    ],
    build: (v) => shellyCmd(v, v.cmd),
  },
  {
    id: 'get',
    label: tr('GET generica'),
    hint: tr('Apre un URL dal PC (senza browser), es. per attivare un automatismo.'),
    fields: [{ id: 'url', label: 'URL', placeholder: 'https://…' }],
    build: (v) => ({ type: 'http', method: 'GET', url: v.url.trim(), bodyType: 'none', timeoutSec: 10 }),
  },
];

export const LIVE_TEMPLATES: LiveTemplate[] = [
  {
    id: 'json',
    label: tr('Valore da un JSON (GET)'),
    hint: tr(
      'Es. Open-Meteo: https://api.open-meteo.com/v1/forecast?latitude=45.46&longitude=9.19&current=temperature_2m con percorso current.temperature_2m.',
    ),
    fields: [
      { id: 'url', label: 'URL', placeholder: 'https://…' },
      { id: 'path', label: tr('Percorso del valore'), optional: true, placeholder: 'current.temperature' },
      { id: 'interval', label: tr('Aggiorna ogni (secondi)'), default: '60' },
    ],
    build: (v) => ({
      http: { ...liveHttpDefault(), url: v.url.trim(), path: v.path?.trim() ?? '', intervalSec: Math.max(5, Number(v.interval) || 60) },
      label: '{text}',
      rules: [offlineRule],
    }),
  },
  {
    id: 'ha',
    label: tr("Home Assistant (stato di un'entità)"),
    hint: tr('Mostra lo stato (es. on/off, 21.5). Premendo il tasto puoi anche chiamare un servizio.'),
    fields: [
      ...haFields,
      { id: 'entity', label: tr('Entità'), placeholder: 'sensor.temperatura_soggiorno' },
      {
        id: 'toggle',
        label: tr('Alla pressione'),
        default: '',
        options: [
          ['', tr('Niente')],
          ['homeassistant.toggle', tr('Accendi / spegni (toggle)')],
        ],
      },
    ],
    build: (v) => {
      const entity = v.entity.trim();
      return {
        http: {
          ...liveHttpDefault(),
          url: `${trimSlash(v.base)}/api/states/${enc(entity)}`,
          headers: haHeaders(v),
          path: 'state',
          intervalSec: 10,
        },
        label: '{text}',
        rules: [
          { when: 'eq', value: 'on', style: { bgColor: GREEN } },
          { when: 'eq', value: 'unavailable', style: { bgColor: '#555555' } },
          offlineRule,
        ],
        onPress: v.toggle
          ? {
              type: 'http',
              method: 'POST',
              url: `${trimSlash(v.base)}/api/services/homeassistant/toggle`,
              headers: haHeaders(v),
              bodyType: 'json',
              body: json({ entity_id: entity }),
              timeoutSec: 10,
            }
          : undefined,
      };
    },
  },
  {
    id: 'shelly',
    label: tr('Shelly (acceso / spento)'),
    hint: tr('Il tasto diventa verde quando il relè è acceso; premendolo lo accendi o lo spegni.'),
    fields: shellyFields,
    build: (v) => {
      const host = `http://${v.ip
        .trim()
        .replace(/^https?:\/\//, '')
        .replace(/\/+$/, '')}`;
      const ch = v.ch.trim() || '0';
      return {
        http: {
          ...liveHttpDefault(),
          url: v.gen === 'gen1' ? `${host}/relay/${ch}` : `${host}/rpc/Switch.GetStatus?id=${ch}`,
          path: v.gen === 'gen1' ? 'ison' : 'output',
          intervalSec: 5,
          timeoutSec: 5,
        },
        label: '{text}',
        rules: [offlineRule, { when: 'true', style: { bgColor: GREEN } }],
        onPress: shellyCmd(v, 'toggle'),
      };
    },
  },
  {
    id: 'status',
    label: tr('Sito raggiungibile (codice di stato)'),
    hint: tr('Verde se il sito risponde 2xx, rosso con errori o senza risposta.'),
    fields: [
      { id: 'url', label: 'URL', placeholder: 'https://…' },
      { id: 'interval', label: tr('Controlla ogni (secondi)'), default: '60' },
    ],
    build: (v) => ({
      http: { ...liveHttpDefault(), url: v.url.trim(), intervalSec: Math.max(5, Number(v.interval) || 60) },
      label: '{sub}',
      rules: [
        { when: 'status', value: '2xx', style: { bgColor: GREEN } },
        { when: 'false', style: { bgColor: RED } },
      ],
    }),
  },
];

/** Picks a template, asks its fields, saves the credentials, then calls onApply with the values. */
export function TemplatePicker<T extends { id: string; label: string; hint: string; fields: TField[] }>({
  templates,
  onApply,
}: {
  templates: T[];
  onApply: (t: T, v: Vals) => void;
}) {
  const names = usePcInfo()?.secrets ?? [];
  const [id, setId] = useState('');
  const [vals, setVals] = useState<Vals>({});
  const [busy, setBusy] = useState(false);
  const t = templates.find((x) => x.id === id);
  const pick = (next: string) => {
    setId(next);
    const tt = templates.find((x) => x.id === next);
    const v: Vals = {};
    for (const f of tt?.fields ?? []) {
      if (f.secret) v[`${f.id}.name`] = f.default ?? f.id;
      else v[f.id] = f.default ?? (f.options ? f.options[0][0] : '');
    }
    setVals(v);
  };
  const set = (k: string, x: string) => setVals((v) => ({ ...v, [k]: x }));
  const has = (n: string) => names.some((x) => x.toLowerCase() === n.trim().toLowerCase());

  const apply = async () => {
    if (!t) return;
    const v = { ...vals };
    for (const f of t.fields) {
      if (f.secret) {
        const name = (v[`${f.id}.name`] ?? '').trim();
        const value = v[`${f.id}.value`] ?? '';
        if (value) continue;
        if (name && has(name)) {
          v[`${f.id}.reuse`] = '1';
          continue;
        }
        if (!f.optional) return showToast(tr('Inserisci «{0}»', f.label), 'error');
      } else if (!f.optional && !f.options && !(v[f.id] ?? '').trim()) return showToast(tr('Inserisci «{0}»', f.label), 'error');
    }
    setBusy(true);
    try {
      for (const f of t.fields)
        if (f.secret && v[`${f.id}.value`]) {
          const name = v[`${f.id}.name`].trim();
          if (!name) throw new Error(tr('Dai un nome alla credenziale «{0}»', f.label));
          await saveSecret(name, v[`${f.id}.value`]);
          v[`${f.id}.name`] = name;
        }
      onApply(t, v);
      showToast(tr('Modello «{0}» applicato', t.label));
      setId('');
      setVals({});
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="field">
      <select className="input" value={id} onChange={(e) => pick(e.target.value)}>
        <option value="">{tr('Modello pronto…')}</option>
        {templates.map((x) => (
          <option key={x.id} value={x.id}>
            {x.label}
          </option>
        ))}
      </select>
      {t && (
        <div className="template-form">
          <p className="muted small hint">{t.hint}</p>
          {t.fields.map((f) =>
            f.secret ? (
              <div key={f.id} className="field">
                <span>
                  {f.label}
                  {f.optional ? tr(' (facoltativo)') : ''}
                </span>
                <input
                  className="input"
                  type="password"
                  autoComplete="off"
                  placeholder={has(vals[`${f.id}.name`] ?? '') ? tr('già salvato: lascia vuoto per riusarlo') : ''}
                  value={vals[`${f.id}.value`] ?? ''}
                  onChange={(e) => set(`${f.id}.value`, e.target.value)}
                />
                <div className="inline-row">
                  <span className="muted small">{tr('Salva come credenziale')}</span>
                  <input className="input" value={vals[`${f.id}.name`] ?? ''} onChange={(e) => set(`${f.id}.name`, e.target.value)} />
                </div>
                {f.hint && <span className="muted small">{f.hint}</span>}
              </div>
            ) : f.options ? (
              <label key={f.id} className="field">
                <span>{f.label}</span>
                <select className="input" value={vals[f.id] ?? ''} onChange={(e) => set(f.id, e.target.value)}>
                  {f.options.map(([o, l]) => (
                    <option key={o} value={o}>
                      {l}
                    </option>
                  ))}
                </select>
              </label>
            ) : (
              <label key={f.id} className="field">
                <span>
                  {f.label}
                  {f.optional ? tr(' (facoltativo)') : ''}
                </span>
                <input className="input" placeholder={f.placeholder} value={vals[f.id] ?? ''} onChange={(e) => set(f.id, e.target.value)} />
                {f.hint && <span className="muted small">{f.hint}</span>}
              </label>
            ),
          )}
          <div className="button-row">
            <button type="button" className="btn primary" disabled={busy} onClick={() => void apply()}>
              {tr('Applica')}
            </button>
            <button type="button" className="btn" onClick={() => setId('')}>
              {tr('Annulla')}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

/** The "Richiesta HTTP" action. */
export function HttpEditor({ value, onChange }: { value: HttpAction; onChange: (a: HttpAction) => void }) {
  return (
    <>
      <TemplatePicker templates={ACTION_TEMPLATES} onApply={(t, v) => onChange(t.build(v))} />
      <HttpRequestFields value={value} onChange={onChange} />
      <HttpTest spec={value} />
    </>
  );
}
