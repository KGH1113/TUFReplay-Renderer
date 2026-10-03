import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, join, resolve, isAbsolute } from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';

export async function captureSnapshot({ url, token, output, appDirectory, signal }) {
  signal?.throwIfAborted();
  const endpoint = new URL(url);
  if (!['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname)) throw new Error('Snapshot capture requires the local ImplDmNote OBS endpoint.');
  if (endpoint.username || endpoint.password || endpoint.search) throw new Error('Pass the OBS token with --token-file.');
  endpoint.protocol = endpoint.protocol === 'https:' || endpoint.protocol === 'wss:' ? 'wss:' : 'ws:';
  const socket = new WebSocket(endpoint);
  const { snapshot, version } = await new Promise((resolve, reject) => {
    let version, finished = false;
    const finish = (error, value) => { if (finished) return; finished = true; clearTimeout(timer); signal?.removeEventListener('abort', cancel); socket.close(); error ? reject(error) : resolve(value); };
    const cancel = () => finish(new Error('Snapshot capture cancelled.'));
    const timer = setTimeout(() => finish(new Error('ImplDmNote OBS did not return a snapshot within 15 seconds.')), 15000);
    signal?.addEventListener('abort', cancel, { once: true });
    socket.addEventListener('error', () => finish(new Error('Cannot connect to the local ImplDmNote OBS endpoint.')));
    socket.addEventListener('open', () => socket.send(JSON.stringify({ v: 1, type: 'hello', seq: 1, ts: Date.now(), payload: { client: 'tuf-replay-renderer', protocol: 1, appVersion: '', resumeFromSeq: 0, token } })));
    socket.addEventListener('message', event => {
      try {
        const message = JSON.parse(String(event.data));
        if (message.v !== 1) return finish(new Error('Unsupported ImplDmNote OBS protocol.'));
        if (message.type === 'error') return finish(new Error('ImplDmNote rejected snapshot access; check the OBS token.'));
        if (message.type === 'hello_ack') version = message.payload.serverVersion;
        if (message.type === 'snapshot') finish(null, { snapshot: message.payload, version });
      } catch { finish(new Error('Malformed ImplDmNote OBS snapshot.')); }
    });
  });
  if (!snapshot?.settings || !snapshot?.keys || !snapshot?.defaults) throw new Error('ImplDmNote returned an incomplete snapshot.');
  const sources = new Set();
  const visit = value => {
    if (typeof value === 'string') {
      if (isAbsolute(value) && /\.(?:woff2?|ttf|otf|png|jpe?g|webp|gif|svg)$/i.test(value)) sources.add(value);
      if (/^https?:\/\/[^\s]+\.(?:woff2?|ttf|otf|png|jpe?g|webp|gif|svg)(?:\?[^\s]*)?$/i.test(value)) sources.add(value);
      for (const match of value.matchAll(/url\(\s*['"]?(https?:\/\/[^'"\s)]+)['"]?\s*\)/g)) sources.add(match[1]);
    } else if (Array.isArray(value)) value.forEach(visit);
    else if (value && typeof value === 'object') Object.values(value).forEach(visit);
  };
  visit(snapshot);
  if (appDirectory) {
    for (const file of ['src/renderer/styles/viewer-compat-v1.css', 'src/types/settings/fonts.ts']) {
      const source = await readFile(join(appDirectory, file), 'utf8');
      for (const match of source.matchAll(/https?:\/\/[^'"\s)\x60]+\.(?:woff2?|ttf|otf)/g)) sources.add(match[0]);
    }
  }
  const assetDirectory = `${resolve(output)}.assets`;
  await mkdir(assetDirectory, { recursive: true });
  const assets = {}; let total = 0;
  const httpOrigin = new URL(endpoint); httpOrigin.protocol = endpoint.protocol === 'wss:' ? 'https:' : 'http:';
  for (const original of sources) {
    signal?.throwIfAborted();
    let fetchUrl = original;
    if (isAbsolute(original)) { const asset = new URL(`/media/${Buffer.from(original).toString('base64url')}`, httpOrigin); asset.searchParams.set('token', token); fetchUrl = asset.href; }
    const response = await fetch(fetchUrl, { signal: AbortSignal.any([signal || new AbortController().signal, AbortSignal.timeout(15000)]) });
    if (!response.ok) throw new Error(`A preset font or image could not be frozen (HTTP ${response.status}).`);
    const chunks = [], reader = response.body.getReader();
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        total += value.byteLength;
        if (total > 256 * 1024 * 1024) throw new Error('Preset assets exceed the 256 MiB export limit.');
        chunks.push(Buffer.from(value));
      }
    } finally { await reader.cancel().catch(() => {}); reader.releaseLock(); }
    const bytes = Buffer.concat(chunks);
    const sha256 = createHash('sha256').update(bytes).digest('hex'), filename = `${sha256}.bin`;
    await writeFile(join(assetDirectory, filename), bytes, { flag: 'wx' }).catch(error => { if (error.code !== 'EEXIST') throw error; });
    assets[original] = { file: join(`${resolve(output).split(/[\\/]/).at(-1)}.assets`, filename), contentType: response.headers.get('content-type') || 'application/octet-stream', sha256 };
  }
  const bundle = { version: 1, implDmNoteVersion: version, snapshot, assets };
  await mkdir(dirname(resolve(output)), { recursive: true });
  await writeFile(output, JSON.stringify(bundle, null, 2), { flag: 'wx' });
  return { assetCount: sources.size, assetBytes: total, output };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = {}; for (let i = 2; i < process.argv.length; i += 2) args[process.argv[i].replace(/^--/, '')] = process.argv[i + 1];
  const controller = new AbortController(); process.once('SIGINT', () => controller.abort()); process.once('SIGTERM', () => controller.abort());
  try {
    const token = (await readFile(args['token-file'], 'utf8')).trim();
    const result = await captureSnapshot({ url: args['capture-snapshot'], token, output: args.output, appDirectory: args.app || process.env.IMPL_DMNOTE_SOURCE, signal: controller.signal });
    process.stdout.write(`${JSON.stringify({ type: 'snapshot', ...result })}\n`);
  } catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = controller.signal.aborted ? 130 : 1; }
}
