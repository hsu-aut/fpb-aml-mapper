const express = require('express');
const path = require('path');

const app = express();
const PORT = process.env.PORT || 3000;

const DOTNET_API = process.env.DOTNET_API || 'http://localhost:5000';

// Static frontend
app.use(express.static(path.join(__dirname, 'public')));

// Proxy /api/* to .NET backend (Aml.Engine)
app.post('/api/:direction', async (req, res) => {
  try {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const body = Buffer.concat(chunks);

    const resp = await fetch(`${DOTNET_API}/api/${req.params.direction}`, {
      method: 'POST',
      headers: { 'Content-Type': 'text/plain' },
      body,
    });

    const result = await resp.text();
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
