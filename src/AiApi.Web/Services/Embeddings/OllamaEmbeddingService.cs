
using AiApi.Web.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

namespace AiApi.Web.Services.Embeddings;

public class OllamaEmbeddingService(
    IHttpClientFactory factory,
    IOptions<AiProviderOptions> opts,
    ILogger<OllamaEmbeddingService> logger) : IEmbeddingService
{
    private string EmbedModel => opts.Value.EmbeddingModel ?? "nomic-embed-text";

    // Embed 3 chunks in parallel — safe for local Ollama without overwhelming it
    private const int MaxConcurrency = 3;

    private record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt);

    private record EmbedResponse(
        [property: JsonPropertyName("embedding")] float[] Embedding);

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var baseUrl = opts.Value.BaseUrl
            .TrimEnd('/')
            .Replace("/v1", "");

        //var client = factory.CreateClient("OllamaClient");
        // In OllamaEmbeddingService.EmbedAsync — change the client name:
        var client = factory.CreateClient("OllamaEmbedClient"); // ← was "OllamaClient"



        var payload = new EmbedRequest(EmbedModel, text);

        // var response = await client.PostAsJsonAsync(
        // $"{baseUrl}/api/embeddings", payload, ct);

        // And simplify the URL since BaseAddress already excludes /v1:
        var response = await client.PostAsJsonAsync("api/embeddings", payload, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<EmbedResponse>(cancellationToken: ct);

        return result?.Embedding
               ?? throw new InvalidOperationException("Empty embedding from Ollama");
    }

    // Parallel batch with bounded concurrency — replaces the slow sequential loop
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        var results = new float[textList.Count][];
        var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        var tasks = textList.Select(async (text, i) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                logger.LogDebug("Embedding chunk {I}/{Total}", i + 1, textList.Count);
                results[i] = await EmbedAsync(text, ct);
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        return results;
    }
}
