using AiApi.Web.Configuration;
using AiApi.Web.Models;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AiApi.Web.Services
{
    public sealed class OllamaChatService(
    IHttpClientFactory httpFactory,
    IOptions<AiProviderOptions> options,
    ILogger<OllamaChatService> logger) : IAIChatService
    {
        private static readonly JsonSerializerOptions _jsonOpts =
            new(JsonSerializerDefaults.Web);

        // ── Non-streaming ────────────────────────────────────── 
        public async Task<string> ChatAsync(
            IEnumerable<ChatMessage> messages,
            string? model = null,
            CancellationToken ct = default)
        {
            var client = httpFactory.CreateClient("OllamaClient");
            var payload = BuildPayload(messages, model, stream: false);

            logger.LogInformation("Sending chat request to Ollama. Model={Model}", payload.Model);

            using var response = await client.PostAsJsonAsync(
                "chat/completions", payload, _jsonOpts, ct);

            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync<OllamaResponse>(_jsonOpts, ct);

            return result?.Choices?.FirstOrDefault()?.Message?.Content
                   ?? throw new InvalidOperationException("Empty response from  Ollama");
        }

        // ── Streaming ────────────────────────────────────────── 
        public async IAsyncEnumerable<string> ChatStreamAsync(
            IEnumerable<ChatMessage> messages,
            string? model = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = httpFactory.CreateClient("OllamaClient");
            var payload = BuildPayload(messages, model, stream: true);

            var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(payload, options: _jsonOpts)
            };

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                    continue;
                var data = line["data: ".Length..];
                if (data == "[DONE]") yield break;

                OllamaStreamChunk? chunk = null;
                try
                {
                    chunk = JsonSerializer.Deserialize<OllamaStreamChunk>(data, _jsonOpts);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Failed to deserialize stream chunk:{ Data}", data);
                    continue;
                }

                var token = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
                if (!string.IsNullOrEmpty(token))
                    yield return token;
            }
        }

        private OllamaRequest BuildPayload(
            IEnumerable<ChatMessage> messages, string? model, bool stream) => new(
            Model: model ?? options.Value.DefaultModel,
            Messages: messages.Select(m => new OllamaMessage(m.Role, m.Content)).ToList(),
            Stream: stream
        );
    }
}
