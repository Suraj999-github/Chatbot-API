namespace AiApi.Web.Services.Documents
{

    public class DocumentParserFactory(IEnumerable<IDocumentParser> parsers)
    {
        public IDocumentParser GetParser(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();

            return parsers.FirstOrDefault(p => p.CanHandle(ext))
                   ?? throw new NotSupportedException(
                       $"No parser available for file type '{ext}'. " +
                       "Supported formats: .pdf, .docx");
        }

        public bool IsSupported(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return parsers.Any(p => p.CanHandle(ext));
        }
    }
}
