import { createHash } from 'node:crypto';

export function validateFrozenAsset(bytes, expectedHash) {
  const signature = bytes.subarray(0, 16).toString('ascii');
  const animated = signature.startsWith('GIF8') || (signature.startsWith('RIFF') && bytes.includes(Buffer.from('ANIM'))) || (bytes[0] === 137 && bytes.subarray(1, 4).toString('ascii') === 'PNG' && bytes.includes(Buffer.from('acTL'))) || (bytes.subarray(0, 256).toString('utf8').includes('<svg') && /<(?:animate|animateTransform|animateMotion|set)\b/i.test(bytes.toString('utf8')));
  if (animated) throw new Error('This preset has an animated image; exact export needs a render-aware image decoder.');
  const sha256 = createHash('sha256').update(bytes).digest('hex');
  if (expectedHash && expectedHash !== sha256) throw new Error('A frozen preset asset changed since snapshot capture.');
  return sha256;
}
