using AiApi.Web.Models;
using AiApi.Web.Services.Chat;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace AiApi.Web.Controllers;

/// <summary>
/// Handles AI chat requests grounded in your company documents.
/// Every answer comes exclusively from the indexed knowledge base.
/// If the answer is not found, a configured fallback message is returned
/// instead of the model guessing or making something up.
/// </summary>
[ApiController]
[Route("api/document-chat")]
public class DocumentChatController(
    IDocumentChatService chatService,
    ILogger<DocumentChatController> logger) : ControllerBase
{
    // ── POST /api/document-chat/ask ────────────────────────────────────────
    // Waits for the full grounded answer before responding.
    // The response includes which source documents were used
    // and whether the answer came from the knowledge base or is a fallback.
    [HttpPost("ask")]
    [ProducesResponseType<DocumentChatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Ask(
        [FromBody] DocumentChatRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            logger.LogWarning("Document chat request rejected — question is empty");
            return BadRequest(new { error = "Question cannot be empty." });
        }

        logger.LogInformation(
            "Document chat request received. Question={Question}, SessionId={SessionId}",
            request.Question, request.SessionId ?? "none");

        var response = await chatService.AskAsync(
            request.Question, request.SessionId, ct);

        // Log whether we found relevant context or fell back to the default message
        if (response.IsGrounded)
        {
            logger.LogInformation(
                "Grounded answer generated. Question={Question}, " +
                "ChunksUsed={ChunksUsed}, Sources={Sources}",
                request.Question,
                response.ChunksUsed,
                string.Join(", ", response.Sources));
        }
        else
        {
            logger.LogInformation(
                "No relevant context found — fallback returned. Question={Question}",
                request.Question);
        }

        return Ok(response);
    }

    // ── POST /api/document-chat/ask/stream ────────────────────────────────
    // Streams the grounded answer token-by-token using Server-Sent Events.
    // If no relevant document context is found, the fallback message is
    // streamed in full as a single event instead of being token-by-token.
    [HttpPost("ask/stream")]
    public async Task AskStream(
        [FromBody] DocumentChatRequest request,
        CancellationToken ct)
    {
        // Tell the client and any proxies this is a long-lived SSE stream
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no"); // disables nginx buffering

        logger.LogInformation(
            "Document stream started. Question={Question}, SessionId={SessionId}",
            request.Question, request.SessionId ?? "none");

        var tokenCount = 0;

        try
        {
            await foreach (var token in chatService.AskStreamAsync(
                               request.Question, request.SessionId, ct))
            {
                // SSE format requires "data: {content}\n\n"
                var bytes = Encoding.UTF8.GetBytes($"data: {token}\n\n");

                await Response.Body.WriteAsync(bytes, ct);
                await Response.Body.FlushAsync(ct); // push each token immediately

                tokenCount++;
            }

            // Signal the client that the stream is complete
            await Response.Body.WriteAsync(
                Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct);

            logger.LogInformation(
                "Document stream completed. Question={Question}, TokensStreamed={Count}",
                request.Question, tokenCount);
        }
        catch (OperationCanceledException)
        {
            // Client closed the browser tab or connection — normal, not an error
            logger.LogInformation(
                "Client disconnected from document stream. " +
                "Question={Question}, TokensStreamed={Count}",
                request.Question, tokenCount);
        }

        // Final flush with a non-cancelled token — ensures the last bytes go out
        await Response.Body.FlushAsync(CancellationToken.None);
    }
}