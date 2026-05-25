namespace AiApi.Web.Services.Ingestion
{
    public interface IDocumentIndexer
    {
        Task<IndexResult> IndexDocumentAsync(
      Stream stream, string fileName,
      Dictionary<string, string>? metadata = null,
      CancellationToken ct = default);

        Task RemoveDocumentAsync(string documentId, CancellationToken ct = default);
    }
}
