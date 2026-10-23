// Serves the built web app and forwards API traffic to the gateway, so one URL runs everything.
// Usage: node deploy/serve-all.mjs   (then tunnel http://localhost:8080)
import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src/Web/digital-banking-web/dist/digital-banking-web/browser');
const port = Number(process.env.PORT ?? 8080);
const gateway = { host: '127.0.0.1', port: 5000 };
const apiPrefixes = ['/api/', '/hubs/', '/sim/', '/uploads/'];
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ico': 'image/x-icon', '.png': 'image/png', '.svg': 'image/svg+xml', '.json': 'application/json', '.woff2': 'font/woff2' };

const isApi = (url) => apiPrefixes.some((p) => url.startsWith(p));

const server = http.createServer((req, res) => {
  if (isApi(req.url)) {
    const upstream = http.request({ ...gateway, path: req.url, method: req.method, headers: req.headers }, (up) => {
      res.writeHead(up.statusCode, up.headers);
      up.pipe(res);
    });
    upstream.on('error', () => { res.writeHead(502); res.end('Gateway not running'); });
    req.pipe(upstream);
    return;
  }

  const file = path.join(root, decodeURIComponent(req.url.split('?')[0]));
  const target = file.startsWith(root) && fs.existsSync(file) && fs.statSync(file).isFile() ? file : path.join(root, 'index.html');
  res.writeHead(200, { 'Content-Type': types[path.extname(target)] ?? 'application/octet-stream' });
  fs.createReadStream(target).pipe(res);
});

// WebSockets (chat/SignalR) go straight through to the gateway.
server.on('upgrade', (req, socket, head) => {
  const upstream = net.connect(gateway.port, gateway.host, () => {
    const headers = Object.entries(req.headers).map(([k, v]) => `${k}: ${v}`).join('\r\n');
    upstream.write(`${req.method} ${req.url} HTTP/1.1\r\n${headers}\r\n\r\n`);
    upstream.write(head);
    socket.pipe(upstream).pipe(socket);
  });
  upstream.on('error', () => socket.destroy());
  socket.on('error', () => upstream.destroy());
});

server.listen(port, () => console.log(`Web + API on http://localhost:${port}`));
