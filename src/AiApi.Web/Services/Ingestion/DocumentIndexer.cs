using AiApi.Web.Configuration;
using AiApi.Web.Services.Documents;
using AiApi.Web.Services.Embeddings;
using AiApi.Web.Services.VectorStore;
using Microsoft.Extensions.Options;

namespace AiApi.Web.Services.Ingestion;

public class DocumentIndexer(
    DocumentParserFactory parserFactory,
    IEmbeddingService embedder,
    IVectorStore vectorStore,
    IOptions<DocumentChatOptions> opts,
    ILogger<DocumentIndexer> logger) : IDocumentIndexer
{
    private DocumentChatOptions Opts => opts.Value;

    public async Task<IndexResult> IndexDocumentAsync(
        Stream stream, string fileName,
        Dictionary<string, string>? extraMetadata = null,
        CancellationToken ct = default)
    {
        // ── Step 1: Parse ──────────────────────────────────────────────────
        logger.LogInformation("Parsing document: {File}", fileName);
        var parser = parserFactory.GetParser(fileName);
        var parsed = await parser.ParseAsync(stream, fileName, ct);

        if (string.IsNullOrWhiteSpace(parsed.FullText))
            throw new InvalidOperationException(
                $"No text could be extracted from '{fileName}'. " +
                $"Pages parsed: {parsed.PageCount}. " +
                "Ensure the file is not a scanned/image-only PDF.");

        var avgCharsPerPage = parsed.FullText.Length / Math.Max(parsed.PageCount, 1);
        if (avgCharsPerPage < 100)
            logger.LogWarning(
                "'{File}' extracted only {Avg} chars/page — quality may be low",
                fileName, avgCharsPerPage);

        // ── Step 2: Chunk ──────────────────────────────────────────────────
        var chunks = ChunkText(parsed.FullText);
        logger.LogInformation("'{File}' → {Count} chunks to embed", fileName, chunks.Count);

        // ── Step 3: Ensure collection ──────────────────────────────────────
        await vectorStore.EnsureCollectionAsync(Opts.CollectionName, vectorSize: 768, ct);

        // ── Step 4: Embed ALL chunks in parallel batch ─────────────────────
        // This replaces the old one-by-one loop — 3-5x faster
        logger.LogInformation("Embedding {Count} chunks in parallel...", chunks.Count);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var vectors = await embedder.EmbedBatchAsync(chunks, ct);

        sw.Stop();
        logger.LogInformation(
            "Embedded {Count} chunks in {Elapsed:N1}s ({Rate:N0} chunks/sec)",
            chunks.Count, sw.Elapsed.TotalSeconds,
            chunks.Count / Math.Max(sw.Elapsed.TotalSeconds, 0.001));

        // ── Step 5: Build vector points ────────────────────────────────────
        var documentId = Guid.NewGuid().ToString();
        var points = chunks.Select((chunk, i) =>
        {
            var metadata = new Dictionary<string, string>(parsed.Metadata)
            {
                ["document_id"] = documentId,
                ["chunk_index"] = i.ToString(),
                ["chunk_total"] = chunks.Count.ToString(),
                ["file_name"] = fileName,
            };
            if (extraMetadata != null)
                foreach (var (k, v) in extraMetadata)
                    metadata[k] = v;

            return new VectorPoint(
                Id: $"{documentId}_chunk_{i}",
                Vector: vectors[i],
                Text: chunk,
                Metadata: metadata);
        }).ToList();

        // ── Step 6: Upsert to Qdrant in batches of 50 ────────────────────
        // Qdrant handles large batches well; 50 is safe without hitting gRPC limits
        const int qdrantBatchSize = 50;
        for (int i = 0; i < points.Count; i += qdrantBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = points.Skip(i).Take(qdrantBatchSize).ToList();
            await vectorStore.UpsertBatchAsync(Opts.CollectionName, batch, ct);
            logger.LogDebug("Stored {Done}/{Total} chunks in Qdrant",
                Math.Min(i + qdrantBatchSize, points.Count), points.Count);
        }

        logger.LogInformation(
            "✓ '{File}' indexed. DocId={DocId}, Chunks={Count}, Pages={Pages}",
            fileName, documentId, chunks.Count, parsed.PageCount);

        return new IndexResult(documentId, fileName, chunks.Count, parsed.PageCount);
    }

    public Task RemoveDocumentAsync(string documentId, CancellationToken ct = default) =>
        vectorStore.DeleteDocumentChunksAsync(Opts.CollectionName, documentId, ct);

    private List<string> ChunkText(string text)
    {
        var chunks = new List<string>();
        int size = Opts.MaxChunkSize;
        int overlap = Opts.ChunkOverlap;
        int start = 0;

        while (start < text.Length)
        {
            int end = Math.Min(start + size, text.Length);

            if (end < text.Length)
            {
                // Break at sentence boundary — search back up to 150 chars
                int boundary = text.LastIndexOfAny(['.', '!', '?', '\n'],
                    end, Math.Min(150, end - start));
                if (boundary > start) end = boundary + 1;
            }

            var chunk = text[start..end].Trim();
            if (chunk.Length > 50)
                chunks.Add(chunk);

            start = end - overlap;
            if (start >= text.Length) break; // prevent infinite loop on short text
        }

        return chunks;
    }
}

public record IndexResult(
    string DocumentId, string FileName,
    int ChunksCreated, int PageCount);