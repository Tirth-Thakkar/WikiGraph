using System;
using System.Xml.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Text.Json;
public class MindMap
{ // Mindmap class creates a map of WikiArticles generated from links
	public string Name { get; set; } //Name of the mindmap determines what you are studying
	public WikiArticle MainArticle= new WikiArticle("");
    public List<WikiArticle> RelatedArticles = new List<WikiArticle>();
	public string Prompt { get; set; }

	public MindMap(string name)
	{
		this.Name = name;
		MainArticle = new WikiArticle(name);
	}
	public override string ToString()
	{
        string s_Full = $"Current Topic: {this.MainArticle.ToString1()}\n";

        foreach (WikiArticle article in RelatedArticles) {
			s_Full += "\n" + article.ToString1();
		}
		return s_Full;
	}
}
