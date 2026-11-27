import { locale, tr } from '../i18n';
import { useRef, useState, type ReactNode } from 'react';
import { refreshPcInfo, request, revokeDevice, updateDevice, updateProfile } from '../net/client';
import { socket } from '../net/socket';
import { normApp } from '../lib/live';
import { showToast } from '../state/store';
import { usePcInfo, usePcInfoWatch } from './ExtraEditors';
import { exportProfile, importProfile } from './transfer';
import { resolveLayout } from '../lib/layout';
import { useCurrentPage, useCurrentProfile, useDeck } from '../state/store';
import type { Align, Background, Layout } from '../types';
import { deletePage, deleteProfile, duplicateProfile, movePage, newProfile, putNewProfile, renamePage } from './ops';
import { lightroomProfile } from './lightroomProfile';
import { ImagePicker } from './pickers';
import { SecretsSection } from './HttpEditor';
import { TwitchSection } from './TwitchEditor';
import { SlobsSection, StreamlabsSection } from './StreamlabsEditor';
import { LightroomSection } from './LightroomEditor';
import { DiscordSection } from './DiscordEditor';
import { ColorField, OverrideBadge, Section, Segmented, Slider, Stepper, SubTabs, Toggle, useRemembered } from './controls';
import { Icon } from '../ui/Icon';
import { LanguageSelect } from '../ui/LanguageSelect';

type Scope = 'profile' | 'page';
type Field = keyof Layout;

const ALIGN_LABEL: Record<Align, string> = { start: tr('inizio'), center: tr('centro'), end: tr('fine') };

type PanelTab = 'grid' | 'page' | 'profile' | 'services' | 'devices';
type ServiceTab = 'obs' | 'slobs' | 'twitch' | 'streamlabs' | 'discord' | 'lightroom' | 'secrets' | 'plugins';

/** Settings shown when no key is selected: one tab at a time instead of one long column. */
export function LayoutPanel({ top, pageExtra }: { top?: ReactNode; pageExtra?: ReactNode }) {
  const page = useCurrentPage();
  const devices = useDeck((s) => s.devices);
  const [tab, setTab] = useRemembered<PanelTab>('panel.tab', 'grid');
  const [service, setService] = useRemembered<ServiceTab>('panel.service', 'obs');
  const isFolder = page?.kind === 'folder';
  const tabs: [PanelTab, string][] = [
    ['grid', tr('Griglia')],
    ['page', isFolder ? tr('Cartella') : tr('Pagina')],
    ['profile', tr('Profilo')],
    ['services', tr('Servizi')],
    ...(devices.length ? ([['devices', tr('Dispositivi')]] as [PanelTab, string][]) : []),
  ];
  const current = tabs.some(([t]) => t === tab) ? tab : 'grid';

  return (
    <div className="inspector">
      <div className="inspector-sticky">
        {top}
        <div className="tabs" role="tablist">
          {tabs.map(([t, label]) => (
            <button
              key={t}
              type="button"
              role="tab"
              aria-selected={current === t}
              className={`tab${current === t ? ' on' : ''}`}
              onClick={() => setTab(t)}
            >
              {label}
            </button>
          ))}
        </div>
      </div>

      {current === 'grid' && <GridPanel />}
      {current === 'page' && (
        <>
          {pageExtra}
          <PageSection />
        </>
      )}
      {current === 'profile' && <ProfileSection />}
      {current === 'devices' && <DevicesSection />}
      {current === 'services' && (
        <>
          <div className="service-picker">
            <SubTabs
              value={service}
              options={[
                ['obs', 'OBS'],
                ['slobs', 'Streamlabs Desktop'],
                ['streamlabs', tr('Alert Streamlabs')],
                ['twitch', 'Twitch'],
                ['discord', 'Discord'],
                ['lightroom', 'Lightroom'],
                ['secrets', tr('Segreti')],
                ['plugins', 'Plugin'],
              ]}
              onChange={setService}
            />
          </div>
          {service === 'obs' && <ObsSection />}
          {service === 'slobs' && <SlobsSection />}
          {service === 'twitch' && <TwitchSection />}
          {service === 'discord' && <DiscordSection />}
          {service === 'streamlabs' && <StreamlabsSection />}
          {service === 'lightroom' && <LightroomSection />}
          {service === 'secrets' && <SecretsSection />}
          {service === 'plugins' && <PluginsSection />}
        </>
      )}
    </div>
  );
}

type GridTab = 'grid' | 'keys' | 'spacing';

/** Grid, spacing and alignment. Edits either the profile defaults or the current page only. */
function GridPanel() {
  const [sub, setSub] = useRemembered<GridTab>('panel.grid', 'grid');
  const profile = useCurrentProfile();
  const page = useCurrentPage();
  const [scope, setScope] = useState<Scope>('profile');
  if (!profile || !page) return null;

  const base = resolveLayout(profile, { ...page, layout: undefined });
  const eff = scope === 'profile' ? base : resolveLayout(profile, page);
  const overrides = page.layout ?? {};
  const isOv = (...fields: Field[]) => scope === 'page' && fields.some((f) => overrides[f] !== undefined);
  const overriddenCount = Object.keys(overrides).length;

  const set = (patch: Partial<Layout>) =>
    updateProfile((p) => {
      if (scope === 'profile') {
        p.layout = { ...base, ...patch };
      } else {
        const pg = p.pages.find((x) => x.id === page.id);
        if (pg) pg.layout = { ...pg.layout, ...patch };
      }
    });

  const reset = (...fields: Field[]) =>
    updateProfile((p) => {
      const pg = p.pages.find((x) => x.id === page.id);
      if (!pg?.layout) return;
      for (const f of fields) delete pg.layout[f];
      if (Object.keys(pg.layout).length === 0) delete pg.layout;
    });

  const badge = (...fields: Field[]) => <OverrideBadge on={isOv(...fields)} onReset={() => reset(...fields)} />;

  return (
    <>
      <Section title={tr('Le modifiche valgono per')}>
        <Segmented
          value={scope}
          options={[
            ['profile', tr('Tutto il profilo')],
            ['page', tr('Solo «{0}»', page.name)],
          ]}
          onChange={setScope}
        />
        <p className="muted small hint">
          {scope === 'profile'
            ? tr('Valori predefiniti per tutte le pagine del profilo.')
            : tr('Le modifiche valgono solo per questa pagina e prevalgono sul profilo.')}
          {scope === 'page' && overriddenCount > 0 && (
            <>
              {' '}
              <button type="button" className="link" onClick={() => reset(...(Object.keys(overrides) as Field[]))}>
                {tr('Ripristina tutto')}
              </button>
            </>
          )}
        </p>
        <div className="subtabs-gap" />
        <SubTabs
          value={sub}
          options={[
            ['grid', tr('Righe e colonne')],
            ['keys', tr('Tasti')],
            ['spacing', tr('Spazi')],
          ]}
          onChange={setSub}
        />
      </Section>

      {sub === 'grid' && (
        <>
          <Section title={tr('Griglia')}>
            <div className="grid-2">
              <div>
                <div className="sub-row">
                  <span className="sub">{tr('Verticale')}</span>
                  {badge('portrait')}
                </div>
                <div className="field-row">
                  <span>{tr('Righe')}</span>
                  <Stepper value={eff.portrait.rows} min={1} max={12} onChange={(rows) => set({ portrait: { ...eff.portrait, rows } })} />
                </div>
                <div className="field-row">
                  <span>{tr('Colonne')}</span>
                  <Stepper value={eff.portrait.cols} min={1} max={12} onChange={(cols) => set({ portrait: { ...eff.portrait, cols } })} />
                </div>
              </div>
              <div>
                <div className="sub-row">
                  <span className="sub">{tr('Orizzontale')}</span>
                  {badge('landscape')}
                </div>
                <div className="field-row">
                  <span>{tr('Righe')}</span>
                  <Stepper
                    value={eff.landscape.rows}
                    min={1}
                    max={12}
                    onChange={(rows) => set({ landscape: { ...eff.landscape, rows } })}
                  />
                </div>
                <div className="field-row">
                  <span>{tr('Colonne')}</span>
                  <Stepper
                    value={eff.landscape.cols}
                    min={1}
                    max={16}
                    onChange={(cols) => set({ landscape: { ...eff.landscape, cols } })}
                  />
                </div>
              </div>
            </div>
          </Section>
        </>
      )}

      {sub === 'keys' && (
        <Section title={tr('Tasti')} extra={badge('keyShape', 'cornerRadius', 'minKeySize')}>
          <div className="field">
            <span className="muted small">{tr('Forma')}</span>
            <Segmented
              value={eff.keyShape}
              options={[
                ['square', tr('Quadrati')],
                ['fill', tr('Riempiono lo spazio')],
              ]}
              onChange={(keyShape) => set({ keyShape })}
            />
          </div>
          <Slider
            label={tr('Raggio degli angoli')}
            value={eff.cornerRadius}
            min={0}
            max={60}
            onChange={(cornerRadius) => set({ cornerRadius })}
          />
          <Slider
            label={tr('Dimensione minima (sotto questa la griglia scorre)')}
            value={eff.minKeySize}
            min={32}
            max={140}
            onChange={(minKeySize) => set({ minKeySize })}
          />
        </Section>
      )}

      {sub === 'spacing' && (
        <>
          <SpacingSection eff={eff} set={set} badge={badge('gapX', 'gapY')} />
          <MarginSection eff={eff} set={set} badge={badge('margin')} />
        </>
      )}

      {sub === 'grid' && (
        <Section title={tr('Allineamento')} extra={badge('align')}>
          <p className="muted small hint">{tr('Posizione della griglia quando non riempie tutto lo schermo.')}</p>
          <div className="align-picker">
            {(['start', 'center', 'end'] as Align[]).map((v) =>
              (['start', 'center', 'end'] as Align[]).map((h) => (
                <button
                  key={`${v}-${h}`}
                  type="button"
                  className={eff.align.h === h && eff.align.v === v ? 'on' : ''}
                  onClick={() => set({ align: { h, v } })}
                  title={tr('Orizzontale: {0}, verticale: {1}', ALIGN_LABEL[h], ALIGN_LABEL[v])}
                >
                  <span />
                </button>
              )),
            )}
          </div>
        </Section>
      )}
    </>
  );
}

function SpacingSection({ eff, set, badge }: { eff: Layout; set: (p: Partial<Layout>) => void; badge: ReactNode }) {
  const [linked, setLinked] = useState(eff.gapX === eff.gapY);
  return (
    <Section title={tr('Spazio tra i tasti')} extra={badge}>
      <Toggle
        label={tr('Uguale in orizzontale e in verticale')}
        checked={linked}
        onChange={(v) => {
          setLinked(v);
          if (v) set({ gapY: eff.gapX });
        }}
      />
      {linked ? (
        <Slider label={tr('Spazio')} value={eff.gapX} min={0} max={60} onChange={(v) => set({ gapX: v, gapY: v })} />
      ) : (
        <>
          <Slider label={tr('Orizzontale (tra le colonne)')} value={eff.gapX} min={0} max={60} onChange={(gapX) => set({ gapX })} />
          <Slider label={tr('Verticale (tra le righe)')} value={eff.gapY} min={0} max={60} onChange={(gapY) => set({ gapY })} />
        </>
      )}
    </Section>
  );
}

function MarginSection({ eff, set, badge }: { eff: Layout; set: (p: Partial<Layout>) => void; badge: ReactNode }) {
  const m = eff.margin;
  const [linked, setLinked] = useState(m.top === m.right && m.top === m.bottom && m.top === m.left);
  const side = (k: keyof Layout['margin'], label: string) => (
    <Slider label={label} value={m[k]} min={0} max={120} onChange={(v) => set({ margin: { ...m, [k]: v } })} />
  );
  return (
    <Section title={tr('Margini dal bordo')} extra={badge}>
      <Toggle
        label={tr('Uguali su tutti i lati')}
        checked={linked}
        onChange={(v) => {
          setLinked(v);
          if (v) set({ margin: { top: m.top, right: m.top, bottom: m.top, left: m.top } });
        }}
      />
      {linked ? (
        <Slider
          label={tr('Margine')}
          value={m.top}
          min={0}
          max={120}
          onChange={(v) => set({ margin: { top: v, right: v, bottom: v, left: v } })}
        />
      ) : (
        <>
          {side('top', tr('Sopra'))}
          {side('bottom', tr('Sotto'))}
          {side('left', tr('Sinistra'))}
          {side('right', tr('Destra'))}
        </>
      )}
      <p className="muted small hint">{tr('Si aggiungono alle aree riservate di iPhone e iPad (notch, barra Home).')}</p>
    </Section>
  );
}

export function BackgroundEditor({ value, onChange }: { value: Background; onChange: (bg: Background) => void }) {
  return (
    <>
      <ColorField value={value.color ?? '#0e0f13'} onChange={(color) => onChange({ ...value, color })} />
      <div className="field">
        <span className="muted small">{tr('Immagine')}</span>
        <ImagePicker value={value.imageId} maxSize={2048} onChange={(imageId) => onChange({ ...value, imageId })} />
      </div>
      {value.imageId && (
        <Segmented
          value={value.fit ?? 'cover'}
          options={[
            ['cover', tr('Riempi')],
            ['contain', tr('Adatta')],
            ['tile', tr('Affianca')],
          ]}
          onChange={(fit) => onChange({ ...value, fit })}
        />
      )}
    </>
  );
}

function PageSection() {
  const profile = useCurrentProfile();
  const page = useCurrentPage();
  if (!profile || !page) return null;
  const isFolder = page.kind === 'folder';

  return (
    <>
      <Section title={isFolder ? tr('Cartella') : tr('Pagina')}>
        <label className="field">
          <span>{tr('Nome')}</span>
          <input className="input" value={page.name} onChange={(e) => renamePage(page.id, e.target.value)} />
        </label>
        {!isFolder && (
          <div className="button-row">
            <button className="btn" onClick={() => movePage(page.id, -1)} title={tr('Sposta a sinistra')}>
              {tr('‹ Sposta')}
            </button>
            <button className="btn" onClick={() => movePage(page.id, 1)} title={tr('Sposta a destra')}>
              {tr('Sposta ›')}
            </button>
            <button className="btn danger" onClick={() => deletePage(page.id)}>
              {tr('Elimina pagina')}
            </button>
          </div>
        )}
      </Section>

      <Section title={tr('Sfondo della pagina')}>
        <Toggle
          label={tr('Sfondo diverso da quello del profilo')}
          checked={!!page.background}
          onChange={(v) =>
            updateProfile((p) => {
              const pg = p.pages.find((x) => x.id === page.id);
              if (pg) pg.background = v ? structuredClone(p.background ?? { color: '#0e0f13' }) : null;
            })
          }
        />
        {page.background && (
          <BackgroundEditor
            value={page.background}
            onChange={(bg) =>
              updateProfile((p) => {
                const pg = p.pages.find((x) => x.id === page.id);
                if (pg) pg.background = bg;
              })
            }
          />
        )}
      </Section>
    </>
  );
}

function ProfileSection() {
  const profile = useCurrentProfile();
  const importInput = useRef<HTMLInputElement>(null);
  if (!profile) return null;

  return (
    <>
      <Section title={tr('Profilo')}>
        <label className="field">
          <span>{tr('Nome')}</span>
          <input className="input" value={profile.name} onChange={(e) => updateProfile((p) => (p.name = e.target.value))} />
        </label>
        <div className="button-row">
          <button className="btn" onClick={newProfile}>
            {tr('+ Nuovo')}
          </button>
          <button className="btn" onClick={duplicateProfile}>
            {tr('Duplica')}
          </button>
          <button
            className="btn"
            title={tr('Profilo già pronto per Lightroom Classic (si attiva quando Lightroom è in primo piano)')}
            onClick={() => putNewProfile(lightroomProfile())}
          >
            + Lightroom
          </button>
          <button className="btn danger" onClick={deleteProfile}>
            {tr('Elimina')}
          </button>
        </div>
        <div className="button-row">
          <button className="btn" onClick={() => exportProfile(profile)}>
            <Icon name="download" />
            {tr('Esporta su file')}
          </button>
          <button className="btn" onClick={() => importInput.current?.click()}>
            <Icon name="upload" />
            {tr('Importa da file…')}
          </button>
          <input
            ref={importInput}
            type="file"
            accept=".json,application/json"
            hidden
            onChange={(e) => {
              const f = e.target.files?.[0];
              e.target.value = '';
              if (f) void importProfile(f);
            }}
          />
        </div>
        <LanguageSelect />
      </Section>

      <Section title={tr('Sfondo del profilo')}>
        <BackgroundEditor value={profile.background ?? { color: '#0e0f13' }} onChange={(bg) => updateProfile((p) => (p.background = bg))} />
      </Section>

      <AutoProfileSection />
    </>
  );
}

/** All paired devices: name, profile shown at startup, revoke. */
function DevicesSection() {
  const profiles = useDeck((s) => s.profiles);
  const deviceId = useDeck((s) => s.deviceId);
  const devices = useDeck((s) => s.devices);
  if (devices.length === 0) return null;
  const sorted = [...devices].sort((a, b) => (a.id === deviceId ? -1 : b.id === deviceId ? 1 : a.name.localeCompare(b.name)));

  return (
    <Section title={tr('Dispositivi')}>
      <p className="muted small hint">{tr("Ogni dispositivo all'apertura mostra il suo profilo; da lì si può comunque cambiare.")}</p>
      {sorted.map((d) => (
        <div key={d.id} className="device">
          <div className="device-head">
            <span className={`device-dot${d.online ? ' online' : ''}`} title={d.online ? tr('Connesso') : tr('Non connesso')} />
            <input
              className="input compact device-name"
              defaultValue={d.name}
              key={d.name}
              onBlur={(e) => {
                const v = e.target.value.trim();
                if (v && v !== d.name) updateDevice(d.id, { name: v });
              }}
            />
            {d.id === deviceId && <span className="muted small">{tr('(questo)')}</span>}
          </div>
          <div className="field-row">
            <span>{tr("All'apertura")}</span>
            <select
              className="input compact"
              value={d.defaultProfileId ?? ''}
              onChange={(e) => updateDevice(d.id, { defaultProfileId: e.target.value || null })}
            >
              <option value="">{tr('Il primo profilo')}</option>
              {profiles.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </div>
          <div className="device-foot">
            <span className="muted small">
              {tr('Ultimo accesso: {0}', new Date(d.lastSeen).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' }))}
            </span>
            <button
              className="btn ghost danger-text"
              onClick={() => {
                const self = d.id === deviceId;
                const msg = self
                  ? tr('Revocare QUESTO dispositivo? Verrà disconnesso subito e dovrai associarlo di nuovo.')
                  : tr('Revocare «{0}»? Verrà disconnesso e dovrà essere associato di nuovo.', d.name);
                if (confirm(msg)) revokeDevice(d.id);
              }}
            >
              {tr('Revoca')}
            </button>
          </div>
        </div>
      ))}
    </Section>
  );
}

/** Automatic switch: this profile appears while one of its apps is in front on the PC. */
function AutoProfileSection() {
  const profile = useCurrentProfile();
  const fgApp = useDeck((s) => s.fgApp);
  const profiles = useDeck((s) => s.profiles);
  const info = usePcInfo();
  const [text, setText] = useState('');
  if (!profile) return null;
  const apps = profile.auto?.apps ?? [];
  const add = (app: string) => {
    const a = normApp(app);
    if (!a || apps.some((x) => normApp(x) === a)) return;
    const other = profiles.find((p) => p.id !== profile.id && p.auto?.apps?.some((x) => normApp(x) === a));
    if (other) showToast(tr('Attenzione: «{0}» è già collegata al profilo «{1}»', a, other.name), 'error', 4000);
    updateProfile((p) => (p.auto = { apps: [...(p.auto?.apps ?? []), a] }));
    setText('');
  };
  const suggestions = [...new Set([...(info?.apps ?? [])])].filter((a) => !apps.some((x) => normApp(x) === a));

  return (
    <Section title={tr('Attivazione automatica')}>
      <p className="muted small hint">
        {tr(
          'Quando una di queste app è in primo piano sul PC, i dispositivi passano da soli a questo profilo (es. Photoshop, OBS, un gioco) e tornano a quello di prima quando la lasci.',
        )}
      </p>
      <div className="tag-list">
        {apps.map((a) => (
          <span key={a} className="tag">
            {a}
            <button
              type="button"
              aria-label={tr('Rimuovi {0}', a)}
              onClick={() =>
                updateProfile((p) => {
                  const next = (p.auto?.apps ?? []).filter((x) => x !== a);
                  if (next.length) p.auto = { apps: next };
                  else delete p.auto;
                })
              }
            >
              ✕
            </button>
          </span>
        ))}
        {apps.length === 0 && <span className="muted small">{tr('Nessuna app collegata.')}</span>}
      </div>
      <form
        className="inline-row"
        onSubmit={(e) => {
          e.preventDefault();
          add(text);
        }}
      >
        <input className="input mono" list="auto-apps" placeholder="photoshop" value={text} onChange={(e) => setText(e.target.value)} />
        <datalist id="auto-apps">
          {suggestions.map((a) => (
            <option key={a} value={a} />
          ))}
        </datalist>
        <button className="btn" type="submit" disabled={!text.trim()}>
          {tr('Aggiungi')}
        </button>
      </form>
      <div className="button-row">
        <button type="button" className="btn" onClick={() => void refreshPcInfo()}>
          {tr('↻ App aperte sul PC')}
        </button>
      </div>
      <p className="muted small">
        {tr(
          "App in primo piano ora: {0}. Scrivi il nome del programma senza «.exe» (es. obs64, photoshop, chrome) o sceglilo dall'elenco. Il cambio automatico si può disattivare per ogni dispositivo dal menu.",
          <b>{fgApp ?? '—'}</b>,
        )}
      </p>
    </Section>
  );
}

function ObsSection() {
  const info = usePcInfoWatch((i) => !i.obs?.enabled || i.obs.connected);
  const obs = info?.obs;
  const [port, setPort] = useState<number | null>(null);
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  if (!obs) return null;
  const save = async (enabled = obs.enabled) => {
    setBusy(true);
    try {
      await request({ t: 'edit.obs.set', enabled, port: port ?? obs.port, ...(password ? { password } : {}) });
      setPassword('');
      showToast(tr('Impostazioni OBS salvate'));
      window.setTimeout(() => void refreshPcInfo(), 1500);
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };
  return (
    <Section title="OBS Studio">
      <p className={`muted small hint${obs.enabled && !obs.connected ? ' error-text' : ''}`}>
        {obs.connected
          ? tr('✓ Connesso a OBS.')
          : obs.enabled
            ? tr('Non connesso{0}.', obs.error ? `: ${obs.error}` : '')
            : tr('Integrazione disattivata.')}
      </p>
      <Toggle label={tr('Collegati a OBS su questo PC')} checked={obs.enabled} onChange={(v) => void save(v)} />
      <div className="field-row">
        <span>{tr('Porta WebSocket')}</span>
        <input className="input num" type="number" value={port ?? obs.port} onChange={(e) => setPort(Number(e.target.value) || 4455)} />
      </div>
      <label className="field">
        <span>{tr('Password del server WebSocket {0}', obs.hasPassword ? tr('(già salvata)') : '')}</span>
        <input className="input" type="password" autoComplete="off" value={password} onChange={(e) => setPassword(e.target.value)} />
      </label>
      <button type="button" className="btn" disabled={busy} onClick={() => void save()}>
        {tr('Salva e riconnetti')}
      </button>
      <p className="muted small hint">
        {tr(
          'In OBS: Strumenti → Impostazioni server WebSocket → Abilita, poi «Mostra informazioni di connessione» per la password. La connessione resta sul PC (127.0.0.1).',
        )}
      </p>
    </Section>
  );
}

function PluginsSection() {
  const plugins = useDeck((s) => s.plugins);
  const errors = useDeck((s) => s.pluginErrors);
  return (
    <Section title="Plugin">
      {plugins.map((p) => (
        <div key={p.id} className="plugin-card">
          <div>
            <b>{p.name}</b> <span className="muted small">{[p.version, p.author].filter(Boolean).join(' · ')}</span>
          </div>
          {p.description && <div className="muted small">{p.description}</div>}
          <div className="muted small">{tr('{0} azioni · {1} sorgenti dinamiche', p.actions.length, p.sources.length)}</div>
        </div>
      ))}
      {plugins.length === 0 && <p className="muted small">{tr('Nessun plugin installato.')}</p>}
      {errors.map((e) => (
        <p key={e} className="small error-text">
          {e}
        </p>
      ))}
      <div className="button-row">
        <button type="button" className="btn" onClick={() => socket.send({ t: 'edit.plugins.reload' })}>
          {tr('↻ Ricarica')}
        </button>
        <button type="button" className="btn" onClick={() => socket.send({ t: 'edit.plugins.openFolder' })}>
          {tr('Apri cartella sul PC')}
        </button>
      </div>
      <p className="muted small hint">
        {tr(
          'Ogni plugin è una cartella con un file plugin.json in %APPDATA%\\PadNook\\plugins: la guida (PLUGIN.md) e un esempio sono già lì.',
        )}
      </p>
    </Section>
  );
}
