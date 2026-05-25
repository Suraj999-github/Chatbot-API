using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
namespace AiApi.Web.Services.Documents
{
    public class WordDocumentParser : IDocumentParser
    {
        public bool CanHandle(string ext) =>
            ext.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".doc", StringComparison.OrdinalIgnoreCase);

        public Task<ParsedDocument> ParseAsync(
            Stream stream, string fileName, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var doc = WordprocessingDocument.Open(stream, isEditable: false);
                var body = doc.MainDocumentPart?.Document?.Body
                                       ?? throw new InvalidOperationException("Invalid Word document");


                var textBuilder = new System.Text.StringBuilder();
                int paraCount = 0;

                foreach (var para in body.Descendants<Paragraph>())
                {
                    var paraText = para.InnerText.Trim();
                    if (paraText.Length > 0)
                    {
                        textBuilder.AppendLine(paraText);
                        paraCount++;
                    }
                }

                // Also extract text from tables 
                foreach (var tableCell in body.Descendants<TableCell>())
                {
                    var cellText = tableCell.InnerText.Trim();
                    if (cellText.Length > 0)
                        textBuilder.AppendLine(cellText);
                }

                var metadata = new Dictionary<string, string>
                {
                    ["source"] = fileName,
                    ["type"] = "docx",
                    ["para_count"] = paraCount.ToString(),
                };

                // Extract core properties (title, author, etc.) 
                var coreProps = doc.PackageProperties;
                if (coreProps.Title is { Length: > 0 } t) metadata["title"] = t;
                if (coreProps.Creator is { Length: > 0 } cr) metadata["author"] = cr;
                if (coreProps.Subject is { Length: > 0 } s) metadata["subject"] = s;

                return new ParsedDocument(
                    FileName: fileName,
                    FullText: textBuilder.ToString().Trim(),
                    PageCount: 1, // Word doesn't have fixed pages 
                    Metadata: metadata);
            }, ct);
        }
    }
}
