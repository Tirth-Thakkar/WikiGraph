using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class AIHordeClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public AIHordeClient(string apiKey = "vIMYMtiJHwoMlQVBLaVBcA")
    {
        _apiKey = apiKey;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("apikey", _apiKey);
    }

    public async Task<string> GenerateTextAsync(string prompt)
    {
        // Step 1: Submit request
        var submitUrl = "https://aihorde.net/api/v2/generate/text/async";

        var payload = new
        {
            prompt = prompt,
            @params = new
            {
                max_length = 300,
                temperature = 0.7
            },
            models = new[] { "aphrodite/TheDrummer/Behemoth-X-123B-v2.1", "koboldcpp/Qwen3.5-27B-heretic" }
        };
        //, "aphrodite/TheDrummer/Rocinante-X-12B-v1", "aphrodite/TheDrummer/Skyfall-31B-v4.1", "koboldcpp/L3-8B-Stheno-v3.2", "koboldcpp/Llama-3.2-3B", "koboldcpp/Llama-3-Lumimaid-8B-v0.1", "koboldcpp/Meta-Llama-3-8B-Instruct-abliterated-v3.i1-Q4_K_M", "koboldcpp/mini-magnum-12b-v1.1", "koboldcpp/NeonMaid-12B-v2", "koboldcpp/pygmalion-2-7b.Q4_K_M", "koboldcpp/Skyfall-31B-v4.2", "TheDrummer/Cydonia-24B-v4.3"
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        var submitResponse = await _httpClient.PostAsync(submitUrl, content);
        submitResponse.EnsureSuccessStatusCode();

        var submitJson = await submitResponse.Content.ReadAsStringAsync();
        var submitData = JsonSerializer.Deserialize<JsonElement>(submitJson);

        string requestId = submitData.GetProperty("id").GetString();

        // Step 2: Poll for result
        var statusUrl = $"https://aihorde.net/api/v2/generate/text/status/{requestId}";
        Console.WriteLine(requestId);
        while (true)
        {
            var statusResponse = await _httpClient.GetAsync(statusUrl);
            statusResponse.EnsureSuccessStatusCode();

            var statusJson = await statusResponse.Content.ReadAsStringAsync();
            var statusData = JsonSerializer.Deserialize<JsonElement>(statusJson);


            bool done = statusData.GetProperty("done").GetBoolean();
            Console.WriteLine($"Queue Position: {statusData.GetProperty("queue_position").GetInt32()} Wait Time: {statusData.GetProperty("wait_time").GetInt32()}");
            if (done)
            {
                var generations = statusData.GetProperty("generations");

                if (generations.GetArrayLength() > 0)
                {
                    return generations[0].GetProperty("text").GetString();
                }

                return "No text generated.";
            }

            await Task.Delay(3000);
        }
    }
}