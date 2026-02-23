const express = require('express');
const path = require('path');
const { jsonToAml } = require('./src/json-to-aml.js');
const { amlToJson } = require('./src/aml-to-json.js');

const app = express();
const PORT = process.env.PORT || 3000;

// CORS - allow cross-origin requests (e.g. fpbjs.net → aml.fpbjs.net)
app.use((req, res, next) => {
  res.header('Access-Control-Allow-Origin', '*');
  res.header('Access-Control-Allow-Headers', 'Content-Type');
  if (req.method === 'OPTIONS') return res.sendStatus(204);
  next();
});

// Parse text bodies up to 10MB
app.use(express.text({ type: '*/*', limit: '10mb' }));

// Static frontend
app.use(express.static(path.join(__dirname, 'public')));

// ── API Endpoints ────────────────────────────────────────────────────────

app.post('/api/to-aml', (req, res) => {
  try {
    const json = JSON.parse(req.body);
    const aml = jsonToAml(json);
    res.type('application/xml').send(aml);
  } catch (err) {
    res.status(400).json({ error: err.message });
  }
});

app.post('/api/to-json', (req, res) => {
  try {
    const json = amlToJson(req.body);
    res.type('application/json').send(JSON.stringify(json, null, 4));
  } catch (err) {
    res.status(400).json({ error: err.message });
  }
});

// ── Start ────────────────────────────────────────────────────────────────

app.listen(PORT, () => {
  console.log(`FPB-AML Mapper running at http://localhost:${PORT}`);
});
