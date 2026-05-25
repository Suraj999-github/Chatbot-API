namespace AiApi.Web.Services
{
    public interface IAIChatService
    {
        Task<string> ChatAsync(
            IEnumerable<ChatMessage> messages,
            string? model = null,
            CancellationToken ct = default);

        IAsyncEnumerable<string> ChatStreamAsync(
            IEnumerable<ChatMessage> messages,
            string? model = null,
            CancellationToken ct = default);
    }
    public record ChatMessage(string Role, string Content);
}
