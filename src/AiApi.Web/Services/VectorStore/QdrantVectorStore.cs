using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AiApi.Web.Services.VectorStore
{
    public class QdrantVectorStore(
        QdrantClient qdrant,
        ILogger<QdrantVectorStore> logger) : IVectorStore
    {
        public async Task EnsureCollectionAsync(string collectionName, uint vectorSize, CancellationToken ct = default)
        {
            var collections = await qdrant.ListCollectionsAsync(ct);

            if (collections.Contains(collectionName))
                return;

            logger.LogInformation("Creating Qdrant collection: {Name}", collectionName);

            await qdrant.CreateCollectionAsync(
                collectionName,
                new VectorParams
                {
                    Size = vectorSize,
                    Distance = Distance.Cosine
                },
                cancellationToken: ct);
        }
        public Task UpsertAsync(string collectionName,
            VectorPoint point, CancellationToken ct = default) =>
            UpsertBatchAsync(collectionName, [point], ct);

        public async Task UpsertBatchAsync(string collectionName,
            IEnumerable<VectorPoint> points, CancellationToken ct = default)
        {
            var qdrantPoints = points.Select(p =>
            {
                var payload = new Dictionary<string, Value>
                {
                    ["text"] = p.Text,
                    ["document_id"] = p.Metadata.GetValueOrDefault("document_id", ""),
                    ["source"] = p.Metadata.GetValueOrDefault("source", ""),
                    ["chunk_index"] = p.Metadata.GetValueOrDefault("chunk_index", "0"),
                };
                foreach (var (k, v) in p.Metadata) payload[k] = v;

                return new PointStruct
                {
                    Id = new PointId { Uuid = p.Id },
                    Vectors = p.Vector,
                    Payload = { payload },
                };
            }).ToList();

            await qdrant.UpsertAsync(collectionName, qdrantPoints,
                cancellationToken: ct);

            logger.LogDebug("Upserted {Count} vectors into {Collection}",
                qdrantPoints.Count, collectionName);
        }

        public async Task<IReadOnlyList<SearchResult>> SearchAsync(
            string collectionName, float[] queryVector,
            int topK = 5, float minScore = 0.65f, CancellationToken ct = default)
        {
            var results = await qdrant.SearchAsync(
                collectionName, queryVector,
                limit: (ulong)topK,
                scoreThreshold: minScore,
                cancellationToken: ct);
            return results.Select(r => new SearchResult(
               Id: r.Id.Uuid,
               Text: r.Payload["text"].StringValue,
               Score: r.Score,
               Metadata: r.Payload
                   .Where(kv => kv.Key != "text")
                   .ToDictionary(kv => kv.Key, kv => kv.Value.StringValue)
           )).ToList();
        }

        public async Task DeleteDocumentChunksAsync(
            string collectionName, string documentId, CancellationToken ct = default)
        {
            // Delete all chunks belonging to a document by filtering on document_id field 
            await qdrant.DeleteAsync(collectionName,
                new Filter
                {
                    Must = {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key   = "document_id",
                            Match = new Match { Text = documentId }
                        }
                    }
                    }
                },
                cancellationToken: ct);

            logger.LogInformation("Deleted all chunks for document: {DocId}", documentId);
        }
    }
}
