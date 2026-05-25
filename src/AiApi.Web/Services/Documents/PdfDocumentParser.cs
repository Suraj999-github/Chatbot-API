using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
namespace AiApi.Web.Services.Documents
{
    public class PdfDocumentParser(ILogger<PdfDocumentParser> logger) : IDocumentParser
    {
        public bool CanHandle(string ext) =>
            ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

        public Task<ParsedDocument> ParseAsync(
            Stream stream, string fileName, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                // Buffer the stream — PdfPig may need to seek
                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                using var pdf = PdfDocument.Open(bytes);
                int pageCount = pdf.NumberOfPages;
                var fullText = new System.Text.StringBuilder();
                int pagesWithText = 0;

                foreach (var page in pdf.GetPages())
                {
                    ct.ThrowIfCancellationRequested();

                    // Strategy 1: ContentOrderTextExtractor (best for multi-column, mixed layouts)
                    var text1 = TryExtractWithContentOrder(page);

                    // Strategy 2: GetWords() — works well for simple single-column PDFs
                    var text2 = TryExtractWithGetWords(page);

                    // Strategy 3: Raw Letters — last resort, catches encoded/embedded fonts
                    var text3 = TryExtractWithLetters(page);

                    // Pick whichever strategy produced the most text
                    var bestText = new[] { text1, text2, text3 }
                        .OrderByDescending(t => t.Length)
                        .First();

                    if (bestText.Length > 10)
                    {
                        fullText.AppendLine(bestText);
                        pagesWithText++;
                    }
                    else
                    {
                        logger.LogDebug(
                            "Page {Page} of '{File}' yielded no text — may be image/scanned",
                            page.Number, fileName);
                    }
                }

                var extracted = fullText.ToString().Trim();

                // Clean up common PDF extraction noise
                extracted = CleanExtractedText(extracted);

                logger.LogInformation(
                    "PDF '{File}': {Pages} pages, {TextPages} with text, {Chars} chars extracted",
                    fileName, pageCount, pagesWithText, extracted.Length);

                if (extracted.Length < 50)
                    throw new InvalidOperationException(
                        $"Could not extract readable text from '{fileName}'. " +
                        $"The file has {pageCount} page(s) but only {extracted.Length} characters were found. " +
                        "This usually means the PDF is scanned/image-based. " +
                        "Convert it to a searchable PDF using Adobe Acrobat or an OCR tool first.");

                var metadata = new Dictionary<string, string>
                {
                    ["source"] = fileName,
                    ["type"] = "pdf",
                    ["page_count"] = pageCount.ToString(),
                    ["pages_text"] = pagesWithText.ToString(),
                };

                try
                {
                    if (pdf.Information.Title is { Length: > 0 } t) metadata["title"] = t;
                    if (pdf.Information.Author is { Length: > 0 } a) metadata["author"] = a;
                }
                catch { /* metadata is optional */ }

                return new ParsedDocument(
                    FileName: fileName,
                    FullText: extracted,
                    PageCount: pageCount,
                    Metadata: metadata);

            }, ct);
        }

        // ── Extraction strategies ──────────────────────────────────────────────

        private static string TryExtractWithContentOrder(Page page)
        {
            try
            {
                // Best overall strategy — respects reading order across columns
                return ContentOrderTextExtractor.GetText(page) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string TryExtractWithGetWords(Page page)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var word in page.GetWords())
                    sb.Append(word.Text).Append(' ');
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        private static string TryExtractWithLetters(Page page)
        {
            try
            {
                // Most raw approach — concatenates every glyph on the page
                var sb = new System.Text.StringBuilder();
                foreach (var letter in page.Letters)
                {
                    sb.Append(letter.Value);
                    // Detect word boundaries by glyph spacing
                    if (letter.GlyphRectangle.Width > 1.5)
                        sb.Append(' ');
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        // ── Post-processing ────────────────────────────────────────────────────

        private static string CleanExtractedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            // Collapse excessive whitespace
            text = System.Text.RegularExpressions.Regex.Replace(text, @" {3,}", "  ");

            // Collapse 3+ blank lines into one
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(\r?\n){3,}", "\n\n");

            // Remove zero-width and control characters (common in encoded PDFs)
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");

            return text.Trim();
        }
    }
}
