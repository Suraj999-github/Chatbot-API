using AiApi.Web.Configuration;
using AiApi.Web.Services.Embeddings;
using AiApi.Web.Services.VectorStore;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace AiApi.Web.Services
{

    /// <summary>
    /// In-memory vector store for development and testing.
    /// Uses brute-force cosine similarity — no persistence, no external dependencies.
    /// Replace with QdrantVectorStore for production.
    /// </summary>
    public sealed class InMemoryVectorStore : IVectorStore
    {
        // collectionName → list of stored points
        private readonly ConcurrentDictionary<string, List<VectorPoint>> _store = new();
        private readonly SemaphoreSlim _lock = new(1, 1);

        public Task EnsureCollectionAsync(
            string collectionName, uint vectorSize, CancellationToken ct = default)
        {
            // Just ensure the key exists — no schema enforcement needed in-memory
            _store.TryAdd(collectionName, []);
            return Task.CompletedTask;
        }

        public Task UpsertAsync(
            string collectionName, VectorPoint point, CancellationToken ct = default) =>
            UpsertBatchAsync(collectionName, [point], ct);

        public async Task UpsertBatchAsync(
            string collectionName,
            IEnumerable<VectorPoint> points,
            CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var collection = _store.GetOrAdd(collectionName, []);

                foreach (var point in points)
                {
                    // Replace existing point with same ID (upsert semantics)
                    var existing = collection.FindIndex(p => p.Id == point.Id);
                    if (existing >= 0)
                        collection[existing] = point;
                    else
                        collection.Add(point);
                }
            }
            finally { _lock.Release(); }
        }

        public async Task<IReadOnlyList<SearchResult>> SearchAsync(
            string collectionName,
            float[] queryVector,
            int topK = 5,
            float minScore = 0.65f,
            CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!_store.TryGetValue(collectionName, out var collection) ||
                    collection.Count == 0)
                    return [];

                return collection
                    .Select(point => new SearchResult(
                        Id: point.Id,
                        Text: point.Text,
                        Score: CosineSimilarity(queryVector, point.Vector),
                        Metadata: point.Metadata))
                    .Where(r => r.Score >= minScore)
                    .OrderByDescending(r => r.Score)
                    .Take(topK)
                    .ToList();
            }
            finally { _lock.Release(); }
        }

        public async Task DeleteDocumentChunksAsync(
            string collectionName, string documentId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!_store.TryGetValue(collectionName, out var collection)) return;

                collection.RemoveAll(p =>
                    p.Metadata.TryGetValue("document_id", out var id) &&
                    id == documentId);
            }
            finally { _lock.Release(); }
        }

        // ── Cosine similarity: measures angle between two vectors ──────────────
        // Returns 1.0 = identical, 0.0 = unrelated, -1.0 = opposite
        private static float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length)
                throw new ArgumentException(
                    $"Vector length mismatch: {a.Length} vs {b.Length}");

            float dot = 0f, magA = 0f, magB = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }

            float denominator = MathF.Sqrt(magA) * MathF.Sqrt(magB);
            return denominator == 0f ? 0f : dot / denominator;
        }
    }

    public class RagService(
        IAIChatService chatService,
        IEmbeddingService embedder,
        IVectorStore vectorStore,
        IOptions<DocumentChatOptions> opts,
        ILogger<RagService> logger)
    {
        private DocumentChatOptions Opts => opts.Value;

        public async Task<RagResponse> QueryAsync(
            string userQuestion, CancellationToken ct = default)
        {
            // Step 1: Embed the question into a vector
            var questionVector = await embedder.EmbedAsync(userQuestion, ct);

            // Step 2: Find semantically similar chunks in the vector store
            var chunks = await vectorStore.SearchAsync(
                Opts.CollectionName,
                questionVector,
                topK: Opts.MaxChunksToRetrieve,
                minScore: Opts.MinRelevanceScore,
                ct: ct);

            logger.LogInformation(
                "RAG: '{Question}' matched {Count} chunks (threshold: {Score})",
                userQuestion, chunks.Count, Opts.MinRelevanceScore);

            // Step 3: No relevant chunks found — return fallback
            if (chunks.Count == 0)
            {
                return new RagResponse(
                    Answer: Opts.FallbackMessage,
                    IsGrounded: false,
                    Sources: [],
                    ChunksUsed: 0);
            }

            // Step 4: Build grounded prompt with retrieved context
            var context = string.Join("\n\n---\n\n",
                chunks.Select((c, i) =>
                {
                    var source = c.Metadata.GetValueOrDefault("file_name", "Company Document");
                    return $"[Source {i + 1}: {source}]\n{c.Text}";
                }));

            var systemPrompt = $"""
            You are a helpful customer service assistant.
            Answer the user's question using ONLY the context provided below.
            If the context does not contain enough information, respond with exactly:
            INSUFFICIENT_CONTEXT

            CONTEXT:
            {context}
            """;

            var messages = new[]
            {
            new ChatMessage("system", systemPrompt),
            new ChatMessage("user",   userQuestion),
        };

            // Step 5: Generate answer
            var answer = await chatService.ChatAsync(messages, ct: ct);

            // Step 6: If LLM signals it couldn't answer, return fallback
            if (answer.Contains("INSUFFICIENT_CONTEXT", StringComparison.OrdinalIgnoreCase))
            {
                return new RagResponse(
                    Answer: Opts.FallbackMessage,
                    IsGrounded: false,
                    Sources: [],
                    ChunksUsed: 0);
            }

            var sources = chunks
                .Select(c => c.Metadata.GetValueOrDefault("file_name", "unknown"))
                .Distinct()
                .ToList();

            return new RagResponse(
                Answer: answer,
                IsGrounded: true,
                Sources: (IReadOnlyList<string>)sources,
                ChunksUsed: chunks.Count);
        }
    }
    public record RagResponse(
    string Answer,
    bool IsGrounded,
    IReadOnlyList<string> Sources,
    int ChunksUsed);

}
