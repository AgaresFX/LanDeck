import { tr } from '../i18n';
// Profile export / import as a single .padnook.json file with the images embedded (base64).
import { imageUrl, uploadBlob } from '../lib/images';
import { showToast, useDeck } from '../state/store';
import type { Profile } from '../types';
import { putNewProfile, withFreshIds } from './ops';

const FORMAT = 'padnook-profile';

interface ExportFile {
  format: typeof FORMAT;
  version: 1;
  exportedAt: string;
  profile: Profile;
  /** image id -> { mime type, base64 data } */
  images: Record<string, { type: string; data: string }>;
}

/** Every image reference in a profile, with a setter to replace it. */
function imageRefs(p: Profile): { id: string; set: (id: string) => void }[] {
  const refs: { id: string; set: (id: string) => void }[] = [];
  const add = (id: string | null | undefined, set: (id: string) => void) => id && refs.push({ id, set });
  add(p.background?.imageId, (id) => (p.background.imageId = id));
  for (const pg of p.pages) {
    add(pg.background?.imageId, (id) => (pg.background!.imageId = id));
    for (const k of pg.keys) {
      add(k.style.bg.imageId, (id) => (k.style.bg.imageId = id));
      if (k.style.icon.kind === 'image') add(k.style.icon.value, (id) => (k.style.icon.value = id));
      const on = k.toggle?.onStyle.icon;
      if (on?.kind === 'image') add(on.value, (id) => (on.value = id));
    }
  }
  return refs;
}

function blobToBase64(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result).split(',')[1] ?? '');
    r.onerror = () => reject(r.error);
    r.readAsDataURL(blob);
  });
}

function base64ToBlob(data: string, type: string): Blob {
  const bin = atob(data);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return new Blob([bytes], { type });
}

export async function exportProfile(profile: Profile) {
  try {
    const images: ExportFile['images'] = {};
    for (const { id } of imageRefs(structuredClone(profile))) {
      if (images[id]) continue;
      const res = await fetch(imageUrl(id));
      if (!res.ok) continue; // missing file: the reference is kept, the image is not
      const blob = await res.blob();
      images[id] = { type: blob.type || 'image/png', data: await blobToBase64(blob) };
    }
    const file: ExportFile = { format: FORMAT, version: 1, exportedAt: new Date().toISOString(), profile, images };
    const blob = new Blob([JSON.stringify(file, null, 2)], { type: 'application/json' });
    const name = `${profile.name.replace(/[\\/:*?"<>|]+/g, '_').trim() || 'profilo'}.padnook.json`;
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 10_000);
    showToast(tr('Profilo esportato: {0}', name));
  } catch (e) {
    showToast(tr('Esportazione non riuscita: {0}', (e as Error).message), 'error', 4000);
  }
}

export async function importProfile(file: File) {
  try {
    const data = JSON.parse(await file.text()) as Partial<ExportFile>;
    const p = data.profile;
    if (data.format !== FORMAT || !p || !Array.isArray(p.pages) || p.pages.length === 0)
      throw new Error(tr('il file non è un profilo PadNook'));

    // Upload the embedded images; the server may assign different ids.
    const idMap = new Map<string, string>();
    for (const [oldId, img] of Object.entries(data.images ?? {})) {
      idMap.set(oldId, await uploadBlob(base64ToBlob(img.data, img.type)));
    }
    for (const ref of imageRefs(p)) {
      const mapped = idMap.get(ref.id);
      if (mapped) ref.set(mapped);
    }

    const taken = new Set(useDeck.getState().profiles.map((x) => x.name));
    let name = p.name || tr('Profilo importato');
    if (taken.has(name)) name = tr('{0} (importato)', name);
    putNewProfile(withFreshIds(p, name));
    showToast(tr('Profilo «{0}» importato', name));
  } catch (e) {
    showToast(tr('Importazione non riuscita: {0}', (e as Error).message), 'error', 5000);
  }
}
