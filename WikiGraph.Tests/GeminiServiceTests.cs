using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using WikiGraph.Api.Application.Models;
using WikiGraph.Api.Application.Services;
using WikiGraph.Api.Configuration;
using WikiGraph.Contracts;
using Xunit;

namespace WikiGraph.Tests;

public sealed class GeminiServiceTests
{
    [Fact]
    public async Task GenerateReplyAsync_UsesKeyedGeminiChatServiceWhenApiKeyIsConfigured()
    {
        using var provider = BuildProvider(new FakeChatCompletionService("""
            {
              "answer": "AI generated answer about the cup controversy.",
              "relatedTopics": ["Cup controversy", "Public response"]
            }
            """));
        var geminiService = BuildGeminiService(provider);
        var article = new WikiArticle("McDonald's", "https://en.wikipedia.org/wiki/McDonald%27s")
        {
            Summary = "McDonald's is an American fast-food restaurant chain."
        };

        var reply = await geminiService.GenerateReplyAsync(
            "Tell me about the cups scandal",
            article,
            Array.Empty<MessageDto>(),
            Array.Empty<WikiMatch>());

        Assert.Equal("AI generated answer about the cup controversy.", reply.Answer);
        Assert.Contains("Cup controversy", reply.RelatedTopics);
    }

    [Fact]
    public async Task PlanWikipediaLookupAsync_UsesKeyedGeminiChatServiceWhenApiKeyIsConfigured()
    {
        using var provider = BuildProvider(new FakeChatCompletionService("""
            {
              "searchQuery": "McDonald's cup controversy",
              "focusPhrases": ["cup scandal", "public criticism"]
            }
            """));
        var geminiService = BuildGeminiService(provider);

        var plan = await geminiService.PlanWikipediaLookupAsync(
            "McDonalds Cups Scanadal",
            null,
            Array.Empty<MessageDto>());

        Assert.Equal("McDonald's cup controversy", plan.SearchQuery);
        Assert.Contains("cup scandal", plan.FocusPhrases);
    }

    private static ServiceProvider BuildProvider(IChatCompletionService chatCompletionService)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatCompletionService>("wikigraph-chat", chatCompletionService);
        return services.BuildServiceProvider();
    }

    private static GeminiService BuildGeminiService(IServiceProvider provider)
    {
        return new GeminiService(
            provider,
            Options.Create(new GeminiOptions { ApiKey = "test-key" }),
            NullLogger<GeminiService>.Instance);
    }

    private sealed class FakeChatCompletionService : IChatCompletionService
    {
        private readonly Queue<string> _responses;

        public FakeChatCompletionService(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var response = _responses.Count == 0 ? string.Empty : _responses.Dequeue();
            IReadOnlyList<ChatMessageContent> messages = [new ChatMessageContent(AuthorRole.Assistant, response)];
            return Task.FromResult(messages);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
