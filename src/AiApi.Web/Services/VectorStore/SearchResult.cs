namespace AiApi.Web.Services.VectorStore
{
    public record SearchResult(
         string Id, string Text, float Score,
         Dictionary<string, string> Metadata);
}
