# WikiGraph

WikiGraph consists of an ASP.NET Core API and a separate Blazor WebAssembly client. The API turns a Wikipedia topic or URL into a saved session with messages, citations, and graph data. It uses SQLite for storage and can use Gemini when configured, but it also works with local fallback behavior.

## Run

```bash
dotnet run --project WikiGraph.Api/WikiGraph.Api.csproj
```

```bash
dotnet run --project WikiGraph.Client/WikiGraph.Client.csproj
```

The API runs on `http://localhost:5052`.
The client runs on `http://localhost:5024`.
For local usage details, see [USAGE.md](./USAGE.md).

When you open the backend host in a browser, the root route redirects to the hosted API reference at `http://localhost:5052/docs/v1`. The Blazor client is served separately from `http://localhost:5024/`.

## Endpoints

- `GET /` redirects to the hosted API reference
- `GET /docs/v1` returns the hosted Scalar API reference UI
- `GET /api/health` returns `{ status: "ok" }`
- `GET /api/sessions` returns `IReadOnlyList<SessionSummary>`
- `POST /api/sessions` returns `SessionSummary`
- `GET /api/sessions/{sessionId}` returns `SessionDetailDto`
- `GET /api/sessions/{sessionId}/graphs` returns `IReadOnlyList<GraphDto>`
- `POST /api/sessions/{sessionId}/articles` returns `SessionDetailDto`
- `GET /openapi/v1.json` returns the live OpenAPI document

## OpenAPI Generation

Build the API project to generate a checked output document:

```bash
dotnet build WikiGraph.Api/WikiGraph.Api.csproj
```

The generated document is written to `WikiGraph.Api/openapi/WikiGraph.Api.json`.

## UML Generation

Generate PlantUML class diagrams for the C# codebase:

```bash
./scripts/generate-uml.sh
```

This writes `.puml` outputs to `uml/` with `uml/include.puml` as the top-level diagram entry.
It renders each diagram to `.svg` by default, and to high-DPI `.png` only when `--png` is used.
See `USAGE.md` for details and extension options.

## API Reference UI

WikiGraph uses `Scalar.AspNetCore` to render a browser-based OpenAPI reference on top of the generated OpenAPI document. The UI is served from `/docs/v1`, and the backend root `/` redirects there so opening the backend in a browser lands on the docs page first.

## Main Files

- `WikiGraph.Api/Program.cs`
  - loads local `.env` values before configuration
  - enables CORS, OpenAPI routing, and the Scalar API reference UI
  - redirects `/` to `/docs/v1`
  - maps controller endpoints for the standalone API host
- `WikiGraph.Api/WikiGraph.Api.csproj`
  - references the Scalar API reference package
  - enables build-time OpenAPI document generation
- `WikiGraph.Api/Controllers/HealthController.cs`
  - returns a simple health status
  - gives a fast liveness check for the API
- `WikiGraph.Api/Controllers/SessionController.cs`
  - creates sessions
  - lists saved sessions
  - loads session details and graphs
  - accepts a topic or Wikipedia URL and saves the generated turn
- `WikiGraph.Api/Application/Services/WikiSessionService.cs`
  - resolves Wikipedia content
  - builds citations and graph data
  - saves the full turn to SQLite in one flow
- `WikiGraph.Api/Infrastructure/Wikipedia/WikipediaService.cs`
  - normalizes topic or URL input
  - fetches article summaries and related links
  - builds a compact article model for the rest of the API
- `WikiGraph.Api/Infrastructure/Persistence/SqliteSessionRepository.cs`
  - stores session metadata
  - stores messages, citations, and graphs
  - reloads full sessions for the UI
- `WikiGraph.Api/Infrastructure/Persistence/SessionMemoryDb.cs`
  - creates the SQLite tables
  - keeps schema setup in one place
  - seeds the default local user record
- `WikiGraph.Contracts/Models.cs`
  - defines shared request and response DTOs
  - keeps the API contract stable between projects
- `WikiGraph.Client`
  - contains the Blazor WebAssembly frontend that runs as a separate app and talks to the API
- `WikiGraph.Api/WikiGraph.Api.http`
  - provides sample calls for local testing
  - shows the real API routes and payload shapes