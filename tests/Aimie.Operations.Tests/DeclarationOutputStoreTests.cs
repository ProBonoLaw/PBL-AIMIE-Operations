using System.Text;
using System.Text.Json;
using Aimie.Operations;
using Xunit;

namespace Aimie.Operations.Tests;

public sealed class DeclarationOutputStoreTests
{
    [Fact]
    public async Task Writes_declaration_and_exhibit_markdown_and_docx_with_legacy_references()
    {
        var files = new FakeSharePointOutputStore();
        var functions = new FakeFunctions();
        var store = new SharePointDeclarationOutputStore(files, functions);
        var indexed = Json("""
            {
              "document":"# Declaration\n\nFinal text",
              "exhibits":[{
                "firstIndex":4,
                "doc":{
                  "title":"Court filing",
                  "sourceUrl":"https://sharepoint.example.test/sites/case-a/filing.pdf",
                  "pages":[2,3],
                  "pageContent":"Filed on Tuesday"
                }
              }]
            }
            """);

        var result = await store.WriteAsync("case-a", "/Shared Documents/_GeneratedDocuments/session-op-a",
            indexed, default, "corr-a", "op-a");

        var saved = Assert.Single(files.WrittenFiles).Files;
        Assert.Equal(["Exhibit 4.md", "Exhibit 4.docx", "Declaration.docx", "Declaration.md"],
            saved.Select(file => file.Name));
        var exhibitMarkdown = Encoding.UTF8.GetString(saved[0].Content);
        Assert.Contains("__Title:__ [Court filing](https://sharepoint.example.test/sites/case-a/filing.pdf)",
            exhibitMarkdown);
        Assert.Contains("__Pages:__ 2, 3", exhibitMarkdown);
        Assert.Contains("## Exhibit Content\nFiled on Tuesday", exhibitMarkdown);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved[1].Content);
        Assert.Equal(2, functions.BinaryCalls.Count);
        Assert.All(functions.BinaryCalls, call => Assert.Equal("MARKDOWN_TO_DOCX", call.Prefix));
        Assert.Equal("corr-a", functions.BinaryCalls[0].CorrelationId);
        Assert.Equal("op-a", functions.BinaryCalls[0].OperationId);

        Assert.Equal("https://sharepoint.example.test/sites/case-a/Shared Documents/_GeneratedDocuments/session-op-a",
            result.SourceUrl);
        Assert.Equal("file-3", result.DeclarationMarkdownId);
        Assert.Collection(result.Attachments,
            exhibit =>
            {
                Assert.Equal("Exhibit 4.docx", exhibit.Name);
                Assert.Equal("file-1", exhibit.FileId);
                Assert.Equal("2, 3", exhibit.Pages);
            },
            declaration =>
            {
                Assert.Equal("Declaration.docx", declaration.Name);
                Assert.Equal("file-2", declaration.FileId);
                Assert.Null(declaration.Pages);
            });
    }

    [Fact]
    public async Task Writes_only_declaration_files_when_there_are_no_exhibits()
    {
        var files = new FakeSharePointOutputStore();
        var functions = new FakeFunctions();
        var store = new SharePointDeclarationOutputStore(files, functions);

        var result = await store.WriteAsync("case-a", "/Shared Documents/_GeneratedDocuments/session-op-b",
            Json("""{"document":"Declaration only"}"""), default);

        var saved = Assert.Single(files.WrittenFiles).Files;
        Assert.Equal(["Declaration.docx", "Declaration.md"], saved.Select(file => file.Name));
        Assert.Equal("Declaration.docx", Assert.Single(result.Attachments).Name);
        Assert.Single(functions.BinaryCalls);
    }

    [Fact]
    public async Task Rejects_duplicate_exhibit_indexes_before_uploading_files()
    {
        var files = new FakeSharePointOutputStore();
        var functions = new FakeFunctions();
        var store = new SharePointDeclarationOutputStore(files, functions);
        var indexed = Json("""
            {
              "document":"Declaration",
              "exhibits":[
                {"firstIndex":1,"doc":{"pageContent":"First"}},
                {"firstIndex":1,"doc":{"pageContent":"Duplicate"}}
              ]
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync("case-a",
            "/Shared Documents/_GeneratedDocuments/session-op-c", indexed, default));

        Assert.Empty(files.WrittenFiles);
        Assert.Single(functions.BinaryCalls);
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
