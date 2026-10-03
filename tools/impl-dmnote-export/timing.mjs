const labels = {
  Apostrophe: ['QUOTE'], ArrowDown: ['DOWN ARROW'], ArrowLeft: ['LEFT ARROW'], ArrowRight: ['RIGHT ARROW'], ArrowUp: ['UP ARROW'],
  BackSlash: ['BACKSLASH'], Backspace: ['BACKSPACE'], CapsLock: ['CAPS LOCK'], Comma: ['COMMA'], Delete: ['DELETE'], Dot: ['DOT', 'PERIOD'], End: ['END'],
  Enter: ['RETURN'], Equal: ['EQUALS', '='], Escape: ['ESCAPE'], Grave: ['SECTION', 'GRAVE'], Home: ['HOME'], Insert: ['INS'],
  KeypadAsterisk: ['NUMPAD MULTIPLY', '*'], KeypadDot: ['NUMPAD DELETE', 'DECIMAL'], KeypadEnter: ['NUMPAD RETURN'], KeypadMinus: ['NUMPAD MINUS', '-'], KeypadPlus: ['NUMPAD PLUS', '+'], KeypadSlash: ['NUMPAD DIVIDE', '/'],
  LAlt: ['LEFT ALT'], LControl: ['LEFT CTRL'], LShift: ['LEFT SHIFT'], LeftBrace: ['SQUARE BRACKET OPEN'], Minus: ['MINUS', '-'], NumLock: ['NUM LOCK'],
  PageDown: ['PAGE DOWN'], PageUp: ['PAGE UP'], PauseBreak: ['19', 'PAUSE'], PrintScreen: ['PRINT SCREEN'], RAlt: ['21', 'RIGHT ALT'], RControl: ['25', 'RIGHT CTRL'],
  RShift: ['RIGHT SHIFT'], RSuper: ['92', 'RIGHT WINDOWS'], RightBrace: ['SQUARE BRACKET CLOSE'], ScrollLock: ['SCROLL LOCK'], Semicolon: ['SEMICOLON'],
  Slash: ['FORWARD SLASH', '/'], Space: ['SPACE'], Super: ['91', 'LEFT WINDOWS'], Tab: ['TAB'],
};
const unityAliases = {
  Return: 'Enter', Quote: 'Apostrophe', DownArrow: 'ArrowDown', LeftArrow: 'ArrowLeft', RightArrow: 'ArrowRight', UpArrow: 'ArrowUp',
  Backslash: 'BackSlash', Period: 'Dot', Equals: 'Equal', BackQuote: 'Grave', KeypadMultiply: 'KeypadAsterisk', KeypadPeriod: 'KeypadDot', KeypadDivide: 'KeypadSlash',
  LeftAlt: 'LAlt', LeftControl: 'LControl', LeftShift: 'LShift', RightAlt: 'RAlt', RightControl: 'RControl', RightShift: 'RShift',
  LeftBracket: 'LeftBrace', RightBracket: 'RightBrace', Pause: 'PauseBreak', Numlock: 'NumLock', Print: 'PrintScreen', LeftCommand: 'Super', LeftWindows: 'Super', RightCommand: 'RSuper', RightWindows: 'RSuper',
};

export function keyCandidates(key) {
  key = unityAliases[key] || key;
  if (/^Mouse[0-4]$/.test(key)) return [`MOUSE${Number(key.slice(5)) + 1}`];
  if (/^[A-Z]$/.test(key) || /^F([1-9]|1[0-9]|2[0-4])$/.test(key)) return [key];
  if (/^Alpha[0-9]$/.test(key)) return [key.slice(5)];
  if (/^Keypad[0-9]$/.test(key)) return [`NUMPAD ${key.slice(6)}`];
  if (!labels[key]) throw new Error(`Unsupported recorded keyboard key: ${key}`);
  return labels[key];
}

export function validateTimeline(manifest) {
  const { timeline, frameCount, fpsNumerator, fpsDenominator } = manifest;
  if (!Number.isSafeInteger(frameCount) || frameCount <= 0 || !timeline?.length || timeline[0].outputTimeUs !== 0) throw new Error('A frame count and a timeline beginning at output time zero are required.');
  for (let i = 0; i < timeline.length; i++) {
    const segment = timeline[i], prev = timeline[i - 1];
    if (!Number.isSafeInteger(segment.outputTimeUs) || !Number.isSafeInteger(segment.replayTimeUs) || !Number.isFinite(segment.rate) || segment.rate < 0 || (prev && (segment.outputTimeUs <= prev.outputTimeUs || segment.replayTimeUs < prev.replayTimeUs))) throw new Error('Invalid or non-monotonic export timeline.');
    if (prev && Math.abs(prev.replayTimeUs + (segment.outputTimeUs - prev.outputTimeUs) * prev.rate - segment.replayTimeUs) > 2) throw new Error('The export timeline has a discontinuity; seek/restart segments require separate jobs.');
  }
  if (!Number.isSafeInteger(fpsNumerator) || !Number.isSafeInteger(fpsDenominator) || fpsNumerator <= 0 || fpsDenominator <= 0) throw new Error('Invalid output frame rate.');
}

export function frameAt(manifest, frameIndex) {
  const outputTimeUs = Math.round(frameIndex * 1000000 * manifest.fpsDenominator / manifest.fpsNumerator);
  let segment = manifest.timeline[0];
  for (const candidate of manifest.timeline) { if (candidate.outputTimeUs > outputTimeUs) break; segment = candidate; }
  return { frameIndex, outputTimeUs, replayTimeUs: Math.round(segment.replayTimeUs + (outputTimeUs - segment.outputTimeUs) * segment.rate) };
}

export function parseEventsCsv(csv) {
  const rows = csv.trim().split(/\r?\n/);
  if (rows.shift()?.replace(/^\uFEFF/, '').toLowerCase() !== 'timeus,key,down,sequence') throw new Error('Expected input CSV header timeUs,key,down,sequence.');
  return rows.filter(Boolean).map(row => {
    const cells = row.split(',');
    if (cells.length !== 4 || !['0', '1', 'true', 'false'].includes(cells[2].toLowerCase())) throw new Error('Malformed input CSV row.');
    return { timeUs: Number(cells[0]), key: cells[1], down: ['1', 'true'].includes(cells[2].toLowerCase()), sequence: Number(cells[3]) };
  });
}

export function mapEvents(manifest, events = manifest.events) {
  validateTimeline(manifest);
  const { timeline, snapshot, viewerKind } = manifest;
  const mode = snapshot.selectedViewerTabs[viewerKind];
  const selected = snapshot.keys[mode];
  if (!Array.isArray(selected)) throw new Error('The selected viewer tab is missing.');
  const ordered = events.map((event, row) => {
    if (!Number.isSafeInteger(event.timeUs) || typeof event.down !== 'boolean' || !Number.isSafeInteger(event.sequence)) throw new Error('Malformed recorded input event.');
    return { ...event, row };
  }).sort((a, b) => a.timeUs - b.timeUs || a.sequence - b.sequence || a.row - b.row);
  const result = [];
  for (const event of ordered) {
    const matches = keyCandidates(event.key).filter(candidate => selected.includes(candidate));
    if (!matches.length) continue;
    let segment = timeline[0];
    for (const candidate of timeline) { if (candidate.replayTimeUs > event.timeUs) break; if (candidate.rate > 0) segment = candidate; }
    if (segment.rate === 0) throw new Error('An input cannot be mapped through a paused timeline without a running segment.');
    const outputTimeUs = segment.outputTimeUs + (event.timeUs - segment.replayTimeUs) / segment.rate;
    for (const key of matches) result.push({ outputTimeUs, replayTimeUs: event.timeUs, key, down: event.down, sequence: event.sequence });
  }
  return result.sort((a, b) => a.outputTimeUs - b.outputTimeUs || a.sequence - b.sequence);
}
