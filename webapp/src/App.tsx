import { DeckView } from './deck/DeckView';
import { EditorBar, EditorPanel } from './editor/Editor';
import { useDeckOrientation, useOrientation } from './lib/hooks';
import { useDeck } from './state/store';
import { InstallHint, MenuSheet, PairScreen, PinDialog, Splash, StatusDot, Toast } from './ui/Overlays';

export function App() {
  const auth = useDeck((s) => s.auth);
  const editing = useDeck((s) => s.editing);
  const actual = useOrientation();
  const orientation = useDeckOrientation();

  return (
    <div className={`app${editing ? ' editing' : ''}`}>
      {auth === 'unknown' && <Splash />}
      {auth === 'pairing' && <PairScreen />}
      {auth === 'ok' && (
        <>
          {editing && <EditorBar />}
          <div className="main">
            <DeckView orientation={orientation} preview={orientation !== actual} />
            {editing && <EditorPanel />}
          </div>
          {/* The layout is edited only on the PC (tray icon → «Apri editor»): devices are just the deck. */}
          {!editing && <StatusDot />}
          <MenuSheet />
          <PinDialog />
          <InstallHint />
        </>
      )}
      <Toast />
    </div>
  );
}
