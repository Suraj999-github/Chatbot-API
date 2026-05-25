namespace AiApi.Web.Configuration
{
    public sealed class DocumentChatOptions
    {
        public const string SectionName = "DocumentChat";

        public string FallbackMessage { get; init; } = "Please contact support for further assistance.";
        public float MinRelevanceScore { get; init; } = 0.65f;
        public int MaxChunksToRetrieve { get; init; } = 5;
        public int MaxChunkSize { get; init; } = 1500;
        public int ChunkOverlap { get; init; } = 200;
        public string CollectionName { get; init; } = "company_documents";
        public string SystemPromptTemplate { get; init; } = string.Empty;
    }
}
