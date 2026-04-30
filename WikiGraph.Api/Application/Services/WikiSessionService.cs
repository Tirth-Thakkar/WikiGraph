using WikiGraph.Api.Application.Models;
using WikiGraph.Api.Infrastructure.Persistence;
using WikiGraph.Api.Infrastructure.Wikipedia;
using WikiGraph.Contracts;

namespace WikiGraph.Api.Application.Services;

public sealed class WikiSessionService
{
    private readonly WikipediaService _wikipediaService;
    private readonly GeminiService _geminiService;
    private readonly SqliteSessionRepository _sessionRepository;
    private readonly SqliteVectorStore _vectorStore;

    // Wires the Wikipedia, reply, repository, and vector store services together.
    public WikiSessionService(
        WikipediaService wikipediaService,
        GeminiService geminiService,
        SqliteSessionRepository sessionRepository,
        SqliteVectorStore vectorStore)
    {
        _wikipediaService = wikipediaService;
        _geminiService = geminiService;
        _sessionRepository = sessionRepository;
        _vectorStore = vectorStore;
    }

    // Adds an article to a session and returns the updated session snapshot.
    public async Task<SessionDetailDto> AddArticleAsync(
        string sessionId,
        AddWikiArticleRequest request,
        CancellationToken cancellationToken = default)
    {
        var topic = TextTools.Clean(request.Topic);
        var wikipediaUrl = TextTools.Clean(request.WikipediaUrl);
        // Get the session's messages if the session exists; otherwise, use an empty collection.
        // Funky operators, huh, null conditional operator if not null -> messages + null coalesce operator (??) uses right value if null 
        var sessionHistory = _sessionRepository.GetSession(sessionId)?.Messages ?? [];
        var lookupPlan = await _geminiService.PlanWikipediaLookupAsync(topic, wikipediaUrl, sessionHistory, cancellationToken);
        var article = await _wikipediaService.GetArticleAsync(topic, wikipediaUrl, lookupPlan, cancellationToken);
        var prompt = string.IsNullOrWhiteSpace(topic) ? article.Title : topic;
        var nowUtc = DateTime.UtcNow;

        _sessionRepository.EnsureSession(sessionId, InferTitle(prompt), nowUtc);
        await _vectorStore.UpsertArticleAsync(sessionId, article, cancellationToken);

        var matches = await _vectorStore.SearchAsync(sessionId, BuildSearchText(prompt, sessionHistory), 4, cancellationToken);
        var reply = await _geminiService.GenerateReplyAsync(prompt, article, sessionHistory, matches, cancellationToken);
        var citations = BuildCitations(article, matches);
        var graphs = BuildGraphs(prompt, article, reply.RelatedTopics, matches);

        _sessionRepository.SaveTurn(
            sessionId,
            InferTitle(prompt),
            new MessageDto("user", BuildUserMessage(topic, wikipediaUrl, article.Title), nowUtc),
            new MessageDto("assistant", reply.Answer, nowUtc),
            citations,
            graphs);

        return _sessionRepository.GetSession(sessionId)!;
    }

    // Combines the prompt with recent user messages for retrieval.
    private static string BuildSearchText(string prompt, IReadOnlyList<MessageDto> messages)
    {
        var recentUserMessages = messages
            .Where(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            .TakeLast(2)
            .Select(message => message.Content)
            .ToArray();

        return recentUserMessages.Length == 0
            ? prompt
            : $"{prompt} {string.Join(' ', recentUserMessages)}";
    }

    // Formats the user's input so the saved message still shows URL context.
    private static string BuildUserMessage(string topic, string wikipediaUrl, string articleTitle)
    {
        if (string.IsNullOrWhiteSpace(wikipediaUrl))
        {
            return string.IsNullOrWhiteSpace(topic) ? articleTitle : topic;
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            return $"Wikipedia URL: {wikipediaUrl}";
        }

        return $"{topic}{Environment.NewLine}Wikipedia URL: {wikipediaUrl}";
    }

    // Converts retrieved matches into citation DTOs.
    private static IReadOnlyList<CitationDto> BuildCitations(WikiArticle article, IReadOnlyList<WikiMatch> matches)
    {
        var citations = matches.Take(3)
            .Select(match =>
            {
                var isOverview = match.Section.Equals("Overview", StringComparison.OrdinalIgnoreCase);
                var section = article.Sections.FirstOrDefault(section =>
                    section.Heading.Equals(match.Section, StringComparison.OrdinalIgnoreCase));

                return new CitationDto(
                    isOverview ? article.Title : $"{article.Title} {match.Section}",
                    isOverview ? article.SourceUrl : BuildCitationUrl(article.SourceUrl, section?.Anchor),
                    match.Section,
                    match.ChunkId);
            })
            .Distinct()
            .ToArray();

        if (citations.Length > 0)
        {
            return citations;
        }

        return
        [
            new CitationDto(article.Title, article.SourceUrl, "Overview", null)
        ];
    }

    // Builds a Wikipedia section link only when the API supplied a real anchor for that section.
    private static string BuildCitationUrl(string sourceUrl, string? anchor)
    {
        var fragmentIndex = sourceUrl.IndexOf('#', StringComparison.Ordinal);
        var baseUrl = fragmentIndex < 0 ? sourceUrl : sourceUrl[..fragmentIndex];
        var fragment = TextTools.Clean(anchor);
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return baseUrl;
        }

        return $"{baseUrl}#{fragment}";
    }

    // Builds a small topic graph around the article and related context.
    private static IReadOnlyList<GraphDto> BuildGraphs(
        string prompt,
        WikiArticle article,
        IReadOnlyList<string> relatedTopics,
        IReadOnlyList<WikiMatch> matches)
    {
        var topic = string.IsNullOrWhiteSpace(prompt) ? article.Title : prompt;
        // First ring: the main related topics directly connected to the requested/base topic.
        var primaryTopics = new[] { article.Title }
            .Concat(relatedTopics)
            .Concat(article.RelatedTopicDetails.Select(item => item.Title))
            .Concat(article.RelatedArticles)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(TextTools.Clean)
            .Where(label => !string.Equals(label, topic, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        var supportingTopics = BuildSupportingTopics(article, matches);

        var nodes = new List<GraphNodeDto> { new("topic", topic, 5) };
        var edges = new List<GraphEdgeDto>();

        for (var index = 0; index < primaryTopics.Length; index++)
        {
            var primaryTopic = primaryTopics[index];
            var primaryId = $"topic-{index + 1}";
            nodes.Add(new GraphNodeDto(primaryId, primaryTopic, Math.Max(3, 4 - Math.Min(index, 1))));
            edges.Add(new GraphEdgeDto("topic", primaryId, "related"));

            var detailLabels = supportingTopics
                .Where(item => !string.Equals(item, primaryTopic, StringComparison.OrdinalIgnoreCase))
                .Where(item => !string.Equals(item, topic, StringComparison.OrdinalIgnoreCase))
                .Skip(index)
                .Take(2)
                .ToArray();

            for (var detailIndex = 0; detailIndex < detailLabels.Length; detailIndex++)
            {
                var detailLabel = detailLabels[detailIndex];
                var detailId = $"{primaryId}-detail-{detailIndex + 1}";
                if (nodes.Any(node => string.Equals(node.Label, detailLabel, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                nodes.Add(new GraphNodeDto(detailId, detailLabel, 2));
                edges.Add(new GraphEdgeDto(primaryId, detailId, "supports"));
            }
        }

        if (nodes.Count == 1)
        {
            nodes.Add(new GraphNodeDto("topic-1", article.Title, 4));
            edges.Add(new GraphEdgeDto("topic", "topic-1", "related"));
        }

        return [new GraphDto(topic, nodes, edges)];
    }

    // Collects short labels that can hang off the main graph topics.
    private static IReadOnlyList<string> BuildSupportingTopics(WikiArticle article, IReadOnlyList<WikiMatch> matches)
    {
        // Second ring: short supporting labels derived from related-topic summaries and retrieved section names.
        return article.RelatedTopicDetails
            .SelectMany(item => new[] { item.Summary, item.Title })
            .Concat(matches.Select(match => match.Section))
            .Concat(article.Sections.Select(section => section.Heading))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => SplitGraphLabels(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    // Splits longer text into a few graph-friendly labels.
    private static IEnumerable<string> SplitGraphLabels(string value)
    {
        var cleaned = TextTools.Clean(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return [];
        }

        var labels = cleaned
            .Split([",", ";", ".", ":"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TextTools.Clean)
            .Where(label => label.Length > 2)
            .Take(3)
            .ToArray();

        return labels.Length == 0 ? [cleaned] : labels;
    }

    // Derives a short session title from the prompt text.
    private static string InferTitle(string prompt)
    {
        var title = prompt
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(5);

        return string.Join(' ', title) is { Length: > 0 } value ? value : "New session";
    }
}
