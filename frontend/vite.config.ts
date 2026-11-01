import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { createHash } from 'node:crypto';

export default defineConfig({
  plugins: [react(), {
    name: 'retailops-offline-shell',
    apply: 'build',
    enforce: 'post',
    generateBundle(_options, bundle) {
      const assets = Object.keys(bundle).filter(name => /\.(?:js|css|woff2?|ttf)$/.test(name)).sort().map(name => `/${name}`);
      const html = bundle['index.html'];
      const htmlSource = html?.type === 'asset' ? String(html.source) : '';
      const version = createHash('sha256').update(assets.join('\n')).update(htmlSource).digest('hex').slice(0, 16);
      this.emitFile({ type: 'asset', fileName: 'sw.js', source: `
const CACHE = 'retailops-shell-${version}';
const PUBLIC = ${JSON.stringify(['/', '/index.html', ...assets])};
self.addEventListener('install', event => event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(PUBLIC)).then(() => self.skipWaiting())));
self.addEventListener('activate', event => event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(key => key.startsWith('retailops-shell-') && key !== CACHE).map(key => caches.delete(key)))).then(() => self.clients.claim())));
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== self.location.origin || url.pathname.startsWith('/api') || url.pathname.startsWith('/health')) return;
  if (event.request.mode === 'navigate') {
    event.respondWith(fetch(event.request).catch(async () => (await caches.open(CACHE)).match('/index.html')));
    return;
  }
  if (PUBLIC.includes(url.pathname)) event.respondWith(caches.open(CACHE).then(async cache => (await cache.match(url.pathname)) || fetch(event.request)));
});` });
    },
  }],
  server: { port: 5191, proxy: { '/api': 'http://127.0.0.1:5190', '/health': 'http://127.0.0.1:5190' } },
  test: {
    environment: 'jsdom',
    setupFiles: './src/test-setup.ts',
    include: ['src/**/*.test.{ts,tsx}'],
    restoreMocks: true,
    maxWorkers: 1,
    fileParallelism: false,
    testTimeout: 15_000,
  },
});
