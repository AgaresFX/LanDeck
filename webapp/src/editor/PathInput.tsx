import { tr } from '../i18n';
import { useState, type DragEvent } from 'react';
import { browse, droppedPaths, isDesktop, type BrowseKind } from '../lib/desktop';
import { Icon } from '../ui/Icon';

/**
 * Text field for a path on the PC. In the desktop editor it gets a «Sfoglia…» button (Windows dialog) and
 * accepts files dropped from Explorer.
 */
export function PathInput({
  value,
  onChange,
  kind,
  placeholder,
  alsoFolder,
}: {
  value: string;
  onChange: (v: string) => void;
  kind: BrowseKind;
  placeholder?: string;
  /** Adds a second button to pick a folder (e.g. "open a program or a folder"). */
  alsoFolder?: boolean;
}) {
  const [over, setOver] = useState(false);
  if (!isDesktop) {
    return <input className="input mono" placeholder={placeholder} value={value} onChange={(e) => onChange(e.target.value)} />;
  }

  const onDrop = async (e: DragEvent) => {
    e.preventDefault();
    setOver(false);
    const [path] = await droppedPaths(e.dataTransfer.files);
    if (path) onChange(path);
  };
  const hasFiles = (e: DragEvent) => Array.from(e.dataTransfer.types).includes('Files');

  return (
    <div
      className={`inline-row path-input${over ? ' drop-over' : ''}`}
      onDragOver={(e) => {
        if (!hasFiles(e)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'copy';
        setOver(true);
      }}
      onDragLeave={() => setOver(false)}
      onDrop={(e) => void onDrop(e)}
    >
      <input
        className="input mono"
        placeholder={
          placeholder ?? (kind === 'folder' ? tr('Trascina qui una cartella o usa Sfoglia') : tr('Trascina qui un file o usa Sfoglia'))
        }
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
      <button
        type="button"
        className="btn"
        onClick={async () => {
          const path = await browse(kind, value);
          if (path) onChange(path);
        }}
      >
        {tr('Sfoglia…')}
      </button>
      {alsoFolder && (
        <button
          type="button"
          className="btn"
          title={tr('Scegli una cartella')}
          onClick={async () => {
            const path = await browse('folder', value);
            if (path) onChange(path);
          }}
        >
          <Icon name="folder-open" />
        </button>
      )}
    </div>
  );
}
