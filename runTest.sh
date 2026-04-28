killall dotnet \
& dotnet test WikiGraph.Tests/WikiGraph.Tests.csproj \
& dotnet run --project WikiGraph.Api/WikiGraph.Api.csproj  \
& dotnet run --project WikiGraph.Client/WikiGraph.Client.csproj \
