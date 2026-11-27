import { tr } from '../i18n';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { autoProfilesEnabled, requestHomeCode, selectProfile, sendPair, setAutoProfilesEnabled, setPin, unlock } from '../net/client';
import { setKeepAwake } from '../lib/keepAwake';
import { LanguageSelect } from './LanguageSelect';
import { canPromptInstall, copyText, guessDeviceName, isAndroid, isIOS, isStandalone, promptInstall, storage } from '../lib/platform';
import { useCurrentPage, useCurrentProfile, useDeck } from '../state/store';

// ---- Connection indicator ------------------------------------------------------------------

/** Discreet status dot. Long press (or right click) opens the menu. */
export function StatusDot({ inline = false }: { inline?: boolean }) {
  const conn = useDeck((s) => s.conn);
  const timer = useRef(0);
  const open = () => useDeck.setState({ menuOpen: true });
  const title = conn === 'open' ? tr('Connesso') : conn === 'connecting' ? tr('Connessione…') : tr('Disconnesso');

  return (
    <div
      className={`status-dot-hit${inline ? ' inline' : ''}`}
      title={tr('{0} – tieni premuto per il menu', title)}
      onPointerDown={() => {
        if (inline) return;
        timer.current = window.setTimeout(open, 600);
      }}
      onPointerUp={() => clearTimeout(timer.current)}
      onPointerCancel={() => clearTimeout(timer.current)}
      onPointerLeave={() => clearTimeout(timer.current)}
      onClick={inline ? open : undefined}
      onContextMenu={(e) => {
        e.preventDefault();
        open();
      }}
    >
      <span className={`status-dot ${conn}`} />
    </div>
  );
}

// ---- Menu ---------------------------------------------------------------------------------

function AutoProfilesToggle() {
  const [on, setOn] = useState(autoProfilesEnabled);
  return (
    <>
      <label className="toggle">
        <input
          type="checkbox"
          checked={on}
          onChange={(e) => {
            setOn(e.target.checked);
            setAutoProfilesEnabled(e.target.checked);
          }}
        />
        <span>{tr("Cambia profilo in base all'app attiva sul PC")}</span>
      </label>
      <p className="muted small hint">{tr('Vale solo per questo dispositivo.')}</p>
    </>
  );
}

export function MenuSheet() {
  const open = useDeck((s) => s.menuOpen);
  const profiles = useDeck((s) => s.profiles);
  const profileId = useDeck((s) => s.profileId);
  const deviceName = useDeck((s) => s.deviceName);
  const conn = useDeck((s) => s.conn);
  const profile = useCurrentProfile();
  const page = useCurrentPage();
  const pages = profile?.pages.filter((p) => p.kind !== 'folder') ?? [];
  const keepAwake = useDeck((s) => s.keepAwake);
  if (!open) return null;
  const close = () => useDeck.setState({ menuOpen: false });

  return (
    <div className="overlay" onPointerDown={(e) => e.target === e.currentTarget && close()}>
      <div className="sheet">
        <div className="sheet-head">
          <div>
            <div className="sheet-title">{deviceName || 'PadNook'}</div>
            <div className="muted small">
              {conn === 'open' ? tr('Connesso al PC') : conn === 'connecting' ? tr('Connessione in corso…') : tr('Non connesso')}
            </div>
          </div>
          <button className="btn ghost" onClick={close}>
            {tr('Chiudi')}
          </button>
        </div>

        <div className="section-title">{tr('Profilo')}</div>
        <div className="list">
          {profiles.map((p) => (
            <button key={p.id} className={`list-item${p.id === profileId ? ' active' : ''}`} onClick={() => selectProfile(p.id)}>
              <span>{p.name}</span>
              {p.id === profileId && <span>✓</span>}
            </button>
          ))}
        </div>

        {pages.length > 1 && (
          <>
            <div className="section-title">{tr('Pagina')}</div>
            <div className="list">
              {pages.map((p) => (
                <button
                  key={p.id}
                  className={`list-item${p.id === page?.id ? ' active' : ''}`}
                  onClick={() => useDeck.setState({ pageStack: [p.id], menuOpen: false })}
                >
                  <span>{p.name}</span>
                  {p.id === page?.id && <span>✓</span>}
                </button>
              ))}
            </div>
          </>
        )}

        {profiles.some((p) => p.auto?.apps?.length) && (
          <>
            <div className="section-title">{tr('Profili automatici')}</div>
            <AutoProfilesToggle />
          </>
        )}

        <div className="section-title">{tr('Schermo')}</div>
        <label className="toggle">
          <input type="checkbox" checked={keepAwake !== 'off'} onChange={(e) => setKeepAwake(e.target.checked)} />
          <span>{tr('Mantieni lo schermo acceso (sperimentale)')}</span>
        </label>
        <p className="muted small hint">
          {keepAwake === 'on'
            ? tr('Attivo su questo dispositivo.')
            : keepAwake === 'waiting'
              ? tr('Si attiva al prossimo tocco.')
              : tr('Su iPhone e iPad il metodo più affidabile è Impostazioni → Schermo e luminosità → Blocco automatico → Mai.')}
        </p>

        <LanguageSelect />

        <div className="sheet-actions">
          {page?.kind === 'folder' && (
            <button className="btn" onClick={() => useDeck.setState({ pageStack: [], menuOpen: false })}>
              {tr('Torna alla pagina iniziale')}
            </button>
          )}
          {(isIOS() || isAndroid()) && !isStandalone() && (
            <button className="btn" onClick={() => useDeck.setState({ menuOpen: false, justPaired: true })}>
              {tr('Aggiungi alla schermata Home')}
            </button>
          )}
          <button className="btn" onClick={() => location.reload()}>
            {tr('Ricarica')}
          </button>
        </div>
      </div>
    </div>
  );
}

// ---- PIN ----------------------------------------------------------------------------------

export function PinDialog() {
  const mode = useDeck((s) => s.pinMode);
  const error = useDeck((s) => s.pinError);
  const [pin, setPinValue] = useState('');
  const [confirm, setConfirm] = useState('');
  const [localError, setLocalError] = useState<string | null>(null);

  useEffect(() => {
    setPinValue('');
    setConfirm('');
    setLocalError(null);
  }, [mode]);

  useEffect(() => {
    if (error) setPinValue('');
  }, [error]);

  if (!mode) return null;
  const close = () => useDeck.setState({ pinMode: null, pinError: null });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!/^\d{4,8}$/.test(pin)) {
      setLocalError(tr('Il PIN deve avere da 4 a 8 cifre'));
      return;
    }
    if (mode === 'set' && pin !== confirm) {
      setLocalError(tr('I due PIN non coincidono'));
      return;
    }
    setLocalError(null);
    useDeck.setState({ pinError: null });
    if (mode === 'unlock') unlock(pin);
    else setPin(pin);
  };

  return (
    <div className="overlay center" onPointerDown={(e) => e.target === e.currentTarget && close()}>
      <form className="dialog" onSubmit={submit}>
        <div className="dialog-title">{mode === 'unlock' ? tr('Modalità modifica') : tr('Imposta il PIN di modifica')}</div>
        <p className="muted small">
          {mode === 'unlock'
            ? tr('Inserisci il PIN per modificare i tasti.')
            : tr('Scegli un PIN di 4–8 cifre: servirà per entrare in modalità modifica da qualsiasi dispositivo.')}
        </p>
        <input
          className="input pin"
          type="password"
          inputMode="numeric"
          pattern="[0-9]*"
          autoComplete="off"
          maxLength={8}
          autoFocus
          placeholder="PIN"
          value={pin}
          onChange={(e) => setPinValue(e.target.value.replace(/\D/g, ''))}
        />
        {mode === 'set' && (
          <input
            className="input pin"
            type="password"
            inputMode="numeric"
            pattern="[0-9]*"
            autoComplete="off"
            maxLength={8}
            placeholder={tr('Conferma PIN')}
            value={confirm}
            onChange={(e) => setConfirm(e.target.value.replace(/\D/g, ''))}
          />
        )}
        {(localError || error) && <div className="error">{localError || error}</div>}
        <div className="dialog-actions">
          <button type="button" className="btn ghost" onClick={close}>
            {tr('Annulla')}
          </button>
          <button type="submit" className="btn primary">
            {mode === 'unlock' ? tr('Sblocca') : tr('Salva PIN')}
          </button>
        </div>
      </form>
    </div>
  );
}

// ---- Pairing ------------------------------------------------------------------------------

export function PairScreen() {
  const conn = useDeck((s) => s.conn);
  const error = useDeck((s) => s.pairError);
  const busy = useDeck((s) => s.pairBusy);
  const [code, setCode] = useState('');
  const [name, setName] = useState(() => storage.get('padnook.deviceName') ?? guessDeviceName());

  const submit = (e: FormEvent) => {
    e.preventDefault();
    storage.set('padnook.deviceName', name);
    sendPair(code, name.trim() || guessDeviceName());
  };

  return (
    <div className="pair-screen">
      <form className="pair-card" onSubmit={submit}>
        <img src="/icon-192.png" alt="" className="pair-logo" />
        <h1>{tr('Associa questo dispositivo')}</h1>
        <p className="muted">
          {tr(
            "Sul PC fai clic sull'icona di {0} nell'area di notifica e scegli {1}. Poi inserisci qui il codice a 6 cifre.",
            <b>PadNook</b>,
            <b>{tr('«Mostra indirizzo e QR code»')}</b>,
          )}
        </p>
        <label className="field">
          <span>{tr('Codice di accoppiamento')}</span>
          <input
            className="input code"
            inputMode="numeric"
            pattern="[0-9 ]*"
            autoComplete="one-time-code"
            maxLength={7}
            placeholder="123 456"
            value={code}
            onChange={(e) => setCode(e.target.value.replace(/[^\d ]/g, ''))}
          />
        </label>
        <label className="field">
          <span>{tr('Nome del dispositivo')}</span>
          <input className="input" value={name} maxLength={40} onChange={(e) => setName(e.target.value)} />
        </label>
        {error && <div className="error">{error}</div>}
        {conn !== 'open' && <div className="error">{tr('Server non raggiungibile: verifica che PadNook sia avviato sul PC.')}</div>}
        <button className="btn primary big" disabled={busy || code.replace(/\s/g, '').length !== 6 || conn !== 'open'}>
          {busy ? tr('Associazione…') : tr('Associa')}
        </button>
      </form>
    </div>
  );
}

// ---- Add to Home Screen hint (iOS Safari / Android Chrome) ---------------------------------

export function InstallHint() {
  const show = useDeck((s) => s.justPaired && s.auth === 'ok');
  if (!show || isStandalone()) return null;
  if (isIOS()) return <IosInstallHint />;
  if (isAndroid()) return <AndroidInstallHint />;
  return null;
}

function CopyRow({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="inline-row">
      <input className="input mono" readOnly value={text} onFocus={(e) => e.currentTarget.select()} />
      <button
        type="button"
        className="btn"
        onClick={() => {
          setCopied(copyText(text));
          window.setTimeout(() => setCopied(false), 1500);
        }}
      >
        {copied ? tr('Copiato') : tr('Copia')}
      </button>
    </div>
  );
}

/**
 * Chrome on Android only installs PWAs from secure origins; PadNook runs on plain HTTP in the LAN,
 * so the origin must be whitelisted with a Chrome flag first.
 */
function AndroidInstallHint() {
  const close = () => useDeck.setState({ justPaired: false });
  const [, tick] = useState(0);
  useEffect(() => {
    // The install prompt can arrive after the dialog opens.
    const t = window.setInterval(() => tick((x) => x + 1), 1000);
    return () => clearInterval(t);
  }, []);

  if (window.isSecureContext) {
    return (
      <div className="overlay center">
        <div className="dialog">
          <div className="dialog-title">{tr('Installa PadNook')}</div>
          {canPromptInstall() ? (
            <p>{tr("Installala come app per usarla a schermo intero dall'icona sulla schermata Home.")}</p>
          ) : (
            <ol className="steps">
              <li>{tr('Apri il menu {0} di Chrome.', <b>⋮</b>)}</li>
              <li>{tr('Scegli {0} (o «Aggiungi a schermata Home» → {1}).', <b>{tr('«Installa app»')}</b>, <b>{tr('Installa')}</b>)}</li>
            </ol>
          )}
          <div className="dialog-actions">
            <button className="btn ghost" onClick={close}>
              {tr('Continua in Chrome')}
            </button>
            {canPromptInstall() && (
              <button className="btn primary" onClick={() => void promptInstall().then(close)}>
                {tr('Installa')}
              </button>
            )}
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="overlay center">
      <div className="dialog">
        <div className="dialog-title">{tr('Installa PadNook')}</div>
        <p className="muted small">
          {tr(
            'Chrome installa le app solo da siti HTTPS. PadNook funziona nella rete di casa senza HTTPS, quindi va autorizzato una volta:',
          )}
        </p>
        <ol className="steps">
          <li>
            {tr('Copia questo indirizzo e incollalo nella barra degli indirizzi di Chrome:')}
            <CopyRow text="chrome://flags/#unsafely-treat-insecure-origin-as-secure" />
          </li>
          <li>
            {tr("Nel riquadro di testo incolla l'indirizzo di PadNook:")}
            <CopyRow text={location.origin} />
          </li>
          <li>{tr('Imposta {0} e tocca {1}.', <b>Enabled</b>, <b>Relaunch</b>)}</li>
          <li>{tr('Torna qui e scegli menu {0} → {1}.', <b>⋮</b>, <b>{tr('«Installa app»')}</b>)}</li>
        </ol>
        <p className="muted small">{tr("Se l'indirizzo del PC cambia, ripeti il passo 2 con quello nuovo.")}</p>
        <div className="dialog-actions">
          <button className="btn primary" onClick={close}>
            {tr('Continua in Chrome')}
          </button>
        </div>
      </div>
    </div>
  );
}

function IosInstallHint() {
  const homeCode = useDeck((s) => s.homeCode);
  const conn = useDeck((s) => s.conn);
  const [, tick] = useState(0);

  useEffect(() => {
    if (conn === 'open' && (!homeCode || homeCode.expiresAt < Date.now())) requestHomeCode();
  }, [conn, homeCode]);

  useEffect(() => {
    const t = window.setInterval(() => tick((x) => x + 1), 1000);
    return () => clearInterval(t);
  }, []);

  const left = homeCode ? Math.max(0, Math.round((homeCode.expiresAt - Date.now()) / 1000)) : 0;

  return (
    <div className="overlay center">
      <div className="dialog">
        <div className="dialog-title">{tr('Usala a schermo intero')}</div>
        <ol className="steps">
          <li>{tr('Tocca il pulsante {0} di Safari (il quadrato con la freccia).', <b>{tr('Condividi')}</b>)}</li>
          <li>{tr('Scegli {0}, poi {1}.', <b>{tr('«Aggiungi alla schermata Home»')}</b>, <b>{tr('Aggiungi')}</b>)}</li>
          <li>{tr("Apri PadNook dall'icona e inserisci questo codice:")}</li>
        </ol>
        <div className="home-code">{homeCode ? `${homeCode.code.slice(0, 3)} ${homeCode.code.slice(3)}` : '…'}</div>
        <div className="muted small center-text">
          {homeCode && left > 0 ? tr('Valido per {0}:{1}', Math.floor(left / 60), String(left % 60).padStart(2, '0')) : tr('Generazione…')}
        </div>
        <div className="dialog-actions">
          <button className="btn ghost" onClick={requestHomeCode}>
            {tr('Nuovo codice')}
          </button>
          <button className="btn primary" onClick={() => useDeck.setState({ justPaired: false })}>
            {tr('Continua in Safari')}
          </button>
        </div>
      </div>
    </div>
  );
}

// ---- Toast --------------------------------------------------------------------------------

export function Toast() {
  const toast = useDeck((s) => s.toast);
  if (!toast) return null;
  return <div className={`toast ${toast.kind}`}>{toast.text}</div>;
}

// ---- Splash (first connection) --------------------------------------------------------------

export function Splash() {
  const conn = useDeck((s) => s.conn);
  const [slow, setSlow] = useState(false);
  useEffect(() => {
    const t = window.setTimeout(() => setSlow(true), 4000);
    return () => clearTimeout(t);
  }, []);
  return (
    <div className="pair-screen">
      <div className="pair-card">
        <img src="/icon-192.png" alt="" className="pair-logo" />
        <p className="muted">{conn === 'open' ? tr('Caricamento…') : tr('Connessione al PC…')}</p>
        {slow && conn !== 'open' && (
          <p className="error">
            {tr('Il PC non risponde. Controlla che PadNook sia avviato e che il telefono sia sulla stessa rete Wi-Fi.')}
          </p>
        )}
      </div>
    </div>
  );
}
