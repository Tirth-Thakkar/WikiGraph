# WikiGraph API

WikiGraph is a simple ASP.NET Core API that turns a Wikipedia topic or URL into a saved session with messages, citations, and graph data. It uses SQLite for storage and can use Gemini when configured, but it also works with local fallback behavior.

## Run

```bash
dotnet run --project WikiGraph.Api/WikiGraph.Api.csproj
```

The API runs on `http://localhost:5052`.
For the frontend and backend together, see [USAGE.md](./USAGE.md).

## Endpoints

- `GET /api/health` returns `{ status: "ok" }`
- `GET /api/sessions` returns `IReadOnlyList<SessionSummary>`
- `POST /api/sessions` returns `SessionSummary`
- `GET /api/sessions/{sessionId}` returns `SessionDetailDto`
- `GET /api/sessions/{sessionId}/graphs` returns `IReadOnlyList<GraphDto>`
- `POST /api/sessions/{sessionId}/articles` returns `SessionDetailDto`

## Main Files

- `WikiGraph.Api/Program.cs`
  - loads local `.env` values before configuration
  - enables CORS for the API
  - maps controllers to OpenAPI for deployment config
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
- `WikiGraph.Api/WikiGraph.Api.http`
  - provides sample calls for local testing
  - shows the real API routes and payload shapes


Testing 34