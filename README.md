# fpb-aml-mapper

Bidirectional mapping between [FPB.JS](https://fpbjs.net) JSON and [AutomationML](https://www.automationml.org/) (CAEX 3.0) based on VDI 3682.

## Usage

### CLI

```bash
npm install

# FPB.JS JSON → AutomationML
node index.js to-aml input.json output.aml

# AutomationML → FPB.JS JSON
node index.js to-json input.aml output.json
```

### API Server

```bash
npm start
```

Starts an Express server on port 3000 with a web UI and two endpoints:

```
POST /api/to-aml   # JSON body → AML response
POST /api/to-json   # AML body  → JSON response
```

### Programmatic

```js
import { jsonToAml } from './src/json-to-aml.js';
import { amlToJson } from './src/aml-to-json.js';

const aml = jsonToAml(fpbJsonArray);
const json = amlToJson(amlXmlString);
```

## Structure

```
src/
├── mappings.js        # Type mappings (FPB ↔ AML)
├── json-to-aml.js     # FPB.JS JSON → CAEX 3.0
├── aml-to-json.js     # CAEX 3.0 → FPB.JS JSON
└── aml-libraries.js   # FPD library definitions
```

## License

MIT
