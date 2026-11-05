using System.Text.RegularExpressions;

namespace MarkdownConverter;

public sealed class DocumentIndexer(ILogger<DocumentIndexer> logger)
{
    private static readonly Regex RegularDocIdMatcher = new(
        "\\(?\\\"?docId\\\"?:\\s*\\\"?([a-zA-Z0-9\\-]+)\\\"?(?:,\\s*\\\"?page[s]?\\\"?:?\\s*\\[\\d*(?:,\\s*\\d*)*\\])?\\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex IrregularDocIdMatcher = new(
        "\\\"?docId\\\"?:\\s*\\\"?([a-zA-Z0-9\\-]+)\\\"?(?:,\\s*\\\"?page[s]?\\\"?:?\\s*\\[\\d*(?:,\\s*\\d*)*\\])?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public DocumentIndexResponse Index(DocumentIndexRequest request)
    {
        if (string.IsNullOrEmpty(request.Document))
            throw new ArgumentException("Please provide Markdown content.", nameof(request));

        var references = new Dictionary<string, DocumentReference>(StringComparer.Ordinal);
        foreach (var reference in request.Context ?? [])
            references[reference.DocId] = reference;

        var markdown = MarkdownText.NormalizeTypography(request.Document);
        var regularMatches = RegularDocIdMatcher.Matches(markdown);
        var irregularMatches = IrregularDocIdMatcher.Matches(markdown);
        var isIrregular = regularMatches.Count == 0 || irregularMatches.Count > regularMatches.Count;
        var matcher = isIrregular ? IrregularDocIdMatcher : RegularDocIdMatcher;
        var exhibitsById = new Dictionary<string, IndexedExhibit>(StringComparer.Ordinal);
        var exhibitIndex = 0;

        foreach (Match match in matcher.Matches(markdown))
        {
            var docId = match.Groups[1].Value;
            if (!references.TryGetValue(docId, out var reference))
            {
                logger.LogWarning("Document reference not found for docId {DocumentId}", docId);
                continue;
            }

            if (string.IsNullOrEmpty(reference.SourceUrl))
            {
                logger.LogWarning("Document reference source URL does not originate from primary account ID for docId {DocumentId}", docId);
                continue;
            }

            if (!exhibitsById.ContainsKey(docId))
                exhibitsById.Add(docId, new IndexedExhibit(reference, ++exhibitIndex));
        }

        var nomenclature = request.UseExhibitNomenclature
            ? string.IsNullOrEmpty(request.IndexNomenclature) ? "Exhibit" : request.IndexNomenclature
            : string.Empty;
        var indexedDocument = matcher.Replace(markdown, match =>
        {
            if (!exhibitsById.TryGetValue(match.Groups[1].Value, out var exhibit))
                return string.Empty;

            var indexName = $"{nomenclature} {exhibit.FirstIndex}".Trim();
            var link = $"[{indexName}]({exhibit.Doc.SourceUrl})";
            return isIrregular ? link : $"({link})";
        });

        var exhibits = exhibitsById.Values.OrderBy(exhibit => exhibit.FirstIndex).ToArray();
        logger.LogInformation("Indexed document with {CharacterCount} characters and {ExhibitCount} exhibits",
            indexedDocument.Length, exhibits.Length);
        return new DocumentIndexResponse(indexedDocument, exhibits);
    }
}
