using AiApi.Web.Configuration;
using AiApi.Web.Models;
using AiApi.Web.Services.Documents;
using AiApi.Web.Services.Embeddings;
using AiApi.Web.Services.VectorStore;
using Microsoft.Extensions.Options;

namespace AiApi.Web.Services.Jobs
{

    // Hosted service — runs as a long-lived background loop
    public sealed class DocumentIndexingWorker(
        DocumentIndexingQueue queue,
        IJobStore jobStore,
        IServiceScopeFactory scopeFactory,
        ILogger<DocumentIndexingWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Document indexing worker started");

            await foreach (var item in queue.ReadAllAsync(stoppingToken))
            {
                await ProcessItemAsync(item, stoppingToken);
            }
        }

        private async Task ProcessItemAsync(
            IndexingWorkItem item, CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "Processing job {JobId} for file '{File}'", item.JobId, item.FileName);

            try
            {
                // Use a fresh DI scope per job — avoids scoped service lifetime issues
                await using var scope = scopeFactory.CreateAsyncScope();
                var parserFactory = scope.ServiceProvider.GetRequiredService<DocumentParserFactory>();
                var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
                var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
                var chatOpts = scope.ServiceProvider.GetRequiredService<IOptions<DocumentChatOptions>>();
                var opts = chatOpts.Value;

                // ── Phase 1: Parse ─────────────────────────────────────────────
                jobStore.Update(item.JobId, j => j.Status = JobStatus.Parsing);

                var parser = parserFactory.GetParser(item.FileName);
                ParsedDocument parsed;

                await using (var fileStream = File.OpenRead(item.TempFilePath))
                {
                    parsed = await parser.ParseAsync(fileStream, item.FileName, stoppingToken);
                }

                if (string.IsNullOrWhiteSpace(parsed.FullText))
                {
                    jobStore.Update(item.JobId, j =>
                    {
                        j.Status = JobStatus.Failed;
                        j.Error = $"No text extracted from '{item.FileName}'. " +
                                   "File may be scanned/image-only.";
                    });
                    return;
                }

                // ── Phase 2: Chunk ─────────────────────────────────────────────
                var chunks = ChunkText(parsed.FullText, opts);
                jobStore.Update(item.JobId, j =>
                {
                    j.Status = JobStatus.Embedding;
                    j.TotalChunks = chunks.Count;
                });

                logger.LogInformation(
                    "Job {JobId}: {Count} chunks to embed", item.JobId, chunks.Count);

                // ── Phase 3: Ensure collection ─────────────────────────────────
                await vectorStore.EnsureCollectionAsync(
                    opts.CollectionName, vectorSize: 768, stoppingToken);

                // ── Phase 4: Embed + store in streaming mini-batches ──────────
                // Process MINI_BATCH chunks at a time — keeps memory flat
                // regardless of document size. Never loads all vectors into RAM.
                const int miniBatch = 5;
                var documentId = Guid.NewGuid().ToString();
                int done = 0;

                for (int i = 0; i < chunks.Count; i += miniBatch)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    var batch = chunks.Skip(i).Take(miniBatch).ToList();
                    var vectors = await EmbedBatchAsync(embedder, batch, stoppingToken);
                    var points = new List<VectorPoint>();

                    for (int j = 0; j < batch.Count; j++)
                    {
                        var chunkIdx = i + j;
                        var metadata = new Dictionary<string, string>(parsed.Metadata)
                        {
                            ["document_id"] = documentId,
                            ["chunk_index"] = chunkIdx.ToString(),
                            ["chunk_total"] = chunks.Count.ToString(),
                            ["file_name"] = item.FileName,
                        };
                        foreach (var (k, v) in item.Metadata) metadata[k] = v;

                        points.Add(new VectorPoint(
                            Id: $"{documentId}_chunk_{chunkIdx}",
                            Vector: vectors[j],
                            Text: batch[j],
                            Metadata: metadata));
                    }

                    // Store this mini-batch immediately — frees vectors from RAM
                    await vectorStore.UpsertBatchAsync(opts.CollectionName, points, stoppingToken);

                    done += batch.Count;
                    jobStore.Update(item.JobId, j => j.ChunksDone = done);

                    logger.LogDebug("Job {JobId}: {Done}/{Total} chunks stored",
                        item.JobId, done, chunks.Count);

                    // Yield to other work between batches — keeps app responsive
                    await Task.Delay(10, stoppingToken);
                }

                // ── Phase 5: Complete ──────────────────────────────────────────
                jobStore.Update(item.JobId, j =>
                {
                    j.Status = JobStatus.Completed;
                    j.DocumentId = documentId;
                    j.ChunksDone = chunks.Count;
                    j.CompletedAt = DateTime.UtcNow;
                });

                logger.LogInformation(
                    "Job {JobId} completed. DocId={DocId}, Chunks={Count}",
                    item.JobId, documentId, chunks.Count);
            }
            catch (OperationCanceledException)
            {
                jobStore.Update(item.JobId, j =>
                {
                    j.Status = JobStatus.Failed;
                    j.Error = "Processing cancelled (server shutting down).";
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {JobId} failed", item.JobId);
                jobStore.Update(item.JobId, j =>
                {
                    j.Status = JobStatus.Failed;
                    j.Error = ex.Message;
                });
            }
            finally
            {
                // Always delete the temp file — prevents disk fill-up
                TryDeleteTemp(item.TempFilePath);
            }
        }

        // Embed a small batch with concurrency=2 to keep RAM low
        private static async Task<float[][]> EmbedBatchAsync(
            IEmbeddingService embedder,
            List<string> texts,
            CancellationToken ct)
        {
            var results = new float[texts.Count][];
            var semaphore = new SemaphoreSlim(2, 2); // max 2 concurrent embeds

            var tasks = texts.Select(async (text, i) =>
            {
                await semaphore.WaitAsync(ct);
                try { results[i] = await embedder.EmbedAsync(text, ct); }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
            return results;
        }

        private static List<string> ChunkText(string text, DocumentChatOptions opts)
        {
            var chunks = new List<string>();
            int size = opts.MaxChunkSize;
            int overlap = opts.ChunkOverlap;
            int start = 0;

            while (start < text.Length)
            {
                int end = Math.Min(start + size, text.Length);

                if (end < text.Length)
                {
                    int boundary = text.LastIndexOfAny(['.', '!', '?', '\n'],
                        end, Math.Min(150, end - start));
                    if (boundary > start) end = boundary + 1;
                }

                var chunk = text[start..end].Trim();
                if (chunk.Length > 50) chunks.Add(chunk);

                start = end - overlap;
                if (start >= text.Length) break;
            }

            return chunks;
        }

        private void TryDeleteTemp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex)
            { logger.LogWarning(ex, "Could not delete temp file: {Path}", path); }
        }
    }
}
