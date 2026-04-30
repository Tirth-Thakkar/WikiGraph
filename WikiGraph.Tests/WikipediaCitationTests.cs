using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WikiGraph.Api.Application.Models;
using WikiGraph.Api.Application.Services;
using WikiGraph.Api.Configuration;
using WikiGraph.Api.Infrastructure.Persistence;
using WikiGraph.Api.Infrastructure.Wikipedia;
using WikiGraph.Contracts;
using Xunit;

namespace WikiGraph.Tests;

public sealed class WikipediaCitationTests
{
    [Fact]
    public async Task AddArticleAsync_LoadsPromptRelevantSectionsBeyondTheArticleOpening()
    {
        var handler = new QueuedWikipediaHandler();
        handler.Enqueue("list=search", """
            {
              "query": {
                "search": [
                  {
                    "title": "McDonald's",
                    "snippet": "The article includes material about a McDonalds cups scandal.",
                    "sectiontitle": "Controversies",
                    "sectionsnippet": "A cup scandal and other controversies are discussed."
                  }
                ]
              }
            }
            """);
        EnqueueMcdonaldsArticle(handler);

        using var database = new TempSqliteConnectionFactory();
        var sessionService = BuildSessionService(handler, database);

        var session = await sessionService.AddArticleAsync(
            "prompt-section-test",
            new AddWikiArticleRequest("McDonalds Cups Scanadal", null));

        Assert.Contains(session.Citations, citation => citation.Section == "Controversies");
        Assert.Equal(0, handler.PendingResponses);
    }

    [Fact]
    public async Task AddArticleAsync_WhenUrlIsProvided_KeepsDiscussionCenteredOnThatLink()
    {
        var handler = new QueuedWikipediaHandler();
        handler.Enqueue("list=search", """
            {
              "query": {
                "search": [
                  {
                    "title": "McDonald's",
                    "sectiontitle": "Controversies",
                    "sectionsnippet": "The article section discusses cup scandal claims."
                  }
                ]
              }
            }
            """);
        EnqueueMcdonaldsArticle(handler, firstExpectedQueryPart: "titles=McDonald%27s");

        using var database = new TempSqliteConnectionFactory();
        var sessionService = BuildSessionService(handler, database);

        var session = await sessionService.AddArticleAsync(
            "linked-article-test",
            new AddWikiArticleRequest(
                "Tell me about the cups scandal",
                "https://en.wikipedia.org/wiki/McDonald%27s"));

        Assert.Contains(session.Citations, citation =>
            citation.Section == "Controversies" &&
            citation.Url.StartsWith("https://en.wikipedia.org/wiki/McDonald%27s#", StringComparison.Ordinal));
        Assert.Equal(0, handler.PendingResponses);
    }

    [Fact]
    public async Task GetArticleAsync_SearchesOriginalPromptWhenAiLookupPlanDrifts()
    {
        var handler = new QueuedWikipediaHandler();
        handler.Enqueue("srsearch=Starbucks%20holiday%20cups", """
            {
              "query": {
                "search": [
                  {
                    "title": "Starbucks",
                    "snippet": "Starbucks holiday cups have sometimes been controversial."
                  }
                ]
              }
            }
            """);
        handler.Enqueue("srsearch=McDonalds%20Cups%20Scanadal", """
            {
              "query": {
                "search": [
                  {
                    "title": "McDonald's",
                    "snippet": "McDonalds cups scandal discussion appears in the article.",
                    "sectiontitle": "Controversies",
                    "sectionsnippet": "Cup scandal claims and public criticism."
                  }
                ]
              }
            }
            """);
        EnqueueMcdonaldsArticle(handler);

        var wikipediaService = new WikipediaService(
            new HttpClient(handler) { BaseAddress = new Uri("https://en.wikipedia.org/w/api.php") },
            NullLogger<WikipediaService>.Instance);

        var article = await wikipediaService.GetArticleAsync(
            "McDonalds Cups Scanadal",
            null,
            new WikiLookupPlan("Starbucks holiday cups", ["McDonalds Cups Scanadal"]));

        Assert.Equal("McDonald's", article.Title);
        Assert.Equal(0, handler.PendingResponses);
    }

    [Fact]
    public void BuildCitations_DoesNotInventFragmentForGeneratedSections()
    {
        var article = new WikiArticle("Example", "https://en.wikipedia.org/wiki/Example");
        article.Sections.Add(new WikiSection("Related Topic: Cups", "Generated supporting context without a Wikipedia anchor."));
        var matches = new[]
        {
            new WikiMatch("chunk-1", "Related Topic: Cups", "Example generated context", article.SourceUrl, 3d)
        };

        var method = typeof(WikiSessionService).GetMethod("BuildCitations", BindingFlags.NonPublic | BindingFlags.Static);
        var citations = Assert.IsAssignableFrom<IReadOnlyList<CitationDto>>(method!.Invoke(null, [article, matches]));

        var citation = Assert.Single(citations);
        Assert.Equal(article.SourceUrl, citation.Url);
    }

    private static WikiSessionService BuildSessionService(QueuedWikipediaHandler handler, TempSqliteConnectionFactory database)
    {
        var memoryDb = new SessionMemoryDb(database);
        var geminiOptions = Options.Create(new GeminiOptions());
        var geminiService = new GeminiService(geminiOptions, NullLogger<GeminiService>.Instance);
        var wikipediaService = new WikipediaService(
            new HttpClient(handler) { BaseAddress = new Uri("https://en.wikipedia.org/w/api.php") },
            NullLogger<WikipediaService>.Instance);

        return new WikiSessionService(
            wikipediaService,
            geminiService,
            new SqliteSessionRepository(database, memoryDb),
            new SqliteVectorStore(database, memoryDb, geminiService, NullLogger<SqliteVectorStore>.Instance));
    }

    private static void EnqueueMcdonaldsArticle(
        QueuedWikipediaHandler handler,
        string firstExpectedQueryPart = "prop=extracts%7Cinfo%7Clinks")
    {
        handler.Enqueue(firstExpectedQueryPart, """
            {
              "query": {
                "pages": [
                  {
                    "pageid": 2,
                    "title": "McDonald's",
                    "fullurl": "https://en.wikipedia.org/wiki/McDonald%27s",
                    "extract": "McDonald's is an American fast-food restaurant chain.",
                    "links": []
                  }
                ]
              }
            }
            """);
        handler.Enqueue("prop=sections", """
            {
              "parse": {
                "title": "McDonald's",
                "sections": [
                  { "level": "2", "line": "History", "index": "1", "anchor": "History" },
                  { "level": "2", "line": "Corporate overview", "index": "2", "anchor": "Corporate_overview" },
                  { "level": "2", "line": "Products", "index": "3", "anchor": "Products" },
                  { "level": "2", "line": "Advertising", "index": "4", "anchor": "Advertising" },
                  { "level": "2", "line": "Restaurants", "index": "5", "anchor": "Restaurants" },
                  { "level": "2", "line": "Business model", "index": "6", "anchor": "Business_model" },
                  { "level": "2", "line": "International operations", "index": "7", "anchor": "International_operations" },
                  { "level": "2", "line": "Controversies", "index": "8", "anchor": "Controversies" }
                ]
              }
            }
            """);
        handler.Enqueue("section=1", SectionJson("McDonald's history covers the growth of the restaurant chain."));
        handler.Enqueue("section=2", SectionJson("The corporate overview describes franchise operations and company structure."));
        handler.Enqueue("section=3", SectionJson("Products include hamburgers, fries, drinks, and desserts sold by McDonald's."));
        handler.Enqueue("section=4", SectionJson("Advertising describes campaigns, mascots, and promotional strategy."));
        handler.Enqueue("section=5", SectionJson("Restaurants describes store formats, service counters, and drive-through locations."));
        handler.Enqueue("section=6", SectionJson("The business model section describes franchising and revenue."));
        handler.Enqueue("section=7", SectionJson("International operations describes locations outside the United States."));
        handler.Enqueue("section=8", SectionJson("The controversies section discusses a McDonalds cups scandal, cup hoax claims, and public criticism."));
    }

    private static string SectionJson(string text)
    {
        return $$"""
            {
              "parse": {
                "text": "<div class=\"mw-parser-output\"><p>{{text}}</p></div>"
              }
            }
            """;
    }

    private sealed class QueuedWikipediaHandler : HttpMessageHandler
    {
        private readonly Queue<(string ExpectedQueryPart, string Json)> _responses = [];

        public int PendingResponses => _responses.Count;

        public void Enqueue(string expectedQueryPart, string json)
        {
            _responses.Enqueue((expectedQueryPart, json));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException($"Unexpected Wikipedia API request: {request.RequestUri}");
            }

            var query = request.RequestUri?.Query ?? string.Empty;
            Assert.Contains(response.ExpectedQueryPart, query);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.Json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TempSqliteConnectionFactory : ISqliteConnectionFactory, IDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"wikigraph-test-{Guid.NewGuid():N}.db");

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();

            return connection;
        }

        public void Dispose()
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch
            {
                // Test cleanup should not hide the assertion that actually failed.
            }
        }
    }
}
