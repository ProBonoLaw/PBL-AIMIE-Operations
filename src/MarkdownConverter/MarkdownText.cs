namespace MarkdownConverter;

internal static class MarkdownText
{
    public static string NormalizeTypography(string text) => text
        .Replace('\u2014', '-')
        .Replace('\u2010', '-')
        .Replace('\u2011', '-')
        .Replace('\u2013', '-')
        .Replace('\u2018', '\'')
        .Replace('\u2019', '\'')
        .Replace('\u201c', '"')
        .Replace('\u201d', '"');
}
