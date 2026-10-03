import assert from 'node:assert/strict';
import http from 'node:http';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { captureSnapshot } from './snapshot.mjs';

export async function verifySnapshotCapture(directory) {
  const secret = 'local-verification-session', fontPath = '/mock/fonts/custom.woff2', font = Buffer.from('frozen-font-bytes');
  let authenticated = false, assetAuthenticated = false;
  const sockets = new Set();
  const server = http.createServer((request, response) => {
    const url = new URL(request.url, 'http://localhost');
    assetAuthenticated = url.searchParams.get('token') === secret && Buffer.from(url.pathname.split('/').at(-1), 'base64url').toString() === fontPath;
    response.writeHead(assetAuthenticated ? 200 : 403, { 'content-type': 'font/woff2' }); response.end(font);
  });
  const send = (socket, payload) => {
    const body = Buffer.from(JSON.stringify(payload));
    const header = body.length < 126 ? Buffer.from([0x81, body.length]) : Buffer.from([0x81, 126, body.length >> 8, body.length & 255]);
    socket.write(Buffer.concat([header, body]));
  };
  server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
  server.on('upgrade', (request, socket) => {
    const accept = createHash('sha1').update(request.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    socket.once('data', data => {
      const length = (data[1] & 127) === 126 ? data.readUInt16BE(2) : data[1] & 127;
      const offset = (data[1] & 127) === 126 ? 4 : 2, mask = data.subarray(offset, offset + 4), body = data.subarray(offset + 4, offset + 4 + length);
      const decoded = Buffer.from(body.map((value, index) => value ^ mask[index % 4]));
      authenticated = JSON.parse(decoded.toString()).payload.token === secret;
      assert(authenticated);
      send(socket, { v: 1, type: 'hello_ack', payload: { serverVersion: '0.1.0' } });
      send(socket, { v: 1, type: 'snapshot', payload: { settings: { fontSettings: { customFonts: [{ localPath: fontPath }] } }, defaults: {}, keys: {} } });
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    const output = join(directory, 'frozen.json');
    const result = await captureSnapshot({ url: `ws://127.0.0.1:${server.address().port}`, token: secret, output });
    const raw = await readFile(output, 'utf8'), bundle = JSON.parse(raw);
    assert.equal(result.assetCount, 1); assert(authenticated && assetAuthenticated);
    assert(!raw.includes(secret), 'OBS session credentials must not be saved in the bundle.');
    assert.deepEqual(await readFile(join(directory, bundle.assets[fontPath].file)), font);
    assert.equal(bundle.assets[fontPath].sha256, createHash('sha256').update(font).digest('hex'));
  } finally { for (const socket of sockets) socket.destroy(); await new Promise(resolve => server.close(resolve)); }
}
