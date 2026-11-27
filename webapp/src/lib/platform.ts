// Browser/platform helpers. Storage access can throw (private mode), so it is always guarded.

export const storage = {
  get(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set(key: string, value: string | null) {
    try {
      if (value === null) localStorage.removeItem(key);
      else localStorage.setItem(key, value);
    } catch {
      /* ignore */
    }
  },
};

export const sessionStore = {
  get(key: string): string | null {
    try {
      return sessionStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set(key: string, value: string | null) {
    try {
      if (value === null) sessionStorage.removeItem(key);
      else sessionStorage.setItem(key, value);
    } catch {
      /* ignore */
    }
  },
};

export const isIOS = (): boolean =>
  /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);

export const isAndroid = (): boolean => /Android/.test(navigator.userAgent);

/** Chrome's install prompt, captured so the app can offer its own "Installa" button. */
interface InstallPromptEvent extends Event {
  prompt: () => Promise<void>;
}
let installPrompt: InstallPromptEvent | null = null;
window.addEventListener('beforeinstallprompt', (e) => {
  e.preventDefault();
  installPrompt = e as InstallPromptEvent;
});
window.addEventListener('appinstalled', () => (installPrompt = null));

export const canPromptInstall = (): boolean => installPrompt !== null;

export async function promptInstall(): Promise<void> {
  const p = installPrompt;
  installPrompt = null;
  await p?.prompt();
}

/** Copies text without the Clipboard API, which plain-HTTP pages don't get. */
export function copyText(text: string): boolean {
  const el = document.createElement('textarea');
  el.value = text;
  el.setAttribute('readonly', '');
  el.style.position = 'fixed';
  el.style.opacity = '0';
  document.body.appendChild(el);
  el.select();
  let ok = false;
  try {
    ok = document.execCommand('copy');
  } catch {
    /* not supported */
  }
  el.remove();
  return ok;
}

export const isStandalone = (): boolean =>
  (navigator as unknown as { standalone?: boolean }).standalone === true || window.matchMedia('(display-mode: standalone)').matches;

export function guessDeviceName(): string {
  const ua = navigator.userAgent;
  if (/iPad/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)) return 'iPad';
  if (/iPhone/.test(ua)) return 'iPhone';
  if (/Android/.test(ua)) return 'Android';
  return 'PC';
}

export function newId(prefix: string): string {
  const chars = 'abcdefghijklmnopqrstuvwxyz0123456789';
  let s = '';
  const rnd = crypto.getRandomValues(new Uint8Array(8));
  for (const b of rnd) s += chars[b % chars.length];
  return `${prefix}_${s}`;
}
