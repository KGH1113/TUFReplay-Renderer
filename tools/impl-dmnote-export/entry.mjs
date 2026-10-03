import { VirtualClock } from './virtual-clock.mjs';
import { installFrozenShim } from './frozen-shim.mjs';

async function start() {
  const clock = new VirtualClock();
  clock.nowMs = window.__dmnExportConfig.initialOutputTimeUs / 1000;
  const restoreClock = clock.install();
  const { snapshot, viewerKind, assetUrls } = window.__dmnExportConfig;
  const shim = installFrozenShim(snapshot, viewerKind, assetUrls);
  const [{ createRoot }, { flushSync }, React, { I18nProvider }, { default: App }, { useKeyStore }, { getStatsSnapshot }] = await Promise.all([
    import('react-dom/client'), import('react-dom'), import('react'), import('@contexts/I18nContext'),
    import('@src/renderer/windows/overlay/App'), import('@stores/data/useKeyStore'), import('@stores/signals/statsSignals'),
    import('@components/overlay/WebGLTracksOGL'), import('@api/dmnoteApi'), import('@styles/viewer-compat-v1.css'),
  ]);
  const root = createRoot(document.getElementById('root'));
  flushSync(() => root.render(React.createElement(I18nProvider, null, React.createElement(App))));
  const animations = new WeakMap();
  const sampleStyles = () => {
    flushSync(() => {});
    for (const animation of document.getAnimations()) {
      if (!animations.has(animation)) { animations.set(animation, clock.nowMs); animation.pause(); }
      animation.currentTime = clock.nowMs - animations.get(animation);
    }
  };
  clock.afterTask = sampleStyles;
  let nextFrame = 0, disposed = false;
  const active = new Set();
  window.__dmnExport = {
    ready: () => useKeyStore.getState().isBootstrapped && shim.ready(),
    assetsReady: async () => {
      await document.fonts.ready;
      await Promise.all([...document.images].map(image => image.decode()));
    },
    frame: async ({ frameIndex, outputTimeUs, events }) => {
      if (disposed || frameIndex !== nextFrame) throw new Error('Export frame is out of sequence.');
      for (const event of events) {
        clock.advanceTo(event.outputTimeUs / 1000); sampleStyles();
        const mode = useKeyStore.getState().selectedKeyType;
        if (!useKeyStore.getState().keyMappings[mode]?.includes(event.key)) continue;
        const changed = event.down ? !active.has(event.key) : active.has(event.key);
        if (!changed) continue;
        if (event.down) active.add(event.key); else active.delete(event.key);
        flushSync(() => {
          if (event.down) {
            const byKey = shim.counters[mode] ||= {};
            const count = byKey[event.key] = (byKey[event.key] || 0) + 1;
            shim.emit('keys:counter', { mode, key: event.key, count });
          }
          shim.emit('keys:state', { mode, key: event.key, state: event.down ? 'DOWN' : 'UP', eventAgeMs: 0 });
          shim.emit('input:raw', { device: event.key.startsWith('MOUSE') ? 'mouse' : 'keyboard', label: event.key, labels: [event.key], state: event.down ? 'DOWN' : 'UP' });
        });
        await Promise.resolve(); sampleStyles();
      }
      clock.advanceTo(outputTimeUs / 1000); sampleStyles();
      clock.sampleFrame(); await Promise.resolve(); sampleStyles();
      nextFrame++;
      return { frameIndex, outputTimeUs, activeKeys: [...active], counters: structuredClone(shim.counters), stats: getStatsSnapshot() };
    },
    dispose: () => { if (disposed) return; disposed = true; flushSync(() => root.unmount()); shim.dispose(); restoreClock(); },
  };
}

start().catch(error => { window.__dmnExport = { error: String(error), stack: error.stack }; console.error(error); });
