import { useEffect, useLayoutEffect, useState, type RefObject } from 'react';
import { useDeck } from '../state/store';
import type { Orientation } from '../types';

const calcOrientation = (): Orientation => (window.innerWidth > window.innerHeight ? 'landscape' : 'portrait');

/** Orientation of the whole screen (not of the deck area, which shrinks in edit mode). */
export function useOrientation(): Orientation {
  const [o, setO] = useState(calcOrientation);
  useEffect(() => {
    const f = () => setO(calcOrientation());
    window.addEventListener('resize', f);
    window.addEventListener('orientationchange', f);
    return () => {
      window.removeEventListener('resize', f);
      window.removeEventListener('orientationchange', f);
    };
  }, []);
  return o;
}

/** Orientation whose grid is shown: the screen's, or the one forced by the editor preview. */
export function useDeckOrientation(): Orientation {
  const actual = useOrientation();
  const preview = useDeck((s) => (s.editing ? s.previewOrientation : null));
  return preview ?? actual;
}

export function useElementSize(ref: RefObject<HTMLElement | null>) {
  const [size, setSize] = useState({ w: 0, h: 0 });
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    // Measure synchronously so the first paint already has the grid.
    setSize({ w: el.clientWidth, h: el.clientHeight });
    const ro = new ResizeObserver((entries) => {
      const r = entries[0].contentRect;
      setSize((s) => (s.w === r.width && s.h === r.height ? s : { w: r.width, h: r.height }));
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [ref]);
  return size;
}
