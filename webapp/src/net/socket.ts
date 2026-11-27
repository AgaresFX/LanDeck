// WebSocket with automatic reconnection, heartbeat and fast resume when iOS brings the app back.

export type ConnState = 'connecting' | 'open' | 'closed';
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type ServerMsg = { t: string; [k: string]: any };

type Listener = (msg: ServerMsg) => void;

const PING_INTERVAL = 10_000;
const STALE_AFTER = 25_000;
const MAX_DELAY = 5_000;

class DeckSocket {
  private ws: WebSocket | null = null;
  private retry = 0;
  private retryTimer: number | undefined;
  private pingTimer: number | undefined;
  private lastPong = 0;
  private listeners = new Set<Listener>();
  private stateListeners = new Set<(s: ConnState) => void>();
  private onOpenCb: (() => void) | null = null;
  state: ConnState = 'closed';

  start(onOpen: () => void) {
    this.onOpenCb = onOpen;
    this.connect();
    // iOS suspends pages in background and silently kills sockets: check as soon as we are visible.
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') this.kick();
    });
    window.addEventListener('pageshow', () => this.kick());
    window.addEventListener('online', () => this.kick());
    window.addEventListener('focus', () => this.kick());
  }

  on(l: Listener) {
    this.listeners.add(l);
    return () => this.listeners.delete(l);
  }

  onState(l: (s: ConnState) => void) {
    this.stateListeners.add(l);
    return () => this.stateListeners.delete(l);
  }

  send(msg: object): boolean {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(msg));
      return true;
    }
    return false;
  }

  private setState(s: ConnState) {
    if (this.state === s) return;
    this.state = s;
    this.stateListeners.forEach((l) => l(s));
  }

  private url() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${proto}//${location.host}/ws`;
  }

  private connect() {
    clearTimeout(this.retryTimer);
    if (this.ws) return;
    this.setState('connecting');
    let ws: WebSocket;
    try {
      ws = new WebSocket(this.url());
    } catch {
      this.scheduleRetry();
      return;
    }
    this.ws = ws;
    ws.onopen = () => {
      if (this.ws !== ws) return;
      this.retry = 0;
      this.lastPong = Date.now();
      this.setState('open');
      this.startPing();
      this.onOpenCb?.();
    };
    ws.onmessage = (e) => {
      let msg: ServerMsg;
      try {
        msg = JSON.parse(e.data);
      } catch {
        return;
      }
      this.lastPong = Date.now();
      if (msg.t === 'pong') return;
      this.listeners.forEach((l) => l(msg));
    };
    ws.onclose = () => {
      if (this.ws !== ws) return;
      this.ws = null;
      this.stopPing();
      this.setState('closed');
      this.scheduleRetry();
    };
    ws.onerror = () => {
      try {
        ws.close();
      } catch {
        /* ignore */
      }
    };
  }

  private scheduleRetry() {
    const delay = Math.min(MAX_DELAY, 250 * 2 ** this.retry);
    this.retry++;
    clearTimeout(this.retryTimer);
    this.retryTimer = window.setTimeout(() => this.connect(), delay);
  }

  /** Reconnect now if closed; if apparently open, verify it is still alive. */
  kick() {
    if (!this.ws) {
      this.retry = 0;
      this.connect();
      return;
    }
    if (this.ws.readyState === WebSocket.OPEN) {
      const sentAt = Date.now();
      this.send({ t: 'ping' });
      window.setTimeout(() => {
        if (this.lastPong < sentAt && this.ws?.readyState === WebSocket.OPEN) this.forceReconnect();
      }, 1500);
    }
  }

  private forceReconnect() {
    const old = this.ws;
    this.ws = null;
    this.stopPing();
    try {
      old?.close();
    } catch {
      /* ignore */
    }
    this.setState('closed');
    this.retry = 0;
    this.connect();
  }

  private startPing() {
    this.stopPing();
    this.pingTimer = window.setInterval(() => {
      if (Date.now() - this.lastPong > STALE_AFTER) this.forceReconnect();
      else this.send({ t: 'ping' });
    }, PING_INTERVAL);
  }

  private stopPing() {
    clearInterval(this.pingTimer);
  }
}

export const socket = new DeckSocket();
