#!/usr/bin/env node
// Local helper for rendering the music: serves this folder on http://localhost:8765 and saves anything POSTed to
// /upload/<name> into <out dir>. Open /render.html in a browser, wait for DONE, then run encode.sh.
// Usage: node serve.js <out dir>
'use strict';
const http = require('http'), fs = require('fs'), path = require('path');

const outDir = path.resolve(process.argv[2] || 'out');
fs.mkdirSync(outDir, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript' };

http.createServer((req, res) => {
  if (req.method === 'POST' && req.url.startsWith('/upload/')) {
    const name = path.basename(decodeURIComponent(req.url.slice(8)));
    const chunks = [];
    req.on('data', c => chunks.push(c));
    req.on('end', () => { fs.writeFileSync(path.join(outDir, name), Buffer.concat(chunks)); console.log('saved', name); res.end('ok'); });
    return;
  }
  const file = path.join(__dirname, path.basename(req.url.split('?')[0]) || 'render.html');
  if (!fs.existsSync(file)) { res.statusCode = 404; return res.end(); }
  res.setHeader('Content-Type', types[path.extname(file)] || 'application/octet-stream');
  fs.createReadStream(file).pipe(res);
}).listen(8765, '127.0.0.1', () => console.log('http://localhost:8765/render.html → ' + outDir));
