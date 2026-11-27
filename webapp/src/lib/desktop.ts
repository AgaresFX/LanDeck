// Desktop editor: the webapp running inside the PadNook window on the PC (WebView2).
// The host injects window.__padnookDesktop = { key } and answers requests through chrome.webview messages.

interface HostBridge {
  postMessage: (msg: unknown) => void;
  postMessageWithAdditionalObjects?: (msg: unknown, objects: unknown) => void;
  addEventListener: (type: 'message', fn: (e: { data: unknown }) => void) => void;
}

const w = window as unknown as { __padnookDesktop?: { key: string }; chrome?: { webview?: HostBridge } };
const bridge = w.chrome?.webview;

/** True inside the desktop editor window. */
export const isDesktop = !!(w.__padnookDesktop?.key && bridge);

export const desktopKey = (): string | undefined => w.__padnookDesktop?.key;

let seq = 0;
const waiting = new Map<number, (v: unknown) => void>();
bridge?.addEventListener('message', (e) => {
  const m = e.data as { id?: number; result?: unknown };
  if (typeof m?.id === 'number' && waiting.has(m.id)) {
    waiting.get(m.id)!(m.result);
    waiting.delete(m.id);
  }
});

function call<T>(cmd: string, args: object = {}): Promise<T> {
  return new Promise((resolve) => {
    if (!bridge) return resolve(null as T);
    const id = ++seq;
    waiting.set(id, resolve as (v: unknown) => void);
    bridge.postMessage({ id, cmd, ...args });
  });
}

export type BrowseKind = 'program' | 'file' | 'folder' | 'sound';

/** Windows "Open" / folder dialog. Returns the chosen path, or null if cancelled. */
export function browse(kind: BrowseKind, current?: string): Promise<string | null> {
  return call<string | null>('browse', { kind, current: current ?? '' });
}

/** Full paths of files dropped on the page (only the desktop host can see them). */
export function droppedPaths(files: FileList): Promise<string[]> {
  return new Promise((resolve) => {
    if (!bridge?.postMessageWithAdditionalObjects || files.length === 0) return resolve([]);
    const id = ++seq;
    waiting.set(id, (v) => resolve((v as string[]) ?? []));
    bridge.postMessageWithAdditionalObjects({ id, cmd: 'dropPaths' }, files);
  });
}
