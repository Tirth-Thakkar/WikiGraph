# Usage

WikiGraph has two local entry points:

- Backend API: `WikiGraph.Api`
- Frontend client: `WikiGraph.Client`

## Run

```bash
dotnet run --project WikiGraph.Api/WikiGraph.Api.csproj
```

```bash
dotnet run --project WikiGraph.Client/WikiGraph.Client.csproj
```

Open `http://localhost:5052` for the hosted API reference.
Open `http://localhost:5024` for the WikiGraph client.

## Ports

- API: `http://localhost:5052`
- Client: `http://localhost:5024`

## OpenAPI

- Hosted API reference UI: `http://localhost:5052/docs/v1`
- Runtime document: `http://localhost:5052/openapi/v1.json`
- Build-time document: `dotnet build WikiGraph.Api/WikiGraph.Api.csproj`
- Generated file output: `WikiGraph.Api/openapi/WikiGraph.Api.json`

## Gemini

The API uses Gemini for Wikipedia lookup planning, embeddings, and user-facing replies only when `GEMINI_API_KEY` is configured.

```bash
export GEMINI_API_KEY="your-api-key"
dotnet run --project WikiGraph.Api/WikiGraph.Api.csproj
```

You can also put `GEMINI_API_KEY=your-api-key` in a local `.env` file at the repo root or API project path.

If `GEMINI_API_KEY` is not set, the backend still runs with deterministic local fallback responses and keyword retrieval. The API logs will say when fallback behavior is being used.

## Notes

- The backend stores data in SQLite.
- Opening the backend root redirects to the hosted API reference at `/docs/v1`.
- The Blazor client runs separately and calls the API at `http://localhost:5052/`.
- If you change local environment values, restart the backend.

run.sh will run the testing first and then run everything else. 

To use run.sh 
Set the file permission
```bash 
chmod +x run.sh
```
Then you can use this command. 
```bash 
./run.sh
```

Run tests:
```bash
dotnet test WikiGraph.Tests/WikiGraph.Tests.csproj
```

## UML Diagram Generation (TreeUML via PlantUML)

This repo includes an automated C# to PlantUML workflow for generating class diagrams across the codebase.

Generate UML text files and SVG renders:
```bash
./scripts/generate-uml.sh
```

Generate UML text files to a custom output folder:
```bash
./scripts/generate-uml.sh ./uml-output
```

Enable PNG image rendering (disabled by default):
```bash
./scripts/generate-uml.sh --png
```

Output:
- Main diagram entry file: `uml/include.puml`
- Per-file diagrams grouped by namespace/project under `uml/`
- One SVG image per `.puml` file
- Optional PNG image per `.puml` file when `--png` is used

Override PNG DPI when PNG export is enabled:
```bash
UML_PNG_DPI=600 ./scripts/generate-uml.sh --png
```

How it works:
- Uses the local .NET tool `PlantUmlClassDiagramGenerator` (`puml-gen`)
- Scans C# source and emits PlantUML (`.puml`) files
- Adds inheritance and association links automatically
- Excludes generated/build folders like `bin` and `obj`

Dependencies:
- .NET SDK 10.x
- Local .NET tools restored from `dotnet-tools.json`
- PlantUML CLI
- Graphviz (`dot`)

Install/restore UML generator dependency:
```bash
dotnet tool restore
```

Image rendering runtime requirements:
- Java Runtime (OpenJDK 17+)

Ubuntu/Debian install example:
```bash
sudo apt update
sudo apt install -y openjdk-17-jre plantuml graphviz
```

Optional VS Code extensions:
- `pierre3.csharp-to-plantuml` for direct C# to PlantUML support
- `jebbs.plantuml` to preview and render `.puml` diagrams

If a PlantUML renderer is configured, open `uml/include.puml` to visualize the full model graph.
