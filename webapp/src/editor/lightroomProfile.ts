import { tr } from '../i18n';
// Ready-made "Lightroom Classic" profile: Develop sliders and Library rating/flags, shown automatically
// while Lightroom is in front on the PC.
import { DEFAULT_LAYOUT } from '../lib/layout';
import { newId } from '../lib/platform';
import { defaultKeyStyle } from '../deck/DeckView';
import type { Action, Key, LightroomAction, LrParam, Page, Profile } from '../types';
import { LR_PARAMS } from './LightroomEditor';

const LR_BLUE = '#1f4e79';
const DARK = '#2b2f3a';

function key(label: string, icon: string, onPress: Action | null, opts: { bg?: string; long?: Action; emoji?: boolean } = {}): Key {
  const style = defaultKeyStyle();
  style.label = { ...style.label, text: label, size: 12 };
  style.icon = opts.emoji ? { kind: 'emoji', value: icon } : { kind: 'lucide', value: icon, color: '#ffffff' };
  style.bg = { color: opts.bg ?? DARK };
  return { id: newId('k'), span: { w: 1, h: 1 }, pos: {}, style, kind: 'action', onPress, onLongPress: opts.long ?? null };
}

const lr = (a: Omit<LightroomAction, 'type'>): LightroomAction => ({ type: 'lightroom', ...a });

function slider(param: LrParam, label: string): Key {
  const style = defaultKeyStyle();
  style.label = { ...style.label, text: `${label}\n{text}`, size: 12, position: 'center', bold: true };
  style.icon = { kind: 'none', value: '' };
  style.bg = { color: DARK };
  return {
    id: newId('k'),
    span: { w: 1, h: 1 },
    pos: {},
    style,
    kind: 'slider',
    onPress: null,
    onLongPress: null,
    slider: { target: 'lightroom', param, orientation: 'horizontal', color: '#3a7bd5' },
  };
}

function dynamic(source: string, text: string, rules: NonNullable<Key['live']>['rules'], onPress: Action | null = null): Key {
  const k = key(text, '', onPress);
  k.kind = 'dynamic';
  k.style.icon = { kind: 'none', value: '' };
  k.style.label = { ...k.style.label, position: 'center', size: 14, bold: true };
  k.live = { source, rules };
  return k;
}

export function lightroomProfile(): Profile {
  const devId = newId('pg');
  const libId = newId('pg');
  const grid = { portrait: { rows: 6, cols: 4 }, landscape: { rows: 4, cols: 6 } };

  const develop: Page = {
    id: devId,
    name: tr('Sviluppo'),
    kind: 'page',
    layout: grid,
    keys: [
      ...LR_PARAMS.map(([p, label]) => slider(p, label)),
      key(tr('Annulla@@undo'), 'Undo2', lr({ command: 'undo' })),
      key(tr('Ripeti'), 'Redo2', lr({ command: 'redo' })),
      key(tr('Prima/dopo'), 'Columns2', lr({ command: 'beforeAfter' })),
      key(tr('Copia'), 'Copy', lr({ command: 'copy' })),
      key(tr('Incolla'), 'ClipboardPaste', lr({ command: 'paste' }), { long: lr({ command: 'sync' }) }),
      key('Auto', 'WandSparkles', lr({ command: 'autoTone' }), { long: lr({ command: 'resetAll' }) }),
      key(tr('Foto ←'), 'ChevronLeft', lr({ command: 'prev' })),
      key(tr('Foto →'), 'ChevronRight', lr({ command: 'next' })),
      key(tr('Libreria'), 'Library', { type: 'navigate', target: 'page', id: libId }, { bg: LR_BLUE }),
    ],
  };

  const labels: [NonNullable<LightroomAction['label']>, string, string][] = [
    ['red', tr('Rosso'), '#b3261e'],
    ['yellow', tr('Giallo'), '#b38f00'],
    ['green', tr('Verde'), '#1f7a4a'],
    ['blue', tr('Blu'), '#1f4e9e'],
    ['purple', tr('Viola'), '#6b3fa0'],
    ['none', tr('Nessuna'), DARK],
  ];
  const library: Page = {
    id: libId,
    name: tr('Libreria'),
    kind: 'page',
    layout: grid,
    keys: [
      dynamic('lr.rating', '{text}', []),
      dynamic('lr.flag', '{text}', [
        { when: 'true', style: { bgColor: '#1f7a4a' } },
        { when: 'lt', value: '0', style: { bgColor: '#b3261e' } },
      ]),
      ...[0, 1, 2, 3, 4, 5].map((n) =>
        key(n === 0 ? tr('0 stelle') : '★'.repeat(n), n === 0 ? 'StarOff' : 'Star', lr({ command: 'rating', rating: n })),
      ),
      key(tr('Scelta'), 'Flag', lr({ command: 'flag', flag: 'pick' }), { bg: '#1f7a4a' }),
      key(tr('Rifiuta'), 'FlagOff', lr({ command: 'flag', flag: 'reject' }), { bg: '#b3261e' }),
      key(tr('Togli'), 'Eraser', lr({ command: 'flag', flag: 'none' })),
      ...labels.map(([l, name, bg]) => key(name, 'Tag', lr({ command: 'label', label: l }), { bg })),
      key(tr('Foto ←'), 'ChevronLeft', lr({ command: 'prev' })),
      key(tr('Foto →'), 'ChevronRight', lr({ command: 'next' })),
      key(tr('Sviluppo'), 'SlidersHorizontal', { type: 'navigate', target: 'page', id: devId }, { bg: LR_BLUE }),
    ],
  };
  // Opening a page also switches Lightroom to the matching module (long press on the navigation keys).
  develop.keys[develop.keys.length - 1].onLongPress = lr({ command: 'module', module: 'library' });
  library.keys[library.keys.length - 1].onLongPress = lr({ command: 'module', module: 'develop' });

  return {
    id: newId('p'),
    name: 'Lightroom Classic',
    auto: { apps: ['lightroom'] },
    layout: structuredClone(DEFAULT_LAYOUT),
    background: { color: '#0e0f13' },
    pages: [develop, library],
  };
}
