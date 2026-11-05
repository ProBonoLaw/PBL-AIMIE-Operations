using System.Text.Json;
using Aimie.Shared;
using Microsoft.Azure.Cosmos;

namespace Aimie.Operations;

public sealed class GenerationWorker : EventWorker
{
    private readonly GenerationHandler handler;

    public GenerationWorker(IHostApplicationLifetime lifetime, ILogger<GenerationWorker> logger,
        FunctionClient functions, EventsPublisher responses, CosmosClient cosmos,
        SharePointOutputStore outputs) :
        base(lifetime, logger, Settings.Required("EVENTHUB_CONSUMER_GROUP"))
    {
        var database = Settings.Get("COSMOS_DATABASE") ?? "db_conversation_history";
        handler = new GenerationHandler(Settings.Required("ROLE"),
            Settings.Get("LOCAL_DEBUG") == "1" ? Settings.Required("LOCAL_ACCOUNT_ID") : null,
            database, functions, responses,
            new CosmosRequestStore<RequestRecord>(cosmos.GetContainer(database, "requests")),
            outputs, logger);
    }

    protected override Task HandleAsync(JsonElement content, string partition, long sequence,
        CancellationToken token, string? correlationId) =>
        handler.ProcessAsync(content, partition, sequence, token, correlationId);
}

public sealed record RequestRecord(
    [property: Newtonsoft.Json.JsonProperty("id")] string Id,
    [property: Newtonsoft.Json.JsonProperty("accountId")] string AccountId,
    [property: Newtonsoft.Json.JsonProperty("sessionId")] string SessionId,
    [property: Newtonsoft.Json.JsonProperty("input")] string Input,
    [property: Newtonsoft.Json.JsonProperty("type")] string Type,
    [property: Newtonsoft.Json.JsonProperty("status")] string Status,
    [property: Newtonsoft.Json.JsonProperty("oid")] string? Oid,
    [property: Newtonsoft.Json.JsonProperty("startedAt")] DateTimeOffset StartedAt,
    [property: Newtonsoft.Json.JsonProperty("endedAt")] DateTimeOffset? EndedAt,
    [property: Newtonsoft.Json.JsonProperty("sharePointPath")] string SharePointPath,
    [property: Newtonsoft.Json.JsonProperty("sourceUrl")] string? SourceUrl,
    [property: Newtonsoft.Json.JsonProperty("contents")] string? Contents,
    [property: Newtonsoft.Json.JsonProperty("rawOutputJson")] string? RawOutputJson,
    [property: Newtonsoft.Json.JsonProperty("resultStored")] bool ResultStored = false,
    [property: Newtonsoft.Json.JsonProperty("operationId")] string? OperationId = null,
    [property: Newtonsoft.Json.JsonProperty("correlationId")] string? CorrelationId = null);
