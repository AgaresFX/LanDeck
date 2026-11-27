import { tr } from '../i18n';
// Discord: connection panel (own Developer Portal app + login) with a step-by-step guide, and the action editor.
import { useState } from 'react';
import { refreshPcInfo, request } from '../net/client';
import { showToast } from '../state/store';
import type { Action, DiscordAction, DiscordCommand, DiscordInfo } from '../types';
import { Section, Slider, Toggle } from './controls';
import { usePcInfoWatch } from './ExtraEditors';

const PORTAL = 'https://discord.com/developers/applications';

export const discordDefault = (): DiscordAction => ({ type: 'discord', command: 'toggleMute' });

export const DISCORD_COMMANDS: [string, [DiscordCommand, string][]][] = [
  [
    tr('Microfono'),
    [
      ['toggleMute', tr('Muto sì / no')],
      ['mute', tr('Attiva il muto')],
      ['unmute', tr('Togli il muto')],
    ],
  ],
  [
    tr('Audio (silenzia)'),
    [
      ['toggleDeaf', tr('Silenzia sì / no')],
      ['deaf', tr('Silenzia')],
      ['undeaf', tr('Togli silenzia')],
    ],
  ],
  [
    tr('Impostazioni voce'),
    [
      ['togglePtt', tr('Push-to-talk ↔ attività vocale')],
      ['inputVolume', tr('Volume del microfono')],
      ['outputVolume', tr("Volume dell'audio in uscita")],
    ],
  ],
  [
    tr('Canali vocali'),
    [
      ['join', tr('Entra in un canale vocale')],
      ['leave', tr('Esci dal canale vocale')],
    ],
  ],
];

export const discordCommandLabel = (c: DiscordCommand) => DISCORD_COMMANDS.flatMap(([, items]) => items).find(([v]) => v === c)?.[1] ?? c;

// ---- Connection panel ---------------------------------------------------------------------

function Guide({ info }: { info: DiscordInfo }) {
  return (
    <ol className="guide">
      <li>
        {tr(
          'Apri il{0} {1}{2} e accedi con il tuo account Discord.',
          ' ',
          <a href={PORTAL} target="_blank" rel="noreferrer">
            Discord Developer Portal
          </a>,
          ' ',
        )}
      </li>
      <li>
        {tr(
          'Premi {0}, chiamala per esempio {1}, accetta le condizioni e premi {2}.',
          <b>New Application</b>,
          <i>PadNook</i>,
          <b>Create</b>,
        )}
      </li>
      <li>{tr('Nel menu a sinistra apri {0}. Copia il {1} e incollalo qui sotto.', <b>OAuth2</b>, <b>Client ID</b>)}</li>
      <li>
        {tr(
          'Sempre in OAuth2, sotto {0} premi {1}, conferma e copia il codice: incollalo qui sotto (Discord lo mostra una volta sola).',
          <b>Client Secret</b>,
          <b>Reset Secret</b>,
        )}
      </li>
      <li>
        {tr(
          'Ancora in OAuth2, in {0} premi {1}, scrivi {2} e premi {3}{4} in basso.',
          <b>Redirects</b>,
          <b>Add Redirect</b>,
          <code>{info.redirectUri}</code>,
          <b>Save Changes</b>,
          ' ',
        )}
      </li>
      <li>
        {tr(
          'Qui sotto premi {0}, poi, con Discord aperto sul PC, {1}: in Discord compare una finestra, premi{2} {3}.',
          <b>{tr('Salva')}</b>,
          <b>{tr('Accedi con Discord')}</b>,
          ' ',
          <b>{tr('Autorizza')}</b>,
        )}
      </li>
    </ol>
  );
}

export function DiscordSection() {
  const info = usePcInfoWatch((i) => !i.discord?.enabled || !i.discord.clientId || i.discord.authorized)?.discord;
  const [clientId, setClientId] = useState<string | null>(null);
  const [secret, setSecret] = useState('');
  const [busy, setBusy] = useState<'save' | 'login' | null>(null);
  const [guideOpen, setGuideOpen] = useState<boolean | null>(null);
  if (!info) return null;
  const id = clientId ?? info.clientId;
  const showGuide = guideOpen ?? !info.hasToken;

  const save = async (enabled = info.enabled) => {
    setBusy('save');
    try {
      await request({ t: 'edit.discord.set', enabled, clientId: id, ...(secret ? { secret } : {}) });
      setSecret('');
      setClientId(null);
      showToast(tr('Impostazioni di Discord salvate'));
      window.setTimeout(() => void refreshPcInfo(), 1500);
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setBusy(null);
    }
  };

  const login = async () => {
    setBusy('login');
    try {
      await request({ t: 'edit.discord.login' }, 150000);
      showToast(tr('Discord collegato'));
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setBusy(null);
      void refreshPcInfo(true);
    }
  };

  const logout = async () => {
    if (!confirm(tr('Scollegare PadNook da Discord? Per usarlo di nuovo dovrai rifare «Accedi con Discord».'))) return;
    try {
      await request({ t: 'edit.discord.logout' });
    } catch (e) {
      showToast((e as Error).message, 'error');
    }
    void refreshPcInfo(true);
  };

  const status = !info.enabled
    ? tr('Integrazione disattivata.')
    : !info.clientId
      ? tr('Da configurare: segui i passaggi qui sotto.')
      : info.authorized
        ? tr('✓ Collegato a Discord{0}.', info.user ? tr(' come {0}', info.user) : '')
        : info.connected
          ? info.hasToken
            ? tr('Accesso non riuscito{0}.', info.error ? `: ${info.error}` : '')
            : tr("Discord è aperto: manca l'accesso (passo 6).")
          : tr('Non connesso{0}.', info.error ? `: ${info.error}` : '');
  const problem = info.enabled && !!info.clientId && !info.authorized;

  return (
    <Section title="Discord">
      <p className={`muted small hint${problem ? ' error-text' : ''}`}>{status}</p>
      <Toggle label={tr('Collegati a Discord su questo PC')} checked={info.enabled} onChange={(v) => void save(v)} />

      <button type="button" className="link small guide-toggle" onClick={() => setGuideOpen(!showGuide)}>
        {showGuide ? tr('▾ Nascondi la guida') : tr('▸ Come si configura (guida passo passo)')}
      </button>
      {showGuide && <Guide info={info} />}

      <label className="field">
        <span>Client ID</span>
        <input
          className="input mono"
          inputMode="numeric"
          placeholder="123456789012345678"
          value={id}
          onChange={(e) => setClientId(e.target.value.trim())}
        />
      </label>
      <label className="field">
        <span>Client Secret {info.hasSecret ? tr('(già salvato: lascia vuoto per non cambiarlo)') : ''}</span>
        <input className="input" type="password" autoComplete="off" value={secret} onChange={(e) => setSecret(e.target.value)} />
      </label>
      <div className="button-row">
        <button type="button" className="btn" disabled={!!busy} onClick={() => void save()}>
          {tr('Salva')}
        </button>
        <button
          type="button"
          className="btn primary"
          disabled={!!busy || !info.connected || !info.clientId || !info.hasSecret}
          onClick={() => void login()}
          title={!info.connected ? tr('Apri Discord sul PC') : undefined}
        >
          {busy === 'login' ? tr('Conferma in Discord…') : info.authorized ? tr('Accedi di nuovo') : tr('Accedi con Discord')}
        </button>
        {info.hasToken && (
          <button type="button" className="btn danger" onClick={() => void logout()}>
            {tr('Scollega')}
          </button>
        )}
      </div>
      {busy === 'login' && (
        <p className="muted small hint">{tr('In Discord è comparsa una finestra di autorizzazione: premi «Autorizza» (hai 2 minuti).')}</p>
      )}
      <p className="muted small hint">
        {tr(
          "L'applicazione che crei resta privata: la usi solo tu. Il Client Secret e l'accesso sono salvati cifrati su questo PC e l'accesso si rinnova da solo. Poi crea un tasto con l'azione «Discord», o un tasto dinamico con lo stato del muto.",
        )}
      </p>
    </Section>
  );
}

// ---- Action editor ------------------------------------------------------------------------

export function DiscordActionEditor({ value, onChange }: { value: DiscordAction; onChange: (a: Action) => void }) {
  const info = usePcInfoWatch((i) => !!i.discord?.authorized)?.discord;
  const c = value.command;
  return (
    <>
      <label className="field">
        <span>{tr('Comando')}</span>
        <select
          className="input"
          value={c}
          onChange={(e) => {
            const command = e.target.value as DiscordCommand;
            onChange({
              type: 'discord',
              command,
              ...(command === 'inputVolume' || command === 'outputVolume' ? { level: 100 } : {}),
              ...(command === 'join' ? { channelId: value.channelId ?? '' } : {}),
            });
          }}
        >
          {DISCORD_COMMANDS.map(([g, items]) => (
            <optgroup key={g} label={g}>
              {items.map(([v, l]) => (
                <option key={v} value={v}>
                  {l}
                </option>
              ))}
            </optgroup>
          ))}
        </select>
      </label>
      {(c === 'inputVolume' || c === 'outputVolume') && (
        <Slider
          label={c === 'inputVolume' ? tr('Volume del microfono') : tr('Volume in uscita')}
          unit="%"
          value={value.level ?? 100}
          min={0}
          max={c === 'inputVolume' ? 100 : 200}
          onChange={(level) => onChange({ ...value, level })}
        />
      )}
      {c === 'join' && (
        <>
          <label className="field">
            <span>{tr('ID del canale vocale')}</span>
            <input
              className="input mono"
              inputMode="numeric"
              placeholder="123456789012345678"
              value={value.channelId ?? ''}
              onChange={(e) => onChange({ ...value, channelId: e.target.value.trim() })}
            />
          </label>
          <p className="muted small hint">
            {tr(
              "Per copiare l'ID: in Discord apri Impostazioni → Avanzate e attiva {0}, poi clic destro sul canale vocale → {1}.",
              <b>{tr('Modalità sviluppatore')}</b>,
              <b>{tr('Copia ID canale')}</b>,
            )}
          </p>
        </>
      )}
      {info && !info.authorized && (
        <p className="muted small hint error-text">
          {tr('Discord non è ancora collegato: configuralo nel pannello senza tasti selezionati → Servizi → Discord.')}
        </p>
      )}
    </>
  );
}
