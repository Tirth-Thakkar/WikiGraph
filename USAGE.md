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