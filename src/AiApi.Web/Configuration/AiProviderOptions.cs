namespace AiApi.Web.Configuration
{
    public sealed class AiProviderOptions
    {
        public const string SectionName = "AiProvider";

        public string Provider { get; init; } = "Ollama";
        public string BaseUrl { get; init; } = "http://localhost:11434/v1";
        public string ApiKey { get; init; } = string.Empty;

        public string DefaultModel { get; init; } = "llama3";

        public string EmbeddingModel { get; init; } = "nomic-embed-text";

        public int TimeoutSeconds { get; init; } = 120;
        public int MaxRetries { get; init; } = 3;
    }
}
