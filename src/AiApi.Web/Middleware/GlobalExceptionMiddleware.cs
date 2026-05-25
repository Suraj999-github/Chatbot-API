using System.Net;
using System.Text.Json;

namespace AiApi.Web.Middleware
{
    /// <summary>
    /// Middleware for handling unhandled exceptions globally.
    /// Converts exceptions into consistent JSON responses.
    /// </summary>
    public sealed class GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger)
    {
        /// <summary>
        /// Executes the middleware pipeline.
        /// </summary>
        /// <param name="ctx">Current HTTP context.</param>
        public async Task InvokeAsync(HttpContext ctx)
        {
            try
            {
                // Continue request pipeline
                await next(ctx);
            }
            catch (OperationCanceledException) when
                (ctx.RequestAborted.IsCancellationRequested)
            {
                // Client disconnected before request completed
                logger.LogInformation(
                    "Request cancelled by client. Path: {Path}",
                    ctx.Request.Path);
            }
            catch (HttpRequestException ex)
            {
                // External AI provider/network related failure
                logger.LogError(
                    ex,
                    "AI provider HTTP error occurred. Path: {Path}",
                    ctx.Request.Path);

                await WriteErrorAsync(
                    ctx,
                    HttpStatusCode.BadGateway,
                    "AI provider is unavailable. Is Ollama running?");
            }
            catch (Exception ex)
            {
                // Unexpected application error
                logger.LogError(
                    ex,
                    "Unhandled exception occurred. Path: {Path}",
                    ctx.Request.Path);

                await WriteErrorAsync(
                    ctx,
                    HttpStatusCode.InternalServerError,
                    "An unexpected error occurred.");
            }
        }

        /// <summary>
        /// Writes standardized JSON error response.
        /// </summary>
        /// <param name="ctx">HTTP context.</param>
        /// <param name="status">HTTP status code.</param>
        /// <param name="message">Error message.</param>
        private static async Task WriteErrorAsync(
            HttpContext ctx,
            HttpStatusCode status,
            string message)
        {
            // Avoid writing response multiple times
            if (ctx.Response.HasStarted)
                return;

            ctx.Response.Clear();
            ctx.Response.StatusCode = (int)status;
            ctx.Response.ContentType = "application/json";

            var response = new
            {
                error = message,
                status = (int)status,
                traceId = ctx.TraceIdentifier
            };

            var json = JsonSerializer.Serialize(response);

            await ctx.Response.WriteAsync(json);
        }
    }
}