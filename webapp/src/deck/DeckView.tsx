import { tr } from '../i18n';
import { useRef, useState, type CSSProperties } from 'react';
import { moveKey } from '../editor/keyOps';
import { imageUrl } from '../lib/images';
import { updateProfile } from '../net/client';
import { computeGrid, flexAlign, placeKeys, resolveLayout } from '../lib/layout';
import { useElementSize } from '../lib/hooks';
import { newId } from '../lib/platform';
import { showToast, useCurrentPage, useCurrentProfile, useDeck } from '../state/store';
import type { Background, Cell, Key, KeyStyle, Orientation } from '../types';
import { KeyView } from './KeyView';
import { TimerBar } from './TimerBar';
import { isSoundpadPage, newSoundKey } from '../editor/SoundpadEditor';

/** Look of a brand new key (also used when a key changes type). */
export function defaultKeyStyle(): KeyStyle {
  return {
    label: { text: '', size: 13, color: '#ffffff', position: 'bottom' },
    icon: { kind: 'emoji', value: '' },
    bg: { color: '#2b2f3a' },
  };
}

export function newKey(orientation: Orientation, cell: Cell): Key {
  return {
    id: newId('k'),
    span: { w: 1, h: 1 },
    pos: { [orientation]: cell },
    style: defaultKeyStyle(),
    kind: 'action',
    onPress: null,
    onLongPress: null,
  };
}

export function backgroundStyle(bg: Background | null | undefined): CSSProperties {
  const style: CSSProperties = { backgroundColor: bg?.color ?? '#0e0f13' };
  if (bg?.imageId) {
    style.backgroundImage = `url(${imageUrl(bg.imageId)})`;
    style.backgroundPosition = 'center';
    style.backgroundSize = bg.fit === 'contain' ? 'contain' : bg.fit === 'tile' ? 'auto' : 'cover';
    style.backgroundRepeat = bg.fit === 'tile' ? 'repeat' : 'no-repeat';
  }
  return style;
}

export function DeckView({ orientation, preview = false }: { orientation: Orientation; preview?: boolean }) {
  const profile = useCurrentProfile();
  const page = useCurrentPage();
  const editing = useDeck((s) => s.editing);
  const selectedKeyId = useDeck((s) => s.selectedKeyId);
  const areaRef = useRef<HTMLDivElement>(null);
  const gridRef = useRef<HTMLDivElement>(null);
  const size = useElementSize(areaRef);
  const [drop, setDrop] = useState<{ r: number; c: number; w: number; h: number } | null>(null);

  const bg = page?.background ?? profile?.background;

  const content = (() => {
    if (!profile || !page) return <div className="deck-empty">{tr('Nessun profilo disponibile')}</div>;
    if (size.w === 0 || size.h === 0) return null;

    const layout = resolveLayout(profile, page);
    const m = computeGrid(layout, orientation, size.w, size.h);
    const { placed, hidden, empty } = placeKeys(page.keys, m.rows, m.cols, orientation);

    const addKeyAt = (cell: Cell) => {
      const key = isSoundpadPage(page) ? newSoundKey(orientation, cell) : newKey(orientation, cell);
      updateProfile((p) => p.pages.find((x) => x.id === page.id)?.keys.push(key));
      useDeck.setState({ selectedKeyId: key.id });
    };

    // Drag & drop (edit mode): the cell under the pointer, minus where the key was grabbed.
    const targetCell = (x: number, y: number, grab: Cell): Cell | null => {
      const rect = gridRef.current?.getBoundingClientRect();
      if (!rect) return null;
      const c = Math.floor((x - rect.left) / (m.cellW + layout.gapX)) - grab.c;
      const r = Math.floor((y - rect.top) / (m.cellH + layout.gapY)) - grab.r;
      return { r: Math.max(0, Math.min(m.rows - 1, r)), c: Math.max(0, Math.min(m.cols - 1, c)) };
    };
    const drag = {
      move: (keyId: string, x: number, y: number, grab: Cell) => {
        const p = placed.find((pl) => pl.key.id === keyId);
        const t = targetCell(x, y, grab);
        if (!p || !t) return;
        const next = { ...t, w: p.w, h: p.h };
        setDrop((d) => (d && d.r === next.r && d.c === next.c && d.w === next.w && d.h === next.h ? d : next));
      },
      end: (keyId: string, x: number, y: number, grab: Cell) => {
        setDrop(null);
        const t = targetCell(x, y, grab);
        if (t && !moveKey(page.id, orientation, placed, keyId, t, m.rows, m.cols)) {
          const me = placed.find((pl) => pl.key.id === keyId);
          if (me && (me.r !== t.r || me.c !== t.c)) showToast(tr('Non c’è spazio per il tasto in quel punto'), 'error');
        }
      },
      cancel: () => setDrop(null),
    };

    return (
      <>
        <div
          className="deck-scroll"
          style={{
            padding: `${layout.margin.top}px ${layout.margin.right}px ${layout.margin.bottom}px ${layout.margin.left}px`,
            overflowX: m.scrollX ? 'auto' : 'hidden',
            overflowY: m.scrollY ? 'auto' : 'hidden',
            justifyContent: m.scrollX ? 'flex-start' : flexAlign(layout.align.h),
            alignItems: m.scrollY ? 'flex-start' : flexAlign(layout.align.v),
          }}
          onPointerDown={(e) => {
            if (editing && e.target === e.currentTarget) useDeck.setState({ selectedKeyId: null });
          }}
        >
          <div
            className="grid"
            ref={gridRef}
            style={{
              gridTemplateColumns: `repeat(${m.cols}, ${m.cellW}px)`,
              gridTemplateRows: `repeat(${m.rows}, ${m.cellH}px)`,
              columnGap: layout.gapX,
              rowGap: layout.gapY,
            }}
          >
            {placed.map((p) => (
              <KeyView
                key={p.key.id}
                p={p}
                pageId={page.id}
                layout={layout}
                cellW={m.cellW}
                cellH={m.cellH}
                editing={editing}
                selected={editing && selectedKeyId === p.key.id}
                drag={editing ? drag : undefined}
              />
            ))}
            {drop && (
              <div
                className="drop-target"
                style={{
                  gridRow: `${drop.r + 1} / span ${drop.h}`,
                  gridColumn: `${drop.c + 1} / span ${drop.w}`,
                  borderRadius: layout.cornerRadius,
                }}
              />
            )}
            {editing &&
              empty.map((c) => (
                <button
                  key={`e${c.r}-${c.c}`}
                  className="cell-empty"
                  style={{ gridRow: c.r + 1, gridColumn: c.c + 1, borderRadius: layout.cornerRadius }}
                  onClick={() => addKeyAt(c)}
                  aria-label={tr('Aggiungi tasto')}
                >
                  +
                </button>
              ))}
          </div>
        </div>
        {editing && hidden.length > 0 && (
          <div className="hidden-note">
            {tr(
              '{0} nella griglia in {1}',
              hidden.length === 1 ? tr('1 tasto non entra') : tr('{0} tasti non entrano', hidden.length),
              orientation === 'portrait' ? tr('verticale') : tr('orizzontale'),
            )}
          </div>
        )}
      </>
    );
  })();

  return (
    <div className={`deck${preview ? ' previewing' : ''}`}>
      {/* In preview the grid is drawn inside a frame with the other orientation's proportions. */}
      <div className={preview ? `deck-frame ${orientation}` : 'deck-full'} style={backgroundStyle(bg)}>
        <div className="deck-safe">
          {!preview && !editing && <TimerBar />}
          <div className="deck-area" ref={areaRef}>
            {content}
          </div>
        </div>
      </div>
    </div>
  );
}
