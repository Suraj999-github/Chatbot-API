namespace AiApi.Web.Models
{
    public record DocumentChatRequest(
        string Question,
        string? SessionId = null,
        bool Stream = false
    );
}
