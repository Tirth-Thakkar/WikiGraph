using System.Security.Cryptography;
using System.Text;

namespace WikiGraph.Api.Application.Models;

public sealed record WikiSection(string Heading, string Content, string? Anchor = null);

public sealed record WikiMatch(string ChunkId, string Section, string Text, string SourceUrl, double Score);

public sealed record WikiTopicReference(string Title, string SourceUrl, string Summary);

public sealed record WikiLookupPlan(string SearchQuery, IReadOnlyList<string> FocusPhrases);

public sealed class WikiArticle
{
    public WikiArticle(string title, string sourceUrl)
    {
        Title = title;
        SourceUrl = sourceUrl;
    }

    public string Title { get; set; }
    public string SourceUrl { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DateTime RetrievedUtc { get; set; } = DateTime.UtcNow;
    public List<string> RelatedArticles { get; } = [];
    public List<WikiTopicReference> RelatedTopicDetails { get; } = [];
    public List<WikiSection> Sections { get; } = [];
    public string ArticleId => TextTools.Slugify(Title);

    public override string ToString()
    {
        var links = RelatedArticles.Count == 0 ? "none" : string.Join(", ", RelatedArticles);
        return $"""
            Topic: {Title}
            Summary: {Summary}
            Links: {links}
            """;
    }
}

internal static class TextTools
{
    // Normalizes whitespace: "  hello\tworld  " -> "hello world".
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;

        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString();
    }

    // Extracts unique lowercase words from text, for example "Roman architecture" -> ["roman", "architecture"].
    public static IReadOnlyList<string> ExtractTerms(string text, int maxTerms = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var terms = new List<string>();
        var current = new StringBuilder();

        foreach (var character in text)
        {
            if (character is '\'' or '’')
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }

            AddCurrentTerm(current, terms);
            if (terms.Count >= maxTerms)
            {
                return terms;
            }
        }

        AddCurrentTerm(current, terms);
        return terms.Count > maxTerms ? terms.Take(maxTerms).ToArray() : terms;
    }

    // Builds a slug from the extracted terms, for example "Roman Architecture" -> "roman-architecture".
    public static string Slugify(string value)
    {
        var slug = string.Join('-', ExtractTerms(value));
        return string.IsNullOrWhiteSpace(slug) ? "item" : slug;
    }

    // Returns a SHA-256 hex digest for the input text.
    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Trims text to a maximum length and adds "..." when it is shortened.
    public static string TrimToLength(string text, int maxLength)
    {
        var cleaned = Clean(text);
        if (cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        return $"{cleaned[..Math.Max(0, maxLength - 3)].TrimEnd()}...";
    }

    private static void AddCurrentTerm(StringBuilder current, List<string> terms)
    {
        if (current.Length <= 2)
        {
            current.Clear();
            return;
        }

        var term = current.ToString();
        if (!terms.Contains(term, StringComparer.Ordinal))
        {
            terms.Add(term);
        }

        current.Clear();
    }
}
