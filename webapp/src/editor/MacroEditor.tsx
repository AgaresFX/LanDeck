import { tr } from '../i18n';
import { useEffect, useState } from 'react';
import { request } from '../net/client';
import { showToast } from '../state/store';
import type { Action, MacroAction, MacroStep, MouseAction } from '../types';
import { ActionEditor, typeLabel } from './ActionEditor';
import { Segmented, Toggle } from './controls';
import { MEDIA_GROUPS, OBS_GROUPS, SYSTEM_GROUPS, WINDOW_GROUPS } from './ExtraEditors';
import { MODIFIERS, keyLabel } from './keys';
import { discordCommandLabel } from './DiscordEditor';
import { Icon, type IconName } from '../ui/Icon';

// ---- Mouse action ---------------------------------------------------------------------------

const MOUSE_OPS: [MouseAction['op'], string][] = [
  ['click', tr('Clic')],
  ['down', tr('Premi il pulsante (senza rilasciare)')],
  ['up', tr('Rilascia il pulsante')],
  ['move', tr('Sposta in un punto')],
  ['moveBy', tr('Sposta di un tot di pixel')],
  ['scroll', tr('Rotellina')],
];

const BUTTONS: [NonNullable<MouseAction['button']>, string][] = [
  ['left', tr('Sinistro')],
  ['right', tr('Destro')],
  ['middle', tr('Centrale')],
];

export const mouseDefault = (): MouseAction => ({ type: 'mouse', op: 'click', button: 'left', count: 1 });

function NumField({ label, value, onChange }: { label: string; value: number | undefined; onChange: (v: number) => void }) {
  return (
    <label className="coord">
      <span>{label}</span>
      <input
        className="input num"
        type="number"
        inputMode="numeric"
        value={value ?? ''}
        onChange={(e) => onChange(Math.round(Number(e.target.value) || 0))}
      />
    </label>
  );
}

/** X/Y fields plus «Prendi dal cursore»: a short countdown, then the PC reports where the mouse is. */
function PointField({ x, y, onChange }: { x?: number; y?: number; onChange: (x: number, y: number) => void }) {
  const [count, setCount] = useState(0);
  useEffect(() => {
    if (count <= 0) return;
    const t = window.setTimeout(async () => {
      if (count > 1) return setCount(count - 1);
      setCount(0);
      try {
        const m = await request({ t: 'edit.mouse.pos' });
        onChange(Number(m.x), Number(m.y));
      } catch (e) {
        showToast((e as Error).message, 'error');
      }
    }, 1000);
    return () => clearTimeout(t);
  }, [count]); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div className="point-field">
      <div className="coords">
        <NumField label="X" value={x} onChange={(v) => onChange(v, y ?? 0)} />
        <NumField label="Y" value={y} onChange={(v) => onChange(x ?? 0, v)} />
      </div>
      <button type="button" className={`btn${count ? ' primary' : ''}`} onClick={() => setCount(count ? 0 : 3)}>
        {count ? tr('Porta il mouse sul punto… {0}', count) : tr('Prendi la posizione del mouse')}
      </button>
      {!!count && (
        <p className="muted small hint">{tr('Tra {0} s leggo dove si trova il puntatore sul PC. Tocca di nuovo per annullare.', count)}</p>
      )}
    </div>
  );
}

export function MouseEditor({ value, onChange }: { value: MouseAction; onChange: (a: Action) => void }) {
  const op = value.op;
  const hasButton = op === 'click' || op === 'down' || op === 'up';
  const optionalPoint = hasButton || op === 'scroll';
  const atPoint = value.x != null && value.y != null;
  const set = (patch: Partial<MouseAction>) => onChange({ ...value, ...patch });
  const amount = value.amount ?? -3;

  return (
    <>
      <label className="field">
        <span>{tr('Cosa fa il mouse')}</span>
        <select
          className="input"
          value={op}
          onChange={(e) => {
            const next = e.target.value as MouseAction['op'];
            const base: MouseAction = { type: 'mouse', op: next };
            if (next === 'click' || next === 'down' || next === 'up') base.button = value.button ?? 'left';
            if (next === 'click') base.count = 1;
            if (next === 'move' || (next !== 'moveBy' && atPoint)) Object.assign(base, { x: value.x ?? 0, y: value.y ?? 0 });
            if (next === 'moveBy') Object.assign(base, { dx: 0, dy: 0 });
            if (next === 'scroll') base.amount = -3;
            onChange(base);
          }}
        >
          {MOUSE_OPS.map(([v, l]) => (
            <option key={v} value={v}>
              {l}
            </option>
          ))}
        </select>
      </label>

      {hasButton && (
        <div className="field">
          <span>{tr('Pulsante')}</span>
          <Segmented value={value.button ?? 'left'} options={BUTTONS} onChange={(button) => set({ button })} />
        </div>
      )}

      {op === 'click' && (
        <Segmented
          value={value.count === 2 ? '2' : '1'}
          options={[
            ['1', tr('Clic singolo')],
            ['2', tr('Doppio clic')],
          ]}
          onChange={(c) => set({ count: Number(c) })}
        />
      )}

      {op === 'scroll' && (
        <>
          <Segmented
            value={amount > 0 ? 'pos' : 'neg'}
            options={
              value.horizontal
                ? [
                    ['neg', tr('← Sinistra')],
                    ['pos', tr('Destra →')],
                  ]
                : [
                    ['pos', tr('↑ Su')],
                    ['neg', tr('↓ Giù')],
                  ]
            }
            onChange={(d) => set({ amount: (d === 'pos' ? 1 : -1) * Math.abs(amount) })}
          />
          <div className="field-row">
            <span>{tr('Scatti della rotellina')}</span>
            <input
              className="input num"
              type="number"
              min={1}
              max={50}
              value={Math.abs(amount)}
              onChange={(e) => set({ amount: Math.sign(amount || -1) * Math.max(1, Math.min(50, Number(e.target.value) || 1)) })}
            />
          </div>
          <Toggle label={tr('Scorrimento orizzontale')} checked={!!value.horizontal} onChange={(horizontal) => set({ horizontal })} />
        </>
      )}

      {op === 'moveBy' && (
        <div className="coords">
          <NumField label="→ X" value={value.dx} onChange={(dx) => set({ dx })} />
          <NumField label="↓ Y" value={value.dy} onChange={(dy) => set({ dy })} />
        </div>
      )}

      {optionalPoint && (
        <div className="field">
          <span>{tr('Dove')}</span>
          <Segmented
            value={atPoint ? 'point' : 'here'}
            options={[
              ['here', tr('Dove si trova il mouse')],
              ['point', tr('In un punto preciso')],
            ]}
            onChange={(v) => (v === 'point' ? set({ x: value.x ?? 0, y: value.y ?? 0 }) : set({ x: undefined, y: undefined }))}
          />
        </div>
      )}
      {(op === 'move' || (optionalPoint && atPoint)) && <PointField x={value.x} y={value.y} onChange={(x, y) => set({ x, y })} />}
      {(op === 'move' || atPoint) && (
        <p className="muted small hint">
          {tr('Coordinate in pixel dello schermo del PC (0, 0 = angolo in alto a sinistra dello schermo principale).')}
        </p>
      )}
    </>
  );
}

// ---- Step summaries ---------------------------------------------------------------------------

const groupLabel = (groups: [string, [string, string][]][], v: string) => groups.flatMap(([, items]) => items).find(([c]) => c === v)?.[1];
const fileName = (p: string) => p.split(/[\\/]/).filter(Boolean).pop() ?? p;
const btnName = (b?: string) => (b === 'right' ? tr('destro') : b === 'middle' ? tr('centrale') : tr('sinistro'));

function mouseSummary(a: MouseAction): string {
  const at = a.x != null && a.y != null ? tr(' su {0}, {1}', a.x, a.y) : '';
  switch (a.op) {
    case 'click':
      return `${a.count === 2 ? tr('Doppio clic') : tr('Clic')} ${btnName(a.button)}${at}`;
    case 'down':
      return tr('Premi il {0}{1}', btnName(a.button), at);
    case 'up':
      return tr('Rilascia il {0}{1}', btnName(a.button), at);
    case 'move':
      return tr('Sposta il mouse a {0}, {1}', a.x ?? 0, a.y ?? 0);
    case 'moveBy':
      return tr('Sposta il mouse di {0}, {1}', a.dx ?? 0, a.dy ?? 0);
    case 'scroll': {
      const n = a.amount ?? -3;
      const dir = a.horizontal ? (n > 0 ? tr('a destra') : tr('a sinistra')) : n > 0 ? tr('su') : tr('giù');
      return tr('Rotellina {0} × {1}{2}', dir, Math.abs(n), at);
    }
  }
}

export function actionIcon(a: Action | null): IconName {
  switch (a?.type) {
    case undefined:
      return 'pause';
    case 'mouse':
      return 'mouse-pointer-click';
    case 'hotkey':
    case 'text':
      return 'keyboard';
    case 'launch':
      return 'rocket';
    case 'media':
      return 'volume-2';
    case 'sound':
      return 'music';
    case 'command':
      return 'terminal';
    default:
      return 'zap';
  }
}

export function actionSummary(a: Action | null): string {
  if (!a) return tr('Pausa');
  switch (a.type) {
    case 'mouse':
      return mouseSummary(a);
    case 'hotkey':
      return a.keys.length
        ? a.keys.map((k) => ((MODIFIERS as readonly string[]).includes(k) ? k : keyLabel(k))).join(' + ')
        : tr('Scorciatoia (nessun tasto)');
    case 'text':
      return a.text ? tr('Scrivi «{0}»', a.text.split('\n')[0]) : tr('Scrivi un testo');
    case 'launch':
      return a.target ? tr('Apri {0}', fileName(a.target)) : tr('Apri…');
    case 'sound':
      return a.command === 'stopAll' ? tr('Ferma tutti i suoni') : a.file ? tr('Suona {0}', fileName(a.file)) : tr('Suona un file');
    case 'command':
      return a.command ? `${a.shell === 'powershell' ? 'PowerShell' : 'cmd'}: ${a.command.split('\n')[0]}` : tr('Comando');
    case 'media':
      return groupLabel(MEDIA_GROUPS, a.command) ?? typeLabel(a.type);
    case 'system':
      return groupLabel(SYSTEM_GROUPS, a.command) ?? typeLabel(a.type);
    case 'window':
      return groupLabel(WINDOW_GROUPS, a.command) ?? typeLabel(a.type);
    case 'obs':
      return `OBS: ${groupLabel(OBS_GROUPS, a.command) ?? a.command}`;
    case 'discord':
      return `Discord: ${discordCommandLabel(a.command)}`;
    case 'counter':
      return tr('Contatore «{0}»', a.name);
    case 'timer':
      return `Timer «${a.name}»`;
    default:
      return typeLabel(a.type);
  }
}

// ---- Macro ------------------------------------------------------------------------------------

const DEFAULT_GAP = 50;

export function MacroEditor({ value, onChange }: { value: MacroAction; onChange: (a: Action) => void }) {
  const steps = value.steps;
  const [open, setOpen] = useState<number | null>(steps.length === 1 ? 0 : null);
  const [testing, setTesting] = useState(false);
  const setSteps = (next: MacroStep[]) => onChange({ ...value, steps: next });
  const update = (i: number, patch: Partial<MacroStep>) => setSteps(steps.map((s, j) => (j === i ? { ...s, ...patch } : s)));
  const move = (i: number, d: -1 | 1) => {
    const next = [...steps];
    [next[i], next[i + d]] = [next[i + d], next[i]];
    setSteps(next);
    if (open === i) setOpen(i + d);
    else if (open === i + d) setOpen(i);
  };
  const remove = (i: number) => {
    setSteps(steps.filter((_, j) => j !== i));
    if (open === i) setOpen(null);
    else if (open !== null && open > i) setOpen(open - 1);
  };
  const duplicate = (i: number) => {
    setSteps([...steps.slice(0, i + 1), structuredClone(steps[i]), ...steps.slice(i + 1)]);
    setOpen(i + 1);
  };
  const add = (step: MacroStep) => {
    setSteps([...steps, step]);
    setOpen(steps.length);
  };

  const test = async () => {
    setTesting(true);
    try {
      const m = await request({ t: 'edit.action.test', action: value }, 180000);
      if (m.ok) showToast(tr('Macro eseguita'));
      else showToast(String(m.error ?? tr('Macro non riuscita')), 'error');
    } catch (e) {
      showToast((e as Error).message, 'error');
    } finally {
      setTesting(false);
    }
  };

  const total = steps.reduce((t, s) => t + (s.delayMs || 0), 0);

  return (
    <div className="macro">
      {steps.length > 0 && (
        <ol className="macro-list">
          {steps.map((step, i) => {
            const pause = !step.action;
            return (
              <li key={i} className={`macro-row${open === i ? ' open' : ''}${pause ? ' pause' : ''}`}>
                <div className="macro-row-head">
                  <button type="button" className="macro-row-main" onClick={() => setOpen(open === i ? null : i)}>
                    <span className="macro-num">{i + 1}</span>
                    <span className="macro-icon">
                      <Icon name={actionIcon(step.action)} />
                    </span>
                    <span className="macro-text ellipsis">{pause ? tr('Pausa {0} ms', step.delayMs) : actionSummary(step.action)}</span>
                    {!pause && step.delayMs > 0 && <span className="macro-wait">+{step.delayMs} ms</span>}
                  </button>
                  <div className="macro-row-tools">
                    <button type="button" className="btn mini" disabled={i === 0} onClick={() => move(i, -1)} title={tr('Sposta su')}>
                      ↑
                    </button>
                    <button
                      type="button"
                      className="btn mini"
                      disabled={i === steps.length - 1}
                      onClick={() => move(i, 1)}
                      title={tr('Sposta giù')}
                    >
                      ↓
                    </button>
                    <button type="button" className="btn mini" onClick={() => duplicate(i)} title={tr('Duplica passo')}>
                      ⧉
                    </button>
                    <button type="button" className="btn mini" onClick={() => remove(i)} title={tr('Elimina passo')}>
                      ✕
                    </button>
                  </div>
                </div>
                {open === i && (
                  <div className="macro-detail">
                    {pause ? (
                      <DelayField label={tr('Durata della pausa')} value={step.delayMs} onChange={(delayMs) => update(i, { delayMs })} />
                    ) : (
                      <>
                        <ActionEditor nested value={step.action} onChange={(action) => update(i, { action })} />
                        <DelayField
                          label={tr('Attesa dopo questo passo')}
                          value={step.delayMs}
                          onChange={(delayMs) => update(i, { delayMs })}
                        />
                      </>
                    )}
                  </div>
                )}
              </li>
            );
          })}
        </ol>
      )}

      <div className="macro-add">
        <button type="button" className="btn" onClick={() => add({ action: mouseDefault(), delayMs: DEFAULT_GAP })}>
          <Icon name="mouse-pointer-click" />
          Mouse
        </button>
        <button type="button" className="btn" onClick={() => add({ action: { type: 'hotkey', keys: [] }, delayMs: DEFAULT_GAP })}>
          <Icon name="keyboard" />
          {tr('Tasti')}
        </button>
        <button type="button" className="btn" onClick={() => add({ action: null, delayMs: 500 })}>
          <Icon name="pause" />
          {tr('Pausa')}
        </button>
        <button type="button" className="btn" onClick={() => add({ action: { type: 'text', text: '' }, delayMs: DEFAULT_GAP })}>
          <Icon name="plus" />
          {tr('Altro')}
        </button>
      </div>

      <div className="macro-foot">
        <button type="button" className="btn primary" disabled={testing || steps.length === 0} onClick={test}>
          {testing ? tr('In esecuzione…') : tr('Prova la macro')}
        </button>
        <span className="muted small">
          {steps.length} {steps.length === 1 ? tr('passo') : tr('passi')}
          {total > 0 && tr(' · {0} di attese', total >= 1000 ? `${(total / 1000).toFixed(1)} s` : `${total} ms`)}
        </span>
      </div>
    </div>
  );
}

function DelayField({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) {
  return (
    <div className="field-row">
      <span>{label}</span>
      <div className="slider-row">
        <input
          className="input num"
          type="number"
          inputMode="numeric"
          min={0}
          max={60000}
          step={50}
          value={value}
          onChange={(e) => onChange(Math.max(0, Math.min(60000, Number(e.target.value) || 0)))}
        />
        <span className="muted small">ms</span>
      </div>
    </div>
  );
}
