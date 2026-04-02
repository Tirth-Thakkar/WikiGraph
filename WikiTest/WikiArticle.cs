using System;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Text.Json;
public class WikiArticle
{ 
    public string LinkToWikiArticle { get; set; }
    public List<string> links { get; set; } = new List<string>();// related Article

    private string links_str = ""; // For ToString

    public string ArticleSummary { get; set; }
    public string AISummary { get; set; }

    public virtual async Task fetch()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WikiGraphProject (andykzhang@cpp.edu)");
        var response = await client.GetStringAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{this.LinkToWikiArticle}");
        var document = JsonDocument.Parse(response);
        this.ArticleSummary = document.RootElement.GetProperty("extract").GetString();

        response = await client.GetStringAsync($"https://en.wikipedia.org/w/api.php?action=parse&page={this.LinkToWikiArticle}&prop=links&format=json");
        document = JsonDocument.Parse(response);
        var links = document.RootElement.GetProperty("parse").GetProperty("links").EnumerateArray();


        int i = 0;
        foreach (var link in links)
        {
            // "*": link text
            if (link.TryGetProperty("*", out JsonElement linkText))
            {
                this.links.Add(linkText.GetString());
                links_str += $"[{i}]: {linkText.GetString()}\n";
            }
            i++;
        }
    }
    public WikiArticle(string topic)
    {
        LinkToWikiArticle = topic;
    }
    public override string ToString() =>
        $"Topic: {LinkToWikiArticle}\n" +
        $"Summary: {ArticleSummary}\n" +
        $"AI Summary: {AISummary}\n" +
        $"Links: {links_str}";
    public string  ToString1() =>
     $"Topic: {LinkToWikiArticle}\n" +
     $"Summary: {ArticleSummary}\n";
}