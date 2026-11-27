import { tr } from '../i18n';
// Key names follow KeyboardEvent.code; the server maps them to scan codes.

export const MODIFIERS = ['Ctrl', 'Alt', 'Shift', 'Win'] as const;

const range = (n: number, f: (i: number) => [string, string]) => Array.from({ length: n }, (_, i) => f(i));

export const KEY_GROUPS: { label: string; keys: [string, string][] }[] = [
  { label: tr('Lettere'), keys: range(26, (i) => [`Key${String.fromCharCode(65 + i)}`, String.fromCharCode(65 + i)]) },
  { label: tr('Numeri'), keys: range(10, (i) => [`Digit${i}`, String(i)]) },
  { label: tr('Tasti funzione'), keys: range(24, (i) => [`F${i + 1}`, `F${i + 1}`]) },
  {
    label: tr('Speciali'),
    keys: [
      ['Enter', tr('Invio')],
      ['Escape', 'Esc'],
      ['Tab', 'Tab'],
      ['Space', tr('Spazio')],
      ['Backspace', 'Backspace'],
      ['Delete', tr('Canc')],
      ['Insert', 'Ins'],
      ['PrintScreen', tr('Stamp')],
      ['Pause', tr('Pausa')],
      ['CapsLock', tr('Bloc Maiusc')],
      ['NumLock', tr('Bloc Num')],
      ['ScrollLock', tr('Bloc Scorr')],
      ['ContextMenu', tr('Menu contestuale')],
    ],
  },
  {
    label: tr('Navigazione'),
    keys: [
      ['ArrowUp', tr('↑ Su')],
      ['ArrowDown', tr('↓ Giù')],
      ['ArrowLeft', tr('← Sinistra')],
      ['ArrowRight', tr('→ Destra')],
      ['Home', 'Home'],
      ['End', tr('Fine')],
      ['PageUp', tr('Pag ↑')],
      ['PageDown', tr('Pag ↓')],
    ],
  },
  {
    label: tr('Punteggiatura (posizione fisica)'),
    keys: [
      ['Minus', tr("' ? (Minus)")],
      ['Equal', tr('ì ^ (Equal)')],
      ['BracketLeft', tr('è é (BracketLeft)')],
      ['BracketRight', tr('+ * (BracketRight)')],
      ['Semicolon', tr('ò ç (Semicolon)')],
      ['Quote', tr('à ° (Quote)')],
      ['Backquote', tr('\\ | (Backquote)')],
      ['Backslash', tr('ù § (Backslash)')],
      ['Comma', ', ;'],
      ['Period', '. :'],
      ['Slash', tr('- _ (Slash)')],
      ['IntlBackslash', '< >'],
    ],
  },
  {
    label: tr('Tastierino numerico'),
    keys: [
      ...range(10, (i): [string, string] => [`Numpad${i}`, `Num ${i}`]),
      ['NumpadAdd', 'Num +'],
      ['NumpadSubtract', 'Num -'],
      ['NumpadMultiply', 'Num *'],
      ['NumpadDivide', 'Num /'],
      ['NumpadDecimal', 'Num .'],
      ['NumpadEnter', tr('Num Invio')],
    ],
  },
  {
    label: tr('Multimediali'),
    keys: [
      ['MediaPlayPause', 'Play/Pausa'],
      ['MediaTrackNext', tr('Traccia successiva')],
      ['MediaTrackPrevious', tr('Traccia precedente')],
      ['MediaStop', 'Stop'],
      ['AudioVolumeUp', 'Volume +'],
      ['AudioVolumeDown', 'Volume -'],
      ['AudioVolumeMute', tr('Muto')],
    ],
  },
];

const LABELS = new Map<string, string>(KEY_GROUPS.flatMap((g) => g.keys));

export function keyLabel(code: string): string {
  return LABELS.get(code)?.replace(/ \(.*\)$/, '') ?? code;
}

export const KNOWN_KEYS = new Set(LABELS.keys());
