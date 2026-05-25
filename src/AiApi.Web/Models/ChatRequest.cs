namespace AiApi.Web.Models
{
    public record ChatRequest(
        List<MessageDto> Messages,
        string? Model = null,
        bool Stream = false
    );
    public record MessageDto(string Role, string Content);

}
