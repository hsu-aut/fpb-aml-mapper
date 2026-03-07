const fetch = require('node-fetch');
const express = require('express');
const path = require('path');

const app = express();
const PORT = process.env.PORT || 3000;

const DOTNET_API = process.env.DOTNET_API || 'http://localhost:5000';

// --- Rate limiting (in-memory, per IP) ---
const RATE_WINDOW_MS = 60 * 1000; // 1 minute
const RATE_MAX = 30;              // max requests per window
const MAX_BODY_BYTES = 2 * 1024 * 1024; // 2 MB

const hits = new Map(); // IP -> [timestamps]

function rateLimit(req, res, next) {
  const ip = req.ip;
  const now = Date.now();
  const window = hits.get(ip) || [];
  const recent = window.filter(t => now - t < RATE_WINDOW_MS);
  if (recent.length >= RATE_MAX) {
    return res.status(429).json({ error: 'Too many requests. Please wait a moment.' });
  }
  recent.push(now);
  hits.set(ip, recent);
  next();
}

// Clean up stale entries every 5 minutes
setInterval(() => {
  const now = Date.now();
  for (const [ip, times] of hits) {
    const recent = times.filter(t => now - t < RATE_WINDOW_MS);
    if (recent.length === 0) hits.delete(ip);
    else hits.set(ip, recent);
  }
}, 5 * 60 * 1000).unref();

// Static frontend
app.use(express.static(path.join(__dirname, 'public')));

// Proxy /api/* to .NET backend (Aml.Engine)
app.post('/api/:direction', rateLimit, async (req, res) => {
  if (req.params.direction !== 'to-aml' && req.params.direction !== 'to-json') {
    return res.status(400).json({ error: 'Invalid direction. Use to-aml or to-json.' });
  }
  try {
    const chunks = [];
    let size = 0;
    for await (const chunk of req) {
      size += chunk.length;
      if (size > MAX_BODY_BYTES) {
        return res.status(413).json({ error: 'Input too large (max 2 MB).' });
      }
      chunks.push(chunk);
    }
    const body = Buffer.concat(chunks);

    const resp = await fetch(`${DOTNET_API}/api/${req.params.direction}`, {
      method: 'POST',
      headers: { 'Content-Type': 'text/plain' },
      body,
    });

    const result = await resp.text();
    const warnings = resp.headers.get('x-conversion-warnings');
    if (warnings) res.set('X-Conversion-Warnings', warnings);
    res.status(resp.status)
       .type(resp.headers.get('content-type'))
       .send(result);
  } catch (err) {
    res.status(502).json({ error: 'Conversion backend unavailable: ' + err.message });
  }
});

app.listen(PORT, () => {
  console.log(`FPB-AML Mapper (proxy) running at http://localhost:${PORT}`);
  console.log(`Backend: ${DOTNET_API}`);
});
