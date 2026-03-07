# fpb-aml-mapper

Bidirectional mapping between [FPB.JS](https://fpbjs.net) JSON and [AutomationML](https://www.automationml.org/) (CAEX 3.0) based on VDI 3682.

Two implementations are included:

- **JavaScript** (standalone CLI + library) in `src/`
- **.NET / Aml.Engine** (web API) in `dotnet/`

The JS version uses fast-xml-parser for XML handling. The .NET version uses the official Aml.Engine SDK, which produces cleaner CAEX output and handles edge cases (library generation, GUID normalization) more robustly.

## JavaScript

### CLI

```bash
npm install

# FPB.JS JSON -> AutomationML
node index.js to-aml input.json output.aml

# AutomationML -> FPB.JS JSON
node index.js to-json input.aml output.json
```

### Programmatic

```js
import { jsonToAml } from './src/json-to-aml.js';
import { amlToJson } from './src/aml-to-json.js';

const aml = jsonToAml(fpbJsonArray);
const json = amlToJson(amlXmlString);
```

## .NET Backend

The `dotnet/` folder contains an ASP.NET Core web API that wraps the Aml.Engine-based conversion.

### Build & Run

```bash
cd dotnet
dotnet run --project FpbMapper.Web
```

Starts on `http://localhost:5000` by default.

### Endpoints

```
POST /api/to-aml   # JSON body (text/plain) -> AML response (application/xml)
POST /api/to-json   # AML body (text/plain)  -> JSON response (application/json)
```

### Deploy

```bash
cd dotnet
dotnet publish FpbMapper.Web -c Release -o publish
```

The `publish/` folder can be deployed to any host that supports .NET 8 (Azure App Service, Linux VM, Docker, etc.).

## Proxy Server (Optional)

`server.js` is a lightweight Express proxy that serves a web UI on port 3000 and forwards `/api/*` requests to the .NET backend.

```bash
# Point to your .NET backend
export DOTNET_API=http://localhost:5000

npm start
```

Useful if you want a single origin for frontend + API, e.g. behind a webhoster that runs Node.js.

## CORS

The .NET backend allows requests from `*.fpbjs.net` and `localhost`. If you self-host, adjust the CORS policy in `dotnet/FpbMapper.Web/Program.cs`.

## Structure

```
src/
├── mappings.js          # Type mappings (FPB <-> AML)
├── json-to-aml.js       # FPB.JS JSON -> CAEX 3.0
├── aml-to-json.js       # CAEX 3.0 -> FPB.JS JSON
└── aml-libraries.js     # FPD library definitions

dotnet/
├── FpbMapper.sln
├── FpbMapper.Conversion/ # Conversion logic (Aml.Engine)
└── FpbMapper.Web/        # ASP.NET Core API wrapper
```

## License

MIT
