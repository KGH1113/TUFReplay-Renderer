import assert from 'node:assert/strict';
import { readFile, mkdtemp, rm, readdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { runInNewContext } from 'node:vm';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { renderDmNoteLayer } from './render.mjs';
import { verifySnapshotCapture } from './snapshot.verify.mjs';

const app = process.env.IMPL_DMNOTE_SOURCE || '/Users/kgh/dev/src/impl-resourcepack/apps/impl-dm-note';
const runtimeRequire = createRequire(join(app, 'package.json'));
const defaultsSource = await readFile(join(app, 'src/renderer/defaults.ts'), 'utf8');
const { code } = await runtimeRequire('esbuild').transform(defaultsSource, { loader: 'ts', format: 'cjs' });
const module = { exports: {} }; runInNewContext(code, { module, exports: module.exports });
const settings = module.exports.getDefaultSettingsState();
settings.noteEffect = true; settings.keyCounterEnabled = true; settings.useCustomCSS = true;
settings.customCSS = { path: null, content: '* { font-family: monospace !important; }' };
settings.fontSettings = { customFonts: [] };
const storedPositions = JSON.parse(await readFile(join(app, 'src-tauri/default_positions.json'), 'utf8'));
const first = Object.values(storedPositions).find(positions => positions.length)?.[0];
const snapshot = {
  settings, defaults: { settings, counterSettings: module.exports.getDefaultCounterSettings() },
  keys: { 'hand-export': ['A', 'S', 'MOUSE1'], 'foot-export': ['A', 'S', 'MOUSE1'] },
  positions: { 'hand-export': [0, 90, 180].map(dx => ({ ...first, dx, dy: 170, width: 60, height: 60 })), 'foot-export': [0, 90, 180].map(dx => ({ ...first, dx, dy: 170, width: 60, height: 60 })) },
  selectedViewerTabs: { hand: 'hand-export', foot: 'foot-export' }, selectedKeyType: 'hand-export', currentMode: 'hand-export',
  tabs: [{ id: 'hand-export', name: 'Hands', viewerKind: 'hand' }, { id: 'foot-export', name: 'Feet', viewerKind: 'foot' }],
  overlay: { visible: true, locked: true, anchor: 'top-left' }, keyCounters: {}, layerGroups: {}, tabNoteOverrides: {}, tabCssOverrides: {},
};
const manifest = { version: 1, width: 320, height: 320, viewerKind: 'hand', fpsNumerator: 60, fpsDenominator: 1, frameCount: 40, timeline: [{ outputTimeUs: 0, replayTimeUs: 0, rate: 2 }], snapshot,
  events: [ { timeUs: 10000, key: 'A', down: true, sequence: 0 }, { timeUs: 20000, key: 'A', down: false, sequence: 1 }, { timeUs: 200000, key: 'S', down: true, sequence: 2 }, { timeUs: 600000, key: 'S', down: false, sequence: 3 }, { timeUs: 300000, key: 'Mouse0', down: true, sequence: 4 }, { timeUs: 500000, key: 'Mouse0', down: false, sequence: 5 } ], };
const directory = await mkdtemp(join(tmpdir(), 'dmnote-verification-'));
const chrome = process.env.CHROME_EXECUTABLE || '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const hashes = [], states = [];
try {
  await verifySnapshotCapture(directory);
  for (let pass = 0; pass < 2; pass++) {
    const output = join(directory, `alpha-${pass}.mkv`), frameHashes = [];
    const result = await renderDmNoteLayer({ manifest, appDirectory: app, executablePath: chrome, output, onFrame: frame => {
      frameHashes.push(createHash('sha256').update(frame.png).digest('hex'));
      if (!pass) states.push({ ...frame, png: undefined });
    } });
    assert.equal(result.frameCount, 40); hashes.push(frameHashes);
    const probe = spawnSync('ffprobe', ['-v', 'error', '-count_frames', '-show_entries', 'stream=pix_fmt,nb_read_frames', '-of', 'json', output], { encoding: 'utf8' });
    assert.equal(probe.status, 0, probe.stderr); const stream = JSON.parse(probe.stdout).streams[0];
    assert.equal(stream.pix_fmt, 'bgra'); assert.equal(stream.nb_read_frames, '40');
    const rgba = spawnSync('ffmpeg', ['-v', 'error', '-i', output, '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'rgba', 'pipe:1'], { maxBuffer: 320 * 320 * 4 + 1024 });
    assert.equal(rgba.status, 0, String(rgba.stderr));
    let transparent = 0, opaque = 0; for (let i = 3; i < rgba.stdout.length; i += 4) { if (rgba.stdout[i] === 0) transparent++; if (rgba.stdout[i] > 200) opaque++; }
    assert(transparent > 10000, 'The overlay must retain transparent pixels.'); assert(opaque > 100, 'The full overlay must contain visible key/label pixels.');
  }
  assert.deepEqual(hashes[0], hashes[1], 'Identical jobs must produce identical transparent frames.');
  assert.equal(states[1].counters['hand-export'].A, 1, 'A short between-frame tap must increment the counter.');
  assert(!states[1].activeKeys.includes('A'), 'A short between-frame tap must release.');
  assert(states[8].activeKeys.includes('S'), 'A held key must remain active at the mapped output time.');
  assert(!states[19].activeKeys.includes('S'), 'A held key must release at the mapped output time.');
  assert(states[10].activeKeys.includes('MOUSE1'), 'A recorded mouse button must map to the existing viewer label.');
  assert(!states[16].activeKeys.includes('MOUSE1'), 'A mouse button must release at the mapped output time.');
  assert.equal(states[16].counters['hand-export'].MOUSE1, 1);
  const controller = new AbortController(); let consumed = 0;
  await assert.rejects(renderDmNoteLayer({ manifest, appDirectory: app, executablePath: chrome, output: join(directory, 'cancelled.mkv'), signal: controller.signal, onFrame: () => { consumed++; controller.abort(); } }), /abort|cancel/i);
  assert.equal(consumed, 1);
  assert(!(await readdir(directory)).some(file => file.startsWith('cancelled.mkv')), 'Cancellation must remove its partial alpha output.');
  console.log('PASS: real Chromium full overlay, alpha FFV1, exact input time, stable pixels, bounded streaming and cancellation.');
} finally { await rm(directory, { recursive: true, force: true }); }
