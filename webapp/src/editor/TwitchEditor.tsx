import { tr } from '../i18n';
// Twitch: profile panel (login with a device code, status, event rules) and the "Twitch" action editor.
import { useEffect, useState } from 'react';
import { request } from '../net/client';
import { showToast, useDeck } from '../state/store';
import type { Action, EventRule, StreamEventKind, TwitchAction, TwitchCommand, TwitchInfo } from '../types';
import { ActionEditor } from './ActionEditor';
import { ColorField, Section, Segmented } from './controls';
import { usePcInfo } from './ExtraEditors';

const PURPLE = '#9146ff';

function setTwitchInfo(info: TwitchInfo) {
  useDeck.setState((s) => (s.pcInfo ? { pcInfo: { ...s.pcInfo, twitch: info } } : {}));
}

async function twitchRequest(msg: object): Promise<TwitchInfo | null> {
  try {
    const m = await request(msg, 30000);
    const { t: _t, rid: _rid, ...info } = m;
    setTwitchInfo(info as unknown as TwitchInfo);
    return info as unknown as TwitchInfo;
  } catch (e) {
    showToast((e as Error).message, 'error');
    return null;
  }
}

// ---- Profile panel ------------------------------------------------------------------------

export function TwitchSection() {
  const info = usePcInfo()?.twitch;
  const [clientId, setClientId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const pending = info?.pending;

  // Fresh state when the panel opens, then every 10 s (connection and events change on their own).
  useEffect(() => {
    void twitchRequest({ t: 'edit.twitch.info' });
    const id = window.setInterval(() => void twitchRequest({ t: 'edit.twitch.info' }), 10000);
    return () => window.clearInterval(id);
  }, []);

  // While the device code is waiting, refresh the state every 2 s.
  useEffect(() => {
    if (!pending) return;
    const id = window.setInterval(() => void twitchRequest({ t: 'edit.twitch.info' }), 2000);
    return () => window.clearInterval(id);
  }, [pending]);

  if (!info) return null;
  const run = async (msg: object, ok?: string) => {
    setBusy(true);
    const r = await twitchRequest(msg);
    setBusy(false);
    if (r && ok) showToast(ok);
  };
  const needsClientId = !info.clientId && !info.hasDefaultClientId;

  return (
    <Section title="Twitch">
      <p className={`muted small hint${info.error ? ' error-text' : ''}`}>
        {info.connected ? tr('✓ Connesso come {0}.', info.name ?? info.login) : (info.error ?? tr('Non connesso.'))}
        {info.connected && info.error ? ` ${info.error}` : ''}
      </p>
      {info.connected && (
        <p className={`muted small hint${info.eventsub.connected ? '' : ' error-text'}`}>
          {tr('Eventi in tempo reale: {0}', info.eventsub.connected ? tr('attivi') : (info.eventsub.error ?? tr('in connessione…')))}
          {info.eventsub.failed.length > 0 && (
            <> {tr('· non disponibili: {0}', info.eventsub.failed.map((f) => f.split(':')[0]).join(', '))}</>
          )}
        </p>
      )}
      {info.connected && info.missingScopes.length > 0 && (
        <p className="small error-text">
          {tr('Mancano dei permessi ({0}): esci e accedi di nuovo per concederli.', info.missingScopes.join(', '))}
        </p>
      )}

      {pending ? (
        <div className="twitch-code">
          <span className="muted small">{tr('Sul PC si è aperta la pagina di Twitch: conferma questo codice')}</span>
          <b>{pending.userCode}</b>
          <a href={pending.uri} target="_blank" rel="noreferrer" className="small">
            {pending.uri.replace(/\?.*$/, '')}
          </a>
          <button type="button" className="btn" onClick={() => void run({ t: 'edit.twitch.cancel' })}>
            {tr('Annulla')}
          </button>
        </div>
      ) : (
        <div className="button-row">
          {!info.connected ? (
            <button
              type="button"
              className="btn primary"
              disabled={busy || needsClientId}
              onClick={() => void run({ t: 'edit.twitch.login' })}
            >
              {tr('Accedi con Twitch')}
            </button>
          ) : (
            <>
              <button
                type="button"
                className="btn"
                disabled={busy}
                onClick={() => void run({ t: 'edit.twitch.test' }, tr('Twitch risponde correttamente'))}
              >
                {tr('Prova connessione')}
              </button>
              <button
                type="button"
                className="btn"
                disabled={busy}
                onClick={() => window.confirm(tr('Scollegare Twitch da PadNook?')) && void run({ t: 'edit.twitch.logout' })}
              >
                {tr('Esci')}
              </button>
            </>
          )}
        </div>
      )}

      <label className="field">
        <span>{tr("Client ID dell'app Twitch {0}", info.hasDefaultClientId ? tr('(facoltativo: vuoto = quello di PadNook)') : '')}</span>
        <div className="inline-row">
          <input
            className="input mono"
            value={clientId ?? info.clientId}
            spellCheck={false}
            onChange={(e) => setClientId(e.target.value)}
            placeholder={tr('dev.twitch.tv/console → la tua app → Client ID')}
          />
          <button
            type="button"
            className="btn"
            disabled={clientId === null || busy}
            onClick={() =>
              void run({ t: 'edit.twitch.set', clientId: clientId ?? '' }, tr('Client ID salvato')).then(() => setClientId(null))
            }
          >
            {tr('Salva')}
          </button>
        </div>
      </label>
      <p className="muted small hint">
        {tr(
          'Il login usa un codice da confermare su twitch.tv/activate: PadNook non vede mai la tua password e i token restano cifrati sul PC. {0}',
          needsClientId &&
            tr(
              ' Prima registra l’app su dev.twitch.tv/console (tipo di client «Public», URL di reindirizzamento http://localhost) e incolla qui il Client ID.',
            ),
        )}
      </p>

      {info.connected && <EventRulesEditor rules={info.rules} />}
    </Section>
  );
}

// ---- Event rules --------------------------------------------------------------------------

const EVENTS: [StreamEventKind, string, string?][] = [
  ['follow', tr('Nuovo follower')],
  ['sub', tr('Nuovo abbonamento')],
  ['resub', tr('Rinnovo abbonamento'), tr('mesi minimi')],
  ['gift', tr('Abbonamenti regalati'), tr('numero minimo')],
  ['raid', tr('Raid ricevuto'), tr('spettatori minimi')],
  ['cheer', 'Bits (cheer)', tr('bits minimi')],
  ['redeem', tr('Riscatto punti canale'), tr('nome del premio (contiene)')],
  ['online', tr('Diretta iniziata')],
  ['offline', tr('Diretta terminata')],
];

function useAllKeys() {
  const profiles = useDeck((s) => s.profiles);
  return profiles.flatMap((p) =>
    p.pages.flatMap((pg) =>
      pg.keys
        .filter((k) => k.kind !== 'slider')
        .map((k) => ({
          id: k.id,
          label: `${p.name} › ${pg.name || 'Home'} › ${
            k.style.label.text
              .replace(/\n/g, ' ')
              .replace(/\{[a-z]+\}/g, '…')
              .trim() || tr('(senza testo)')
          }`,
        })),
    ),
  );
}

export function EventRulesEditor({
  rules,
  events = EVENTS,
  onSave = async (rules) => !!(await twitchRequest({ t: 'edit.twitch.set', rules })),
}: {
  rules: EventRule[];
  events?: [StreamEventKind, string, string?][];
  /** Saves the rules on the PC; true when saved. */
  onSave?: (rules: EventRule[]) => Promise<boolean>;
}) {
  const [draft, setDraft] = useState<EventRule[] | null>(null);
  const list = draft ?? rules;
  const keys = useAllKeys();
  const edit = (i: number, patch: Partial<EventRule>) => setDraft(list.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const save = async () => {
    if (await onSave(list)) {
      setDraft(null);
      showToast(tr('Regole degli eventi salvate'));
    }
  };
  return (
    <div className="field">
      <b className="small">{tr('Quando succede qualcosa')}</b>
      <p className="muted small hint">
        {tr(
          "Fai lampeggiare un tasto e/o esegui un'azione (anche una macro). Nei testi puoi usare {0},{1} {2}, {3}, {4}.",
          <code>{'{utente}'}</code>,
          ' ',
          <code>{'{importo}'}</code>,
          <code>{'{premio}'}</code>,
          <code>{'{messaggio}'}</code>,
        )}
      </p>
      {list.map((r, i) => {
        const ev = events.find((e) => e[0] === r.event);
        return (
          <div key={r.id} className="rule">
            <div className="rule-head">
              <select className="input" value={r.event} onChange={(e) => edit(i, { event: e.target.value as StreamEventKind, filter: '' })}>
                {events.map(([v, l]) => (
                  <option key={v} value={v}>
                    {l}
                  </option>
                ))}
              </select>
              <button
                type="button"
                className="btn mini"
                title={tr('Elimina regola')}
                onClick={() => setDraft(list.filter((_, j) => j !== i))}
              >
                ✕
              </button>
            </div>
            {ev?.[2] && (
              <input
                className="input"
                placeholder={tr('Filtro: {0} (facoltativo)', ev[2])}
                value={r.filter ?? ''}
                onChange={(e) => edit(i, { filter: e.target.value })}
              />
            )}
            <label className="field">
              <span>{tr('Fai lampeggiare il tasto')}</span>
              <select className="input" value={r.flashKeyId ?? ''} onChange={(e) => edit(i, { flashKeyId: e.target.value || undefined })}>
                <option value="">{tr('— nessuno —')}</option>
                {keys.map((k) => (
                  <option key={k.id} value={k.id}>
                    {k.label}
                  </option>
                ))}
              </select>
            </label>
            {r.flashKeyId && (
              <>
                <ColorField value={r.flashColor ?? PURPLE} onChange={(c) => edit(i, { flashColor: c })} />
                <div className="field-row">
                  <span>{tr('Durata del lampeggio (secondi)')}</span>
                  <input
                    className="input num"
                    type="number"
                    min={1}
                    max={30}
                    value={r.flashSec ?? 3}
                    onChange={(e) => edit(i, { flashSec: Math.max(1, Math.min(30, Number(e.target.value) || 3)) })}
                  />
                </div>
              </>
            )}
            <ActionEditor serverOnly value={r.action ?? null} onChange={(a: Action | null) => edit(i, { action: a })} />
          </div>
        );
      })}
      <div className="button-row">
        <button
          type="button"
          className="btn"
          onClick={() =>
            setDraft([...list, { id: `r${Date.now().toString(36)}`, event: events[0][0], flashColor: PURPLE, flashSec: 3, action: null }])
          }
        >
          {tr('+ Aggiungi regola')}
        </button>
        {draft && (
          <>
            <button type="button" className="btn primary" onClick={() => void save()}>
              {tr('Salva regole')}
            </button>
            <button type="button" className="btn" onClick={() => setDraft(null)}>
              {tr('Annulla')}
            </button>
          </>
        )}
      </div>
    </div>
  );
}

// ---- Action editor ------------------------------------------------------------------------

const GROUPS: [string, [TwitchCommand, string][]][] = [
  [
    tr('Diretta'),
    [
      ['marker', tr('Marker nel video')],
      ['clip', tr('Crea clip (link negli appunti del PC)')],
      ['channel', tr('Titolo, categoria e tag')],
      ['commercial', tr('Pubblicità')],
    ],
  ],
  [
    'Chat',
    [
      ['chat', tr('Messaggio in chat')],
      ['announce', tr('Annuncio in chat')],
      ['chatMode', tr('Modalità chat (slow, follower, sub, emote)')],
      ['shoutout', tr('Shoutout a un canale')],
    ],
  ],
  [
    'Raid',
    [
      ['raid', tr('Avvia un raid')],
      ['raidCancel', tr('Annulla il raid')],
    ],
  ],
  [
    tr('Sondaggi e pronostici'),
    [
      ['poll', tr('Crea sondaggio')],
      ['pollEnd', tr('Termina sondaggio')],
      ['prediction', tr('Crea pronostico')],
      ['predictionLock', tr('Blocca le puntate del pronostico')],
      ['predictionResolve', tr('Risolvi pronostico (esito vincente)')],
      ['predictionCancel', tr('Annulla pronostico (rimborsa)')],
    ],
  ],
];

export const twitchDefault = (command: TwitchCommand = 'marker'): TwitchAction => {
  switch (command) {
    case 'commercial':
      return { type: 'twitch', command, length: 60 };
    case 'announce':
      return { type: 'twitch', command, message: '', color: 'primary' };
    case 'chatMode':
      return { type: 'twitch', command, mode: 'slow', set: 'toggle', seconds: 30 };
    case 'poll':
      return { type: 'twitch', command, title: '', choices: [tr('Sì'), 'No'], durationSec: 120 };
    case 'prediction':
      return { type: 'twitch', command, title: '', outcomes: [tr('Sì'), 'No'], windowSec: 120 };
    case 'predictionResolve':
      return { type: 'twitch', command, outcome: 1 };
    default:
      return { type: 'twitch', command };
  }
};

function ListField({
  label,
  value,
  min,
  max,
  maxLen,
  onChange,
}: {
  label: string;
  value: string[];
  min: number;
  max: number;
  maxLen: number;
  onChange: (v: string[]) => void;
}) {
  return (
    <div className="field">
      <span className="muted small">{tr('{0} ({1}–{2}, max {3} caratteri)', label, min, max, maxLen)}</span>
      {value.map((c, i) => (
        <div key={i} className="inline-row">
          <input
            className="input"
            maxLength={maxLen}
            value={c}
            onChange={(e) => onChange(value.map((x, j) => (j === i ? e.target.value : x)))}
          />
          {value.length > min && (
            <button type="button" className="btn mini" onClick={() => onChange(value.filter((_, j) => j !== i))}>
              ✕
            </button>
          )}
        </div>
      ))}
      {value.length < max && (
        <button type="button" className="btn mini" onClick={() => onChange([...value, ''])}>
          {tr('+ Aggiungi')}
        </button>
      )}
    </div>
  );
}

function CategoryPicker({ value, onChange }: { value: TwitchAction; onChange: (a: TwitchAction) => void }) {
  const [q, setQ] = useState('');
  const [busy, setBusy] = useState(false);
  const [results, setResults] = useState<{ id: string; name: string; art?: string }[]>([]);
  const search = async () => {
    setBusy(true);
    try {
      const m = await request({ t: 'edit.twitch.categories', q });
      setResults(m.results ?? []);
      if (!m.results?.length) showToast(tr('Nessuna categoria trovata'), 'error');
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="field">
      <span className="muted small">
        {tr('Categoria:')} {value.gameName ? <b>{value.gameName}</b> : tr('invariata')}{' '}
        {value.gameId && (
          <button type="button" className="btn mini" onClick={() => onChange({ ...value, gameId: undefined, gameName: undefined })}>
            ✕
          </button>
        )}
      </span>
      <form
        className="inline-row"
        onSubmit={(e) => {
          e.preventDefault();
          if (q.trim().length >= 2) void search();
        }}
      >
        <input className="input" placeholder={tr('Cerca una categoria…')} value={q} onChange={(e) => setQ(e.target.value)} />
        <button className="btn" type="submit" disabled={busy || q.trim().length < 2}>
          {busy ? '…' : tr('Cerca')}
        </button>
      </form>
      {results.length > 0 && (
        <div className="list">
          {results.map((r) => (
            <button
              key={r.id}
              type="button"
              className="list-item"
              onClick={() => {
                onChange({ ...value, gameId: r.id, gameName: r.name });
                setResults([]);
              }}
            >
              {r.art && <img src={r.art} alt="" width={26} height={36} />}
              <span>{r.name}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export function TwitchActionEditor({ value, onChange }: { value: TwitchAction; onChange: (a: TwitchAction) => void }) {
  const info = usePcInfo()?.twitch;
  const c = value.command;
  const text = (field: 'description' | 'message' | 'title' | 'channel', label: string, max: number, placeholder = '') => (
    <label className="field">
      <span>{label}</span>
      <input
        className="input"
        maxLength={max + 40}
        placeholder={placeholder}
        value={value[field] ?? ''}
        onChange={(e) => onChange({ ...value, [field]: e.target.value })}
      />
    </label>
  );
  return (
    <>
      <label className="field">
        <span>{tr('Comando')}</span>
        <select className="input" value={c} onChange={(e) => onChange(twitchDefault(e.target.value as TwitchCommand))}>
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

      {c === 'marker' && text('description', tr('Descrizione (facoltativa, max 140)'), 140, tr('Momento epico {ora}'))}
      {c === 'clip' && (
        <p className="muted small hint">
          {tr('La clip (ultimi ~30 s) viene creata durante la diretta; il link finisce negli appunti del PC.')}
        </p>
      )}
      {c === 'channel' && (
        <>
          {text('title', tr('Titolo (vuoto = invariato, max 140)'), 140)}
          <CategoryPicker value={value} onChange={onChange} />
          <label className="field">
            <span>{tr('Tag separati da virgola (vuoto = invariati, max 10)')}</span>
            <input
              className="input"
              value={(value.tags ?? []).join(', ')}
              onChange={(e) =>
                onChange({
                  ...value,
                  tags: e.target.value
                    .split(',')
                    .map((t) => t.trim())
                    .filter(Boolean),
                })
              }
            />
          </label>
        </>
      )}
      {c === 'commercial' && (
        <label className="field">
          <span>{tr('Durata')}</span>
          <select className="input" value={value.length ?? 60} onChange={(e) => onChange({ ...value, length: Number(e.target.value) })}>
            {[30, 60, 90, 120, 150, 180].map((n) => (
              <option key={n} value={n}>
                {tr('{0} secondi', n)}
              </option>
            ))}
          </select>
        </label>
      )}
      {(c === 'chat' || c === 'announce') && text('message', tr('Messaggio (max 500)'), 500, tr('Ciao chat! Sono le {ora}'))}
      {c === 'announce' && (
        <Segmented
          value={value.color ?? 'primary'}
          options={[
            ['primary', tr('Canale')],
            ['blue', tr('Blu')],
            ['green', tr('Verde')],
            ['orange', tr('Arancio')],
            ['purple', tr('Viola')],
          ]}
          onChange={(color) => onChange({ ...value, color })}
        />
      )}
      {(c === 'shoutout' || c === 'raid') && text('channel', tr('Canale (nome o link twitch.tv/…)'), 100, tr('nomecanale'))}
      {c === 'shoutout' && (
        <p className="muted small hint">{tr('Twitch consente uno shoutout ogni 2 minuti e lo stesso canale una volta ogni ora.')}</p>
      )}
      {c === 'chatMode' && (
        <>
          <Segmented
            value={value.mode ?? 'slow'}
            options={[
              ['slow', 'Slow'],
              ['follower', tr('Solo follower')],
              ['sub', tr('Solo sub')],
              ['emote', tr('Solo emote')],
            ]}
            onChange={(mode) => onChange({ ...value, mode })}
          />
          <Segmented
            value={value.set ?? 'toggle'}
            options={[
              ['toggle', tr('Alterna')],
              ['on', tr('Attiva')],
              ['off', tr('Disattiva')],
            ]}
            onChange={(set) => onChange({ ...value, set })}
          />
          {value.mode === 'slow' && (
            <div className="field-row">
              <span>{tr('Secondi tra i messaggi (3–120)')}</span>
              <input
                className="input num"
                type="number"
                min={3}
                max={120}
                value={value.seconds ?? 30}
                onChange={(e) => onChange({ ...value, seconds: Number(e.target.value) || 30 })}
              />
            </div>
          )}
          {value.mode === 'follower' && (
            <div className="field-row">
              <span>{tr('Follower da almeno (minuti, 0 = subito)')}</span>
              <input
                className="input num"
                type="number"
                min={0}
                value={value.minutes ?? 0}
                onChange={(e) => onChange({ ...value, minutes: Math.max(0, Number(e.target.value) || 0) })}
              />
            </div>
          )}
          <p className="muted small hint">
            {tr("Per vedere lo stato sul tasto, usa il modello dinamico «Twitch slow mode» (o un'altra modalità).")}
          </p>
        </>
      )}
      {c === 'poll' && (
        <>
          {text('title', tr('Domanda (max 60)'), 60)}
          <ListField
            label={tr('Scelte')}
            value={value.choices ?? [tr('Sì'), 'No']}
            min={2}
            max={5}
            maxLen={25}
            onChange={(choices) => onChange({ ...value, choices })}
          />
          <div className="field-row">
            <span>{tr('Durata (secondi, 15–1800)')}</span>
            <input
              className="input num"
              type="number"
              min={15}
              max={1800}
              value={value.durationSec ?? 120}
              onChange={(e) => onChange({ ...value, durationSec: Number(e.target.value) || 120 })}
            />
          </div>
        </>
      )}
      {c === 'pollEnd' && (
        <Segmented
          value={value.archive ? 'archive' : 'end'}
          options={[
            ['end', tr('Termina (resta visibile)')],
            ['archive', tr('Termina e nascondi')],
          ]}
          onChange={(v) => onChange({ ...value, archive: v === 'archive' })}
        />
      )}
      {c === 'prediction' && (
        <>
          {text('title', tr('Domanda (max 45)'), 45)}
          <ListField
            label={tr('Esiti')}
            value={value.outcomes ?? [tr('Sì'), 'No']}
            min={2}
            max={10}
            maxLen={25}
            onChange={(outcomes) => onChange({ ...value, outcomes })}
          />
          <div className="field-row">
            <span>{tr('Tempo per puntare (secondi, 1–1800)')}</span>
            <input
              className="input num"
              type="number"
              min={1}
              max={1800}
              value={value.windowSec ?? 120}
              onChange={(e) => onChange({ ...value, windowSec: Number(e.target.value) || 120 })}
            />
          </div>
        </>
      )}
      {c === 'predictionResolve' && (
        <div className="field-row">
          <span>{tr('Esito vincente (numero, 1 = il primo)')}</span>
          <input
            className="input num"
            type="number"
            min={1}
            max={10}
            value={value.outcome ?? 1}
            onChange={(e) => onChange({ ...value, outcome: Math.max(1, Number(e.target.value) || 1) })}
          />
        </div>
      )}

      <p className={`muted small hint${info && !info.connected ? ' error-text' : ''}`}>
        {info?.connected
          ? tr('Twitch connesso come {0}.', info.name ?? info.login)
          : tr('Twitch non connesso: accedi dal pannello del profilo → Twitch.')}
        {(c === 'chat' || c === 'announce' || c === 'marker') && tr(' Variabili: {ora} {data} {contatore:Nome} {timer:Nome}.')}
      </p>
    </>
  );
}
