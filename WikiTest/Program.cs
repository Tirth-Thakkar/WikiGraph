using System;
using System.Diagnostics;


class WikiTest
{
    static async Task Main()
    {


        
                Console.WriteLine("Choose Your Topic: ");
                MindMap m = new MindMap(Console.ReadLine());
                await m.MainArticle.fetch();
                Console.WriteLine(m.MainArticle);

                while (1 == 1) {
                    Console.WriteLine("\n\nChoose Which Topics to Put in your mind Map.");
                    string topic = Console.ReadLine();
                    if (topic == "42069") break;
                    m.RelatedArticles.Add(new WikiArticle(topic));
                    Console.WriteLine("Loading...");
                    m.RelatedArticles.Last().fetch();
                    await Task.Delay(2000);
                    Console.WriteLine("Loaded");
                }


                var client = new AIHordeClient("vIMYMtiJHwoMlQVBLaVBcA");
                m.Prompt = m.ToString();
                string conversation = $"You are WikiGraph a Wikipedia MindGrapher Summerizer. Do not use any Latex or MarkDown or xml. Do not repeat yourself or answer questions twice. Do not think just send your response in this form WikiGraph:[Response]. Here is some topics and there summary's connect them together: {m.Prompt}. Now begin summary and answer questions from User as Wikigraph. ";
                string conversation_display = "";
        
        while (1 == 1) {
            Console.Write("Ask a question or for Clarification: ");
            string User_Ask = Console.ReadLine();
            conversation += "\nUser: " + User_Ask;
            conversation_display += "\nUser: " + User_Ask;
            string result = await client.GenerateTextAsync(conversation);
            conversation_display += result;
            Console.Clear();
            Console.WriteLine(conversation_display);
        }
    }
}