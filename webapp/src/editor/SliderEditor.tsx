import { lang, tr } from '../i18n';
import type { Key, SliderConfig } from '../types';
import { sliderHasMute, sliderVertical } from '../lib/live';
import { refreshPcInfo } from '../net/client';
import { ColorField, Section, Segmented, Toggle } from './controls';
import { AppField, usePcInfo } from './ExtraEditors';
import { LrParamField, lrLabel } from './LightroomEditor';

type Edit = (fn: (k: Key) => void) => void;

const TARGETS: [SliderConfig['target'], string][] = [
  ['vol', 'Volume'],
  ['mic', tr('Microfono')],
  ['app', 'App'],
  ['brightness', tr('Luminosità')],
  ['lightroom', 'Lightroom'],
];

/** Default label and icon for each target: changing the target updates them unless the user customized them. */
export function sliderDefaults(cfg: SliderConfig): { label: string; icon: string } {
  switch (cfg.target) {
    case 'mic':
      return { label: tr('Microfono\n{text}'), icon: 'Mic' };
    case 'app':
      return { label: `${cfg.app?.trim() || 'App'}\n{text}`, icon: 'AppWindow' };
    case 'brightness':
      return { label: tr('Luminosità\n{text}'), icon: 'Sun' };
    case 'lightroom':
      return { label: `${lrLabel(cfg.param ?? 'Exposure')}\n{text}`, icon: 'SlidersHorizontal' };
    default:
      return { label: 'Volume\n{text}', icon: 'Volume2' };
  }
}

function applyDefaults(k: Key, prev: SliderConfig, next: SliderConfig) {
  const before = sliderDefaults(prev);
  const after = sliderDefaults(next);
  if (k.style.label.text === before.label) k.style.label.text = after.label;
  if (k.style.icon.kind === 'lucide' && k.style.icon.value === before.icon) k.style.icon = { ...k.style.icon, value: after.icon };
}

const ORIENTATIONS: [NonNullable<SliderConfig['orientation']>, string][] = [
  ['auto', tr('Automatico')],
  ['vertical', tr('Verticale@@dir')],
  ['horizontal', tr('Orizzontale@@dir')],
];

/** Output (volume) or input (microphone) device: the Windows default, or a specific one. */
function DeviceField({ cfg, set }: { cfg: SliderConfig; set: (patch: Partial<SliderConfig>) => void }) {
  const info = usePcInfo();
  const mic = cfg.target === 'mic';
  const devices = (mic ? info?.inputs : info?.outputs) ?? [];
  const def = devices.find((d) => d.isDefault);
  const missing = cfg.device && !devices.some((d) => d.id === cfg.device);
  return (
    <label className="field">
      <span>{mic ? tr('Microfono') : tr('Dispositivo di uscita')}</span>
      <div className="inline-row">
        <select
          className="input"
          value={cfg.device ?? ''}
          onChange={(e) => {
            const d = devices.find((x) => x.id === e.target.value);
            set({ device: d?.id, deviceName: d?.name });
          }}
        >
          <option value="">{tr('Predefinito di Windows{0}', def ? tr(' (ora: {0})', def.name) : '')}</option>
          {devices.map((d) => (
            <option key={d.id} value={d.id}>
              {d.name}
            </option>
          ))}
          {missing && <option value={cfg.device}>{tr('{0} (non collegato)', cfg.deviceName ?? tr('Dispositivo'))}</option>}
        </select>
        <button type="button" className="btn" onClick={() => void refreshPcInfo()} title={tr("Aggiorna l'elenco dei dispositivi")}>
          ↻
        </button>
      </div>
      <span className="muted small">
        {cfg.device ? tr('Regola sempre questo dispositivo.') : tr('Segue il dispositivo predefinito, anche se lo cambi in Windows.')}
      </span>
    </label>
  );
}

export function SliderSections({ k, cfg, edit }: { k: Key; cfg: SliderConfig; edit: Edit }) {
  const set = (patch: Partial<SliderConfig>) =>
    edit((x) => {
      const prev = x.slider ?? cfg;
      const next = { ...prev, ...patch };
      // Output and input devices are different lists: switching target drops the chosen device.
      if (patch.target && patch.target !== prev.target) {
        delete next.device;
        delete next.deviceName;
      }
      applyDefaults(x, prev, next);
      x.slider = next;
    });
  const vertical = sliderVertical(cfg, k.span.w, k.span.h);

  return (
    <>
      <Section title={tr('Cosa regola')}>
        <Segmented value={cfg.target} options={TARGETS} onChange={(target) => set({ target })} />
        {(cfg.target === 'vol' || cfg.target === 'mic') && <DeviceField cfg={cfg} set={set} />}
        {cfg.target === 'app' && (
          <AppField
            label={tr('App (deve stare riproducendo audio)')}
            value={cfg.app ?? ''}
            source="sessions"
            onChange={(app) => set({ app })}
          />
        )}
        {cfg.target === 'lightroom' && (
          <>
            <LrParamField value={cfg.param} onChange={(param) => set({ param })} />
            <p className="muted small hint">
              {tr(
                'Trascina per regolare il cursore di Lightroom (Sviluppo); un tocco lo riporta al valore predefinito. Serve il plugin (pannello del profilo → Lightroom Classic).',
              )}
            </p>
          </>
        )}
        {cfg.target === 'brightness' && (
          <p className="muted small hint">
            {tr('Funziona sullo schermo del portatile e sui monitor esterni con «DDC/CI» attivo nel menu del monitor.')}
          </p>
        )}
      </Section>

      <Section title={tr('Direzione')}>
        <Segmented value={cfg.orientation ?? 'auto'} options={ORIENTATIONS} onChange={(orientation) => set({ orientation })} />
        <p className="muted small">
          {tr(
            '{0}{1} Per un tasto più lungo usa la scheda Dimensione.',
            (cfg.orientation ?? 'auto') === 'auto'
              ? tr(
                  'Ora è {0}: segue la forma del tasto (verticale se è più alto che largo).',
                  lang === 'en' ? (vertical ? 'vertical' : 'horizontal') : vertical ? 'verticale' : 'orizzontale',
                )
              : tr('Trascina il dito {0} sul tasto.', vertical ? tr('in su o in giù') : tr('a destra o a sinistra')),
            ' ',
          )}
        </p>
      </Section>

      <Section title={tr('Uso')}>
        {sliderHasMute(cfg) && (
          <Toggle
            label={tr('Un tocco senza trascinare attiva/disattiva il muto')}
            checked={cfg.tapMute !== false}
            onChange={(v) => set({ tapMute: v })}
          />
        )}
      </Section>

      <Section title={tr('Colore della barra')}>
        <ColorField value={cfg.color ?? '#3b82f6'} onChange={(color) => set({ color })} />
      </Section>
    </>
  );
}
