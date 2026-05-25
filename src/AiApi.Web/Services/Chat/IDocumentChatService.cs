using AiApi.Web.Models;

namespace AiApi.Web.Services.Chat
{
    public interface IDocumentChatService
    {
        Task<DocumentChatResponse> AskAsync(
      string question,
      string? sessionId = null,
      CancellationToken ct = default);

        IAsyncEnumerable<string> AskStreamAsync(
            string question,
            string? sessionId = null,
            CancellationToken ct = default);
    }
}
