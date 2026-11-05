using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HtmlToOpenXml;
using Markdig;
using ReverseMarkdown;

namespace MarkdownConverter;

public sealed class MarkdownConversionService(ILogger<MarkdownConversionService> logger)
{
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().Build();

    public async Task<byte[]> ToDocxAsync(string markdown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = MarkdownText.NormalizeTypography(markdown);
        var html = Markdown.ToHtml(normalized, MarkdownPipeline);
        logger.LogInformation("Rendering Markdown as DOCX from {CharacterCount} characters", markdown.Length);
        await using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var converter = new HtmlConverter(mainPart);
            await converter.ParseBody(html);
            mainPart.Document.Save();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = stream.ToArray();
        logger.LogInformation("Rendered DOCX with {OutputByteCount} bytes from {CharacterCount} Markdown characters",
            result.Length, markdown.Length);
        return result;
    }

    public string ToMarkdown(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        logger.LogInformation("Converting HTML to Markdown from {CharacterCount} characters", html.Length);
        var converter = new Converter(new Config { GithubFlavored = true });
        var markdown = converter.Convert(html);
        logger.LogInformation("Converted HTML to Markdown with {OutputCharacterCount} characters", markdown.Length);
        return markdown;
    }

    public static string DocxContentTypeHeader => DocxContentType;
}
