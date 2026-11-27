import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { initKeepAwake } from './lib/keepAwake';
import { initClient } from './net/client';
import './styles.css';

// iOS: block pinch-zoom and gesture zoom (user-scalable=no is ignored by Safari).
document.addEventListener('gesturestart', (e) => e.preventDefault());
document.addEventListener(
  'touchmove',
  (e) => {
    if (e.touches.length > 1) e.preventDefault();
  },
  { passive: false },
);

initClient();
initKeepAwake();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
