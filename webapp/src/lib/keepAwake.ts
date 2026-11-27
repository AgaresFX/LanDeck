// "Keep the screen on" (experimental). Over plain http iOS exposes no Wake Lock API, so NoSleep.js
// falls back to a tiny looping inline video, which can only start from a user gesture.
// On the PC (localhost is a secure context) the real Wake Lock API is used.
import NoSleep from 'nosleep.js';
import { useDeck } from '../state/store';
import { storage } from './platform';

const KEY = 'padnook.keepAwake';
export type AwakeState = 'off' | 'waiting' | 'on';

let noSleep: NoSleep | null = null;
let pendingGesture: (() => void) | null = null;

const set = (keepAwake: AwakeState) => useDeck.setState({ keepAwake });
const wanted = () => storage.get(KEY) === '1';

function instance(): NoSleep {
  if (!noSleep) {
    noSleep = new NoSleep();
    // The fallback video has a (silent) audio track: muted, it does not interrupt music playing in other apps.
    const video = (noSleep as unknown as { noSleepVideo?: HTMLVideoElement }).noSleepVideo;
    if (video) {
      video.muted = true;
      video.setAttribute('muted', '');
    }
  }
  return noSleep;
}

function enableNow() {
  instance()
    .enable()
    .then(() => {
      // The option may have been switched off while the request was pending.
      if (wanted()) set('on');
      else noSleep?.disable();
    })
    .catch(() => {
      if (wanted()) arm();
    });
}

function disarm() {
  if (!pendingGesture) return;
  document.removeEventListener('touchend', pendingGesture, true);
  document.removeEventListener('click', pendingGesture, true);
  pendingGesture = null;
}

/** Waits for the next tap (iOS requires a user gesture to start the video). */
function arm() {
  set('waiting');
  if (pendingGesture) return;
  const go = () => {
    disarm();
    // Let the tap's own handlers run first (the tap may be the one switching the option off).
    setTimeout(() => wanted() && enableNow(), 0);
  };
  pendingGesture = go;
  document.addEventListener('touchend', go, true);
  document.addEventListener('click', go, true);
}

export function initKeepAwake() {
  if (wanted()) arm();
  // iOS stops the video when the app goes to the background: re-arm when it comes back.
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && wanted() && !noSleep?.isEnabled) arm();
  });
}

/** Called from a tap on the switch, so we are inside a user gesture and can start right away. */
export function setKeepAwake(on: boolean) {
  storage.set(KEY, on ? '1' : null);
  if (on) enableNow();
  else {
    disarm();
    noSleep?.disable();
    set('off');
  }
}
