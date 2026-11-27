import { useEffect, useRef, useState, type CSSProperties, type PointerEvent } from 'react';
import { pressKey, sendSlider, tapSlider } from '../net/client';
import { useDeck } from '../state/store';
import type { Cell, Key, KeyStyle, Layout, LiveValue } from '../types';
import type { Placement } from '../lib/layout';
import { imageUrl } from '../lib/images';
import { LucideGlyph } from '../lib/lucide';
import { liveStyle, resolveLive, sliderHasMute, sliderSource, sliderStyle, sliderVertical, tickPhase, ticks, useNow } from '../lib/live';

interface Props {
  p: Placement;
  pageId: string;
  layout: Layout;
  cellW: number;
  cellH: number;
  editing: boolean;
  selected: boolean;
  /** Edit mode only: drag & drop callbacks from the grid. */
  drag?: {
    move: (keyId: string, x: number, y: number, grab: Cell) => void;
    end: (keyId: string, x: number, y: number, grab: Cell) => void;
    cancel: () => void;
  };
}

const DRAG_THRESHOLD = 8;

function flash(el: HTMLElement, cls: string) {
  el.classList.remove(cls);
  void el.offsetWidth; // restart the animation
  el.classList.add(cls);
  window.setTimeout(() => el.classList.remove(cls), 450);
}

export function KeyView({ p, pageId, layout, cellW, cellH, editing, selected, drag }: Props) {
  const { key } = p;
  const state = useRef({ pointer: -1, timer: 0, longFired: false, tapTimer: 0, consumed: false });
  const dragState = useRef<{
    pointer: number;
    x: number;
    y: number;
    active: boolean;
    grab: Cell;
  } | null>(null);

  // ---- Edit mode: select on press, drag to move ----
  const editDown = (e: PointerEvent<HTMLDivElement>) => {
    useDeck.setState({ selectedKeyId: key.id });
    const el = e.currentTarget;
    const rect = el.getBoundingClientRect();
    const grab = {
      r: Math.floor((e.clientY - rect.top) / (cellH + layout.gapY)),
      c: Math.floor((e.clientX - rect.left) / (cellW + layout.gapX)),
    };
    dragState.current = {
      pointer: e.pointerId,
      x: e.clientX,
      y: e.clientY,
      active: false,
      grab,
    };
    try {
      el.setPointerCapture(e.pointerId);
    } catch {
      /* ignore */
    }
  };
  const editMove = (e: PointerEvent<HTMLDivElement>) => {
    const d = dragState.current;
    if (!d || d.pointer !== e.pointerId || !drag) return;
    const dx = e.clientX - d.x;
    const dy = e.clientY - d.y;
    if (!d.active && Math.hypot(dx, dy) < DRAG_THRESHOLD) return;
    d.active = true;
    e.currentTarget.classList.add('dragging');
    e.currentTarget.style.transform = `translate(${dx}px, ${dy}px)`;
    drag.move(key.id, e.clientX, e.clientY, d.grab);
  };
  const editEnd = (e: PointerEvent<HTMLDivElement>, commit: boolean) => {
    const d = dragState.current;
    if (!d || d.pointer !== e.pointerId) return;
    dragState.current = null;
    if (!d.active) return;
    e.currentTarget.classList.remove('dragging');
    e.currentTarget.style.transform = '';
    if (commit) drag?.end(key.id, e.clientX, e.clientY, d.grab);
    else drag?.cancel();
  };
  const hasLong = !!key.onLongPress && !editing;
  const hasDouble = !!key.onDoubleTap && !editing && key.kind !== 'folder' && key.kind !== 'slider';
  const toggleOn = useDeck((st) => key.kind === 'toggle' && !!st.toggles[key.id]);
  const liveCfg = key.kind === 'dynamic' ? key.live : undefined;
  const liveSrc = useDeck((st) => (liveCfg && liveCfg.source !== 'clock' ? st.live[liveCfg.source] : undefined));
  const liveMap = liveCfg && liveSrc ? { [liveCfg.source]: liveSrc } : {};
  const now = useNow(ticks(liveCfg, liveMap), tickPhase(liveCfg, liveMap));

  const width = p.w * cellW + (p.w - 1) * layout.gapX;
  const height = p.h * cellH + (p.h - 1) * layout.gapY;

  const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    const el = e.currentTarget;
    if (editing) {
      editDown(e);
      return;
    }
    state.current.pointer = e.pointerId;
    try {
      el.setPointerCapture(e.pointerId);
    } catch {
      /* ignore */
    }
    el.classList.add('pressed');

    // Second touch while a single tap is waiting: it is a double tap.
    if (hasDouble && state.current.tapTimer) {
      clearTimeout(state.current.tapTimer);
      state.current.tapTimer = 0;
      state.current.consumed = true;
      if (!pressKey(key, pageId, 'double')) flash(el, 'failed');
      return;
    }
    state.current.consumed = false;

    if (!hasLong && !hasDouble) {
      // Fire immediately on touch: no waiting for click/pointerup.
      if (!pressKey(key, pageId, 'press')) flash(el, 'failed');
      return;
    }
    state.current.longFired = false;
    if (!hasLong) return;
    state.current.timer = window.setTimeout(() => {
      state.current.longFired = true;
      el.classList.add('long');
      if (!pressKey(key, pageId, 'long')) flash(el, 'failed');
    }, key.longPressMs ?? 500);
  };

  const end = (e: PointerEvent<HTMLDivElement>, commit: boolean) => {
    if (editing) {
      editEnd(e, commit);
      return;
    }
    if (state.current.pointer !== e.pointerId) return;
    state.current.pointer = -1;
    const el = e.currentTarget;
    el.classList.remove('pressed', 'long');
    if (!hasLong && !hasDouble) return;
    clearTimeout(state.current.timer);
    if (state.current.consumed || !commit || state.current.longFired) return;
    const r = el.getBoundingClientRect();
    const inside = e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom;
    if (!inside) return;
    if (!hasDouble) {
      if (!pressKey(key, pageId, 'press')) flash(el, 'failed');
      return;
    }
    // Wait a moment for a second tap before running the normal action.
    state.current.tapTimer = window.setTimeout(() => {
      state.current.tapTimer = 0;
      if (!pressKey(key, pageId, 'press')) flash(el, 'failed');
    }, key.doubleTapMs ?? 300);
  };

  // ---- Slider keys: drag along the key to set the level, tap to mute ----
  const sliderCfg = key.kind === 'slider' ? key.slider : undefined;
  const sliderSrc = useDeck((st) => {
    const src = key.kind === 'slider' ? sliderSource(key.slider) : null;
    return src ? st.live[src] : undefined;
  });
  const [sliderDrag, setSliderDrag] = useState<number | null>(null);
  const [dragging, setDragging] = useState(false);
  // Mute state shown right after a tap, until the PC confirms it (fast taps feel instant).
  const [muteGuess, setMuteGuess] = useState<{ muted: boolean; at: number } | null>(null);
  const sl = useRef({ pointer: -1, x: 0, y: 0, start: 0, moved: false, latest: 0, lastSent: 0, timer: 0, release: 0, sent: -1 });
  const vertical = sliderVertical(sliderCfg, p.w, p.h);

  // After release, keep the dragged value until the PC reports it (no jump back to an older value).
  const liveLevel = sliderSrc?.value;
  const liveAt = sliderSrc?.at ?? 0;
  useEffect(() => {
    if (sliderDrag !== null && !dragging && liveLevel !== undefined && Math.abs(liveLevel - sl.current.sent) <= 1) {
      clearTimeout(sl.current.release);
      setSliderDrag(null);
    }
    if (muteGuess && liveAt > muteGuess.at) setMuteGuess(null);
  }, [liveLevel, liveAt, sliderDrag, dragging, muteGuess]);

  const sliderSend = (v: number) => {
    sl.current.lastSent = Date.now();
    sendSlider(key, pageId, v);
  };
  const sliderDown = (e: PointerEvent<HTMLDivElement>) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      /* ignore */
    }
    clearTimeout(sl.current.release);
    Object.assign(sl.current, {
      pointer: e.pointerId,
      x: e.clientX,
      y: e.clientY,
      start: sliderDrag ?? sliderSrc?.value ?? 0,
      moved: false,
    });
  };
  const sliderMove = (e: PointerEvent<HTMLDivElement>) => {
    const d = sl.current;
    if (d.pointer !== e.pointerId) return;
    const dx = e.clientX - d.x;
    const dy = e.clientY - d.y;
    if (!d.moved && Math.hypot(dx, dy) < 6) return;
    if (!d.moved) setDragging(true);
    d.moved = true;
    // Relative drag: the whole range over the key length (at least 140px, so small keys stay precise).
    const len = Math.max(vertical ? height : width, 140);
    const v = Math.min(100, Math.max(0, d.start + ((vertical ? -dy : dx) / len) * 100));
    d.latest = v;
    setSliderDrag(v);
    clearTimeout(d.timer);
    const wait = 60 - (Date.now() - d.lastSent);
    if (wait <= 0) sliderSend(v);
    else d.timer = window.setTimeout(() => sliderSend(d.latest), wait);
  };
  const sliderUp = (e: PointerEvent<HTMLDivElement>, commit: boolean) => {
    const d = sl.current;
    if (d.pointer !== e.pointerId) return;
    d.pointer = -1;
    const el = e.currentTarget;
    if (d.moved) {
      clearTimeout(d.timer);
      sliderSend(d.latest);
      d.sent = Math.round(d.latest);
      setDragging(false);
      // Safety net if the PC never confirms (e.g. the app stopped playing).
      d.release = window.setTimeout(() => setSliderDrag(null), 1500);
    } else if (commit && sliderCfg?.target === 'lightroom' && sliderCfg.tapMute !== false) {
      // Lightroom slider: a tap resets the parameter.
      flash(el, 'tapped');
      if (!tapSlider(key, pageId)) flash(el, 'failed');
    } else if (commit && sliderHasMute(sliderCfg) && sliderCfg?.tapMute !== false) {
      flash(el, 'tapped');
      const wasMuted = muteGuess?.muted ?? sliderSrc?.state === true;
      if (tapSlider(key, pageId)) setMuteGuess({ muted: !wasMuted, at: Date.now() });
      else flash(el, 'failed');
    }
  };

  if (sliderCfg) {
    const level = Math.min(100, Math.max(0, sliderDrag ?? sliderSrc?.value ?? 0));
    const muted = sliderDrag === null && (muteGuess?.muted ?? sliderSrc?.state === true);
    const ss = sliderStyle(key, sliderSrc, sliderDrag);
    return (
      <div
        className={`key slider${vertical ? ' vertical' : ''}${muted ? ' muted' : ''}${dragging ? ' dragging-slider' : ''}${selected ? ' selected' : ''}${editing ? ' editing' : ''}`}
        style={{
          gridRow: `${p.r + 1} / span ${p.h}`,
          gridColumn: `${p.c + 1} / span ${p.w}`,
          borderRadius: ss.cornerRadius ?? layout.cornerRadius,
          backgroundColor: ss.bg.color,
          backgroundImage: ss.bg.imageId ? `url(${imageUrl(ss.bg.imageId)})` : undefined,
        }}
        onPointerDown={editing ? onPointerDown : sliderDown}
        onPointerMove={editing ? editMove : sliderMove}
        onPointerUp={(e) => (editing ? end(e, true) : sliderUp(e, true))}
        onPointerCancel={(e) => (editing ? end(e, false) : sliderUp(e, false))}
        onContextMenu={(e) => e.preventDefault()}
      >
        <SliderFill color={sliderCfg.color} level={level} vertical={vertical} muted={muted} />
        <KeyFace s={ss} width={width} height={height} />
      </div>
    );
  }

  const s = keyStyleFor(key, toggleOn, liveSrc, now);
  return (
    <div
      className={`key${selected ? ' selected' : ''}${editing ? ' editing' : ''}${toggleOn ? ' toggle-on' : ''}${liveCfg ? ' live' : ''}`}
      style={{
        gridRow: `${p.r + 1} / span ${p.h}`,
        gridColumn: `${p.c + 1} / span ${p.w}`,
        borderRadius: s.cornerRadius ?? layout.cornerRadius,
        backgroundColor: s.bg.color,
        backgroundImage: s.bg.imageId ? `url(${imageUrl(s.bg.imageId)})` : undefined,
      }}
      onPointerDown={onPointerDown}
      onPointerMove={editing ? editMove : undefined}
      onPointerUp={(e) => end(e, true)}
      onPointerCancel={(e) => end(e, false)}
      onContextMenu={(e) => e.preventDefault()}
    >
      <KeyFace s={s} width={width} height={height} folder={key.kind === 'folder'} />
      <KeyFlash keyId={key.id} />
    </div>
  );
}

/** Blinking overlay requested by an event rule (Twitch follow, raid…). */
function KeyFlash({ keyId }: { keyId: string }) {
  const f = useDeck((s) => s.flashes[keyId]);
  if (!f) return null;
  return <div key={f.n} className="key-flash" style={{ '--flash-color': f.color } as CSSProperties} />;
}

/**
 * Bar of a slider key. Uses transform (GPU, smooth while dragging on iPhone) and a plain color for mute:
 * iOS Safari does not always repaint a CSS filter change, so the muted look is a different color.
 */
function SliderFill({ color, level, vertical, muted }: { color?: string; level: number; vertical: boolean; muted: boolean }) {
  return (
    <div
      className="slider-fill"
      style={{
        backgroundColor: muted ? '#5b6170' : (color ?? '#3b82f6'),
        opacity: muted ? 0.4 : 0.55,
        transform: vertical ? `scaleY(${level / 100})` : `scaleX(${level / 100})`,
        transformOrigin: vertical ? 'bottom' : 'left',
      }}
    />
  );
}

/** Style of a key right now: toggle ON overrides, or the live value and rules of a dynamic key. */
export function keyStyleFor(key: Key, toggleOn: boolean, liveSrc: (LiveValue & { at?: number }) | undefined, now: number): KeyStyle {
  const liveCfg = key.kind === 'dynamic' ? key.live : undefined;
  if (liveCfg) {
    // A timer that was never started only exists with the server defaults (5 min countdown): show it
    // as the key's own action will start it (e.g. a stopwatch at 00:00).
    const a = key.onPress;
    const t = liveSrc?.timer;
    if (t && !t.running && t.elapsedMs === 0 && a?.type === 'timer' && liveCfg.source === `timer:${a.name}`) {
      const mode = a.mode ?? t.mode;
      const durationMs = a.durationSec && a.durationSec > 0 ? a.durationSec * 1000 : t.durationMs;
      liveSrc = { ...liveSrc!, timer: { ...t, mode, durationMs } };
    }
    return liveStyle(key, resolveLive(liveCfg, liveSrc ? { [liveCfg.source]: liveSrc } : {}, now));
  }
  const os = key.kind === 'toggle' ? key.toggle?.onStyle : undefined;
  if (!toggleOn || !os) return key.style;
  return {
    ...key.style,
    label: {
      ...key.style.label,
      text: os.labelText?.trim() ? os.labelText : key.style.label.text,
    },
    icon: os.icon ?? key.style.icon,
    bg: { ...key.style.bg, color: os.bgColor ?? key.style.bg.color },
  };
}

/** Icon, label and folder badge of a key, sized for a key of width x height. */
export function KeyFace({
  s,
  width,
  height,
  folder,
  textScale = 1,
}: {
  s: KeyStyle;
  width: number;
  height: number;
  folder?: boolean;
  textScale?: number;
}) {
  const minDim = Math.min(width, height);
  const label = s.label;
  const icon = s.icon;
  const showLabel = label.position !== 'hidden' && label.text.trim() !== '';
  const showIcon = icon.kind !== 'none' && (icon.value ?? '').trim() !== '';
  const labelSize = Math.min(label.size * textScale, minDim / 3);
  const labelEdge = showLabel && showIcon && (label.position === 'bottom' || label.position === 'top');
  // Space the label takes from the key edge (8% inset + its lines): the icon is centered in what is left.
  let labelBlock = 0;
  if (labelEdge) {
    const lines = label.text.split('\n').reduce((n, l) => n + Math.max(1, Math.ceil((l.length * labelSize * 0.55) / (width * 0.88))), 0);
    labelBlock = height * 0.08 + Math.min(2, lines) * labelSize * 1.15 + minDim * 0.04;
  }
  const freeH = height - labelBlock;
  const wanted = Math.round(minDim * (icon.scale ?? (showLabel ? 0.4 : 0.5)));
  // Never let the icon spill past the key or under the label.
  const iconSize = Math.max(8, Math.min(wanted, Math.round(freeH * 0.9), Math.round(width * 0.9)));
  const iconShift = labelEdge ? (label.position === 'bottom' ? -labelBlock / 2 : labelBlock / 2) : 0;
  return (
    <>
      {showIcon && (
        <span
          className="key-icon"
          style={{
            fontSize: iconSize,
            transform: iconShift ? `translateY(${iconShift}px)` : undefined,
          }}
        >
          {icon.kind === 'emoji' && icon.value}
          {icon.kind === 'lucide' && <LucideGlyph name={icon.value} size={iconSize} color={icon.color} />}
          {icon.kind === 'image' && (
            <img src={imageUrl(icon.value)} alt="" draggable={false} style={{ width: iconSize, height: iconSize }} />
          )}
        </span>
      )}
      {folder && <span className="folder-badge" />}
      {showLabel && (
        <span
          className={`key-label pos-${showIcon ? label.position : 'center'}`}
          style={{
            fontSize: labelSize,
            color: label.color,
            fontWeight: label.bold ? 700 : 600,
            textAlign: label.align ?? 'center',
          }}
        >
          {label.text}
        </span>
      )}
    </>
  );
}

/** Small live preview of a key, for the editor panel. */
export function KeyPreview({ k, size = 72, radius = 16 }: { k: Key; size?: number; radius?: number }) {
  const toggleOn = useDeck((st) => k.kind === 'toggle' && !!st.toggles[k.id]);
  const liveCfg = k.kind === 'dynamic' ? k.live : undefined;
  const liveSrc = useDeck((st) => (liveCfg && liveCfg.source !== 'clock' ? st.live[liveCfg.source] : undefined));
  const sliderSrc = useDeck((st) => {
    const src = k.kind === 'slider' ? sliderSource(k.slider) : null;
    return src ? st.live[src] : undefined;
  });
  const liveMap = liveCfg && liveSrc ? { [liveCfg.source]: liveSrc } : {};
  const now = useNow(ticks(liveCfg, liveMap), tickPhase(liveCfg, liveMap));
  const s = k.kind === 'slider' ? sliderStyle(k, sliderSrc, null) : keyStyleFor(k, toggleOn, liveSrc, now);
  const w = size * k.span.w;
  const h = size * k.span.h;
  const scale = Math.min(1, size / Math.max(w, h));
  const level = sliderSrc?.value ?? 0;
  return (
    <div
      className={`key key-preview${k.kind === 'slider' ? ` slider${sliderVertical(k.slider, k.span.w, k.span.h) ? ' vertical' : ''}` : ''}`}
      style={{
        width: w * scale,
        height: h * scale,
        borderRadius: Math.min(s.cornerRadius ?? radius, 22),
        backgroundColor: s.bg.color,
        backgroundImage: s.bg.imageId ? `url(${imageUrl(s.bg.imageId)})` : undefined,
      }}
    >
      {k.kind === 'slider' && (
        <SliderFill
          color={k.slider?.color}
          level={Math.min(100, Math.max(0, level))}
          vertical={sliderVertical(k.slider, k.span.w, k.span.h)}
          muted={sliderSrc?.state === true}
        />
      )}
      <KeyFace s={s} width={w * scale} height={h * scale} folder={k.kind === 'folder'} textScale={(size * scale) / 110} />
    </div>
  );
}
