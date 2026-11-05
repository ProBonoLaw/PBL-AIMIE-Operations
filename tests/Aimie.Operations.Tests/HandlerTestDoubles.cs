using System.Text.Json;
using Aimie.Operations;
using Aimie.Shared;

namespace Aimie.Operations.Tests;

internal sealed class FakeFunctions : IFunctionClient
{
    private readonly Dictionary<string, JsonElement> results = [];
    public Exception? NextCallError { get; set; }
    public Exception? NextBinaryCallError { get; set; }
    public List<(string Prefix, JsonElement Body, bool Wait)> Calls { get; } = [];
    public List<(string Prefix, JsonElement Body, string? CorrelationId, string? OperationId)> BinaryCalls { get; } = [];
    public List<(string Prefix, JsonElement Body)> Notifications { get; } = [];
    public byte[] BinaryResult { get; set; } = [1, 2, 3];

    public void Returns(string prefix, string json) => results[prefix] = JsonDocument.Parse(json).RootElement.Clone();

    public Task<JsonElement> CallAsync(string prefix, object body, bool wait, CancellationToken token,
        string? correlationId = null, string? operationId = null)
    {
        Calls.Add((prefix, JsonSerializer.SerializeToElement(body), wait));
        if (NextCallError is { } error)
        {
            NextCallError = null;
            throw error;
        }
        return Task.FromResult(results[prefix]);
    }

    public Task<byte[]> CallBinaryAsync(string prefix, object body, CancellationToken token,
        string? correlationId = null, string? operationId = null)
    {
        BinaryCalls.Add((prefix, JsonSerializer.SerializeToElement(body), correlationId, operationId));
        if (NextBinaryCallError is { } error)
        {
            NextBinaryCallError = null;
            throw error;
        }
        return Task.FromResult(BinaryResult);
    }

    public Task NotifyAsync(string prefix, object body, CancellationToken token,
        string? correlationId = null, string? operationId = null)
    {
        Notifications.Add((prefix, JsonSerializer.SerializeToElement(body)));
        return Task.CompletedTask;
    }
}

internal sealed class FakeResponses : IEventsPublisher
{
    public List<JsonElement> Sent { get; } = [];
    public Exception? NextSendError { get; set; }

    public Task SendAsync(object content, CancellationToken token, string? correlationId = null,
        string? operationId = null)
    {
        if (NextSendError is { } error)
        {
            NextSendError = null;
            throw error;
        }
        Sent.Add(JsonSerializer.SerializeToElement(content));
        return Task.CompletedTask;
    }
}

internal sealed class FakeRequestStore<T>(Func<T, string> id) : IRequestStore<T>
{
    private readonly Dictionary<(string AccountId, string Id), T> items = [];
    public List<T> Saved { get; } = [];

    public void Seed(string accountId, T record) => items[(accountId, id(record))] = record;
    public T? Get(string accountId, string requestId) =>
        items.TryGetValue((accountId, requestId), out var record) ? record : default;

    public Task<T?> ReadAsync(string accountId, string requestId, CancellationToken token) =>
        Task.FromResult(Get(accountId, requestId));

    public Task SaveAsync(string accountId, T record, CancellationToken token)
    {
        items[(accountId, id(record))] = record;
        Saved.Add(record);
        return Task.CompletedTask;
    }
}

internal sealed class FakeOutputStore : IOutputStore
{
    public List<(string Name, JsonElement Content)> Written { get; } = [];
    public Exception? NextWriteError { get; set; }

    public Task<string> WriteAsync(string blobName, JsonElement content, CancellationToken token,
        string? correlationId = null, string? operationId = null)
    {
        if (NextWriteError is { } error)
        {
            NextWriteError = null;
            throw error;
        }
        Written.Add((blobName, content.Clone()));
        return Task.FromResult("https://test.blob.core.windows.net/results/" + blobName);
    }
}

internal sealed class FakeSharePointOutputStore : ISharePointOutputStore
{
    public List<(string AccountId, string FolderPath, JsonElement Content)> Written { get; } = [];
    public List<(string AccountId, string FolderPath, IReadOnlyList<SharePointFileContent> Files)> WrittenFiles { get; } = [];
    public Exception? NextWriteError { get; set; }

    public Task<string> WriteAsync(string accountId, string folderPath, JsonElement content,
        CancellationToken token, string? correlationId = null, string? operationId = null)
    {
        if (NextWriteError is { } error)
        {
            NextWriteError = null;
            throw error;
        }
        Written.Add((accountId, folderPath, content.Clone()));
        return Task.FromResult($"https://sharepoint.example.test/sites/{accountId}{folderPath}");
    }

    public Task<IReadOnlyList<SharePointFile>> WriteFilesAsync(string accountId, string folderPath,
        IReadOnlyList<SharePointFileContent> files, CancellationToken token,
        string? correlationId = null, string? operationId = null)
    {
        if (NextWriteError is { } error)
        {
            NextWriteError = null;
            throw error;
        }
        WrittenFiles.Add((accountId, folderPath, files));
        return Task.FromResult<IReadOnlyList<SharePointFile>>(files
            .Select((file, index) => new SharePointFile(file.Name, $"file-{index}"))
            .ToArray());
    }

    public string FolderUrl(string accountId, string folderPath) =>
        $"https://sharepoint.example.test/sites/{accountId}{folderPath}";
}

internal sealed class FakeDeclarationOutputStore : IDeclarationOutputStore
{
    public List<(string AccountId, string FolderPath, JsonElement Indexed)> Written { get; } = [];
    public Exception? NextWriteError { get; set; }

    public Task<DeclarationStoredOutput> WriteAsync(string accountId, string folderPath, JsonElement indexed,
        CancellationToken token, string? correlationId = null, string? operationId = null)
    {
        if (NextWriteError is { } error)
        {
            NextWriteError = null;
            throw error;
        }
        Written.Add((accountId, folderPath, indexed.Clone()));
        return Task.FromResult(new DeclarationStoredOutput(
            $"https://sharepoint.example.test/sites/{accountId}{folderPath}",
            "declaration-md-id",
            [new DeclarationAttachment("Declaration.docx", "declaration-docx-id")]));
    }
}
