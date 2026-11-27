import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The production build is written into the server project and embedded in PadNook.exe.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../server/wwwroot',
    emptyOutDir: true,
    target: ['es2020', 'safari15'],
    // The Lucide icon set is a separate lazy chunk (~120 KB gzip), loaded only when used.
    chunkSizeWarningLimit: 800,
  },
  server: {
    host: true,
    proxy: {
      '/ws': { target: 'ws://localhost:8765', ws: true },
      '/api': 'http://localhost:8765',
      '/images': 'http://localhost:8765',
    },
  },
});
