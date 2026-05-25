using AiApi.Web.Models;
using AiApi.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace AiApi.Web.Controllers;

/// <summary>
/// Handles direct LLM chat requests via Ollama.
/// Supports both standard (wait for full reply) and streaming (token-by-token) modes.
/// This controller talks to the LLM directly — it does NOT use document context.
/// Use DocumentChatController for document-grounded answers.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ChatController(
    IAIChatService chatService,
    ILogger<ChatController> logger) : ControllerBase
{
    // ── POST /api/chat ─────────────────────────────────────────────────────
    // Standard chat: waits for the full response before returning.
    // Best for programmatic use where you process the whole answer at once.
    [HttpPost]
    [ProducesResponseType<ChatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Chat(
        [FromBody] ChatRequest request,
        CancellationToken ct)
    {
        if (request.Messages is not { Count: > 0 })
        {
            logger.LogWarning("Chat request rejected — no messages provided");
            return BadRequest("At least one message is required.");
        }

        var model = request.Model ?? "default";

        logger.LogInformation(
            "Chat request received. Model={Model}, MessageCount={Count}",
            model, request.Messages.Count);

        var messages = request.Messages
            .Select(m => new ChatMessage(m.Role, m.Content));

        var content = await chatService.ChatAsync(messages, request.Model, ct);

        logger.LogInformation(
            "Chat response generated. Model={Model}, ResponseLength={Length}",
            model, content.Length);

        return Ok(new ChatResponse(content, model));
    }

    // ── POST /api/chat/stream ──────────────────────────────────────────────
    // Streaming chat: sends tokens to the client as they are generated.
    // Uses Server-Sent Events (SSE) — the client receives data: {token} lines
    // in real time. Best for chat UIs where you want word-by-word output.
    [HttpPost("stream")]
    public async Task ChatStream(
        [FromBody] ChatRequest request,
        CancellationToken ct)
    {
        // Tell the client and any proxies this is a long-lived SSE stream
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no"); // disables nginx buffering

        var model = request.Model ?? "default";

        logger.LogInformation(
            "Streaming chat started. Model={Model}, MessageCount={Count}",
            model, request.Messages?.Count ?? 0);

        var messages = request.Messages
            .Select(m => new ChatMessage(m.Role, m.Content));

        var tokenCount = 0;

        try
        {
            await foreach (var token in chatService.ChatStreamAsync(
                               messages, request.Model, ct))
            {
                // SSE format requires "data: {content}\n\n"
                var sseEvent = $"data: {token}\n\n";
                var bytes = Encoding.UTF8.GetBytes(sseEvent);

                await Response.Body.WriteAsync(bytes, ct);
                await Response.Body.FlushAsync(ct); // push each token immediately

                tokenCount++;
            }

            // Signal the client that the stream is finished
            await Response.Body.WriteAsync(
                Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct);

            logger.LogInformation(
                "Streaming chat completed. Model={Model}, TokensStreamed={Count}",
                model, tokenCount);
        }
        catch (OperationCanceledException)
        {
            // Client closed the connection — this is normal, not an error
            logger.LogInformation(
                "Client disconnected from stream. Model={Model}, TokensStreamed={Count}",
                model, tokenCount);
        }

        // Final flush with a non-cancelled token — ensures the last bytes go out
        // even if the request CancellationToken was already cancelled
        await Response.Body.FlushAsync(CancellationToken.None);
    }

    // ── GET /api/chat/models ───────────────────────────────────────────────
    // Returns the list of models currently available in Ollama.
    // Useful for verifying which models are pulled and ready to use.
    [HttpGet("models")]
    public async Task<IActionResult> GetModels(
        [FromServices] IHttpClientFactory factory,
        CancellationToken ct)
    {
        logger.LogInformation("Fetching available models from Ollama");

        var client = factory.CreateClient("OllamaClient");
        var tags = await client.GetFromJsonAsync<object>("/api/tags", ct);

        logger.LogInformation("Successfully retrieved model list from Ollama");

        return Ok(tags);
    }
}