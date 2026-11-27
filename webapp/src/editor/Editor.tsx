import { tr } from '../i18n';
import { useEffect, useRef, useState } from 'react';
import { copyKey, duplicateKey, pasteKey } from './keyOps';
import { closeEditor, lockEditor, selectProfile, setToggleState, updateProfile } from '../net/client';
import { useDeckOrientation, useOrientation } from '../lib/hooks';
import { resolveLayout } from '../lib/layout';
import { LucideGlyph } from '../lib/lucide';
import { currentPage, homePage, showToast, useCurrentPage, useCurrentProfile, useDeck } from '../state/store';
import type { IconStyle, Key, ToggleConfig } from '../types';
import { StatusDot } from '../ui/Overlays';
import { ActionEditor } from './ActionEditor';
import { ColorField, Section, Segmented, Slider, Stepper, SubTabs, Toggle, useRemembered } from './controls';
import { LayoutPanel } from './LayoutPanel';
import { addPage, deleteKey, goToPage, openFolder, renamePage, setKeyKind, setTimerKind } from './ops';
import { IconPicker, ImagePicker } from './pickers';
import { LiveSections, PRESETS, applyPresetKeepingAction, isTimerKey, presetForAction, syncLiveWithAction } from './LiveEditor';
import { TimerSections } from './TimerKeyEditor';
import { KeyPreview } from '../deck/KeyView';
import { isDesktop } from '../lib/desktop';
import { fetchProgramIcon, hasProgramIcon, programIconIds } from '../lib/images';
import { SliderSections } from './SliderEditor';
import { SoundKeySections, SoundpadSettingsPanel, addSoundpadPage, isSoundpadPage } from './SoundpadEditor';
import { Icon, type IconName } from '../ui/Icon';

// ---- Top bar ------------------------------------------------------------------------------

export function EditorBar() {
  const profiles = useDeck((s) => s.profiles);
  const profileId = useDeck((s) => s.profileId);
  const actual = useOrientation();
  const shown = useDeckOrientation();

  const togglePreview = () => {
    const next = shown === 'portrait' ? 'landscape' : 'portrait';
    useDeck.setState({ previewOrientation: next === actual ? null : next });
  };

  return (
    <div className="editor-top">
      <div className="editor-bar">
        <StatusDot inline />
        <select className="input compact" value={profileId ?? ''} onChange={(e) => selectProfile(e.target.value)}>
          {profiles.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>
        <button
          className={`btn ghost orient${shown !== actual ? ' active' : ''}`}
          onClick={togglePreview}
          title={tr(
            "Stai modificando la griglia {0}: tocca per passare all'altra",
            shown === 'portrait' ? tr('verticale') : tr('orizzontale'),
          )}
        >
          <span className={`orient-icon ${shown}`} />
          <span className="orient-label">{shown === 'portrait' ? tr('Verticale') : tr('Orizzontale')}</span>
        </button>
        <div className="spacer" />
        {!isDesktop && (
          <>
            <button className="btn ghost hide-narrow" onClick={() => useDeck.setState({ pinMode: 'set', pinError: null })}>
              {tr('Cambia PIN')}
            </button>
            <button className="btn ghost" onClick={lockEditor} title={tr('Esci e blocca con il PIN')}>
              <Icon name="lock" />
            </button>
            <button className="btn primary" onClick={closeEditor}>
              {tr('Fine')}
            </button>
          </>
        )}
      </div>
      <PagesBar />
    </div>
  );
}

/** Page tabs, or the folder path when editing inside a folder. */
function PagesBar() {
  const profile = useCurrentProfile();
  const page = useCurrentPage();
  const pageStack = useDeck((s) => s.pageStack);
  if (!profile || !page) return null;

  if (page.kind === 'folder') {
    const path = pageStack.map((id) => profile.pages.find((p) => p.id === id)).filter((p) => !!p);
    const root = path[0]?.kind === 'folder' ? homePage(profile) : null;
    const names = [...(root ? [root] : []), ...path].map((p) => p!.name);
    return (
      <div className="pages-bar">
        <button
          className="chip"
          onClick={() =>
            useDeck.setState((s) => ({
              pageStack: s.pageStack.slice(0, -1),
              selectedKeyId: null,
            }))
          }
        >
          {tr('‹ Indietro')}
        </button>
        <span className="crumbs">{names.join(' › ')}</span>
      </div>
    );
  }

  const pages = profile.pages.filter((p) => p.kind !== 'folder');
  return (
    <div className="pages-bar">
      {pages.map((p) => (
        <button key={p.id} className={`chip${p.id === page.id ? ' on' : ''}`} onClick={() => goToPage(p.id)}>
          {p.kind === 'soundpad' && <Icon name="volume-2" size={14} />}
          {p.name}
        </button>
      ))}
      <button className="chip add" onClick={addPage} title={tr('Nuova pagina')}>
        {tr('+ Pagina')}
      </button>
      <button className="chip add" onClick={addSoundpadPage} title={tr('Nuova pagina con soli tasti soundpad')}>
        + Soundpad
      </button>
    </div>
  );
}

// ---- Side panel ---------------------------------------------------------------------------

export function EditorPanel() {
  const page = useCurrentPage();
  const selectedKeyId = useDeck((s) => s.selectedKeyId);
  const hasClipboard = useDeck((s) => s.hasClipboard);
  const orientation = useDeckOrientation();
  const key = page?.keys.find((k) => k.id === selectedKeyId);

  // PC shortcuts: Ctrl+C / Ctrl+V / Ctrl+D / Canc, unless the user is typing in a field.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target;
      if (t instanceof Element && t.closest('input, textarea, select, [contenteditable]')) return;
      if (useDeck.getState().pinMode || document.querySelector('.overlay')) return; // a dialog is open
      const sel = useDeck.getState().selectedKeyId;
      const pg = currentPage(useDeck.getState());
      const mod = e.ctrlKey || e.metaKey;
      if (mod && e.key.toLowerCase() === 'c' && sel) copyKey(sel);
      else if (mod && e.key.toLowerCase() === 'v') pasteKey(orientation);
      else if (mod && e.key.toLowerCase() === 'd' && sel) duplicateKey(sel, orientation);
      else if ((e.key === 'Delete' || e.key === 'Backspace') && sel && pg) {
        const k = pg.keys.find((x) => x.id === sel);
        if (k?.kind === 'folder' && !confirm(tr('Eliminare la cartella e tutti i tasti che contiene?'))) return;
        deleteKey(pg.id, sel);
      } else if (e.key === 'Escape') useDeck.setState({ selectedKeyId: null });
      else return;
      e.preventDefault();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [orientation]);

  return (
    <aside className="panel">
      {key && page ? (
        <KeyInspector key={key.id} k={key} pageId={page.id} />
      ) : (
        <LayoutPanel
          top={
            <div className="panel-intro">
              <p className="muted small">
                {tr('Clicca un tasto per modificarlo o una cella vuota per crearne uno. Trascina un tasto per spostarlo.')}
              </p>
              {hasClipboard && (
                <button className="btn tool" onClick={() => pasteKey(orientation)}>
                  {tr('Incolla tasto')}
                </button>
              )}
            </div>
          }
          pageExtra={isSoundpadPage(page) ? <SoundpadSettingsPanel /> : undefined}
        />
      )}
    </aside>
  );
}

// ---- Toggle keys --------------------------------------------------------------------------

function ToggleSections({
  k,
  toggle,
  edit,
  part,
}: {
  k: Key;
  toggle: ToggleConfig;
  edit: (fn: (key: Key) => void) => void;
  part: 'actions' | 'look';
}) {
  const on = useDeck((s) => !!s.toggles[k.id]);
  const [picking, setPicking] = useState(false);
  const editT = (fn: (t: ToggleConfig) => void) =>
    edit((x) => {
      if (x.toggle) fn(x.toggle);
    });
  const os = toggle.onStyle;

  if (part === 'actions')
    return (
      <>
        <Section title={tr('Stato attuale')}>
          <Segmented
            value={on ? 'on' : 'off'}
            options={[
              ['off', 'OFF'],
              ['on', 'ON'],
            ]}
            onChange={(v) => setToggleState(k.id, v === 'on')}
          />
          <p className="muted small hint">{tr("Cambia lo stato senza eseguire azioni: utile per vedere e regolare l'aspetto ON.")}</p>
        </Section>

        <Section title={tr('Quando si attiva (OFF → ON)')}>
          <ActionEditor value={toggle.onAction} onChange={(a) => editT((t) => (t.onAction = a))} />
        </Section>

        <Section title={tr('Quando si disattiva (ON → OFF)')}>
          <Toggle
            label={tr("Stessa azione dell'attivazione")}
            checked={toggle.sameAction}
            onChange={(v) => editT((t) => (t.sameAction = v))}
          />
          {!toggle.sameAction && <ActionEditor value={toggle.offAction} onChange={(a) => editT((t) => (t.offAction = a))} />}
        </Section>
      </>
    );

  return (
    <>
      <Section title={tr('Aspetto quando è ON')}>
        <Segmented
          value={on ? 'on' : 'off'}
          options={[
            ['off', tr('Vedi OFF')],
            ['on', tr('Vedi ON')],
          ]}
          onChange={(v) => setToggleState(k.id, v === 'on')}
        />
        <p className="muted small hint">
          {tr("Le schede Testo, Icona e Sfondo definiscono l'aspetto OFF; qui scegli cosa cambia quando è ON.")}
        </p>
        <label className="field">
          <span>{tr('Testo (vuoto = uguale a OFF)')}</span>
          <input
            className="input"
            value={os.labelText ?? ''}
            placeholder={k.style.label.text}
            onChange={(e) => editT((t) => (t.onStyle.labelText = e.target.value))}
          />
        </label>
        <Toggle
          label={tr('Colore di sfondo diverso')}
          checked={os.bgColor != null}
          onChange={(v) => editT((t) => (v ? (t.onStyle.bgColor = '#1f7a4a') : delete t.onStyle.bgColor))}
        />
        {os.bgColor != null && <ColorField value={os.bgColor} onChange={(c) => editT((t) => (t.onStyle.bgColor = c))} />}
        <Toggle
          label={tr('Icona diversa')}
          checked={!!os.icon}
          onChange={(v) =>
            editT((t) =>
              v
                ? (t.onStyle.icon = {
                    kind: 'lucide',
                    value: 'Check',
                    color: '#ffffff',
                  })
                : delete t.onStyle.icon,
            )
          }
        />
        {os.icon && (
          <>
            <Segmented
              value={os.icon.kind === 'emoji' ? 'emoji' : 'lucide'}
              options={[
                ['emoji', 'Emoji'],
                ['lucide', tr('Icona')],
              ]}
              onChange={(kind) =>
                editT(
                  (t) =>
                    (t.onStyle.icon = {
                      ...t.onStyle.icon!,
                      kind,
                      value: kind === 'emoji' ? '✅' : 'Check',
                    }),
                )
              }
            />
            {os.icon.kind === 'emoji' ? (
              <input
                className="input emoji-input wide"
                value={os.icon.value}
                onChange={(e) =>
                  editT(
                    (t) =>
                      (t.onStyle.icon = {
                        ...t.onStyle.icon!,
                        value: e.target.value,
                      }),
                  )
                }
              />
            ) : (
              <>
                <div className="lucide-current">
                  <span className="lucide-preview">
                    <LucideGlyph name={os.icon.value} size={32} color={os.icon.color} />
                  </span>
                  <button type="button" className="btn" onClick={() => setPicking(true)}>
                    {tr('Scegli icona…')}
                  </button>
                </div>
                <div className="field-row">
                  <span>{tr('Colore icona')}</span>
                  <input
                    type="color"
                    value={os.icon.color ?? '#ffffff'}
                    onChange={(e) =>
                      editT(
                        (t) =>
                          (t.onStyle.icon = {
                            ...t.onStyle.icon!,
                            color: e.target.value,
                          }),
                      )
                    }
                  />
                </div>
              </>
            )}
          </>
        )}
      </Section>

      {picking && (
        <IconPicker
          onClose={() => setPicking(false)}
          onPick={(name) => {
            editT(
              (t) =>
                (t.onStyle.icon = {
                  ...(t.onStyle.icon ?? { color: '#ffffff' }),
                  kind: 'lucide',
                  value: name,
                }),
            );
            setPicking(false);
          }}
        />
      )}
    </>
  );
}

// ---- Key inspector ------------------------------------------------------------------------

const EMOJIS = [
  '⏯️',
  '⏮️',
  '⏭️',
  '🔊',
  '🔉',
  '🔇',
  '🎤',
  '📷',
  '🎬',
  '🖥️',
  '🌐',
  '📝',
  '📁',
  '⚙️',
  '🔒',
  '💡',
  '🎮',
  '🎵',
  '⭐',
  '🚀',
  '✅',
  '❌',
  '📞',
  '💬',
];

const ICON_DEFAULTS: Record<IconStyle['kind'], string> = {
  none: '',
  emoji: '⭐',
  lucide: 'Star',
  image: '',
};

/** Key types shown in the tiles; "timer" is a dynamic key with its own timer, edited in one tab. */
type KindTile = Key['kind'] | 'timer';

const KINDS: [KindTile, IconName, string, string][] = [
  ['action', 'zap', tr('Azione'), tr('Esegue un comando a ogni tocco.')],
  ['toggle', 'repeat', tr('Interruttore'), tr('Ogni tocco alterna ON e OFF; lo stato è condiviso da tutti i dispositivi.')],
  [
    'dynamic',
    'radio',
    tr('Dinamico'),
    tr('Mostra dati in tempo reale (orologio, CPU, meteo, contatori…) e cambia aspetto in base allo stato.'),
  ],
  [
    'timer',
    'hourglass',
    'Timer',
    tr('Conto alla rovescia o cronometro: tocca per avviare / mettere in pausa, tieni premuto per azzerare.'),
  ],
  [
    'slider',
    'sliders-horizontal',
    'Slider',
    tr('Trascina il dito sul tasto per regolare volume, microfono, luminosità o un cursore di Lightroom.'),
  ],
  ['folder', 'folder', tr('Cartella'), tr('Apre una pagina con altri tasti.')],
];

type Tab = 'data' | 'action' | 'timer' | 'sound' | 'look';

const SOUND_TABS: [Tab, string][] = [
  ['sound', tr('Suono')],
  ['look', tr('Aspetto')],
];

const TABS: Record<KindTile, [Tab, string][]> = {
  timer: [
    ['timer', 'Timer'],
    ['look', tr('Aspetto')],
  ],
  action: [
    ['action', tr('Azione')],
    ['look', tr('Aspetto')],
  ],
  toggle: [
    ['action', tr('Azioni')],
    ['look', tr('Aspetto')],
  ],
  dynamic: [
    ['data', tr('Dati')],
    ['action', tr('Azione')],
    ['look', tr('Aspetto')],
  ],
  slider: [
    ['action', 'Slider'],
    ['look', tr('Aspetto')],
  ],
  folder: [
    ['action', tr('Cartella')],
    ['look', tr('Aspetto')],
  ],
};

type Gesture = 'tap' | 'long' | 'double';
type LookTab = 'on' | 'text' | 'icon' | 'bg' | 'shape';

/** The tab stays the same when moving from key to key. */
let lastTab: Tab = 'action';

function KeyInspector({ k, pageId }: { k: Key; pageId: string }) {
  const profile = useCurrentProfile();
  const page = useCurrentPage();
  const orientation = useDeckOrientation();
  const [pickingIcon, setPickingIcon] = useState(false);
  const [kindMenu, setKindMenu] = useState(false);
  const [gestureState, setGesture] = useRemembered<Gesture>('key.gesture', 'tap');
  const [lookState, setLook] = useRemembered<LookTab>('key.look', 'text');
  const [tabState, setTabState] = useState<Tab>(lastTab);
  const layout = profile && page ? resolveLayout(profile, page) : null;
  const grid = layout?.[orientation] ?? { rows: 12, cols: 12 };
  const folder = k.folderPageId ? profile?.pages.find((p) => p.id === k.folderPageId) : undefined;

  const timerKey = isTimerKey(k);
  const tileKind: KindTile = timerKey ? 'timer' : k.kind;
  const soundpad = isSoundpadPage(page);
  const tabs = soundpad ? SOUND_TABS : TABS[tileKind];
  const tab: Tab = tabs.some(([t]) => t === tabState) ? tabState : tabs[0][0];
  const setTab = (t: Tab) => {
    lastTab = t;
    setTabState(t);
  };

  const edit = (fn: (key: Key) => void) =>
    updateProfile((p) => {
      const key = p.pages.find((x) => x.id === pageId)?.keys.find((x) => x.id === k.id);
      if (key) fn(key);
    });

  const s = k.style;
  const icon = s.icon;
  const kindInfo = KINDS.find(([v]) => v === tileKind)!;
  const statePreset = k.kind === 'action' ? presetForAction(k.onPress) : null;
  const hasGestures = k.kind !== 'folder' && k.kind !== 'slider';
  const gesture: Gesture = hasGestures ? gestureState : 'tap';
  const lookTabs: [LookTab, string][] = [
    ...(k.kind === 'toggle' ? ([['on', tr('Stato ON')]] as [LookTab, string][]) : []),
    ['text', tr('Testo')],
    ['icon', tr('Icona')],
    ['bg', tr('Sfondo')],
    ['shape', tr('Forma')],
  ];
  const look: LookTab = lookTabs.some(([t]) => t === lookState) ? lookState : 'text';

  // «Apri programma»: the key takes the program's real icon (still changeable from the Aspetto tab).
  const launchTarget = k.onPress?.type === 'launch' ? k.onPress.target : '';
  const lastTarget = useRef({ keyId: k.id, target: launchTarget });
  useEffect(() => {
    const prev = lastTarget.current;
    if (prev.keyId !== k.id) lastTarget.current = { keyId: k.id, target: launchTarget };
    if (prev.keyId !== k.id || prev.target === launchTarget || !hasProgramIcon(launchTarget)) return;
    const timer = setTimeout(async () => {
      lastTarget.current = { keyId: k.id, target: launchTarget };
      const id = await fetchProgramIcon(launchTarget).catch(() => null);
      if (!id) return;
      edit((x) => {
        if (x.onPress?.type !== 'launch' || x.onPress.target !== launchTarget) return;
        const ic = x.style.icon;
        // Only replace an empty/default icon or one that came from another program.
        const replaceable =
          ic.kind === 'none' ||
          !ic.value.trim() ||
          (ic.kind === 'emoji' && ic.value === ICON_DEFAULTS.emoji) ||
          (ic.kind === 'image' && programIconIds.has(ic.value));
        if (replaceable) x.style.icon = { kind: 'image', value: id, scale: ic.scale };
      });
    }, 400);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [k.id, launchTarget]);

  const setIconKind = (kind: IconStyle['kind']) => {
    edit((x) => (x.style.icon = { ...x.style.icon, kind, value: ICON_DEFAULTS[kind] }));
    if (kind === 'lucide') setPickingIcon(true);
  };

  const setPress = (a: Key['onPress']) => {
    let converted = false;
    edit((x) => {
      const prev = x.onPress;
      x.onPress = a;
      converted = syncLiveWithAction(x, prev);
    });
    if (converted) {
      showToast(a?.type === 'counter' ? tr('Il tasto ora mostra il valore del contatore') : tr('Il tasto ora mostra il tempo del timer'));
    }
  };

  const remove = () => {
    if (k.kind === 'folder' && !confirm(tr('Eliminare la cartella e tutti i tasti che contiene?'))) return;
    deleteKey(pageId, k.id);
  };

  const title =
    s.label.text
      .replace(/\{[a-zA-Z]+\}/g, '')
      .trim()
      .split('\n')[0] || (timerKey ? 'Timer' : k.kind === 'dynamic' ? tr('Tasto dinamico') : tr('Tasto senza nome'));

  return (
    <div className="inspector">
      <div className="inspector-sticky">
        <div className="inspector-head">
          <KeyPreview k={k} radius={layout?.cornerRadius ?? 16} />
          <div className="inspector-title">
            <div className="sheet-title ellipsis">{title}</div>
            {soundpad ? (
              <div className="kind-chip static">
                <Icon name="volume-2" size={14} /> Soundpad
              </div>
            ) : (
              <button
                type="button"
                className={`kind-chip${kindMenu ? ' on' : ''}`}
                onClick={() => setKindMenu(!kindMenu)}
                title={tr('Cambia il tipo di tasto')}
              >
                <Icon name={kindInfo[1]} size={14} /> {kindInfo[2]}
                <span className="kind-chip-caret">
                  <Icon name="chevron-down" size={12} />
                </span>
              </button>
            )}
            <div className="inspector-tools">
              <button className="btn tool" onClick={() => copyKey(k.id)} title={tr('Copia (Ctrl+C)')}>
                {tr('Copia')}
              </button>
              <button className="btn tool" onClick={() => duplicateKey(k.id, orientation)} title={tr('Duplica (Ctrl+D)')}>
                {tr('Duplica')}
              </button>
              <button className="btn tool danger" onClick={remove} title={tr('Elimina (Canc)')}>
                {tr('Elimina')}
              </button>
            </div>
          </div>
          <button className="btn ghost close-x" onClick={() => useDeck.setState({ selectedKeyId: null })} title={tr('Chiudi (Esc)')}>
            <Icon name="x" />
          </button>
        </div>
        {kindMenu && !soundpad && (
          <div className="kind-menu">
            <div className="kind-tiles">
              {KINDS.map(([v, icon, label]) => (
                <button
                  key={v}
                  type="button"
                  className={`kind-tile${tileKind === v ? ' on' : ''}`}
                  onClick={() => {
                    setKindMenu(false);
                    if (v === tileKind) return;
                    if (
                      k.kind === 'folder' &&
                      folder &&
                      folder.keys.length > 1 &&
                      !confirm(tr('Eliminare la cartella e tutti i tasti che contiene?'))
                    )
                      return;
                    if (v === 'timer') return setTimerKind(pageId, k.id);
                    // Leaving a timer key: its start/reset actions go away with it.
                    if (timerKey)
                      edit((x) => {
                        x.onPress = null;
                        x.onLongPress = null;
                      });
                    if (v === 'dynamic' && k.kind === 'dynamic') edit((x) => PRESETS.find((p) => p.id === 'clock')!.apply(x));
                    else setKeyKind(pageId, k.id, v);
                  }}
                >
                  <span className="kind-emoji">
                    <Icon name={icon} size={18} />
                  </span>
                  <span>{label}</span>
                </button>
              ))}
            </div>
            <p className="muted small hint kind-hint">{kindInfo[3]}</p>
          </div>
        )}
        <div className="tabs" role="tablist">
          {tabs.map(([t, label]) => (
            <button
              key={t}
              type="button"
              role="tab"
              aria-selected={tab === t}
              className={`tab${tab === t ? ' on' : ''}`}
              onClick={() => setTab(t)}
            >
              {label}
            </button>
          ))}
        </div>
      </div>

      {tab === 'sound' && <SoundKeySections k={k} edit={edit} />}

      {tab === 'data' && k.kind === 'dynamic' && <LiveSections k={k} edit={edit} />}

      {tab === 'timer' && timerKey && <TimerSections k={k} edit={edit} />}

      {tab === 'action' && (
        <>
          {hasGestures && (
            <div className="subtabs-bar">
              <SubTabs
                value={gesture}
                options={[
                  ['tap', tr('Tocco')],
                  ['long', tr('Tieni premuto'), !!k.onLongPress],
                  ['double', tr('Doppio tocco'), !!k.onDoubleTap],
                ]}
                onChange={setGesture}
              />
            </div>
          )}
          {gesture === 'tap' && (
            <>
              {(k.kind === 'action' || k.kind === 'dynamic') && (
                <Section title={tr('Quando premi il tasto')}>
                  <ActionEditor value={k.onPress} onChange={setPress} />
                </Section>
              )}

              {statePreset && (
                <div className="callout">
                  <div>
                    <b>{tr('Vuoi vedere lo stato sul tasto?')}</b>
                    <div className="muted small">
                      {tr(
                        'Il tasto diventa dinamico e mostra in tempo reale lo stato di questa azione ( {0}).',
                        PRESETS.find((p) => p.id === statePreset)?.label,
                      )}
                    </div>
                  </div>
                  <button className="btn primary" onClick={() => edit((x) => applyPresetKeepingAction(x, presetForAction(x.onPress)!))}>
                    {tr('Mostra lo stato')}
                  </button>
                </div>
              )}

              {k.kind === 'toggle' && k.toggle && <ToggleSections k={k} toggle={k.toggle} edit={edit} part="actions" />}

              {k.kind === 'folder' && folder && (
                <Section title={tr('Cartella')}>
                  <label className="field">
                    <span>{tr('Nome della cartella')}</span>
                    <input className="input" value={folder.name} onChange={(e) => renamePage(folder.id, e.target.value)} />
                  </label>
                  <button className="btn primary" onClick={() => openFolder(folder.id)}>
                    {tr('Apri cartella ›')}
                  </button>
                  <p className="muted small hint">{tr('La cartella si apre con un tocco e contiene già un tasto «Indietro».')}</p>
                </Section>
              )}

              {k.kind === 'slider' && k.slider && <SliderSections k={k} cfg={k.slider} edit={edit} />}
            </>
          )}

          {gesture === 'long' && (
            <Section title={tr('Quando tieni premuto')}>
              <Toggle
                label={tr('Azione diversa tenendo premuto')}
                checked={!!k.onLongPress}
                onChange={(v) => edit((x) => (x.onLongPress = v ? { type: 'hotkey', keys: [] } : null))}
              />
              {k.onLongPress && (
                <>
                  <ActionEditor value={k.onLongPress} onChange={(a) => edit((x) => (x.onLongPress = a))} />
                  <Slider
                    label={tr('Durata della pressione')}
                    unit="ms"
                    value={k.longPressMs ?? 500}
                    min={250}
                    max={2000}
                    onChange={(v) => edit((x) => (x.longPressMs = v))}
                  />
                  <p className="muted small hint">
                    {tr(
                      "Con una pressione prolungata configurata, l'azione normale di questo tasto parte al rilascio invece che al tocco.",
                    )}
                  </p>
                </>
              )}
            </Section>
          )}

          {gesture === 'double' && (
            <Section title={tr('Quando tocchi due volte')}>
              <Toggle
                label={tr('Azione diversa con un doppio tocco')}
                checked={!!k.onDoubleTap}
                onChange={(v) =>
                  edit((x) => {
                    if (v) x.onDoubleTap = { type: 'hotkey', keys: [] };
                    else {
                      delete x.onDoubleTap;
                      delete x.doubleTapMs;
                    }
                  })
                }
              />
              {k.onDoubleTap && (
                <>
                  <ActionEditor value={k.onDoubleTap} onChange={(a) => edit((x) => (x.onDoubleTap = a))} />
                  <Slider
                    label={tr('Tempo tra i due tocchi')}
                    unit="ms"
                    value={k.doubleTapMs ?? 300}
                    min={150}
                    max={800}
                    onChange={(v) => edit((x) => (x.doubleTapMs = v))}
                  />
                  <p className="muted small hint">
                    {tr("Con un doppio tocco configurato, l'azione normale parte solo dopo questo tempo, se non arriva un secondo tocco.")}
                  </p>
                </>
              )}
            </Section>
          )}
        </>
      )}

      {tab === 'look' && (
        <>
          <div className="subtabs-bar">
            <SubTabs value={look} options={lookTabs} onChange={setLook} />
          </div>
          {look === 'on' && k.kind === 'toggle' && k.toggle && <ToggleSections k={k} toggle={k.toggle} edit={edit} part="look" />}

          {look === 'text' && (
            <Section title={tr('Testo')}>
              <textarea
                className="input textarea"
                rows={k.kind === 'dynamic' || s.label.text.includes('\n') ? 2 : 1}
                placeholder={k.kind === 'dynamic' ? '{text}' : tr('Etichetta')}
                value={s.label.text}
                onChange={(e) => edit((x) => (x.style.label.text = e.target.value))}
              />
              {k.kind === 'dynamic' && (
                <p className="muted small hint">
                  <code>{'{text}'}</code> = {timerKey ? tr('il tempo del timer') : tr('valore della sorgente (vedi scheda Dati)')}.
                </p>
              )}
              <div className="field-row">
                <span>{tr('Posizione')}</span>
                <select
                  className="input compact"
                  value={s.label.position}
                  onChange={(e) => edit((x) => (x.style.label.position = e.target.value as Key['style']['label']['position']))}
                >
                  <option value="top">{tr('In alto')}</option>
                  <option value="center">{tr('Al centro')}</option>
                  <option value="bottom">{tr('In basso')}</option>
                  <option value="hidden">{tr('Nascosto')}</option>
                </select>
              </div>
              <div className="field-row">
                <span>{tr('Allineamento')}</span>
                <Segmented
                  value={s.label.align ?? 'center'}
                  options={[
                    ['left', tr('Sinistra')],
                    ['center', tr('Centro')],
                    ['right', tr('Destra')],
                  ]}
                  onChange={(v) => edit((x) => (x.style.label.align = v))}
                />
              </div>
              <Slider
                label={tr('Dimensione')}
                value={s.label.size}
                min={8}
                max={40}
                onChange={(v) => edit((x) => (x.style.label.size = v))}
              />
              <div className="field-row">
                <span>{tr('Colore')}</span>
                <input type="color" value={s.label.color} onChange={(e) => edit((x) => (x.style.label.color = e.target.value))} />
              </div>
              <Toggle label={tr('Grassetto')} checked={!!s.label.bold} onChange={(v) => edit((x) => (x.style.label.bold = v))} />
            </Section>
          )}

          {look === 'icon' && (
            <Section title={tr('Icona')}>
              <Segmented
                value={icon.kind}
                options={[
                  ['none', tr('Nessuna')],
                  ['emoji', 'Emoji'],
                  ['lucide', tr('Icona')],
                  ['image', tr('Immagine')],
                ]}
                onChange={setIconKind}
              />

              {icon.kind === 'emoji' && (
                <>
                  <input
                    className="input emoji-input wide"
                    placeholder="🙂"
                    value={icon.value}
                    onChange={(e) =>
                      edit(
                        (x) =>
                          (x.style.icon = {
                            ...x.style.icon,
                            value: e.target.value,
                          }),
                      )
                    }
                  />
                  <div className="emoji-grid">
                    {EMOJIS.map((em) => (
                      <button
                        key={em}
                        type="button"
                        className="emoji-btn"
                        onClick={() => edit((x) => (x.style.icon = { ...x.style.icon, value: em }))}
                      >
                        {em}
                      </button>
                    ))}
                  </div>
                </>
              )}

              {icon.kind === 'lucide' && (
                <>
                  <div className="lucide-current">
                    <span className="lucide-preview">{icon.value && <LucideGlyph name={icon.value} size={32} color={icon.color} />}</span>
                    <button type="button" className="btn" onClick={() => setPickingIcon(true)}>
                      {tr('Scegli icona…')}
                    </button>
                  </div>
                  <div className="field-row">
                    <span>{tr('Colore icona')}</span>
                    <input
                      type="color"
                      value={icon.color ?? '#ffffff'}
                      onChange={(e) =>
                        edit(
                          (x) =>
                            (x.style.icon = {
                              ...x.style.icon,
                              color: e.target.value,
                            }),
                        )
                      }
                    />
                  </div>
                </>
              )}

              {icon.kind === 'image' && (
                <ImagePicker
                  value={icon.value || null}
                  maxSize={512}
                  searchHint={s.label.text}
                  onChange={(id) => edit((x) => (x.style.icon = { ...x.style.icon, value: id ?? '' }))}
                />
              )}

              {icon.kind !== 'none' && (
                <Slider
                  label={tr('Dimensione icona')}
                  unit="%"
                  value={Math.round((icon.scale ?? (s.label.text && s.label.position !== 'hidden' ? 0.4 : 0.5)) * 100)}
                  min={10}
                  max={100}
                  onChange={(v) => edit((x) => (x.style.icon = { ...x.style.icon, scale: v / 100 }))}
                />
              )}
            </Section>
          )}

          {look === 'bg' && (
            <Section title={tr('Sfondo del tasto')}>
              <ColorField value={s.bg.color} onChange={(c) => edit((x) => (x.style.bg.color = c))} />
              <div className="field">
                <span className="muted small">{tr('Immagine di sfondo (riempie il tasto)')}</span>
                <ImagePicker
                  value={s.bg.imageId}
                  maxSize={1024}
                  searchHint={s.label.text}
                  onChange={(id) => edit((x) => (x.style.bg.imageId = id))}
                />
              </div>
            </Section>
          )}

          {look === 'shape' && (
            <>
              <Section title={tr('Dimensione')}>
                <div className="field-row">
                  <span>{tr('Larghezza (celle)')}</span>
                  <Stepper value={k.span.w} min={1} max={grid.cols} onChange={(w) => edit((x) => (x.span = { ...x.span, w }))} />
                </div>
                <div className="field-row">
                  <span>{tr('Altezza (celle)')}</span>
                  <Stepper value={k.span.h} min={1} max={grid.rows} onChange={(h) => edit((x) => (x.span = { ...x.span, h }))} />
                </div>
                <p className="muted small hint">
                  {tr("Se non c'è spazio libero intorno, il tasto viene spostato nel primo punto in cui entra.")}
                </p>
              </Section>
              <Section title={tr('Angoli')}>
                <Toggle
                  label={tr('Angoli propri')}
                  checked={s.cornerRadius != null}
                  onChange={(v) => edit((x) => (x.style.cornerRadius = v ? (layout?.cornerRadius ?? 18) : null))}
                />
                {s.cornerRadius != null && (
                  <Slider
                    label={tr('Raggio degli angoli')}
                    value={s.cornerRadius}
                    min={0}
                    max={80}
                    onChange={(v) => edit((x) => (x.style.cornerRadius = v))}
                  />
                )}
              </Section>
            </>
          )}
        </>
      )}

      {pickingIcon && (
        <IconPicker
          onClose={() => setPickingIcon(false)}
          onPick={(name) => {
            edit(
              (x) =>
                (x.style.icon = {
                  ...x.style.icon,
                  kind: 'lucide',
                  value: name,
                }),
            );
            setPickingIcon(false);
          }}
        />
      )}
    </div>
  );
}
