namespace AiApi.Web.Services.Documents
{

    public interface IDocumentParser
    {
        /// <summary>Returns true if this parser handles the given file extension.</summary> 
        bool CanHandle(string fileExtension);

        /// <summary>Extract all text content from the document stream.</summary> 
        Task<ParsedDocument> ParseAsync(Stream stream, string fileName,
                                        CancellationToken ct = default);
    }

    public record ParsedDocument(
        string FileName,
        string FullText,
        int PageCount,
         Dictionary<string, string> Metadata
     );
}
