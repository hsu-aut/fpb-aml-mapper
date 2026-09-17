const fetch = require('node-fetch');
const express = require('express');
const path = require('path');

const app = express();

// Passenger sits behind nginx, which appends the visitor to X-Forwarded-For.
// Trusting that one hop makes req.ip the visitor, not the local proxy, so the
// rate limit below counts per visitor instead of once for everybody.
app.set('trust proxy', 1);
const PORT = process.env.PORT || 3000;

const DOTNET_API = process.env.DOTNET_API || 'http://localhost:5000';

// The Petri net mapper is a second Azure app that serves its own page and its
// own API. Everything under /pt is handed to it unchanged, so both languages
// live under one address and the paper can cite one URL.
const PT_APP = process.env.PT_APP || 'https://pt-aml-mapper.azurewebsites.net';
// Shared with the PT app, which then takes X-Client-Address as the visitor for
// its rate limit. Without it every visitor coming through here shares one limit.
const PT_PROXY_KEY = process.env.PT_PROXY_KEY || '';

// --- Rate limiting (in-memory, per IP) ---
const RATE_WINDOW_MS = 60 * 1000; // 1 minute
const RATE_MAX = 30;              // max requests per window
const MAX_BODY_BYTES = 2 * 1024 * 1024; // 2 MB
const PT_MAX_BODY_BYTES = 8 * 1024 * 1024; // the PT app's own limit

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

// --- Petri net mapper, proxied whole ---
//
// Unlike /api below this passes requests through as they are: the target app
// serves its page, its bundle and its API from one origin, so rewriting parts
// of it would only break the relative paths in its HTML.
app.use('/pt', async (req, res) => {
  // /pt without the slash has to redirect, or the browser resolves the page's
  // ./ptnjs.esm.js against the root. Handled here rather than as its own route
  // because Express treats /pt and /pt/ as the same path unless strict routing
  // is on, and a route for one would answer the other and loop.
  if (req.originalUrl === '/pt') return res.redirect(301, '/pt/');

  const target = PT_APP + (req.originalUrl.slice('/pt'.length) || '/');

  try {
    const chunks = [];
    let size = 0;
    for await (const chunk of req) {
      size += chunk.length;
      if (size > PT_MAX_BODY_BYTES) {
        return res.status(413).json({ error: 'Input too large (max 8 MB).' });
      }
      chunks.push(chunk);
    }

    const headers = {};
    if (req.headers['content-type']) headers['content-type'] = req.headers['content-type'];
    // The app behind this cannot see the visitor: the App Service front end
    // puts this server's address last in X-Forwarded-For. The key proves the
    // visitor address comes from here.
    headers['x-forwarded-proto'] = req.protocol;
    if (PT_PROXY_KEY) {
      headers['x-proxy-key'] = PT_PROXY_KEY;
      headers['x-client-address'] = req.ip;
    }

    const upstream = await fetch(target, {
      method: req.method,
      headers,
      body: req.method === 'GET' || req.method === 'HEAD' ? undefined : Buffer.concat(chunks),
      redirect: 'manual',
    });

    for (const name of ['content-type', 'cache-control', 'content-disposition',
                        'x-pt-info', 'x-pt-hierarchy', 'retry-after']) {
      const value = upstream.headers.get(name);
      if (value) res.set(name, value);
    }
    // A redirect must stay on this address, not send the visitor to the app's own host.
    const location = upstream.headers.get('location');
    if (location) res.set('location', location.startsWith(PT_APP) ? '/pt' + location.slice(PT_APP.length) : location);

    res.status(upstream.status);
    upstream.body.pipe(res);
  } catch (err) {
    res.status(502).json({ error: 'Petri net mapper unavailable: ' + err.message });
  }
});

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
