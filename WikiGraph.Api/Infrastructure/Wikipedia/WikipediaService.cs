using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WikiGraph.Api.Application.Models;

namespace WikiGraph.Api.Infrastructure.Wikipedia;

public sealed class WikipediaService
{
    private const int SearchResultLimit = 8;
    private const int MaxArticleSectionsToFetch = 16;
    private const int MaxSearchQueryLength = 280;

    private static readonly HashSet<string> NonContentSectionHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "References",
        "Notes",
        "Citations",
        "Bibliography",
        "Sources",
        "Further reading",
        "External links",
        "See also"
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<WikipediaService> _logger;

    private sealed record WikiApiSection(string Heading, string Index, string? Anchor);
    private sealed record WikiSearchCandidate(
        string Title,
        string Snippet,
        string TitleSnippet,
        string SectionTitle,
        string SectionSnippet,
        int Position);
    private sealed record WikiSearchResults(IReadOnlyList<WikiSearchCandidate> Candidates, string? Suggestion);
    private sealed record WikiSearchResolution(
        string Title,
        IReadOnlyList<string> SuggestedSections,
        IReadOnlyList<string> FocusPhrases);

    // Creates the Wikipedia client wrapper used by the API.
    public WikipediaService(HttpClient httpClient, ILogger<WikipediaService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // Resolves a Wikipedia article, or a fallback article when the fetch fails.
    public async Task<WikiArticle> GetArticleAsync(
        string topic,
        string? wikipediaUrl,
        WikiLookupPlan? lookupPlan = null,
        CancellationToken cancellationToken = default)
    {
        var cleanTopic = TextTools.Clean(topic);
        var cleanUrl = TextTools.Clean(wikipediaUrl);
        var cleanSearchQuery = TextTools.Clean(lookupPlan?.SearchQuery);
        var lookupText = string.IsNullOrWhiteSpace(cleanUrl) && !string.IsNullOrWhiteSpace(cleanSearchQuery)
            ? cleanSearchQuery
            : cleanTopic;
        var requestedTitle = ResolveRequestedTitle(lookupText, cleanUrl);
        var focusPhrases = BuildFocusPhrases(cleanTopic, lookupPlan);
        var resolution = await ResolveSearchAsync(requestedTitle, cleanUrl, cleanTopic, focusPhrases, cancellationToken);
        var sourceUrl = ResolveUrl(resolution.Title, cleanUrl);

        try
        {
            var page = await LoadPageAsync(resolution.Title, includeLinks: true, cancellationToken);
            if (page is null)
            {
                return BuildFallbackArticle(resolution.Title, cleanTopic, sourceUrl);
            }

            return await BuildArticleAsync(page.Value, cleanTopic, sourceUrl, resolution, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Wikipedia fetch failed for {Title}; using fallback content.", resolution.Title);
            return BuildFallbackArticle(resolution.Title, cleanTopic, sourceUrl);
        }
    }

    // Searches for the best canonical title and any matching section hints when the user passed free-form text.
    private async Task<WikiSearchResolution> ResolveSearchAsync(
        string requestedTitle,
        string wikipediaUrl,
        string originalPrompt,
        IReadOnlyList<string> focusPhrases,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedTitle))
        {
            return new WikiSearchResolution(requestedTitle, [], focusPhrases);
        }

        if (!string.IsNullOrWhiteSpace(wikipediaUrl))
        {
            var linkedSectionHints = await SearchLinkedArticleSectionHintsAsync(requestedTitle, focusPhrases, cancellationToken);
            return new WikiSearchResolution(requestedTitle, linkedSectionHints, focusPhrases);
        }

        try
        {
            var candidates = (await SearchAllCandidateQueriesAsync(
                    new[] { requestedTitle, originalPrompt }.Concat(focusPhrases),
                    cancellationToken))
                .DistinctBy(candidate => $"{candidate.Title}\u001f{candidate.SectionTitle}", StringComparer.OrdinalIgnoreCase)
                .ToList();

            var scoringText = BuildScoringText(originalPrompt, focusPhrases);
            var best = SelectBestSearchCandidate(scoringText, candidates);
            return best is null
                ? new WikiSearchResolution(requestedTitle, [], focusPhrases)
                : new WikiSearchResolution(best.Title, ReadSuggestedSections(best), focusPhrases);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Wikipedia title search failed for {Title}; keeping requested title.", requestedTitle);
        }

        return new WikiSearchResolution(requestedTitle, [], focusPhrases);
    }

    // Searches the AI-planned query, original prompt, focus phrases, and MediaWiki suggestions.
    private async Task<IReadOnlyList<WikiSearchCandidate>> SearchAllCandidateQueriesAsync(
        IEnumerable<string> rawSearchTexts,
        CancellationToken cancellationToken)
    {
        var candidates = new List<WikiSearchCandidate>();
        var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingSearches = new Queue<string>(rawSearchTexts
            .Select(BuildSearchQuery)
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4));

        while (pendingSearches.TryDequeue(out var searchText))
        {
            if (!searched.Add(searchText))
            {
                continue;
            }

            var searchResults = await SearchCandidatesAsync(searchText, cancellationToken);
            candidates.AddRange(searchResults.Candidates);

            if (!string.IsNullOrWhiteSpace(searchResults.Suggestion) &&
                !searched.Contains(searchResults.Suggestion))
            {
                pendingSearches.Enqueue(searchResults.Suggestion);
            }
        }

        return candidates;
    }

    // Loads MediaWiki search candidates and the search engine's own spelling suggestion, when available.
    private async Task<WikiSearchResults> SearchCandidatesAsync(string searchText, CancellationToken cancellationToken)
    {
        using var document = await GetJsonDocumentAsync(
            $"?action=query&format=json&formatversion=2&list=search&srnamespace=0&srlimit={SearchResultLimit}&srinfo=suggestion&srprop=snippet|titlesnippet|sectiontitle|sectionsnippet&srsearch={Uri.EscapeDataString(searchText)}",
            cancellationToken);

        if (!document.RootElement.TryGetProperty("query", out var query))
        {
            return new WikiSearchResults([], null);
        }

        var suggestion = query.TryGetProperty("searchinfo", out var searchInfo)
            ? ReadSearchText(searchInfo, "suggestion")
            : null;

        if (!query.TryGetProperty("search", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return new WikiSearchResults([], suggestion);
        }

        var candidates = new List<WikiSearchCandidate>();
        var position = 0;
        foreach (var result in results.EnumerateArray())
        {
            var title = TextTools.Clean(ReadString(result, "title"));
            if (!string.IsNullOrWhiteSpace(title))
            {
                candidates.Add(new WikiSearchCandidate(
                    title,
                    ReadSearchText(result, "snippet"),
                    ReadSearchText(result, "titlesnippet"),
                    ReadSearchText(result, "sectiontitle"),
                    ReadSearchText(result, "sectionsnippet"),
                    position));
            }

            position++;
        }

        return new WikiSearchResults(candidates, suggestion);
    }

    // Uses search only to find likely sections inside a URL-provided article; the linked title remains authoritative.
    private async Task<IReadOnlyList<string>> SearchLinkedArticleSectionHintsAsync(
        string linkedTitle,
        IReadOnlyList<string> focusPhrases,
        CancellationToken cancellationToken)
    {
        var focusText = BuildScoringText(string.Empty, focusPhrases);
        if (string.IsNullOrWhiteSpace(focusText))
        {
            return [];
        }

        try
        {
            var searchResults = await SearchCandidatesAsync(BuildSearchQuery($"{linkedTitle} {focusText}"), cancellationToken);
            return searchResults.Candidates
                .Where(candidate => TextTools.Slugify(candidate.Title).Equals(TextTools.Slugify(linkedTitle), StringComparison.Ordinal))
                .SelectMany(ReadSuggestedSections)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Wikipedia linked article section hint lookup failed for {Title}.", linkedTitle);
            return [];
        }
    }

    // Builds a compact MediaWiki search query from AI-planned or fallback prompt text.
    private static string BuildSearchQuery(string prompt)
    {
        return TextTools.TrimToLength(TextTools.Clean(prompt), MaxSearchQueryLength);
    }

    // Picks the search result that best matches the whole prompt instead of blindly using the first hit.
    private static WikiSearchCandidate? SelectBestSearchCandidate(string prompt, IReadOnlyList<WikiSearchCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var queryTerms = TextTools.ExtractTerms(prompt).ToHashSet(StringComparer.Ordinal);
        if (queryTerms.Count == 0)
        {
            return candidates.OrderBy(candidate => candidate.Position).First();
        }

        var termWeights = BuildQueryTermWeights(queryTerms, candidates.Select(SearchCandidateText));
        var best = candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = ScoreSearchCandidate(candidate, prompt, termWeights)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.Position)
            .First();

        return best.Score > 0.5d ? best.Candidate : null;
    }

    // Scores title, page snippets, and section snippets against the user's prompt terms.
    private static double ScoreSearchCandidate(
        WikiSearchCandidate candidate,
        string prompt,
        IReadOnlyDictionary<string, double> termWeights)
    {
        var normalizedPrompt = TextTools.Slugify(prompt);
        var normalizedTitle = TextTools.Slugify(candidate.Title);

        var score = WeightedTermOverlap(SearchCandidateText(candidate), termWeights);
        score += WeightedTermOverlap(candidate.Title, termWeights) * 2d;
        score += WeightedTermOverlap(candidate.SectionTitle, termWeights) * 3d;

        if (normalizedTitle.Equals(normalizedPrompt, StringComparison.Ordinal))
        {
            score += 12d;
        }
        else if (normalizedPrompt.Contains(normalizedTitle, StringComparison.Ordinal))
        {
            score += 1.5d;
        }

        return score + Math.Max(0d, 0.5d - (candidate.Position * 0.05d));
    }

    // Combines all candidate text used by search ranking.
    private static string SearchCandidateText(WikiSearchCandidate candidate)
    {
        return $"{candidate.Title} {candidate.TitleSnippet} {candidate.Snippet} {candidate.SectionTitle} {candidate.SectionSnippet}";
    }

    // Pulls optional section hints out of the selected search result.
    private static IReadOnlyList<string> ReadSuggestedSections(WikiSearchCandidate candidate)
    {
        return new[] { candidate.SectionTitle }
            .Select(TextTools.Clean)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Reads MediaWiki search fields, which often contain highlighted HTML snippets.
    private static string ReadSearchText(JsonElement element, string propertyName)
    {
        return TextTools.Clean(WebUtility.HtmlDecode(StripHtml(ReadString(element, propertyName))));
    }

    // Loads a single Wikipedia page payload with extracts and optional links.
    private async Task<JsonElement?> LoadPageAsync(string title, bool includeLinks, CancellationToken cancellationToken)
    {
        var props = includeLinks ? "extracts|info|links" : "extracts|info";
        var linkOptions = includeLinks ? "&plnamespace=0&pllimit=8" : string.Empty;

        // MediaWiki `action=query&prop=extracts|info|links` provides the article summary, URL, and outbound links.
        using var document = await GetJsonDocumentAsync(
            $"?action=query&format=json&formatversion=2&redirects=1&prop={props}&inprop=url&explaintext=1&exintro=1&titles={Uri.EscapeDataString(title)}{linkOptions}",
            cancellationToken);

        if (!document.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("missing", out _))
            {
                // Clone the page element because the backing JsonDocument is disposed when this method returns.
                return page.Clone();
            }
        }

        return null;
    }

    // Builds the article model from the fetched Wikipedia page payload.
    private async Task<WikiArticle> BuildArticleAsync(
        JsonElement page,
        string originalTopic,
        string fallbackUrl,
        WikiSearchResolution resolution,
        CancellationToken cancellationToken)
    {
        var title = ReadString(page, "title") ?? "Wikipedia topic";
        var sourceUrl = ReadString(page, "fullurl") ?? fallbackUrl;
        var extract = TextTools.Clean(ReadString(page, "extract"));
        var summary = string.IsNullOrWhiteSpace(extract)
            ? $"Wikipedia article for {title}."
            : FirstSentence(extract);

        var article = new WikiArticle(title, sourceUrl)
        {
            Summary = summary,
            RetrievedUtc = DateTime.UtcNow
        };

        // Keep the article model small and UI-friendly: overview, a few real Wikipedia sections, then related-topic summaries.
        await AddSectionsAsync(article, extract, title, originalTopic, resolution.SuggestedSections, resolution.FocusPhrases, cancellationToken);

        var linkedTitles = ReadLinkedTitles(page, title);
        var relatedTopics = await LoadRelatedTopicsAsync(linkedTitles, cancellationToken);
        if (relatedTopics.Count == 0)
        {
            relatedTopics = BuildSeedRelatedTopics(title, originalTopic);
        }

        foreach (var relatedTopic in relatedTopics)
        {
            article.RelatedArticles.Add(relatedTopic.Title);
            article.RelatedTopicDetails.Add(relatedTopic);
            article.Sections.Add(new WikiSection($"Related Topic: {relatedTopic.Title}", relatedTopic.Summary));
        }

        article.Sections.Add(new WikiSection(
            "Related Topics",
            string.Join(" ", article.RelatedTopicDetails.Take(4).Select(topic => $"{topic.Title}: {topic.Summary}"))));

        return article;
    }

    // Loads related topic summaries for the linked article titles.
    private async Task<IReadOnlyList<WikiTopicReference>> LoadRelatedTopicsAsync(
        IReadOnlyList<string> linkedTitles,
        CancellationToken cancellationToken)
    {
        if (linkedTitles.Count == 0)
        {
            return [];
        }

        try
        {
            var titleList = string.Join('|', linkedTitles.Select(title => title.Replace('|', ' ')));
            // MediaWiki `action=query` also accepts multiple titles, which keeps related-topic lookup to one request.
            using var document = await GetJsonDocumentAsync(
                $"?action=query&format=json&formatversion=2&redirects=1&prop=extracts|info&inprop=url&explaintext=1&exintro=1&titles={Uri.EscapeDataString(titleList)}",
                cancellationToken);

            if (!document.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages) ||
                pages.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var topics = new List<WikiTopicReference>();
            foreach (var page in pages.EnumerateArray())
            {
                var title = TextTools.Clean(ReadString(page, "title"));
                var extract = TextTools.Clean(ReadString(page, "extract"));
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(extract))
                {
                    continue;
                }

                topics.Add(new WikiTopicReference(
                    title,
                    ReadString(page, "fullurl") ?? ResolveUrl(title, null),
                    FirstSentence(extract)));
            }

            return topics
                .DistinctBy(topic => topic.Title, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Wikipedia related-topic lookup failed.");
            return [];
        }
    }

    // Fetches raw JSON from Wikipedia and parses it into a document.
    private async Task<JsonDocument> GetJsonDocumentAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(relativeUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    // Adds overview and real Wikipedia article sections to the article model.
    private async Task AddSectionsAsync(
        WikiArticle article,
        string extract,
        string title,
        string prompt,
        IReadOnlyList<string> suggestedSections,
        IReadOnlyList<string> focusPhrases,
        CancellationToken cancellationToken)
    {
        article.Sections.Add(new WikiSection("Overview", article.Summary));

        var addedWikipediaSections = false;
        var sections = await LoadArticleSectionsAsync(title, cancellationToken);
        foreach (var section in SelectSectionsForPrompt(sections, prompt, suggestedSections, focusPhrases, MaxArticleSectionsToFetch))
        {
            var content = await LoadSectionTextAsync(title, section, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            article.Sections.Add(new WikiSection(section.Heading, content, section.Anchor));
            addedWikipediaSections = true;
        }

        if (!addedWikipediaSections)
        {
            AddFallbackExtractSections(article, extract);
        }
    }

    // Chooses sections that best match the user's prompt, with early article sections as fallback context.
    private static IReadOnlyList<WikiApiSection> SelectSectionsForPrompt(
        IReadOnlyList<WikiApiSection> sections,
        string prompt,
        IReadOnlyList<string> suggestedSections,
        IReadOnlyList<string> focusPhrases,
        int maxSections)
    {
        if (sections.Count <= maxSections)
        {
            return sections;
        }

        var scoringText = BuildScoringText(prompt, focusPhrases);
        var queryTerms = TextTools.ExtractTerms(scoringText).ToHashSet(StringComparer.Ordinal);
        var termWeights = BuildQueryTermWeights(
            queryTerms,
            sections.Select(section => $"{section.Heading} {section.Anchor}"));
        var suggestedHeadings = suggestedSections
            .Select(TextTools.Clean)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scoredSections = sections
            .Select((section, index) => new
            {
                Section = section,
                Index = index,
                Score = ScoreSection(section, termWeights, suggestedHeadings)
            })
            .ToArray();
        var selected = scoredSections
            .Where(item => item.Score > 0d)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(maxSections)
            .ToList();

        foreach (var section in scoredSections.Take(3))
        {
            if (selected.Count >= maxSections)
            {
                break;
            }

            if (selected.Any(item => item.Section.Index.Equals(section.Section.Index, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            selected.Add(section);
        }

        foreach (var section in scoredSections)
        {
            if (selected.Count >= maxSections)
            {
                break;
            }

            if (!selected.Any(item => item.Section.Index.Equals(section.Section.Index, StringComparison.OrdinalIgnoreCase)))
            {
                selected.Add(section);
            }
        }

        return selected
            .OrderBy(item => item.Index)
            .Select(item => item.Section)
            .ToArray();
    }

    // Scores an article section heading against prompt terms and search-provided section hints.
    private static double ScoreSection(
        WikiApiSection section,
        IReadOnlyDictionary<string, double> termWeights,
        IReadOnlySet<string> suggestedHeadings)
    {
        var score = 0d;
        if (suggestedHeadings.Contains(section.Heading))
        {
            score += 20d;
        }

        score += WeightedTermOverlap($"{section.Heading} {section.Anchor}", termWeights) * 3d;

        return score;
    }

    // Combines the original prompt with the AI-planned focus phrases for local scoring only.
    private static string BuildScoringText(string prompt, IReadOnlyList<string> focusPhrases)
    {
        return TextTools.Clean(string.Join(' ', new[] { prompt }.Concat(focusPhrases)));
    }

    // Preserves the user's original prompt while adding AI-provided focus phrases when they exist.
    private static IReadOnlyList<string> BuildFocusPhrases(string prompt, WikiLookupPlan? lookupPlan)
    {
        var focusPhrases = new[] { prompt }
            .Concat(lookupPlan?.FocusPhrases ?? [])
            .Select(TextTools.Clean)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return focusPhrases;
    }

    // Learns which prompt terms matter from the returned Wikipedia candidate/section corpus.
    private static IReadOnlyDictionary<string, double> BuildQueryTermWeights(
        IReadOnlySet<string> queryTerms,
        IEnumerable<string> documents)
    {
        if (queryTerms.Count == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var documentTermSets = documents
            .Select(document => TextTools.ExtractTerms(document).ToHashSet(StringComparer.Ordinal))
            .ToArray();
        if (documentTermSets.Length == 0)
        {
            return queryTerms.ToDictionary(term => term, _ => 1d, StringComparer.Ordinal);
        }

        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var term in queryTerms)
        {
            var documentFrequency = documentTermSets.Count(document => document.Contains(term));
            if (documentFrequency == 0)
            {
                continue;
            }

            weights[term] = Math.Log((documentTermSets.Length + 1d) / (documentFrequency + 1d)) + 1d;
        }

        return weights;
    }

    // Scores exact term overlap with corpus-derived weights instead of curated stop words or synonym lists.
    private static double WeightedTermOverlap(string text, IReadOnlyDictionary<string, double> termWeights)
    {
        if (termWeights.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return 0d;
        }

        return TextTools.ExtractTerms(text)
            .Distinct(StringComparer.Ordinal)
            .Where(termWeights.ContainsKey)
            .Sum(term => termWeights[term]);
    }

    // Fetches the table of contents metadata so citations can use Wikipedia's actual section names and anchors.
    private async Task<IReadOnlyList<WikiApiSection>> LoadArticleSectionsAsync(string title, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonDocumentAsync(
                $"?action=parse&format=json&formatversion=2&page={Uri.EscapeDataString(title)}&prop=sections",
                cancellationToken);

            if (!document.RootElement.TryGetProperty("parse", out var parse) ||
                !parse.TryGetProperty("sections", out var sections) ||
                sections.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var articleSections = new List<WikiApiSection>();
            foreach (var section in sections.EnumerateArray())
            {
                var heading = TextTools.Clean(WebUtility.HtmlDecode(StripHtml(ReadString(section, "line"))));
                var index = TextTools.Clean(ReadString(section, "index"));
                if (string.IsNullOrWhiteSpace(heading) ||
                    string.IsNullOrWhiteSpace(index) ||
                    NonContentSectionHeadings.Contains(heading) ||
                    (ReadInt(section, "level") ?? int.MaxValue) > 2)
                {
                    continue;
                }

                articleSections.Add(new WikiApiSection(
                    heading,
                    index,
                    TextTools.Clean(ReadString(section, "anchor") ?? ReadString(section, "linkAnchor"))));
            }

            return articleSections
                .DistinctBy(section => section.Index, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Wikipedia section metadata lookup failed for {Title}.", title);
            return [];
        }
    }

    // Fetches and normalizes the body text for a single Wikipedia section.
    private async Task<string> LoadSectionTextAsync(
        string title,
        WikiApiSection section,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonDocumentAsync(
                $"?action=parse&format=json&formatversion=2&page={Uri.EscapeDataString(title)}&prop=text&section={Uri.EscapeDataString(section.Index)}&disableeditsection=1&disabletoc=1",
                cancellationToken);

            if (!document.RootElement.TryGetProperty("parse", out var parse) ||
                !TryReadParseText(parse, out var html))
            {
                return string.Empty;
            }

            return ExtractSectionText(html, section.Heading);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Wikipedia section text lookup failed for {Title} section {Section}.", title, section.Heading);
            return string.Empty;
        }
    }

    // Adds derived extract chunks only when section-level Wikipedia content is unavailable.
    private static void AddFallbackExtractSections(WikiArticle article, string extract)
    {
        foreach (var paragraph in SplitParagraphs(extract).Take(2))
        {
            if (!string.Equals(paragraph, article.Summary, StringComparison.OrdinalIgnoreCase))
            {
                article.Sections.Add(new WikiSection("Details", paragraph));
            }
        }

        foreach (var sentence in SplitSentences(extract).Skip(1).Take(3))
        {
            article.Sections.Add(new WikiSection("Key Point", sentence));
        }
    }

    // Converts the parsed Wikipedia HTML into compact section text for storage and retrieval.
    private static string ExtractSectionText(string html, string heading)
    {
        var withoutComments = Regex.Replace(html, "<!--.*?-->", " ", RegexOptions.Singleline);
        var withoutScripts = Regex.Replace(withoutComments, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withoutReferences = Regex.Replace(withoutScripts, "<sup[^>]*class=\"[^\"]*reference[^\"]*\"[^>]*>.*?</sup>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withParagraphBreaks = Regex.Replace(withoutReferences, "</?(p|div|section|h[1-6]|ul|ol|li|table|tr|br)[^>]*>", "\n\n", RegexOptions.IgnoreCase);
        var text = WebUtility.HtmlDecode(StripHtml(withParagraphBreaks));

        var paragraphs = SplitParagraphs(text)
            .Where(paragraph => !string.Equals(paragraph, heading, StringComparison.OrdinalIgnoreCase))
            .Where(paragraph => paragraph.Length > 20)
            .Take(2)
            .ToArray();

        return TextTools.TrimToLength(string.Join(" ", paragraphs), 1400);
    }

    // Removes simple HTML tags from small API fields and parsed text after block handling.
    private static string StripHtml(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value, "<[^>]+>", " ");
    }

    // Reads action=parse text across MediaWiki response shapes.
    private static bool TryReadParseText(JsonElement parse, out string html)
    {
        html = string.Empty;
        if (!parse.TryGetProperty("text", out var text))
        {
            return false;
        }

        if (text.ValueKind == JsonValueKind.String)
        {
            html = text.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(html);
        }

        if (text.ValueKind == JsonValueKind.Object &&
            text.TryGetProperty("*", out var legacyText) &&
            legacyText.ValueKind == JsonValueKind.String)
        {
            html = legacyText.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(html);
        }

        return false;
    }

    // Reads an integer property from a JSON element when MediaWiki returns numeric-looking strings.
    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number)
            ? number
            : null;
    }

    // Reads and filters linked article titles from the page payload.
    private static IReadOnlyList<string> ReadLinkedTitles(JsonElement page, string articleTitle)
    {
        if (!page.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return links.EnumerateArray()
            .Select(link => TextTools.Clean(ReadString(link, "title")))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Where(title => !title.Contains(':', StringComparison.Ordinal))
            .Where(title => !string.Equals(title, articleTitle, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    // Builds fallback related topics from the current title and prompt.
    private static IReadOnlyList<WikiTopicReference> BuildSeedRelatedTopics(string title, string originalTopic)
    {
        var seedTitles = TextTools.ExtractTerms(originalTopic, 4)
            .Select(Capitalize)
            .Concat([$"{title} history", $"{title} applications", $"{title} materials"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4);

        return seedTitles
            .Select(seedTitle => new WikiTopicReference(
                seedTitle,
                ResolveUrl(seedTitle, null),
                $"{seedTitle} is a related Wikipedia topic that adds context around {title}."))
            .ToArray();
    }

    // Builds a fallback article when Wikipedia cannot be fetched or parsed.
    private static WikiArticle BuildFallbackArticle(string title, string topic, string wikipediaUrl)
    {
        var article = new WikiArticle(title, wikipediaUrl)
        {
            Summary = $"{title} is treated here as a Wikipedia research topic. Start with the main idea, note its definitions and uses, and compare it with nearby related topics for context.",
            RetrievedUtc = DateTime.UtcNow
        };

        article.Sections.Add(new WikiSection("Overview", article.Summary));
        article.Sections.Add(new WikiSection(
            "Details",
            $"Use {title} as the center topic, then branch into history, structure, applications, and related pages for a broader picture."));

        foreach (var relatedTopic in BuildSeedRelatedTopics(title, topic))
        {
            article.RelatedArticles.Add(relatedTopic.Title);
            article.RelatedTopicDetails.Add(relatedTopic);
        }

        article.Sections.Add(new WikiSection(
            "Related Topics",
            string.Join(" ", article.RelatedTopicDetails.Select(topicInfo => $"{topicInfo.Title}: {topicInfo.Summary}"))));

        return article;
    }

    // Resolves a Wikipedia title from a URL path segment or the original topic.
    private static string ResolveRequestedTitle(string topic, string? wikipediaUrl)
    {
        if (!string.IsNullOrWhiteSpace(wikipediaUrl) && Uri.TryCreate(wikipediaUrl, UriKind.Absolute, out var uri))
        {
            var segment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(segment))
            {
                return Uri.UnescapeDataString(segment.Replace('_', ' '));
            }
        }

        return string.IsNullOrWhiteSpace(topic) ? "Wikipedia topic" : topic;
    }

    // Resolves the final article URL, or builds one from the title.
    private static string ResolveUrl(string title, string? wikipediaUrl)
    {
        if (!string.IsNullOrWhiteSpace(wikipediaUrl))
        {
            return wikipediaUrl;
        }

        return $"https://en.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
    }

    // Reads a string property from a JSON element when the property exists.
    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    // Returns the first sentence, or a trimmed fallback when none exists.
    private static string FirstSentence(string text)
    {
        var sentence = SplitSentences(text).FirstOrDefault();
        return string.IsNullOrWhiteSpace(sentence) ? TextTools.TrimToLength(text, 280) : sentence;
    }

    // Splits text into cleaned paragraph-sized chunks.
    private static IEnumerable<string> SplitParagraphs(string text)
    {
        return text
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TextTools.Clean)
            .Where(value => !string.IsNullOrWhiteSpace(value));
    }

    // Splits text into cleaned sentences with trailing punctuation restored.
    private static IEnumerable<string> SplitSentences(string text)
    {
        return text
            .Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TextTools.Clean)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.EndsWith(".", StringComparison.Ordinal) ? value : $"{value}.");
    }

    // Capitalizes the first letter of a word for display.
    private static string Capitalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Topic";
        }

        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }
}
