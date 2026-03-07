# fpb-aml-mapper

Bidirectional mapping between [FPB.js](https://fpbjs.net) JSON and [AutomationML](https://www.automationml.org/) (CAEX 3.0) based on VDI 3682.

The conversion uses the official [Aml.Engine](https://www.nuget.org/packages/Aml.Engine) SDK for CAEX-conformant output: proper library generation (RCL, SUCL, ICL, ATL), class instantiation via `CreateClassInstance()`, and automatic `RoleRequirements` on all `InternalElement` nodes.

## Live

**[aml.fpbjs.net](https://aml.fpbjs.net)** — Web UI for drag & drop conversion.

## Architecture

```
Browser (aml.fpbjs.net)
  └── Node.js proxy (server.js, Plesk)
        └── POST /api/to-aml | /api/to-json
              └── .NET 8 API (Azure App Service)
                    └── Aml.Engine SDK
```

## .NET Backend

The `dotnet/` folder contains the ASP.NET Core web API with the Aml.Engine-based conversion.

### Build & Run locally

```bash
cd dotnet
dotnet run --project FpbMapper.Web
```

### Deploy to Azure

```bash
cd dotnet
dotnet publish FpbMapper.Web -c Release -o publish
cd publish && zip -r ../deploy.zip . && cd ..
az webapp deploy --resource-group <rg> --name <app> --src-path deploy.zip --type zip
```

### API Endpoints

| Endpoint | Input | Output |
|----------|-------|--------|
| `POST /api/to-aml` | FPB.js JSON (`text/plain`) | AutomationML (`application/xml`) |
| `POST /api/to-json` | AutomationML (`text/plain`) | FPB.js JSON (`application/json`) |

## Node.js Proxy

`server.js` is a lightweight Express proxy that serves the web UI and forwards `/api/*` requests to the .NET backend. Requires the `DOTNET_API` environment variable.

```bash
DOTNET_API=https://your-backend-url npm start
```

## Structure

```
server.js                    # Node.js proxy (Plesk)
dotnet/
├── FpbMapper.sln
├── FpbMapper.Conversion/    # Conversion logic (Aml.Engine)
│   ├── FpbJsonToCaex.cs     # JSON -> CAEX 3.0
│   ├── CaexToFpbJson.cs     # CAEX 3.0 -> JSON
│   ├── FpdLibraries.cs      # FPD library definitions
│   └── FpbMappings.cs       # Type mappings
└── FpbMapper.Web/           # ASP.NET Core API
```

## License

MIT
