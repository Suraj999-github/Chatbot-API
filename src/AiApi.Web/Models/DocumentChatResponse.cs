namespace AiApi.Web.Models
{
    public record DocumentChatResponse(
    string Answer,
    bool IsGrounded,
    IReadOnlyList<string> Sources,
    int ChunksUsed
);
}
