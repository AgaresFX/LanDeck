import { useEffect, useState, type ComponentType } from 'react';

// The whole Lucide set is loaded lazily as a separate chunk, only when a key uses it
// (or the icon picker opens), so the deck itself stays light.

type IconComponent = ComponentType<{ size?: number | string; color?: string; strokeWidth?: number }>;
export type IconMap = Record<string, IconComponent>;

let cache: IconMap | null = null;
let promise: Promise<IconMap> | null = null;

export function loadLucide(): Promise<IconMap> {
  promise ??= import('lucide-react').then((m) => (cache = m.icons as unknown as IconMap));
  return promise;
}

export function useLucide(): IconMap | null {
  const [icons, setIcons] = useState<IconMap | null>(cache);
  useEffect(() => {
    if (!icons) loadLucide().then(setIcons);
  }, [icons]);
  return icons;
}

export function LucideGlyph({ name, size, color }: { name: string; size: number; color?: string }) {
  const icons = useLucide();
  const Icon = icons?.[name];
  if (!Icon) return <span style={{ width: size, height: size, display: 'inline-block' }} />;
  return <Icon size={size} color={color ?? '#ffffff'} strokeWidth={2} />;
}
