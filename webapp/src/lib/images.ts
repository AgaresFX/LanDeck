import { tr } from '../i18n';
import { uploadAuth } from '../net/client';

export const imageUrl = (id: string) => `/images/${id}`;

function loadImage(file: Blob): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      URL.revokeObjectURL(url);
      resolve(img);
    };
    img.onerror = () => {
      URL.revokeObjectURL(url);
      reject(new Error(tr('Immagine non leggibile')));
    };
    img.src = url;
  });
}

/**
 * Downscales the image before upload (photos from the iPhone can be 10+ MB; HEIC is converted).
 * GIFs are kept as they are to preserve animation.
 */
export async function prepareImage(file: File, maxSize: number): Promise<Blob> {
  if (file.type === 'image/gif') return file;
  const img = await loadImage(file);
  const scale = Math.min(1, maxSize / Math.max(img.naturalWidth, img.naturalHeight));
  const keepsAlpha = file.type === 'image/png' || file.type === 'image/webp';
  if (scale === 1 && file.size < 400_000 && (keepsAlpha || file.type === 'image/jpeg')) return file;

  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(img.naturalWidth * scale));
  canvas.height = Math.max(1, Math.round(img.naturalHeight * scale));
  const ctx = canvas.getContext('2d')!;
  ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
  return new Promise((resolve, reject) =>
    canvas.toBlob(
      (b) => (b ? resolve(b) : reject(new Error(tr('Conversione non riuscita')))),
      keepsAlpha ? 'image/png' : 'image/jpeg',
      0.88,
    ),
  );
}

/** Uploads an image to the PC (after downscaling) and returns its id. */
export async function uploadImage(file: File, maxSize: number): Promise<string> {
  return uploadBlob(await prepareImage(file, maxSize));
}

function authHeaders(): Record<string, string> {
  const { token, editToken, desktopKey } = uploadAuth();
  return {
    Authorization: `Bearer ${token ?? ''}`,
    'X-Edit-Token': editToken ?? '',
    ...(desktopKey ? { 'X-Desktop-Key': desktopKey } : {}),
  };
}

/** Uploads image bytes as they are and returns the id assigned by the server. */
export async function uploadBlob(blob: Blob): Promise<string> {
  const res = await fetch('/api/images', {
    method: 'POST',
    headers: { ...authHeaders(), 'Content-Type': blob.type || 'application/octet-stream' },
    body: blob,
  });
  if (!res.ok) throw new Error((await res.text()) || tr('Errore {0}', res.status));
  return (await res.json()).id as string;
}

// ---- Online search ------------------------------------------------------------------------

export type SearchKind = 'icons' | 'photos';
export interface SearchHit {
  thumb: string;
  url: string;
  title: string;
  credit: string | null;
}

/** Searches icons/logos (Iconify) or photos (Wikimedia Commons + Openverse) through the PC. */
export async function searchImages(q: string, kind: SearchKind): Promise<SearchHit[]> {
  const res = await fetch(`/api/imagesearch?kind=${kind}&q=${encodeURIComponent(q)}`, { headers: authHeaders() });
  if (!res.ok) throw new Error((await res.text()) || tr('Errore {0}', res.status));
  return res.json();
}

/** Renders an SVG to a square-bounded PNG (keeps transparency). */
async function rasterizeSvg(svg: Blob, maxSize: number): Promise<Blob> {
  const img = await loadImage(svg);
  const w = img.naturalWidth || maxSize;
  const h = img.naturalHeight || maxSize;
  const scale = maxSize / Math.max(w, h);
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(w * scale));
  canvas.height = Math.max(1, Math.round(h * scale));
  canvas.getContext('2d')!.drawImage(img, 0, 0, canvas.width, canvas.height);
  return new Promise((resolve, reject) =>
    canvas.toBlob((b) => (b ? resolve(b) : reject(new Error(tr('Conversione non riuscita')))), 'image/png'),
  );
}

/** Downloads a search result through the PC, resizes it and saves it; returns the image id. */
export async function importOnlineImage(url: string, maxSize: number): Promise<string> {
  // Icons arrive as data: URLs (already downloaded by the PC); photos are fetched through the PC.
  const res = url.startsWith('data:')
    ? await fetch(url)
    : await fetch(`/api/imagefetch?url=${encodeURIComponent(url)}`, { headers: authHeaders() });
  if (!res.ok) throw new Error((await res.text()) || tr('Errore {0}', res.status));
  const blob = await res.blob();
  if (blob.type.includes('svg')) return uploadBlob(await rasterizeSvg(blob, maxSize));
  return uploadBlob(await prepareImage(new File([blob], 'web', { type: blob.type }), maxSize));
}

// ---- Program icons ------------------------------------------------------------------------

/** Image ids that came from a program's icon: they are replaced when the program changes. */
export const programIconIds = new Set<string>();

/** Saves the real icon of a program/shortcut on the PC and returns its image id (null if there is none). */
export async function fetchProgramIcon(path: string): Promise<string | null> {
  const res = await fetch(`/api/programicon?path=${encodeURIComponent(path)}`, { headers: authHeaders() });
  if (!res.ok) return null;
  const id = (await res.json()).id as string;
  programIconIds.add(id);
  return id;
}

/** Targets that have their own icon (programs and shortcuts, not URLs, folders or documents). */
export const hasProgramIcon = (target: string) => /\.(exe|lnk|appref-ms)"?\s*$/i.test(target.trim()) && !target.includes('://');
