import { tr } from '../i18n';
import { useState, type ReactNode } from 'react';

export function Section({ title, extra, children }: { title: string; extra?: ReactNode; children: ReactNode }) {
  return (
    <section className="panel-section">
      <div className="section-title-row">
        <div className="section-title">{title}</div>
        {extra}
      </div>
      {children}
    </section>
  );
}

/** "Customized for this page" badge with a reset button. */
export function OverrideBadge({ on, onReset }: { on: boolean; onReset: () => void }) {
  if (!on) return null;
  return (
    <button type="button" className="override-badge" onClick={onReset} title={tr('Torna al valore del profilo')}>
      {tr('Solo questa pagina · ↺')}
    </button>
  );
}

export function Stepper({ value, min, max, onChange }: { value: number; min: number; max: number; onChange: (v: number) => void }) {
  return (
    <div className="stepper">
      <button type="button" className="btn" disabled={value <= min} onClick={() => onChange(value - 1)}>
        −
      </button>
      <span>{value}</span>
      <button type="button" className="btn" disabled={value >= max} onClick={() => onChange(value + 1)}>
        +
      </button>
    </div>
  );
}

export function Slider({
  label,
  value,
  min,
  max,
  unit = 'px',
  onChange,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  unit?: string;
  onChange: (v: number) => void;
}) {
  return (
    <label className="field">
      <span>{label}</span>
      <div className="slider-row">
        <input type="range" min={min} max={max} value={value} onChange={(e) => onChange(Number(e.target.value))} />
        <input
          className="input num"
          type="number"
          inputMode="numeric"
          min={min}
          max={max}
          value={value}
          onChange={(e) => {
            const v = Number(e.target.value);
            if (e.target.value !== '' && !Number.isNaN(v)) onChange(Math.min(max, Math.max(min, Math.round(v))));
          }}
        />
        <span className="muted small unit">{unit}</span>
      </div>
    </label>
  );
}

export function Toggle({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="toggle">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>{label}</span>
    </label>
  );
}

export function Segmented<T extends string>({ value, options, onChange }: { value: T; options: [T, string][]; onChange: (v: T) => void }) {
  return (
    <div className="segmented">
      {options.map(([v, l]) => (
        <button key={v} type="button" className={v === value ? 'on' : ''} onClick={() => onChange(v)}>
          {l}
        </button>
      ))}
    </div>
  );
}

export const SWATCHES = ['#2b2f3a', '#3b3f8f', '#1f4d3a', '#5a2a2a', '#7a4b12', '#5b2a6e', '#0f5e6e', '#111111', '#e5e7eb'];

export function ColorField({ value, onChange }: { value: string; onChange: (c: string) => void }) {
  return (
    <div className="color-field">
      <input type="color" value={value} onChange={(e) => onChange(e.target.value)} />
      <div className="swatches">
        {SWATCHES.map((c) => (
          <button
            key={c}
            type="button"
            className={`swatch${c === value ? ' on' : ''}`}
            style={{ background: c }}
            onClick={() => onChange(c)}
            aria-label={c}
          />
        ))}
      </div>
    </div>
  );
}

/** Secondary tabs inside a panel tab: one group of settings visible at a time. A dot marks a configured option. */
export function SubTabs<T extends string>({
  value,
  options,
  onChange,
}: {
  value: T;
  options: [T, string, boolean?][];
  onChange: (v: T) => void;
}) {
  return (
    <div className="subtabs" role="tablist">
      {options.map(([v, l, dot]) => (
        <button
          key={v}
          type="button"
          role="tab"
          aria-selected={v === value}
          className={`subtab${v === value ? ' on' : ''}`}
          onClick={() => onChange(v)}
        >
          {l}
          {dot && <span className="subtab-dot" aria-label={tr('configurato')} />}
        </button>
      ))}
    </div>
  );
}

const remembered = new Map<string, string>();

/** useState that survives switching keys / remounts (e.g. the last sub-tab used), for this session only. */
export function useRemembered<T extends string>(id: string, initial: T): [T, (v: T) => void] {
  const [v, setV] = useState<T>((remembered.get(id) as T) ?? initial);
  return [
    v,
    (next: T) => {
      remembered.set(id, next);
      setV(next);
    },
  ];
}
