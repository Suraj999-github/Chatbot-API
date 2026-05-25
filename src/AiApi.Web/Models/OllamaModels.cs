using System.Text.Json.Serialization;

namespace AiApi.Web.Models
{
    public class OllamaModels
    {
    }

    public record OllamaRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<OllamaMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream
    );

    public record OllamaMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );

    public record OllamaResponse(
        [property: JsonPropertyName("choices")] List<OllamaChoice>? Choices
    );

    public record OllamaChoice(
        [property: JsonPropertyName("message")] OllamaMessage? Message,
        [property: JsonPropertyName("delta")] OllamaMessage? Delta
    );

    public record OllamaStreamChunk(
        [property: JsonPropertyName("choices")] List<OllamaChoice>? Choices
    );
}
