using System.Text.Json;
using Aimie.Shared;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;

namespace Aimie.Operations;

public sealed class DocumentGenerationWorker : EventWorker
{
    private readonly DocumentGenerationHandler handler;

    public DocumentGenerationWorker(IHostApplicationLifetime lifetime, ILogger<DocumentGenerationWorker> logger,
        FunctionClient functions, EventsPublisher responses, CosmosClient cosmos,
        SharePointOutputStore sharePointOutputs) :
        base(lifetime, logger, Settings.Required("EVENTHUB_CONSUMER_GROUP"))
    {
        var database = Settings.Get("COSMOS_DATABASE") ?? "db_conversation_history";
        handler = new DocumentGenerationHandler(Settings.Required("ROLE"),
            Settings.Get("LOCAL_DEBUG") == "1" ? Settings.Required("LOCAL_ACCOUNT_ID") : null,
            database, functions, responses,
            new CosmosRequestStore<DocumentRequestRecord>(cosmos.GetContainer(database, "requests")),
            new BlobOutputStore(new BlobContainerClient(new Uri(Settings.Required("OUTPUT_BLOB_CONTAINER_URI")),
                Settings.Credential)), logger,
            new SharePointDeclarationOutputStore(sharePointOutputs, functions));
    }

    protected override Task HandleAsync(JsonElement content, string partition, long sequence,
        CancellationToken token, string? correlationId) =>
        handler.ProcessAsync(content, partition, sequence, token, correlationId);
}

public sealed record DocumentRequestRecord(
    [property: Newtonsoft.Json.JsonProperty("id")] string Id,
    [property: Newtonsoft.Json.JsonProperty("accountId")] string AccountId,
    [property: Newtonsoft.Json.JsonProperty("type")] string Type,
    [property: Newtonsoft.Json.JsonProperty("input")] string Input,
    [property: Newtonsoft.Json.JsonProperty("sessionId")] string SessionId,
    [property: Newtonsoft.Json.JsonProperty("status")] string Status,
    [property: Newtonsoft.Json.JsonProperty("oid")] string? Oid,
    [property: Newtonsoft.Json.JsonProperty("startedAt")] DateTimeOffset StartedAt,
    [property: Newtonsoft.Json.JsonProperty("endedAt")] DateTimeOffset? EndedAt,
    [property: Newtonsoft.Json.JsonProperty("outputUrl")] string? OutputUrl,
    [property: Newtonsoft.Json.JsonProperty("sourceUrl")] string? SourceUrl,
    [property: Newtonsoft.Json.JsonProperty("operationId")] string? OperationId = null,
    [property: Newtonsoft.Json.JsonProperty("correlationId")] string? CorrelationId = null,
    [property: Newtonsoft.Json.JsonProperty("sharePointPath")] string? SharePointPath = null,
    [property: Newtonsoft.Json.JsonProperty("contents")] IReadOnlyList<DeclarationAttachment>? Contents = null,
    [property: Newtonsoft.Json.JsonProperty("declarationMd")] string? DeclarationMd = null);
