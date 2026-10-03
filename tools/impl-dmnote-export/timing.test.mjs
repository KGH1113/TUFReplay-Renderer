import test from 'node:test';
import assert from 'node:assert/strict';
import { VirtualClock } from './virtual-clock.mjs';
import { frameAt, mapEvents, parseEventsCsv, keyCandidates } from './timing.mjs';
import { validateFrozenAsset } from './assets.mjs';

const base = { frameCount: 180, fpsNumerator: 60, fpsDenominator: 1, viewerKind: 'hand', snapshot: { selectedViewerTabs: { hand: 'h' }, keys: { h: ['A', 'NUMPAD RETURN', 'RIGHT CTRL'] } }, timeline: [{ outputTimeUs: 0, replayTimeUs: 0, rate: 2 }, { outputTimeUs: 1000000, replayTimeUs: 2000000, rate: 1 }] };
test('input time inverse mapping preserves pitch transitions and same-time order', () => {
  const events = parseEventsCsv('timeUs,key,down,sequence\n10000,A,1,0\n20000,A,0,1\n2500000,KeypadEnter,true,3\n2500000,RControl,false,2\n');
  const mapped = mapEvents(base, events);
  assert.deepEqual(mapped.map(e => e.outputTimeUs), [5000, 10000, 1500000, 1500000]);
  assert.deepEqual(mapped.slice(2).map(e => e.sequence), [2, 3]);
  assert.equal(mapped[2].key, 'RIGHT CTRL'); assert.equal(mapped[3].key, 'NUMPAD RETURN');
  assert.equal(frameAt(base, 120).replayTimeUs, 3000000);
});
test('rational FPS does not accumulate delta error', () => { const manifest = { ...base, fpsNumerator: 60000, fpsDenominator: 1001 }; assert.equal(frameAt(manifest, 60000).outputTimeUs, 1001000000); });
test('Unity KeyCode wire names preserve preset aliases and platform modifier labels', () => {
  assert.deepEqual(keyCandidates('Return'), ['RETURN']);
  assert.deepEqual(keyCandidates('LeftArrow'), ['LEFT ARROW']);
  assert.deepEqual(keyCandidates('BackQuote'), ['SECTION', 'GRAVE']);
  assert.deepEqual(keyCandidates('LeftCommand'), ['91', 'LEFT WINDOWS']);
  assert.deepEqual(keyCandidates('RightWindows'), ['92', 'RIGHT WINDOWS']);
  assert.deepEqual(keyCandidates('KeypadMultiply'), ['NUMPAD MULTIPLY', '*']);
  assert.deepEqual(keyCandidates('Mouse0'), ['MOUSE1']);
  assert.deepEqual(keyCandidates('Mouse4'), ['MOUSE5']);
  assert.deepEqual(keyCandidates('Numlock'), ['NUM LOCK']);
  assert.deepEqual(keyCandidates('Print'), ['PRINT SCREEN']);
  const snapshot = { selectedViewerTabs: { hand: 'h' }, keys: { h: ['25', 'RIGHT CTRL'] } };
  assert.deepEqual(mapEvents({ ...base, snapshot }, [{ timeUs: 0, key: 'RightControl', down: true, sequence: 0 }]).map(e => e.key), ['25', 'RIGHT CTRL']);
});
test('timer ordering, short input timers and rAF sampling use exact output time', () => {
  const clock = new VirtualClock(), calls = [];
  clock.schedule(() => calls.push(clock.nowMs), 5); clock.schedule(() => calls.push(clock.nowMs), 10);
  clock.frames.set(1, time => calls.push(time)); clock.advanceTo(1000 / 60); clock.sampleFrame();
  assert.deepEqual(calls, [5, 10, 1000 / 60]); assert.throws(() => clock.advanceTo(0), /monotonic/);
});
test('unknown input and discontinuous timeline are explicit failures', () => {
  assert.throws(() => keyCandidates('Mouse5'), /Unsupported/);
  assert.throws(() => mapEvents({ ...base, timeline: [{ outputTimeUs: 0, replayTimeUs: 0, rate: 1 }, { outputTimeUs: 1000000, replayTimeUs: 3000000, rate: 1 }] }, []), /discontinuity/);
});
test('frozen asset hashes and animated media are checked before export', () => {
  assert.throws(() => validateFrozenAsset(Buffer.from('GIF89a')), /animated/);
  assert.throws(() => validateFrozenAsset(Buffer.from('font data'), 'wrong hash'), /changed/);
  assert.equal(validateFrozenAsset(Buffer.from('font data')).length, 64);
});
