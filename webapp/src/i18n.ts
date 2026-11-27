// UI translation. Source strings are Italian and double as keys; EN maps them to English.
// The language is fixed at load (module-level labels are built once): changing it reloads the page.
import { createElement, Fragment, type ReactNode } from 'react';
import { EN } from './i18n.en';

export type Lang = 'it' | 'en';
export type LangSetting = 'auto' | Lang;

const STORAGE_KEY = 'padnook.lang';

export function getLangSetting(): LangSetting {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    return v === 'it' || v === 'en' ? v : 'auto';
  } catch {
    return 'auto';
  }
}

export function setLangSetting(v: LangSetting) {
  try {
    if (v === 'auto') localStorage.removeItem(STORAGE_KEY);
    else localStorage.setItem(STORAGE_KEY, v);
  } catch {
    /* private mode: ignore */
  }
  location.reload();
}

function detect(): Lang {
  const s = getLangSetting();
  if (s !== 'auto') return s;
  const langs = navigator.languages?.length ? navigator.languages : [navigator.language];
  return (langs[0] ?? '').toLowerCase().startsWith('it') ? 'it' : 'en';
}

export const lang: Lang = detect();
/** Locale for dates and numbers. */
export const locale: string =
  lang === 'it' ? 'it-IT' : navigator.language?.toLowerCase().startsWith('it') ? 'en-GB' : navigator.language || 'en-GB';

if (typeof document !== 'undefined') document.documentElement.lang = lang;

type Prim = string | number | boolean | null | undefined;

const prim = (a: Prim) => (a == null || a === false || a === true ? '' : String(a));

/** Translate an Italian UI string; {0}, {1}… are replaced by args (strings or React nodes). */
export function tr(key: string, ...args: Prim[]): string;
export function tr(key: string, ...args: ReactNode[]): ReactNode;
export function tr(key: string, ...args: ReactNode[]): ReactNode {
  // 'Testo@@contesto' disambiguates identical Italian strings that translate differently.
  const at = key.indexOf('@@');
  const s = lang === 'en' ? (EN[key] ?? (at >= 0 ? key.slice(0, at) : key)) : at >= 0 ? key.slice(0, at) : key;
  if (!args.length) return s;
  const parts = s.split(/\{(\d+)\}/);
  if (args.every((a) => a == null || typeof a !== 'object')) {
    return parts.map((p, i) => (i % 2 ? prim(args[+p] as Prim) : p)).join('');
  }
  return createElement(Fragment, null, ...parts.map((p, i) => (i % 2 ? args[+p] : p)));
}
