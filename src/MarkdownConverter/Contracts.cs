namespace MarkdownConverter;

public sealed record MarkdownRequest(string? Markdown);

public sealed record HtmlRequest(string? Html);

public sealed record DocumentIndexRequest(
    string? Document,
    IReadOnlyList<DocumentReference>? Context,
    string? AccountId = null,
    bool UseExhibitNomenclature = false,
    string? IndexNomenclature = null);

public sealed record DocumentReference(
    string DocId,
    string? Title,
    string? PageContent,
    string? SourceUrl,
    string? SourceId,
    int? Position = null,
    IReadOnlyList<int>? Pages = null,
    IReadOnlyList<string>? Headers = null);

public sealed record IndexedExhibit(DocumentReference Doc, int FirstIndex);

public sealed record DocumentIndexResponse(string Document, IReadOnlyList<IndexedExhibit> Exhibits);
