using System.Text.Json;
using Azure;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Processor;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aimie.Shared;

public abstract class EventWorker : IHostedService
{
    private readonly EventProcessorClient processor;
    private readonly BlobContainerClient failedEvents;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger logger;
    private readonly string consumerGroup;
    private int stopping;

    protected EventWorker(IHostApplicationLifetime lifetime, ILogger logger, string consumerGroup)
    {
        this.lifetime = lifetime;
        this.logger = logger;
        this.consumerGroup = consumerGroup;
        var checkpoint = new BlobContainerClient(new Uri(Settings.Required("CHECKPOINT_BLOB_CONTAINER_URI")), Settings.Credential);
        failedEvents = new BlobContainerClient(new Uri(Settings.Required("FAILED_EVENTS_BLOB_CONTAINER_URI")), Settings.Credential);
        processor = new EventProcessorClient(checkpoint, consumerGroup,
            Settings.Required("EVENTHUB_NAMESPACE"), Settings.Required("EVENTHUB_NAME"),
            Settings.Credential, new EventProcessorClientOptions
            {
                ConnectionOptions = Settings.EventHubConnectionOptions()
            });
        processor.PartitionInitializingAsync += args =>
        {
            args.DefaultStartingPosition = EventPosition.Latest;
            return Task.CompletedTask;
        };
        processor.ProcessEventAsync += ProcessAsync;
        processor.ProcessErrorAsync += ErrorAsync;
    }

    public Task StartAsync(CancellationToken token) => processor.StartProcessingAsync(token);
    public Task StopAsync(CancellationToken token) => processor.StopProcessingAsync(token);

    private async Task ProcessAsync(ProcessEventArgs args)
    {
        if (args.Data is null || Volatile.Read(ref stopping) != 0) return;
        var correlationId = args.Data.CorrelationId;
        if (string.IsNullOrWhiteSpace(correlationId) &&
            args.Data.Properties.TryGetValue("correlationId", out var propertyCorrelation))
            correlationId = propertyCorrelation?.ToString();
        try
        {
            await EventProcessing.ExecuteAsync(args.Data.EventBody.ToMemory(),
                content => HandleAsync(content, args.Partition.PartitionId, args.Data.SequenceNumber,
                    args.CancellationToken, correlationId),
                async error =>
                {
                    await failedEvents.GetBlobClient($"{consumerGroup}/{args.Partition.PartitionId}/{args.Data.SequenceNumber}.json")
                        .UploadAsync(new BinaryData(args.Data.EventBody.ToMemory()),
                            overwrite: true, cancellationToken: args.CancellationToken);
                    logger.LogError(error, "Stored malformed event from partition {Partition} sequence {Sequence}; correlation {CorrelationId}",
                        args.Partition.PartitionId, args.Data.SequenceNumber, correlationId);
                },
                () => args.UpdateCheckpointAsync(args.CancellationToken));
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Transient failures and failed retention stay uncheckpointed.
            logger.LogCritical(error, "Event failed at partition {Partition} sequence {Sequence}; correlation {CorrelationId}; stopping without checkpoint",
                args.Partition.PartitionId, args.Data.SequenceNumber, correlationId);
            if (Interlocked.Exchange(ref stopping, 1) == 0)
            {
                Environment.ExitCode = 1;
                lifetime.StopApplication();
            }
        }
    }

    protected abstract Task HandleAsync(JsonElement content, string partition, long sequence,
        CancellationToken token, string? correlationId);

    private Task ErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception, "Event Hubs processor error on partition {Partition} during {Operation}",
            args.PartitionId, args.Operation);
        if (args.Exception is RequestFailedException && Interlocked.Exchange(ref stopping, 1) == 0)
        {
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
        return Task.CompletedTask;
    }

}
