namespace AiApi.Web.Services.Embeddings
{

    public interface IEmbeddingService
    {
        Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
        Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IEnumerable<string> texts, CancellationToken ct = default);
    }
}
