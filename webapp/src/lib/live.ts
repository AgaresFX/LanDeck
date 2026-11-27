import { locale } from '../i18n';
// Dynamic keys: resolve a live source to a value, fill label templates and evaluate style rules.
import { useEffect, useState } from 'react';
import type { Key, KeyStyle, LiveConfig, LiveRule, LiveValue, SliderConfig } from '../types';

export type LiveMap = Record<string, LiveValue & { at?: number }>;

const pad = (n: number) => String(n).padStart(2, '0');

/** Countdowns round up (05:00 for the whole first second), stopwatches round down (00:00 → 00:01 after one second). */
export function formatMs(ms: number, roundUp = true): string {
  const s = Math.max(0, roundUp ? Math.ceil(ms / 1000) : Math.floor(ms / 1000));
  return s >= 3600 ? `${Math.floor(s / 3600)}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}` : `${pad(Math.floor(s / 60))}:${pad(s % 60)}`;
}

function clock(format: string | undefined, now: Date): LiveValue {
  const it = (o: Intl.DateTimeFormatOptions) => now.toLocaleString(locale, o);
  const time = it({ hour: '2-digit', minute: '2-digit' });
  const date = it({ day: '2-digit', month: '2-digit', year: 'numeric' });
  const weekday = it({ weekday: 'long' });
  let text: string;
  switch (format) {
    case 'timeSec':
      text = it({ hour: '2-digit', minute: '2-digit', second: '2-digit' });
      break;
    case 'date':
      text = date;
      break;
    case 'dateTime':
      text = `${time}\n${it({ day: '2-digit', month: '2-digit' })}`;
      break;
    case 'weekday':
      text = weekday.charAt(0).toUpperCase() + weekday.slice(1);
      break;
    case 'dayMonth':
      text = it({ day: 'numeric', month: 'long' });
      break;
    default:
      text = time;
  }
  return { text, value: now.getHours() * 60 + now.getMinutes(), sub: `${weekday} ${date}` };
}

/** Current value of a source (clock and running timers are computed locally every second). */
export function resolveLive(cfg: LiveConfig, live: LiveMap, now: number): LiveValue {
  if (cfg.source === 'clock') return clock(cfg.format, new Date(now));
  const v = live[cfg.source];
  if (!v) return { text: '…' };
  if (v.timer) {
    const t = v.timer;
    let elapsed =
      t.running && t.startedAtMs ? Math.max(0, now - t.startedAtMs) : t.elapsedMs + (t.running && v.at ? Math.max(0, now - v.at) : 0);
    const down = t.mode === 'countdown';
    if (down) elapsed = Math.min(elapsed, t.durationMs);
    const shown = down ? t.durationMs - elapsed : elapsed;
    return { ...v, text: formatMs(shown, down), value: down ? Math.ceil(shown / 1000) : Math.floor(shown / 1000), state: t.running };
  }
  return v;
}

/** True if the key needs a re-render every second. */
export function ticks(cfg: LiveConfig | undefined, live: LiveMap): boolean {
  if (!cfg) return false;
  if (cfg.source === 'clock') return true;
  return !!live[cfg.source]?.timer?.running;
}

/** Replaces {text} {value} {sub} and any other field sent by the source. */
export function fillTemplate(template: string, v: LiveValue): string {
  return template.replace(/\{([a-zA-Z]+)\}/g, (m, k: string) => {
    const x = v[k];
    if (x === undefined || x === null) return k === 'text' || k === 'value' || k === 'sub' ? '' : m;
    return typeof x === 'number' ? String(Math.round(x * 10) / 10).replace('.', ',') : String(x);
  });
}

export function ruleMatches(rule: LiveRule, v: LiveValue): boolean {
  const target = rule.value ?? '';
  const num = Number(target.replace(',', '.'));
  switch (rule.when) {
    case 'true':
      return v.state === true;
    case 'false':
      return v.state === false;
    case 'gt':
      return v.value !== undefined && !isNaN(num) && v.value > num;
    case 'lt':
      return v.value !== undefined && !isNaN(num) && v.value < num;
    case 'eq':
      if (v.value !== undefined && target.trim() !== '' && !isNaN(num)) return v.value === num;
      return (v.text ?? '').toLowerCase() === target.toLowerCase();
    case 'contains':
      return target !== '' && (v.text ?? '').toLowerCase().includes(target.toLowerCase());
    case 'status': {
      const code = typeof v.status === 'number' ? String(v.status) : '';
      const t = target.trim().toLowerCase();
      if (!code || !t) return false;
      return /^[1-5]xx$/.test(t) ? code.length === 3 && code[0] === t[0] : code === t;
    }
  }
}

/** Key style for a dynamic key: the first matching rule overrides the base look. */
export function liveStyle(key: Key, v: LiveValue): KeyStyle {
  const cfg = key.live!;
  const rule = cfg.rules.find((r) => ruleMatches(r, v));
  const st = rule?.style;
  let icon = st?.icon ?? key.style.icon;
  if (!st?.icon && cfg.sourceIcon && v.icon) icon = { kind: 'emoji', value: v.icon, scale: key.style.icon.scale };
  const text = st?.labelText?.trim() ? st.labelText : key.style.label.text;
  return {
    ...key.style,
    label: { ...key.style.label, text: fillTemplate(text, v), color: st?.labelColor ?? key.style.label.color },
    icon,
    bg: { ...key.style.bg, color: st?.bgColor ?? key.style.bg.color },
  };
}

/**
 * Instant (unix ms) the displayed seconds roll over from: wall-clock seconds for the clock, the timer's own
 * start for a running timer. Ticking on the wrong phase shows a timer up to a second late.
 */
export function tickPhase(cfg: LiveConfig | undefined, live: LiveMap): number {
  const v = cfg && cfg.source !== 'clock' ? live[cfg.source] : undefined;
  if (!v?.timer || !v.at) return 0;
  return v.at - (v.timer.elapsedMs % 1000);
}

/** Re-renders every second while enabled, right after each second boundary of `phase`. */
export function useNow(enabled: boolean, phase = 0): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!enabled) return;
    let timer = 0;
    const tick = () => {
      const t = Date.now();
      setNow(t);
      const into = (((t - phase) % 1000) + 1000) % 1000;
      timer = window.setTimeout(tick, 1000 - into + 5);
    };
    tick();
    return () => clearTimeout(timer);
  }, [enabled, phase]);
  return enabled ? Math.max(now, Date.now()) : now;
}

/** Live source showing the current level of a slider key (same names as the server). */
export function sliderSource(cfg: SliderConfig | undefined): string | null {
  const target = cfg?.target ?? 'vol';
  if ((target === 'vol' || target === 'mic') && cfg?.device) return `devvol:${cfg.device}`;
  switch (target) {
    case 'vol':
      return 'vol';
    case 'mic':
      return 'micvol';
    case 'app':
      return cfg?.app?.trim() ? `appvol:${cfg.app.trim()}` : null;
    case 'brightness':
      return 'brightness';
    case 'lightroom':
      return cfg?.param ? `lr.pct:${cfg.param}` : null;
  }
}

export const sliderHasMute = (cfg: SliderConfig | undefined) => {
  const t = cfg?.target ?? 'vol';
  return t !== 'brightness' && t !== 'lightroom';
};

export function sliderVertical(cfg: SliderConfig | undefined, w: number, h: number): boolean {
  const o = cfg?.orientation ?? 'auto';
  return o === 'auto' ? h > w : o === 'vertical';
}

/** Style of a slider key: {text} {value} in the label show the level (or the value being dragged). */
export function sliderStyle(key: Key, v: LiveValue | undefined, dragging: number | null): KeyStyle {
  let value: LiveValue = v ?? { text: '…' };
  if (dragging !== null) {
    // Lightroom: show the dragged value in the parameter's unit (range sent by the PC).
    if (typeof v?.min === 'number' && typeof v?.max === 'number') {
      const x = v.min + (dragging / 100) * (v.max - v.min);
      const dec = typeof v.decimals === 'number' ? v.decimals : 2;
      const s = x.toLocaleString(locale, { minimumFractionDigits: dec, maximumFractionDigits: dec });
      value = { ...v, text: `${x > 0 && !v.unit ? '+' : ''}${s}${v.unit ?? ''}`, value: x };
    } else value = { text: `${Math.round(dragging)}%`, value: Math.round(dragging) };
  }
  return { ...key.style, label: { ...key.style.label, text: fillTemplate(key.style.label.text, value) } };
}

export function normApp(app: string): string {
  let s = app.trim().replace(/^"|"$/g, '');
  s = s.split(/[\\/]/).pop() ?? s;
  if (s.toLowerCase().endsWith('.exe')) s = s.slice(0, -4);
  return s.toLowerCase();
}
