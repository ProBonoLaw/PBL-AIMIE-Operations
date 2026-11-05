using System.Net;
using System.Text;
using Aimie.Operations;
using Aimie.Shared;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aimie.Operations.Tests;

[Collection("Settings")]
public sealed class SharePointOutputStoreTests
{
    [Theory]
    [InlineData("SHAREPOINT_HOST")]
    [InlineData("SHAREPOINT_SITE_PREFIX")]
    public void Requires_explicit_sharepoint_address_settings(string missing)
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);
        configuration["SHAREPOINT_TENANT_ID"] = "00000000-0000-0000-0000-000000000000";
        configuration["SHAREPOINT_CLIENT_ID"] = "00000000-0000-0000-0000-000000000000";
        configuration["SHAREPOINT_CLIENT_SECRET"] = "test-only";
        configuration["SHAREPOINT_HOST"] = "sharepoint.example.test";
        configuration["SHAREPOINT_SITE_PREFIX"] = "/sites";
        configuration[missing] = "";
        using var http = new HttpClient();

        var error = Assert.Throws<InvalidOperationException>(() => new SharePointOutputStore(http));

        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public async Task Writes_files_under_unique_folder_and_returns_graph_file_ids()
    {
        var handler = new GraphHandler();
        using var http = new HttpClient(handler);
        var store = new SharePointOutputStore(http, new FixedTokenCredential(),
            "sharepoint.example.test", "/sites", new Uri("https://graph.test/v1.0/"));

        var url = await store.WriteAsync("case-a", "/Shared Documents/_GeneratedDocuments/session-op-a",
            System.Text.Json.JsonDocument.Parse("""{"result":"value"}""").RootElement, default,
            "corr-a", "op-a");

        Assert.Equal("https://sharepoint.example.test/sites/case-a/Shared%20Documents/_GeneratedDocuments/session-op-a",
            url);
        var upload = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Contains("/drive/root:/_GeneratedDocuments/session-op-a/rawoutput.json:/content",
            upload.Path);
        Assert.Equal("{\"result\":\"value\"}", upload.Body);
        Assert.Equal("application/json", upload.ContentType);
        Assert.Equal("Bearer test-token", upload.Authorization);
        Assert.Equal("corr-a", upload.CorrelationId);
        Assert.Equal("op-a", upload.OperationId);
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Rejects_unsafe_output_file_names_before_graph_requests()
    {
        var handler = new GraphHandler();
        using var http = new HttpClient(handler);
        var store = new SharePointOutputStore(http, new FixedTokenCredential(),
            "sharepoint.example.test", "/sites", new Uri("https://graph.test/v1.0/"));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteFilesAsync("case-a",
            "/Shared Documents/_GeneratedDocuments/session-op-b",
            [new SharePointFileContent("nested/file.md", Encoding.UTF8.GetBytes("bad"), "text/markdown")],
            default));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Includes_graph_response_body_when_site_lookup_fails()
    {
        var handler = new GraphHandler
        {
            SiteStatusCode = HttpStatusCode.Forbidden,
            SiteResponseBody = "access denied"
        };
        using var http = new HttpClient(handler);
        var store = new SharePointOutputStore(http, new FixedTokenCredential(),
            "sharepoint.example.test", "/sites", new Uri("https://graph.test/v1.0/"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => store.WriteAsync("case-a",
            "/Shared Documents/_GeneratedDocuments/session-op-c",
            System.Text.Json.JsonDocument.Parse("""{"result":"value"}""").RootElement, default));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("403", error.Message);
        Assert.Contains("access denied", error.Message);
    }

    private sealed class FixedTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private sealed class GraphHandler : HttpMessageHandler
    {
        private int fileId;
        public List<GraphRequest> Requests { get; } = [];
        public HttpStatusCode SiteStatusCode { get; set; } = HttpStatusCode.OK;
        public string SiteResponseBody { get; set; } = "{\"id\":\"site-id\"}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null :
                await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new GraphRequest(request.Method, path, body,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-correlation-id", out var correlations)
                    ? correlations.Single() : null,
                request.Headers.TryGetValues("x-operation-id", out var operations)
                    ? operations.Single() : null));

            if (request.Method == HttpMethod.Get && request.RequestUri.Query.Contains("$select=id", StringComparison.Ordinal))
                return Response(SiteStatusCode, SiteResponseBody);
            if (request.Method == HttpMethod.Get && path.Contains("/drive/root:/", StringComparison.Ordinal))
                return Response(HttpStatusCode.NotFound, "folder missing");
            if (request.Method == HttpMethod.Post)
                return Response(HttpStatusCode.Created, "{}");
            if (request.Method == HttpMethod.Put)
                return Response(HttpStatusCode.Created, $"{{\"id\":\"file-{++fileId}\"}}");
            return Response(HttpStatusCode.OK, "{}");
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed record GraphRequest(HttpMethod Method, string Path, string? Body,
        string? ContentType, string? Authorization, string? CorrelationId, string? OperationId);
}
