using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;

namespace Aimie.Operations;

public interface IRequestStore<T>
{
    Task<T?> ReadAsync(string accountId, string id, CancellationToken token);
    Task SaveAsync(string accountId, T record, CancellationToken token);
}

public interface IOutputStore
{
    Task<string> WriteAsync(string blobName, JsonElement content, CancellationToken token,
        string? correlationId = null, string? operationId = null);
}

internal sealed class CosmosRequestStore<T>(Container container) : IRequestStore<T>
{
    public async Task<T?> ReadAsync(string accountId, string id, CancellationToken token)
    {
        try
        {
            var response = await container.ReadItemAsync<T>(id, new PartitionKey(accountId),
                cancellationToken: token);
            return response.Resource;
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }
    }

    public async Task SaveAsync(string accountId, T record, CancellationToken token) =>
        await container.UpsertItemAsync(record, new PartitionKey(accountId), cancellationToken: token);
}

internal sealed class BlobOutputStore(BlobContainerClient container) : IOutputStore
{
    public async Task<string> WriteAsync(string blobName, JsonElement content, CancellationToken token,
        string? correlationId = null, string? operationId = null)
    {
        var blob = container.GetBlobClient(blobName);
        await blob.UploadAsync(BinaryData.FromString(content.GetRawText()), overwrite: true,
            cancellationToken: token);
        var metadata = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(correlationId)) metadata["correlationid"] = correlationId;
        if (!string.IsNullOrWhiteSpace(operationId)) metadata["operationid"] = operationId;
        if (metadata.Count > 0) await blob.SetMetadataAsync(metadata, cancellationToken: token);
        return blob.Uri.ToString();
    }
}
