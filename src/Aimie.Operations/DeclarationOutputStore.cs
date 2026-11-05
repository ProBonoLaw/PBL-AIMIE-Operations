using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aimie.Shared;

namespace Aimie.Operations;

public sealed record DeclarationAttachment(
    [property: JsonPropertyName("name"), Newtonsoft.Json.JsonProperty("name")] string Name,
    [property: JsonPropertyName("fileId"), Newtonsoft.Json.JsonProperty("fileId")] string FileId,
    [property: JsonPropertyName("sourceUrl"), Newtonsoft.Json.JsonProperty("sourceUrl", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore),
     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceUrl = null,
    [property: JsonPropertyName("pages"), Newtonsoft.Json.JsonProperty("pages", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore),
     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Pages = null);

public sealed record DeclarationStoredOutput(
    string SourceUrl,
    string DeclarationMarkdownId,
    IReadOnlyList<DeclarationAttachment> Attachments);

public interface IDeclarationOutputStore
{
    Task<DeclarationStoredOutput> WriteAsync(string accountId, string folderPath, JsonElement indexed,
        CancellationToken token, string? correlationId = null, string? operationId = null);
}

public sealed class SharePointDeclarationOutputStore(
    ISharePointOutputStore files, IFunctionClient functions) : IDeclarationOutputStore
{
    private const string MarkdownContentType = "text/markdown";
    private const string WordContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public async Task<DeclarationStoredOutput> WriteAsync(string accountId, string folderPath,
        JsonElement indexed, CancellationToken token, string? correlationId = null,
        string? operationId = null)
    {
        var document = EventEnvelope.Required(indexed, "document");
        var pendingFiles = new List<SharePointFileContent>();
        var exhibits = new List<(int Index, string? SourceUrl, string Pages)>();
        var attachments = new List<DeclarationAttachment>();

        if (indexed.TryGetProperty("exhibits", out var exhibitItems) &&
            exhibitItems.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (exhibitItems.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Declaration exhibits must be an array");

            var seenIndexes = new HashSet<int>();
            foreach (var exhibit in exhibitItems.EnumerateArray())
            {
                if (exhibit.ValueKind != JsonValueKind.Object ||
                    !exhibit.TryGetProperty("firstIndex", out var indexValue) ||
                    !indexValue.TryGetInt32(out var index) || !seenIndexes.Add(index))
                    throw new InvalidDataException("Declaration exhibit has no unique firstIndex");
                if (!exhibit.TryGetProperty("doc", out var doc) || doc.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Declaration exhibit {index} has no doc object");

                var title = EventEnvelope.String(doc, "title");
                if (string.IsNullOrWhiteSpace(title)) title = "N/A";
                var sourceUrl = EventEnvelope.String(doc, "sourceUrl");
                var pages = Pages(doc);
                var markdown = ExhibitMarkdown(index, title, sourceUrl, pages,
                    EventEnvelope.Required(doc, "pageContent"));
                var fileStem = $"Exhibit {index}";
                pendingFiles.Add(new SharePointFileContent(fileStem + ".md",
                    Encoding.UTF8.GetBytes(markdown), MarkdownContentType));
                pendingFiles.Add(new SharePointFileContent(fileStem + ".docx",
                    await ToWordAsync(markdown, token, correlationId, operationId), WordContentType));
                exhibits.Add((index, sourceUrl, pages));
            }
        }

        pendingFiles.Add(new SharePointFileContent("Declaration.docx",
            await ToWordAsync(document, token, correlationId, operationId), WordContentType));
        pendingFiles.Add(new SharePointFileContent("Declaration.md",
            Encoding.UTF8.GetBytes(document), MarkdownContentType));

        var storedFiles = await files.WriteFilesAsync(accountId, folderPath, pendingFiles, token,
            correlationId, operationId);
        foreach (var exhibit in exhibits)
        {
            var name = $"Exhibit {exhibit.Index}.docx";
            attachments.Add(new DeclarationAttachment(name, FileId(storedFiles, name),
                exhibit.SourceUrl, exhibit.Pages));
        }
        attachments.Add(new DeclarationAttachment("Declaration.docx", FileId(storedFiles, "Declaration.docx")));

        return new DeclarationStoredOutput(files.FolderUrl(accountId, folderPath),
            FileId(storedFiles, "Declaration.md"), attachments);
    }

    private async Task<byte[]> ToWordAsync(string markdown, CancellationToken token,
        string? correlationId, string? operationId)
    {
        return await functions.CallBinaryAsync("MARKDOWN_TO_DOCX", new { markdown },
            token, correlationId, operationId);
    }

    private static string FileId(IReadOnlyList<SharePointFile> files, string name) =>
        files.SingleOrDefault(file => file.Name == name)?.Id ??
        throw new InvalidDataException($"SharePoint did not return an ID for {name}");

    private static string Pages(JsonElement doc)
    {
        if (!doc.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return "NA";
        var values = pages.EnumerateArray().Select(page => page.ToString()).ToArray();
        return values.Length == 0 ? "NA" : string.Join(", ", values);
    }

    private static string ExhibitMarkdown(int index, string title, string? sourceUrl, string pages,
        string content)
    {
        var titleLink = string.IsNullOrWhiteSpace(sourceUrl) ? title : $"[{title}]({sourceUrl})";
        return $"# Exhibit {index}\n\n__Title:__ {titleLink}\n\n__Pages:__ {pages}" +
               $"\n\n## Exhibit Content\n{content}";
    }
}
