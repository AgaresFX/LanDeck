import { tr } from '../i18n';
import { useEffect, useMemo, useRef, useState } from 'react';
import { imageUrl, importOnlineImage, searchImages, uploadImage, type SearchHit, type SearchKind } from '../lib/images';
import { useLucide } from '../lib/lucide';
import { showToast } from '../state/store';

// ---- Image upload -------------------------------------------------------------------------

export function ImagePicker({
  value,
  onChange,
  maxSize,
  label = tr('Scegli immagine'),
  searchHint = '',
}: {
  value: string | null | undefined;
  onChange: (id: string | null) => void;
  maxSize: number;
  label?: string;
  /** Pre-filled text for the online search (e.g. the key label). */
  searchHint?: string;
}) {
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [searching, setSearching] = useState(false);

  const pick = async (file: File | undefined) => {
    if (!file) return;
    setBusy(true);
    try {
      onChange(await uploadImage(file, maxSize));
    } catch (e) {
      showToast(tr('Caricamento non riuscito: {0}', (e as Error).message), 'error', 4000);
    } finally {
      setBusy(false);
      if (input.current) input.current.value = '';
    }
  };

  return (
    <div className="image-picker">
      {value ? <img className="thumb" src={imageUrl(value)} alt="" /> : <div className="thumb empty">—</div>}
      <button type="button" className="btn" disabled={busy} onClick={() => input.current?.click()}>
        {busy ? tr('Caricamento…') : value ? tr('Cambia') : label}
      </button>
      <button type="button" className="btn" disabled={busy} onClick={() => setSearching(true)}>
        {tr('Cerca online')}
      </button>
      {value && (
        <button type="button" className="btn ghost" onClick={() => onChange(null)}>
          {tr('Rimuovi')}
        </button>
      )}
      <input ref={input} type="file" accept="image/*" hidden onChange={(e) => pick(e.target.files?.[0])} />
      {searching && (
        <ImageSearchDialog
          initial={searchHint}
          maxSize={maxSize}
          onPick={(id) => {
            onChange(id);
            setSearching(false);
          }}
          onClose={() => setSearching(false)}
        />
      )}
    </div>
  );
}

// ---- Online image search ------------------------------------------------------------------

function ImageSearchDialog({
  initial,
  maxSize,
  onPick,
  onClose,
}: {
  initial: string;
  maxSize: number;
  onPick: (id: string) => void;
  onClose: () => void;
}) {
  const [q, setQ] = useState(initial.trim());
  const [kind, setKind] = useState<SearchKind>('icons');
  const [hits, setHits] = useState<SearchHit[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState<string | null>(null);
  const seq = useRef(0);

  const run = async (term: string, k: SearchKind) => {
    if (!term.trim()) return;
    const n = ++seq.current;
    setLoading(true);
    setError('');
    try {
      const r = await searchImages(term.trim(), k);
      if (n === seq.current) setHits(r);
    } catch (e) {
      if (n === seq.current) {
        setHits(null);
        setError((e as Error).message);
      }
    } finally {
      if (n === seq.current) setLoading(false);
    }
  };

  useEffect(() => {
    if (initial.trim()) run(initial, 'icons');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const choose = async (h: SearchHit) => {
    setSaving(h.url);
    try {
      // The original host may refuse or rate-limit downloads: the thumbnail is a good fallback.
      onPick(await importOnlineImage(h.url, maxSize).catch(() => importOnlineImage(h.thumb, maxSize)));
    } catch (e) {
      showToast(tr('Download non riuscito: {0}', (e as Error).message), 'error', 4000);
    } finally {
      setSaving(null);
    }
  };

  const switchKind = (k: SearchKind) => {
    setKind(k);
    setHits(null);
    run(q, k);
  };

  return (
    <div className="overlay center" onPointerDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog icon-dialog">
        <div className="panel-head">
          <div className="dialog-title">{tr('Cerca immagini')}</div>
          <button className="btn ghost" onClick={onClose}>
            {tr('Chiudi')}
          </button>
        </div>
        <div className="segmented">
          <button type="button" className={kind === 'icons' ? 'on' : ''} onClick={() => switchKind('icons')}>
            {tr('Loghi e icone')}
          </button>
          <button type="button" className={kind === 'photos' ? 'on' : ''} onClick={() => switchKind('photos')}>
            {tr('Foto')}
          </button>
        </div>
        <form
          className="search-row"
          onSubmit={(e) => {
            e.preventDefault();
            run(q, kind);
          }}
        >
          <input
            className="input"
            placeholder={kind === 'icons' ? tr('Es. discord, spotify, play, mic…') : tr('Es. pizza, montagna, gatto…')}
            value={q}
            onChange={(e) => setQ(e.target.value)}
            autoFocus
          />
          <button type="submit" className="btn" disabled={loading || !q.trim()}>
            {tr('Cerca')}
          </button>
        </form>
        {loading ? (
          <p className="muted">{tr('Ricerca in corso…')}</p>
        ) : error ? (
          <p className="muted">{error}</p>
        ) : hits ? (
          <>
            <div className={kind === 'icons' ? 'icon-grid' : 'photo-grid'}>
              {hits.map((h) => (
                <button
                  key={h.url}
                  type="button"
                  className={kind === 'icons' ? 'icon-btn' : 'photo-btn'}
                  title={h.credit ? `${h.title} — ${h.credit}` : h.title}
                  disabled={saving !== null}
                  onClick={() => choose(h)}
                >
                  <img src={h.thumb} alt="" loading="lazy" style={{ opacity: saving && saving !== h.url ? 0.4 : 1 }} />
                </button>
              ))}
            </div>
            <p className="muted small">
              {saving
                ? tr('Scaricamento…')
                : hits.length === 0
                  ? kind === 'icons'
                    ? tr('Nessun risultato. Le icone hanno nomi inglesi: prova per esempio «music» invece di «musica».')
                    : tr('Nessun risultato: prova con altre parole.')
                  : kind === 'icons'
                    ? tr("{0} risultati da Iconify. Tocca un'immagine per usarla.", hits.length)
                    : tr('{0} foto libere da Wikimedia Commons e Openverse. Tocca una foto per usarla.', hits.length)}
            </p>
          </>
        ) : (
          <p className="muted small">{tr('Scrivi cosa cercare e premi Invio.')}</p>
        )}
      </div>
    </div>
  );
}

// ---- Lucide icon picker -------------------------------------------------------------------

const MAX_RESULTS = 240;

// A few Italian keywords for common searches (icon names are in English).
const SYNONYMS: Record<string, string> = {
  cartella: 'folder',
  musica: 'music',
  volume: 'volume',
  microfono: 'mic',
  camera: 'camera',
  telecamera: 'video',
  casa: 'house',
  impostazioni: 'settings',
  luce: 'lightbulb',
  lampadina: 'lightbulb',
  gioco: 'gamepad',
  freccia: 'arrow',
  indietro: 'arrow-left',
  avanti: 'arrow-right',
  stella: 'star',
  cuore: 'heart',
  chat: 'message',
  messaggio: 'message',
  posta: 'mail',
  telefono: 'phone',
  schermo: 'monitor',
  computer: 'computer',
  lucchetto: 'lock',
  cerca: 'search',
  orologio: 'clock',
  calendario: 'calendar',
  file: 'file',
  pausa: 'pause',
  riproduci: 'play',
  stop: 'square',
  registra: 'circle',
  web: 'globe',
  internet: 'globe',
  cestino: 'trash',
  utente: 'user',
  spegni: 'power',
};

const kebab = (s: string) => s.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();

export function IconPicker({ onPick, onClose }: { onPick: (name: string) => void; onClose: () => void }) {
  const icons = useLucide();
  const [q, setQ] = useState('');

  const names = useMemo(() => {
    if (!icons) return [];
    // Each icon is exported under several aliases (Foo, FooIcon, LucideFoo): keep one.
    return Object.keys(icons).filter((n) => !n.endsWith('Icon') && !n.startsWith('Lucide'));
  }, [icons]);

  const results = useMemo(() => {
    const term = q.trim().toLowerCase();
    if (!term) return names.slice(0, MAX_RESULTS);
    const t = SYNONYMS[term] ?? term;
    return names.filter((n) => kebab(n).includes(t) || n.toLowerCase().includes(t)).slice(0, MAX_RESULTS);
  }, [names, q]);

  return (
    <div className="overlay center" onPointerDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog icon-dialog">
        <div className="panel-head">
          <div className="dialog-title">{tr('Icone')}</div>
          <button className="btn ghost" onClick={onClose}>
            {tr('Chiudi')}
          </button>
        </div>
        <input
          className="input"
          placeholder={tr('Cerca (es. play, mic, cartella, luce)…')}
          value={q}
          onChange={(e) => setQ(e.target.value)}
          autoFocus
        />
        {!icons ? (
          <p className="muted">{tr('Caricamento icone…')}</p>
        ) : (
          <>
            <div className="icon-grid">
              {results.map((n) => {
                const Icon = icons[n];
                return (
                  <button key={n} type="button" className="icon-btn" title={kebab(n)} onClick={() => onPick(n)}>
                    <Icon size={24} color="#eceef3" />
                  </button>
                );
              })}
            </div>
            <p className="muted small">
              {results.length === 0
                ? tr('Nessuna icona trovata. I nomi sono in inglese: prova per esempio «home», «light», «game».')
                : results.length === MAX_RESULTS
                  ? tr('Mostrate le prime {0}: affina la ricerca.', MAX_RESULTS)
                  : tr('{0} icone', results.length)}
            </p>
          </>
        )}
      </div>
    </div>
  );
}
