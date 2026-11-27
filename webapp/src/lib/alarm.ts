import { tr } from '../i18n';
// "Timer finished" alert on the device: a short beep sequence, vibration and a toast.
import { showToast } from '../state/store';

let ctx: AudioContext | null = null;

/** Browsers only allow audio after a user gesture: create/resume the context on the first touches. */
function unlock() {
  try {
    ctx ??= new AudioContext();
    if (ctx.state === 'suspended') void ctx.resume();
  } catch {
    /* no Web Audio */
  }
}
window.addEventListener('pointerdown', unlock, { capture: true, passive: true });

function beep(at: number, freq: number, len: number) {
  if (!ctx) return;
  const osc = ctx.createOscillator();
  const gain = ctx.createGain();
  osc.type = 'sine';
  osc.frequency.value = freq;
  gain.gain.setValueAtTime(0, at);
  gain.gain.linearRampToValueAtTime(0.35, at + 0.01);
  gain.gain.setValueAtTime(0.35, at + len - 0.04);
  gain.gain.linearRampToValueAtTime(0, at + len);
  osc.connect(gain).connect(ctx.destination);
  osc.start(at);
  osc.stop(at + len);
}

export function timerAlarm(title: string) {
  if (ctx && ctx.state === 'running') {
    const t0 = ctx.currentTime + 0.05;
    for (let i = 0; i < 3; i++) {
      beep(t0 + i * 0.9, 880, 0.18);
      beep(t0 + i * 0.9 + 0.25, 880, 0.18);
    }
  }
  try {
    navigator.vibrate?.([300, 150, 300, 150, 300]);
  } catch {
    /* not supported (iPhone) */
  }
  showToast(title === 'Timer' ? tr('Tempo scaduto') : tr('{0}: tempo scaduto', title), 'info', 8000);
}
