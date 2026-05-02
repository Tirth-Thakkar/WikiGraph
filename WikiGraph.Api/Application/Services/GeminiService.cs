using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using WikiGraph.Api.Application.Models;
using WikiGraph.Api.Configuration;
using WikiGraph.Contracts;

namespace WikiGraph.Api.Application.Services;

public sealed class GeminiService
{
    private const string ChatServiceId = "wikigraph-chat";
    private const string EmbeddingServiceId = "wikigraph-embeddings";

    private readonly IChatCompletionService? _chatCompletionService;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiService> _logger;

    // Creates a service instance with Gemini settings but without optional SK integrations.
    public GeminiService(IOptions<GeminiOptions> options, ILogger<GeminiService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // Creates a service instance and resolves optional chat/embedding services from DI.
    public GeminiService(
        IServiceProvider serviceProvider,
        IOptions<GeminiOptions> options,
        ILogger<GeminiService> logger)
        : this(options, logger)
    {
        _chatCompletionService =
            serviceProvider.GetService<IChatCompletionService>() ??
            serviceProvider.GetKeyedService<IChatCompletionService>(ChatServiceId);
        _embeddingGenerator =
            serviceProvider.GetService<IEmbeddingGenerator<string, Embedding<float>>>() ??
            serviceProvider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(EmbeddingServiceId);
    }

    // Generates the answer and related topic labels, falling back to local text if Gemini is unavailable.
    public async Task<GeminiReply> GenerateReplyAsync(
        string prompt,
        WikiArticle article,
        IReadOnlyList<MessageDto> sessionHistory,
        IReadOnlyList<WikiMatch> matches,
        CancellationToken cancellationToken = default)
    {
        // Keep the AI path simple: one Gemini call returns the answer text and graph labels together.
        var fallback = BuildFallbackReply(prompt, article, sessionHistory, matches);
        if (!_options.IsEnabled)
        {
            _logger.LogInformation("Gemini reply generation is disabled because GEMINI_API_KEY is not configured; using local fallback text.");
            return fallback;
        }

        try
        {
            if (_chatCompletionService is null)
            {
                _logger.LogWarning("Gemini reply generation is enabled, but no chat completion service was registered; using local fallback text.");
                return fallback;
            }

            var completions = await _chatCompletionService.GetChatMessageContentsAsync(
                BuildChatHistory(prompt, article, sessionHistory, matches),
                executionSettings: null,
                kernel: null,
                cancellationToken);

            var text = completions.FirstOrDefault()?.Content;
            if (ReadReply(text, article, prompt, matches) is { } reply)
            {
                return reply;
            }

            _logger.LogWarning("Gemini reply generation returned an empty response; using local fallback text.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini Semantic Kernel reply request failed; using local fallback text.");
        }

        return fallback;
    }

    // Creates an embedding vector for retrieval, or null when embeddings are disabled.
    public async Task<float[]?> CreateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_options.IsEnabled || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            if (_embeddingGenerator is null)
            {
                _logger.LogWarning("Gemini embeddings are enabled, but no embedding generator was registered; using keyword retrieval.");
                return null;
            }

            var vector = await _embeddingGenerator.GenerateVectorAsync(text, cancellationToken: cancellationToken);
            return vector.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini Semantic Kernel embedding request failed; using keyword retrieval.");
            return null;
        }
    }

    // Turns a user prompt into a concise Wikipedia lookup plan when Gemini is available.
    public async Task<WikiLookupPlan> PlanWikipediaLookupAsync(
        string prompt,
        string? wikipediaUrl,
        IReadOnlyList<MessageDto> sessionHistory,
        CancellationToken cancellationToken = default)
    {
        var fallback = BuildFallbackLookupPlan(prompt);
        if (!_options.IsEnabled)
        {
            _logger.LogInformation("Gemini Wikipedia lookup planning is disabled because GEMINI_API_KEY is not configured; using local search text.");
            return fallback;
        }

        if (_chatCompletionService is null)
        {
            _logger.LogWarning("Gemini Wikipedia lookup planning is enabled, but no chat completion service was registered; using local search text.");
            return fallback;
        }

        try
        {
            var completions = await _chatCompletionService.GetChatMessageContentsAsync(
                BuildLookupPlanningHistory(prompt, wikipediaUrl, sessionHistory),
                executionSettings: null,
                kernel: null,
                cancellationToken);

            return ReadLookupPlan(completions.FirstOrDefault()?.Content, fallback);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini Wikipedia lookup planning failed; using local search text.");
            return fallback;
        }
    }

    // Builds the prompt that asks Gemini for a search query and article-focus phrases only.
    private static ChatHistory BuildLookupPlanningHistory(
        string prompt,
        string? wikipediaUrl,
        IReadOnlyList<MessageDto> sessionHistory)
    {
        var history = new ChatHistory(
            """
            You prepare lookup plans for a Wikipedia-backed research app.
            Return strict JSON only with:
            - "searchQuery": a concise Wikipedia search query
            - "focusPhrases": 2 to 5 short phrases that identify the user's requested focus inside the article

            Rules:
            - Do not answer the user's question.
            - Correct obvious typos in the search query.
            - Preserve named entities and product/company/person names.
            - If a Wikipedia URL is provided, keep "searchQuery" empty; the URL is authoritative.
            - Make focus phrases useful for choosing article sections.
            - Do not include markdown code fences.
            """
        );

        foreach (var message in sessionHistory.TakeLast(4))
        {
            if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                history.AddAssistantMessage(message.Content);
                continue;
            }

            history.AddUserMessage(message.Content);
        }

        history.AddUserMessage($"""
            User input: {prompt}
            Wikipedia URL: {TextTools.Clean(wikipediaUrl)}

            Respond with valid JSON only.
            """);

        return history;
    }

    // Parses Gemini's lookup plan and falls back cleanly if the JSON is missing fields.
    private static WikiLookupPlan ReadLookupPlan(string? responseText, WikiLookupPlan fallback)
    {
        var text = TextTools.Clean(responseText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(StripCodeFence(text));
            var searchQuery = fallback.SearchQuery;
            if (document.RootElement.TryGetProperty("searchQuery", out var queryElement) &&
                queryElement.ValueKind == JsonValueKind.String)
            {
                searchQuery = TextTools.Clean(queryElement.GetString());
            }

            var focusPhrases = new List<string>();
            if (document.RootElement.TryGetProperty("focusPhrases", out var focusElement) &&
                focusElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in focusElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = TextTools.Clean(item.GetString());
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        focusPhrases.Add(value);
                    }
                }
            }

            return new WikiLookupPlan(
                string.IsNullOrWhiteSpace(searchQuery) ? fallback.SearchQuery : searchQuery,
                focusPhrases.Count == 0 ? fallback.FocusPhrases : focusPhrases.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray());
        }
        catch
        {
            return fallback;
        }
    }

    // Local fallback keeps behavior deterministic when Gemini is disabled or unavailable.
    private static WikiLookupPlan BuildFallbackLookupPlan(string prompt)
    {
        var cleaned = TextTools.Clean(prompt);
        return string.IsNullOrWhiteSpace(cleaned)
            ? new WikiLookupPlan(string.Empty, [])
            : new WikiLookupPlan(cleaned, [cleaned]);
    }

    // Builds the chat prompt that asks Gemini for JSON-only output.
    private static ChatHistory BuildChatHistory(
        string prompt,
        WikiArticle article,
        IReadOnlyList<MessageDto> sessionHistory,
        IReadOnlyList<WikiMatch> matches)
    {
        var history = new ChatHistory(
            """
            You are a session-aware Wikipedia research assistant inside a RAG application.
            Use only the supplied article summary, retrieved sections, and prior session messages.
            Return strict JSON only with:
            - "answer": a grounded response for the user, that is specfifc to the article and retrieved context. 
                If the question in the original prompt cannot be easily answered have that as a preface and then provide the input from context. 
            - "relatedTopics": an array of 2 to 4 short labels for the main graph branches
            - "supportingTopics": an array of 4 to 8 concise noun-phrase labels for supporting graph details

            Rules:
            - Be specific and useful, not generic.
            - If the input is a topic, produce an in-depth study guide grounded in the article and retrieved context.
            - If the input is a question, answer it directly using the provided context.
            - Mention relationships, definitions, and follow-up reading when helpful.
            - Make supportingTopics complete labels, not sentence fragments; prefer 2 to 5 words each.
            - Do not use generic supportingTopics such as Overview, Details, Key Point, or Related Topics.
            - Do not use markdown code fences.
            """
        );

        foreach (var message in sessionHistory.TakeLast(6))
        {
            if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                history.AddAssistantMessage(message.Content);
                continue;
            }

            history.AddUserMessage(message.Content);
        }

        var matchLines = matches
            .Take(4)
            .Select(match => $"- [{match.ChunkId}] {match.Section}: {TextTools.TrimToLength(match.Text, 220)}")
            .DefaultIfEmpty("- no extra sections were retrieved");
        var relatedTopicLines = article.RelatedTopicDetails
            .Take(4)
            .Select(topic => $"- {topic.Title}: {topic.Summary}")
            .DefaultIfEmpty("- no related-topic summaries were available");

        history.AddUserMessage($"""
            User input: {prompt}
            Canonical article: {article.Title}
            Article URL: {article.SourceUrl}
            Article summary: {article.Summary}
            Related Wikipedia articles: {string.Join(", ", article.RelatedArticles.Take(6))}

            Related topic details:
            {string.Join(Environment.NewLine, relatedTopicLines)}

            Retrieved context:
            {string.Join(Environment.NewLine, matchLines)}

            Respond with valid JSON only.
            """);

        return history;
    }

    // Builds a local answer when Gemini is off or unavailable.
    private static GeminiReply BuildFallbackReply(
        string prompt,
        WikiArticle article,
        IReadOnlyList<MessageDto> sessionHistory,
        IReadOnlyList<WikiMatch> matches)
    {
        var priorMessages = sessionHistory
            .Where(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            .TakeLast(2)
            .Select(message => message.Content)
            .ToArray();
        var relatedTopics = article.RelatedArticles.Take(4).DefaultIfEmpty($"{article.Title} references").ToArray();
        var keyIdeas = matches.Count > 0
            ? string.Join(", ", matches.Take(3).Select(match => match.Section.ToLowerInvariant()))
            : string.Join(", ", relatedTopics);
        var relatedTopicNotes = article.RelatedTopicDetails.Count > 0
            ? string.Join(
                Environment.NewLine,
                article.RelatedTopicDetails
                    .Take(4)
                    .Select(topic => $"- {topic.Title}: {topic.Summary}"))
            : string.Join(Environment.NewLine, relatedTopics.Select(topic => $"- {topic}: related context for {article.Title}."));

        return new GeminiReply(
            $"""
            Overview: {article.Summary}

            Key ideas: {keyIdeas}.

            Related topics:
            {relatedTopicNotes}

            Prior focus: {(priorMessages.Length == 0 ? "this is the first turn in the session." : string.Join(" | ", priorMessages))}
            """,
            BuildFallbackTopics(prompt, article, matches),
            []);
    }

    // Parses Gemini JSON output into the reply model.
    private static GeminiReply? ReadReply(
        string? responseText,
        WikiArticle article,
        string prompt,
        IReadOnlyList<WikiMatch> matches)
    {
        var text = TextTools.Clean(responseText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (TryParseReply(text, prompt, article, matches) is { } reply)
        {
            return reply;
        }

        return new GeminiReply(TextTools.Clean(text), BuildFallbackTopics(prompt, article, matches), []);
    }

    // Parses the JSON answer and related topics from the model response.
    private static GeminiReply? TryParseReply(
        string text,
        string prompt,
        WikiArticle article,
        IReadOnlyList<WikiMatch> matches)
    {
        try
        {
            using var document = JsonDocument.Parse(StripCodeFence(text));
            if (!document.RootElement.TryGetProperty("answer", out var answerElement) ||
                answerElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var relatedTopics = ReadStringArray(document.RootElement, "relatedTopics");
            var supportingTopics = ReadStringArray(document.RootElement, "supportingTopics");

            return new GeminiReply(
                TextTools.Clean(answerElement.GetString()),
                relatedTopics.Count == 0 ? BuildFallbackTopics(prompt, article, matches) : relatedTopics,
                supportingTopics);
        }
        catch
        {
            return null;
        }
    }

    // Reads a string array from the JSON response and cleans each label.
    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = TextTools.Clean(item.GetString());
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    // Builds a safe fallback topic list from the prompt, article, and matches.
    private static IReadOnlyList<string> BuildFallbackTopics(
        string prompt,
        WikiArticle article,
        IReadOnlyList<WikiMatch> matches)
    {
        return new[] { prompt, article.Title }
            .Concat(article.RelatedArticles)
            .Concat(matches.Select(match => match.Section))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(TextTools.Clean)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
    }

    // Removes markdown code fences around JSON payloads.
    private static string StripCodeFence(string text)
    {
        var cleaned = text.Trim();
        if (!cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            return cleaned;
        }

        var firstLineBreak = cleaned.IndexOf('\n');
        if (firstLineBreak < 0)
        {
            return cleaned.Trim('`').Trim();
        }

        var content = cleaned[(firstLineBreak + 1)..];
        var closingFence = content.LastIndexOf("```", StringComparison.Ordinal);
        return closingFence < 0 ? content.Trim() : content[..closingFence].Trim();
    }

    // Extracts the first text part from Gemini's JSON response shape.
    private static string? ReadGeneratedText(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return TextTools.Clean(text.GetString());
                }
            }
        }

        return null;
    }
}

public sealed record GeminiReply(
    string Answer,
    IReadOnlyList<string> RelatedTopics,
    IReadOnlyList<string> SupportingTopics);
