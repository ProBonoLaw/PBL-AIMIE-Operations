using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aimie.Shared;
using Azure.Core;
using Azure.Identity;

namespace Aimie.Operations;

public interface ISharePointOutputStore
{
    Task<string> WriteAsync(string accountId, string folderPath, JsonElement content,
        CancellationToken token, string? correlationId = null, string? operationId = null);
    Task<IReadOnlyList<SharePointFile>> WriteFilesAsync(string accountId, string folderPath,
        IReadOnlyList<SharePointFileContent> files, CancellationToken token,
        string? correlationId = null, string? operationId = null);
    string FolderUrl(string accountId, string folderPath);
}

public sealed record SharePointFileContent(string Name, byte[] Content, string ContentType);

public sealed record SharePointFile(string Name, string Id);

public sealed class SharePointOutputStore : ISharePointOutputStore
{
    private readonly HttpClient client;
    private readonly string host;
    private readonly string sitePrefix;
    private readonly Uri graphEndpoint;
    private readonly TokenCredential credential;

    public SharePointOutputStore(HttpClient client) : this(client,
        new ClientSecretCredential(Settings.Required("SHAREPOINT_TENANT_ID"),
            Settings.Required("SHAREPOINT_CLIENT_ID"), Settings.Required("SHAREPOINT_CLIENT_SECRET")),
        Settings.Required("SHAREPOINT_HOST"),
        Settings.Required("SHAREPOINT_SITE_PREFIX"),
        new Uri(Settings.Get("GRAPH_URL") ?? "https://graph.microsoft.com/v1.0/"))
    {
    }

    public SharePointOutputStore(HttpClient client, TokenCredential credential, string host,
        string sitePrefix, Uri graphEndpoint)
    {
        this.client = client;
        this.credential = credential;
        this.host = host;
        this.sitePrefix = sitePrefix;
        this.graphEndpoint = graphEndpoint;
    }

    public async Task<string> WriteAsync(string accountId, string folderPath, JsonElement content,
        CancellationToken token, string? correlationId = null, string? operationId = null)
    {
        await WriteFilesAsync(accountId, folderPath,
            [new SharePointFileContent("rawoutput.json", Encoding.UTF8.GetBytes(content.GetRawText()),
                "application/json")], token, correlationId, operationId);
        return FolderUrl(accountId, folderPath);
    }

    public async Task<IReadOnlyList<SharePointFile>> WriteFilesAsync(string accountId, string folderPath,
        IReadOnlyList<SharePointFileContent> files, CancellationToken token,
        string? correlationId = null, string? operationId = null)
    {
        if (files.Count == 0) return [];
        if (files.Any(file => string.IsNullOrWhiteSpace(file.Name) || file.Name.Contains('/') ||
                              file.Name.Contains('\\')))
            throw new InvalidDataException("SharePoint output file names must be single path segments");

        var relativeFolder = SharePointFolder(folderPath);
        var siteId = await SiteIdAsync(accountId, token, correlationId, operationId);
        await EnsureFoldersAsync(siteId, relativeFolder, token, correlationId, operationId);
        var saved = new List<SharePointFile>(files.Count);
        foreach (var file in files)
        {
            var filePath = $"/sites/{Uri.EscapeDataString(siteId)}/drive/root:/" +
                $"{EscapePath(relativeFolder + "/" + file.Name)}" + ":/content";
            using var body = new ByteArrayContent(file.Content);
            body.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
            using var response = await RequestAsync(HttpMethod.Put, filePath, body, token,
                correlationId, operationId);
            await EnsureSuccessAsync(response, HttpMethod.Put, filePath, token);
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            saved.Add(new SharePointFile(file.Name, EventEnvelope.Required(document.RootElement, "id")));
        }
        return saved;
    }

    private async Task<string> SiteIdAsync(string accountId, CancellationToken token,
        string? correlationId, string? operationId)
    {
        var path = $"/sites/{host}:{sitePrefix.TrimEnd('/')}/{Uri.EscapeDataString(accountId)}?$select=id";
        using var response = await RequestAsync(HttpMethod.Get, path, null, token, correlationId, operationId);
        await EnsureSuccessAsync(response, HttpMethod.Get, path, token);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return EventEnvelope.Required(document.RootElement, "id");
    }

    private async Task EnsureFoldersAsync(string siteId, string relativeFolder, CancellationToken token,
        string? correlationId, string? operationId)
    {
        var segments = relativeFolder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var folderPath = string.Join('/', segments.Take(index + 1));
            var itemPath = $"/sites/{Uri.EscapeDataString(siteId)}/drive/root:/{EscapePath(folderPath)}";
            using (var response = await RequestAsync(HttpMethod.Get, itemPath, null, token,
                       correlationId, operationId))
            {
                if (response.IsSuccessStatusCode) continue;
                if (response.StatusCode != HttpStatusCode.NotFound)
                    await EnsureSuccessAsync(response, HttpMethod.Get, itemPath, token);
            }

            var parentPath = index == 0
                ? $"/sites/{Uri.EscapeDataString(siteId)}/drive/root/children"
                : $"/sites/{Uri.EscapeDataString(siteId)}/drive/root:/" +
                  $"{EscapePath(string.Join('/', segments.Take(index)))}:/children";
            using var body = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = segments[index],
                ["folder"] = new { },
                ["@microsoft.graph.conflictBehavior"] = "fail"
            }), Encoding.UTF8, "application/json");
            using var create = await RequestAsync(HttpMethod.Post, parentPath, body, token,
                correlationId, operationId);
            if (create.StatusCode != HttpStatusCode.Conflict)
                await EnsureSuccessAsync(create, HttpMethod.Post, parentPath, token);
        }
    }

    private async Task<HttpResponseMessage> RequestAsync(HttpMethod method, string path,
        HttpContent? body, CancellationToken token, string? correlationId = null, string? operationId = null)
    {
        var accessToken = await credential.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/.default"]), token);
        using var request = new HttpRequestMessage(method, new Uri(graphEndpoint, path.TrimStart('/')))
        {
            Content = body
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        if (!string.IsNullOrWhiteSpace(correlationId))
            request.Headers.TryAddWithoutValidation("x-correlation-id", correlationId);
        if (!string.IsNullOrWhiteSpace(operationId))
            request.Headers.TryAddWithoutValidation("x-operation-id", operationId);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, HttpMethod method,
        string path, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var status = response.StatusCode;
        var detail = await response.Content.ReadAsStringAsync(token);
        throw new HttpRequestException(
            $"SharePoint Graph {method} {path} returned HTTP {(int)status} ({status}): {detail}",
            null, status);
    }

    private static string SharePointFolder(string path)
    {
        const string documentsPrefix = "/Shared Documents/";
        if (!path.StartsWith(documentsPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SharePoint output folder must be under Shared Documents");
        return path[documentsPrefix.Length..].Trim('/');
    }

    public string FolderUrl(string accountId, string folderPath) =>
        $"https://{host}{sitePrefix.TrimEnd('/')}/{Uri.EscapeDataString(accountId)}/{EscapePath(folderPath)}";

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
