import { tr } from '../i18n';
// Strip above the grid with the timers that are running (or just ended) on other pages of the profile,
// e.g. inside a folder the user left: shows the time left, tap to go to the timer, button to pause/reset.
import { pressKey } from '../net/client';
import { resolveLive, useNow, type LiveMap } from '../lib/live';
import { useCurrentPage, useCurrentProfile, useDeck } from '../state/store';
import type { Key, Page } from '../types';
import { Icon } from '../ui/Icon';

interface Item {
  source: string;
  key: Key;
  page: Page;
  title: string;
  running: boolean;
}

function title(key: Key, page: Page): string {
  const label = key.style.label.text
    .replace(/\{[a-zA-Z]+\}/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return label || page.name.trim() || 'Timer';
}

function timerItems(pages: Page[], current: Page, live: LiveMap): Item[] {
  const visible = new Set(current.keys.map((k) => k.live?.source).filter(Boolean));
  const seen = new Set<string>();
  const items: Item[] = [];
  for (const page of pages) {
    if (page.id === current.id) continue;
    for (const key of page.keys) {
      const source = key.kind === 'dynamic' ? key.live?.source : undefined;
      if (!source?.startsWith('timer:') || visible.has(source) || seen.has(source)) continue;
      const t = live[source]?.timer;
      if (!t) continue;
      const ended = t.mode === 'countdown' && !t.running && t.durationMs > 0 && t.elapsedMs >= t.durationMs;
      if (!t.running && !ended) continue;
      seen.add(source);
      items.push({ source, key, page, title: title(key, page), running: t.running });
    }
  }
  return items;
}

export function TimerBar() {
  const profile = useCurrentProfile();
  const page = useCurrentPage();
  const live = useDeck((s) => s.live);
  const items = profile && page ? timerItems(profile.pages, page, live) : [];
  const now = useNow(items.some((i) => i.running));
  if (items.length === 0) return null;

  return (
    <div className="timer-bar">
      {items.map((it) => {
        const v = resolveLive({ source: it.source, rules: [] }, live, now);
        const canReset = it.key.onLongPress?.type === 'timer' && it.key.onLongPress.op === 'reset';
        return (
          <div key={it.source} className={`timer-chip${it.running ? ' running' : ' ended'}`}>
            <button
              type="button"
              className="timer-chip-main"
              onClick={() => useDeck.setState((s) => ({ pageStack: [...s.pageStack, it.page.id] }))}
              title={tr('Vai a {0}', it.page.name)}
            >
              <span className="timer-chip-title">{it.title}</span>
              <span className="timer-chip-time">{it.running ? v.text : tr('FINE')}</span>
            </button>
            {(it.running || canReset) && (
              <button
                type="button"
                className="timer-chip-btn"
                aria-label={it.running ? tr('Pausa') : tr('Azzera')}
                onClick={() => pressKey(it.key, it.page.id, it.running ? 'press' : 'long')}
              >
                <Icon name={it.running ? 'pause' : 'x'} />
              </button>
            )}
          </div>
        );
      })}
    </div>
  );
}
