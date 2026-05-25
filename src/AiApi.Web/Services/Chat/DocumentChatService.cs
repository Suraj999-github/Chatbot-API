using AiApi.Web.Configuration;
using AiApi.Web.Models;
using AiApi.Web.Services.Embeddings;
using AiApi.Web.Services.VectorStore;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace AiApi.Web.Services.Chat
{

    public class DocumentChatService(
        IAIChatService chatService,
        IEmbeddingService embedder,
        IVectorStore vectorStore,
        IOptions<DocumentChatOptions> opts,
        ILogger<DocumentChatService> logger) : IDocumentChatService
    {
        private DocumentChatOptions Opts => opts.Value;

        // ── Non-streaming ────────────────────────────────────────────────────── 
        public async Task<DocumentChatResponse> AskAsync(
            string question, string? sessionId = null, CancellationToken ct = default)
        {
            var (messages, chunks, isGrounded) =
                await BuildGroundedContextAsync(question, ct);

            if (!isGrounded)
            {
                logger.LogInformation(
                    "No relevant context found for: '{Q}' — returning fallback", question);
                return new DocumentChatResponse(
                    Answer: Opts.FallbackMessage,
                    IsGrounded: false,
                    Sources: [],
                    ChunksUsed: 0);
            }

            var answer = await chatService.ChatAsync(messages, ct: ct);

            // If the LLM itself signals insufficient context, return fallback 
            if (answer.Contains("INSUFFICIENT_CONTEXT", StringComparison.OrdinalIgnoreCase))
            {
                return new DocumentChatResponse(
                    Answer: Opts.FallbackMessage,
                    IsGrounded: false,
                    Sources: [],
                    ChunksUsed: 0);
            }
            var sources = chunks
                        .Select(c => c.Metadata.GetValueOrDefault("file_name", "unknown") ?? "unknown")
                        .Distinct()
                        .ToList();

            return new DocumentChatResponse(
                Answer: answer,
                IsGrounded: true,
                Sources: (IReadOnlyList<string>)sources,
                ChunksUsed: chunks.Count);
        }

        // ── Streaming ────────────────────────────────────────────────────────── 
        public async IAsyncEnumerable<string> AskStreamAsync(
            string question, string? sessionId = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var (messages, chunks, isGrounded) =
                await BuildGroundedContextAsync(question, ct);

            if (!isGrounded)
            {
                yield return Opts.FallbackMessage;
                yield break;
            }

            // Buffer and check for INSUFFICIENT_CONTEXT signal 
            var buffer = new System.Text.StringBuilder();
            var isInsufficient = false;

            await foreach (var token in chatService.ChatStreamAsync(messages, ct: ct))
            {
                buffer.Append(token);

                // Check if response is the fallback signal 
                if (buffer.ToString().Contains("INSUFFICIENT_CONTEXT"))
                {
                    isInsufficient = true;
                    break;
                }

                yield return token;
            }
            if (isInsufficient)
            {
                yield return Opts.FallbackMessage;
            }
        }

        // ── Core RAG logic ───────────────────────────────────────────────────── 
        private async Task<(IEnumerable<ChatMessage> Messages,
                             IReadOnlyList<SearchResult> Chunks,
                             bool IsGrounded)>
            BuildGroundedContextAsync(string question, CancellationToken ct)
        {
            // Step 1: Embed the question 
            var questionVector = await embedder.EmbedAsync(question, ct);

            // Step 2: Similarity search in Qdrant 
            var chunks = await vectorStore.SearchAsync(
                Opts.CollectionName,
                questionVector,
                topK: Opts.MaxChunksToRetrieve, minScore: Opts.MinRelevanceScore,
            ct: ct);

            logger.LogInformation("RAG search: '{Q}' → {Count} chunks (min score: {Score})", question, chunks.Count, Opts.MinRelevanceScore);

            if (chunks.Count == 0)
                return ([], [], false);

            // Step 3: Build grounded prompt 
            var context = BuildContextString(chunks);

            var systemPrompt = $""" 
            {Opts.SystemPromptTemplate} 
  
            ═══════════════════════════════════════ 
            COMPANY KNOWLEDGE BASE CONTEXT: 
            ═══════════════════════════════════════ 
            {context} 
            ═══════════════════════════════════════ 
  
            IMPORTANT RULES: 
            - Answer ONLY from the context above 
            - If the context does not contain the answer, respond with exactly: INSUFFICIENT_CONTEXT 
            - Do not make assumptions or add external information 
            - Be concise and helpful 
            - If citing information, mention the source document name 
            """;

            var messages = new[]
            {
            new ChatMessage("system", systemPrompt),
            new ChatMessage("user",   question),
        };

            return (messages, chunks, true);
        }
        private static string BuildContextString(IReadOnlyList<SearchResult> chunks) =>
          string.Join("---", chunks.Select((c, i) =>
        {

            var source = c.Metadata.GetValueOrDefault("file_name", "Company Document");
            var page = c.Metadata.GetValueOrDefault("chunk_index", "");
            return $"[Source: {source}, Section: {page}] {c.Text}";
        }));
    }
}
