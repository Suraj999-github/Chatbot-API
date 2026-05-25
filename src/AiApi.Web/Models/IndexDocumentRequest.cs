namespace AiApi.Web.Models
{
    public record IndexDocumentResponse(
        string DocumentId,
        string FileName,
        int ChunksCreated,
        int PageCount,
        string Message
    );
}
