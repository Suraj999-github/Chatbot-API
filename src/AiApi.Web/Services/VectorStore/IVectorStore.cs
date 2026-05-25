namespace AiApi.Web.Services.VectorStore
{
    public interface IVectorStore
    {
        Task EnsureCollectionAsync(string collectionName,
                                   uint vectorSize, CancellationToken ct = default);

        Task UpsertAsync(string collectionName, VectorPoint point,
                         CancellationToken ct = default);

        Task UpsertBatchAsync(string collectionName,
                              IEnumerable<VectorPoint> points,
                              CancellationToken ct = default);

        Task<IReadOnlyList<SearchResult>> SearchAsync(
            string collectionName, float[] queryVector,
            int topK = 5, float minScore = 0.65f,
            CancellationToken ct = default);

        Task DeleteDocumentChunksAsync(string collectionName,
                                       string documentId,
                                       CancellationToken ct = default);


    }

    public record VectorPoint(
        string Id,
        float[] Vector,
        string Text,
        Dictionary<string, string> Metadata);


}
