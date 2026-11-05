using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MarkdownConverter.Tests;

public sealed class MarkdownConverterApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private readonly string appSettingsFile;
    private readonly string? previousAppSettingsFile;
    private readonly string? previousCorsOrigins;

    public MarkdownConverterApiTests(WebApplicationFactory<Program> factory)
    {
        appSettingsFile = Path.Combine(Path.GetTempPath(), $"markdown-converter-appsettings-{Guid.NewGuid():N}.json");
        previousAppSettingsFile = Environment.GetEnvironmentVariable("APPSETTINGS_FILE");
        previousCorsOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");
        File.WriteAllText(appSettingsFile, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["CORS_ALLOWED_ORIGINS"] = "https://ui.example.com,http://localhost:3000"
        }));
        Environment.SetEnvironmentVariable("APPSETTINGS_FILE", appSettingsFile);
        Environment.SetEnvironmentVariable("CORS_ALLOWED_ORIGINS", null);
        this.factory = factory.WithWebHostBuilder(_ => { });
        client = this.factory.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        Environment.SetEnvironmentVariable("APPSETTINGS_FILE", previousAppSettingsFile);
        Environment.SetEnvironmentVariable("CORS_ALLOWED_ORIGINS", previousCorsOrigins);
        File.Delete(appSettingsFile);
    }

    [Fact]
    public async Task Markdown_to_docx_returns_a_word_document_with_normalized_text()
    {
        using var response = await client.PostAsJsonAsync(Api.DocxRoute,
            new { markdown = "# Curly — quote ‘test’\n\n**bold**" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Api.DocxMediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".docx", response.Content.Headers.ContentDisposition?.FileName);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("PK", Encoding.ASCII.GetString(bytes, 0, 2));

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var document = archive.GetEntry("word/document.xml");
        Assert.NotNull(document);
        using var reader = new StreamReader(document!.Open());
        var xml = await reader.ReadToEndAsync();
        Assert.Contains("Curly - quote 'test'", xml);
        Assert.Contains("bold", xml);
    }

    [Fact]
    public async Task Exhibit_indexer_rewrites_references_in_order_and_removes_unknown_ids()
    {
        using var response = await client.PostAsJsonAsync(Api.IndexRoute, new
        {
            document = "First (docId: doc-a, pages: [1, 2]), again (docId: doc-a), then (docId: doc-b). Missing (docId: absent).",
            accountId = "case-1",
            useExhibitNomenclature = true,
            context = new[]
            {
                new { docId = "doc-a", title = "Pleading", pageContent = "text", sourceUrl = "https://files.test/a", sourceId = "a", pages = new[] { 1, 2 } },
                new { docId = "doc-b", title = "Order", pageContent = "text", sourceUrl = "https://files.test/b", sourceId = "b", pages = new[] { 3 } }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = result.RootElement;
        Assert.Equal("First ([Exhibit 1](https://files.test/a)), again ([Exhibit 1](https://files.test/a)), then ([Exhibit 2](https://files.test/b)). Missing .",
            root.GetProperty("document").GetString());
        var exhibits = root.GetProperty("exhibits").EnumerateArray().ToArray();
        Assert.Equal(2, exhibits.Length);
        Assert.Equal(1, exhibits[0].GetProperty("firstIndex").GetInt32());
        Assert.Equal("doc-a", exhibits[0].GetProperty("doc").GetProperty("docId").GetString());
        Assert.Equal(2, exhibits[1].GetProperty("firstIndex").GetInt32());
    }

    [Fact]
    public async Task Exhibit_indexer_supports_irregular_doc_id_tokens()
    {
        using var response = await client.PostAsJsonAsync(Api.IndexRoute, new
        {
            document = "Citation docId: abc-123, pages: [4] follows.",
            context = new[]
            {
                new { docId = "abc-123", title = "Record", pageContent = "text", sourceUrl = "https://files.test/record", sourceId = "record", pages = new[] { 4 } }
            }
        });

        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Citation [1](https://files.test/record) follows.",
            result.RootElement.GetProperty("document").GetString());
    }

    [Theory]
    [InlineData(Api.DocxRoute, "markdown", "Please provide Markdown content.")]
    [InlineData(Api.IndexRoute, "document", "Please provide Markdown content.")]
    [InlineData(Api.HtmlRoute, "html", "Please provide HTML content.")]
    public async Task Conversion_endpoints_reject_missing_content(string route, string property, string message)
    {
        using var response = await client.PostAsJsonAsync(route, new Dictionary<string, string?> { [property] = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(message, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Html_to_markdown_returns_markdown_download()
    {
        using var response = await client.PostAsJsonAsync(Api.HtmlRoute, new { html = "<h1>Title</h1><p>Hello <strong>world</strong>.</p>" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".md", response.Content.Headers.ContentDisposition?.FileName);
        var markdown = await response.Content.ReadAsStringAsync();
        Assert.Contains("Title", markdown);
        Assert.Contains("**world**", markdown);
    }

    [Theory]
    [InlineData("https://ui.example.com")]
    [InlineData("http://localhost:3000")]
    public async Task Cors_allows_configured_ui_origins(string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, Api.IndexRoute);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("Content-Type", response.Headers.GetValues("Access-Control-Allow-Headers").Single(),
            StringComparison.OrdinalIgnoreCase);

        using var post = new HttpRequestMessage(HttpMethod.Post, Api.IndexRoute)
        {
            Content = JsonContent.Create(new { document = "A document", context = Array.Empty<object>() })
        };
        post.Headers.Add("Origin", origin);
        using var postResponse = await client.SendAsync(post);
        Assert.Equal(origin, postResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("X-Correlation-Id", postResponse.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public async Task Correlation_and_operation_ids_are_echoed_for_tracing()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api.IndexRoute)
        {
            Content = JsonContent.Create(new { document = "A document", context = Array.Empty<object>() })
        };
        request.Headers.Add("X-Correlation-Id", "corr-ui-42");
        request.Headers.Add("X-Operation-Id", "op-ui-42");

        using var response = await client.SendAsync(request);

        Assert.Equal("corr-ui-42", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("op-ui-42", response.Headers.GetValues("X-Operation-Id").Single());
    }

    [Fact]
    public async Task Missing_correlation_headers_are_generated_and_returned()
    {
        using var response = await client.PostAsJsonAsync(Api.IndexRoute,
            new { document = "A document", context = Array.Empty<object>() });

        Assert.True(response.Headers.Contains("X-Correlation-Id"));
        Assert.True(response.Headers.Contains("X-Operation-Id"));
        Assert.Equal(response.Headers.GetValues("X-Operation-Id").Single(),
            response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Health_endpoint_is_available_for_kubernetes_probes()
    {
        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
