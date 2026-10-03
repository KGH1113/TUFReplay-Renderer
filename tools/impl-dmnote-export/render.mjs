import { createRequire } from 'node:module';
import { readFile, mkdtemp, mkdir, rm, rename, access } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawn } from 'node:child_process';
import { mapEvents, frameAt, parseEventsCsv } from './timing.mjs';
import { validateFrozenAsset } from './assets.mjs';

const helperRoot = dirname(fileURLToPath(import.meta.url));
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export async function renderDmNoteLayer({ manifest, appDirectory, output, executablePath, ffmpegPath = 'ffmpeg', signal, onFrame, pngDirectory, assetsRoot = process.cwd() }) {
  if (manifest.version !== 1 || !['hand', 'foot'].includes(manifest.viewerKind)) throw new Error('Unsupported ImplDmNote export manifest.');
  for (const field of ['width', 'height', 'fpsNumerator', 'fpsDenominator']) if (!Number.isSafeInteger(manifest[field]) || manifest[field] <= 0) throw new Error(`Invalid ${field}.`);
  if (manifest.width > 8192 || manifest.height > 8192) throw new Error('Export dimensions exceed 8192 pixels.');
  if (!output && !pngDirectory && !onFrame) throw new Error('An output video or frame consumer is required.');
  if (!appDirectory) throw new Error('Set --app or IMPL_DMNOTE_SOURCE to the licensed ImplDmNote source directory.');
  if (output) { const exists = await access(output).then(() => true, () => false); if (exists) throw new Error('The output file already exists; choose a new path.'); }
  const events = manifest.events || parseEventsCsv(await readFile(resolve(assetsRoot, manifest.eventsFile), 'utf8'));
  const mapped = mapEvents(manifest, events), snapshot = structuredClone(manifest.snapshot), frozenAssets = new Map();
  const assetUrls = {};
  let assetBytes = 0;
  const remember = (key, bytes, contentType, expectedHash) => {
    assetBytes += bytes.length;
    if (assetBytes > 256 * 1024 * 1024) throw new Error('Preset assets exceed the 256 MiB export limit.');
    const value = { bytes, contentType, sha256: validateFrozenAsset(bytes, expectedHash) };
    frozenAssets.set(key, value); return value;
  };
  for (const [original, asset] of Object.entries(manifest.assets || {})) {
    const filename = typeof asset === 'string' ? asset : asset.file;
    const contentType = typeof asset === 'string' ? 'application/octet-stream' : asset.contentType || 'application/octet-stream';
    const frozen = remember(original, await readFile(resolve(assetsRoot, filename)), contentType, typeof asset === 'string' ? undefined : asset.sha256);
    assetUrls[original] = `data:${contentType};base64,${frozen.bytes.toString('base64')}`;
  }
  const app = resolve(appDirectory), runtimeRequire = createRequire(join(app, 'package.json'));
  const { createServer } = await import(pathToFileURL(runtimeRequire.resolve('vite')).href);
  const { chromium } = runtimeRequire('playwright');
  const work = await mkdtemp(join(tmpdir(), 'tuf-dmnote-'));
  const partial = output ? `${output}.${basename(work)}.partial.mkv` : null;
  let server, browser, page, encoder, failed = false, stderr = '', outputDone = false, freezeComplete = false;
  const abort = () => { failed = true; void browser?.close(); encoder?.stdin.destroy(); encoder?.kill('SIGTERM'); };
  signal?.addEventListener('abort', abort, { once: true });
  const check = () => { signal?.throwIfAborted(); if (failed) throw new Error('ImplDmNote export was cancelled.'); };
  let encodingDone;
  try {
    check();
    const reactRoot = dirname(runtimeRequire.resolve('react/package.json')), domRoot = dirname(runtimeRequire.resolve('react-dom/package.json'));
    const loadConfig = runtimeRequire('jiti')(join(app, 'package.json'), { cache: false });
    const [{ default: reactPlugin }, svgrModule] = await Promise.all([import(pathToFileURL(runtimeRequire.resolve('@vitejs/plugin-react')).href), import(pathToFileURL(runtimeRequire.resolve('vite-plugin-svgr')).href)]);
    const svgr = svgrModule.default?.default || svgrModule.default;
    const aliases = Object.fromEntries(['components', 'styles', 'windows', 'hooks', 'api', 'assets', 'utils', 'stores', 'constants', 'contexts', 'plugins', 'config'].map(name => [`@${name}`, join(app, 'src/renderer', name)]));
    aliases['@src'] = join(app, 'src'); aliases['@shared'] = join(app, 'src/types');
    const appConfig = { root: join(app, 'src/renderer/windows'), define: { __APP_VERSION__: JSON.stringify(runtimeRequire(join(app, 'package.json')).version) }, resolve: { alias: aliases, extensions: ['.mjs', '.js', '.ts', '.jsx', '.tsx', '.json'] }, plugins: [reactPlugin(), svgr({ include: '**/*.svg', svgrOptions: { exportType: 'default' } })] };
    server = await createServer({ ...appConfig, configFile: false, cacheDir: join(work, 'vite-cache'),
      resolve: { ...appConfig.resolve, alias: [{ find: /^react$/, replacement: join(reactRoot, 'index.js') }, { find: /^react\/jsx-runtime$/, replacement: join(reactRoot, 'jsx-runtime.js') }, { find: /^react\/jsx-dev-runtime$/, replacement: join(reactRoot, 'jsx-dev-runtime.js') }, { find: /^react-dom\/client$/, replacement: join(domRoot, 'client.js') }, { find: /^react-dom$/, replacement: join(domRoot, 'index.js') }, ...Object.entries(appConfig.resolve.alias).map(([find, replacement]) => ({ find, replacement }))] },
      css: { postcss: { plugins: [runtimeRequire('tailwindcss')({ ...loadConfig(join(app, 'tailwind.config.cjs')), content: [join(app, 'src/**/*.{js,jsx,ts,tsx,html}')] }), runtimeRequire('autoprefixer')()] } },
      server: { host: '127.0.0.1', port: 0, strictPort: false, open: false, fs: { allow: [resolve(app, '../..'), helperRoot] } },
      plugins: [...appConfig.plugins, { name: 'dmnote-export-entry', configureServer(vite) { vite.middlewares.use('/__dmnote_export', async (_req, res, next) => { try { res.setHeader('Content-Type', 'text/html'); res.end(await vite.transformIndexHtml('/__dmnote_export', `<html><head><meta charset="UTF-8"><link rel="icon" href="data:,"></head><body style="margin:0;background:transparent"><div id="root"></div><script type="module" src="/@fs/${helperRoot}/entry.mjs"></script></body></html>`)); } catch (error) { next(error); } }); } }],
    });
    await server.listen();
    const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
    if (!executablePath) {
      const paths = [chromium.executablePath(), ...(process.platform === 'darwin' ? ['/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'] : process.platform === 'win32' ? [join(process.env.PROGRAMFILES || 'C:\\Program Files', 'Google/Chrome/Application/chrome.exe'), join(process.env['PROGRAMFILES(X86)'] || 'C:\\Program Files (x86)', 'Google/Chrome/Application/chrome.exe'), join(process.env.LOCALAPPDATA || '', 'Google/Chrome/Application/chrome.exe')] : ['/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser'])];
      for (const candidate of paths) if (await access(candidate).then(() => true, () => false)) { executablePath = candidate; break; }
    }
    browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}), args: ['--disable-background-timer-throttling', '--disable-renderer-backgrounding'] });
    page = await browser.newPage({ viewport: { width: manifest.width, height: manifest.height }, deviceScaleFactor: 1 });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    await page.route('**/*', async route => {
      const url = route.request().url();
      if (!/^https?:/.test(url) || new URL(url).origin === origin) return route.continue();
      try {
        let asset = frozenAssets.get(url);
        if (!asset) {
          if (freezeComplete) throw new Error(`Asset requested after the snapshot was frozen: ${url}`);
          const response = await page.request.get(url, { timeout: 15000 });
          if (!response.ok()) throw new Error(`Preset asset could not be loaded (${response.status()}): ${url}`);
          asset = remember(url, await response.body(), response.headers()['content-type'] || 'application/octet-stream');
        }
        await route.fulfill({ body: asset.bytes, contentType: asset.contentType });
      } catch (error) { errors.push(error.message); await route.abort(); }
    });
    await page.addInitScript(config => { window.__dmnExportConfig = config; }, { snapshot, viewerKind: manifest.viewerKind, assetUrls, initialOutputTimeUs: Math.min(0, mapped[0]?.outputTimeUs || 0) });
    await page.goto(`${origin}/__dmnote_export`, { waitUntil: 'load', timeout: 30000 });
    const deadline = Date.now() + 30000;
    for (;;) {
      check();
      const state = await page.evaluate(() => ({ error: window.__dmnExport?.error, ready: window.__dmnExport?.ready?.() }));
      if (state.error || errors.length) throw new Error(state.error || errors.join('\n'));
      if (state.ready) break;
      if (Date.now() > deadline) throw new Error('ImplDmNote overlay did not initialize.');
      await sleep(25);
    }
    await page.evaluate(() => window.__dmnExport.assetsReady());
    await page.waitForLoadState('networkidle');
    if (errors.length) throw new Error(errors.join('\n'));
    freezeComplete = true;
    if (pngDirectory) await mkdir(pngDirectory, { recursive: true });
    if (output) {
      await mkdir(dirname(resolve(output)), { recursive: true });
      encoder = spawn(ffmpegPath, ['-hide_banner', '-loglevel', 'error', '-n', '-f', 'image2pipe', '-framerate', `${manifest.fpsNumerator}/${manifest.fpsDenominator}`, '-c:v', 'png', '-i', 'pipe:0', '-an', '-c:v', 'ffv1', '-level', '3', '-pix_fmt', 'bgra', '-f', 'matroska', partial], { stdio: ['pipe', 'ignore', 'pipe'] });
      encoder.stderr.on('data', data => { stderr = (stderr + data).slice(-8192); });
      encodingDone = new Promise((resolve, reject) => { encoder.once('error', reject); encoder.once('close', code => code === 0 ? resolve() : reject(new Error(`Alpha encoder exited ${code}: ${stderr}`))); });
      encodingDone.catch(() => {});
      encoder.stdin.on('error', () => {});
    }
    let nextEvent = 0;
    for (let frameIndex = 0; frameIndex < manifest.frameCount; frameIndex++) {
      const frame = frameAt(manifest, frameIndex);
      check(); const inputs = [];
      while (nextEvent < mapped.length && mapped[nextEvent].outputTimeUs <= frame.outputTimeUs) inputs.push(mapped[nextEvent++]);
      const ack = await page.evaluate(data => window.__dmnExport.frame(data), { ...frame, events: inputs });
      if (errors.length) throw new Error(errors.join('\n'));
      const png = await page.screenshot({ type: 'png', omitBackground: true, animations: 'allow', scale: 'css', ...(pngDirectory ? { path: join(pngDirectory, `${String(frame.frameIndex).padStart(8, '0')}.png`) } : {}) });
      if (encoder) {
        if (encoder.exitCode !== null || encoder.stdin.destroyed) { await encodingDone; throw new Error('Alpha encoder closed before all frames.'); }
        await new Promise((resolve, reject) => encoder.stdin.write(png, error => error ? reject(error) : resolve()));
      }
      await onFrame?.({ ...ack, replayTimeUs: frame.replayTimeUs, png });
    }
    if (encoder) { encoder.stdin.end(); await encodingDone; await rename(partial, output); outputDone = true; }
    return { version: 1, frameCount: manifest.frameCount, width: manifest.width, height: manifest.height, pixelFormat: 'bgra', output, assets: [...frozenAssets].map(([name, asset]) => ({ name, sha256: asset.sha256 })) };
  } finally {
    signal?.removeEventListener('abort', abort);
    if (page && !page.isClosed()) await page.evaluate(() => window.__dmnExport?.dispose?.()).catch(() => {});
    await browser?.close().catch(() => {});
    if (encoder && encoder.exitCode === null) { encoder.stdin.destroy(); encoder.kill('SIGTERM'); await encodingDone?.catch(() => {}); }
    await server?.close();
    await rm(work, { recursive: true, force: true });
    if (partial && !outputDone) await rm(partial, { force: true });
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = {}; for (let i = 2; i < process.argv.length; i += 2) args[process.argv[i].replace(/^--/, '')] = process.argv[i + 1];
  const controller = new AbortController(); process.once('SIGINT', () => controller.abort()); process.once('SIGTERM', () => controller.abort());
  try {
    const manifestPath = resolve(args.manifest), manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
    if (args.snapshot) {
      const snapshotPath = resolve(args.snapshot), frozen = JSON.parse(await readFile(snapshotPath, 'utf8'));
      if (frozen.version !== 1 || !frozen.snapshot) throw new Error('Unsupported frozen snapshot format.');
      manifest.snapshot = frozen.snapshot;
      manifest.assets = Object.fromEntries(Object.entries(frozen.assets || {}).map(([original, asset]) => [original, { ...asset, file: resolve(dirname(snapshotPath), asset.file) }]));
    }
    const result = await renderDmNoteLayer({ manifest, assetsRoot: dirname(manifestPath), appDirectory: args.app || process.env.IMPL_DMNOTE_SOURCE, output: args.output, pngDirectory: args['png-directory'], executablePath: args.chrome, ffmpegPath: args.ffmpeg || 'ffmpeg', signal: controller.signal,
      onFrame: frame => process.stdout.write(`${JSON.stringify({ type: 'frame', frameIndex: frame.frameIndex, outputTimeUs: frame.outputTimeUs, replayTimeUs: frame.replayTimeUs })}\n`), });
    process.stdout.write(`${JSON.stringify({ type: 'complete', ...result })}\n`);
  } catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = controller.signal.aborted ? 130 : 1; }
}
