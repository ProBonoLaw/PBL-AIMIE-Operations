using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

namespace Aimie.Shared;

public interface IEventsPublisher
{
    Task SendAsync(object content, CancellationToken token, string? correlationId = null,
        string? operationId = null);
}

public sealed class EventsPublisher : IEventsPublisher, IAsyncDisposable
{
    private readonly EventHubProducerClient producer;

    public EventsPublisher(string hubName)
    {
        producer = new EventHubProducerClient(Settings.Required("EVENTHUB_NAMESPACE"), hubName,
            Settings.Credential, new EventHubProducerClientOptions
            {
                ConnectionOptions = Settings.EventHubConnectionOptions()
            });
    }

    public Task SendAsync(object content, CancellationToken token, string? correlationId = null,
        string? operationId = null)
    {
        var data = new EventData(JsonSerializer.SerializeToUtf8Bytes(new { contentData = content }));
        if (!string.IsNullOrWhiteSpace(correlationId)) data.CorrelationId = correlationId;
        if (!string.IsNullOrWhiteSpace(operationId)) data.Properties["operationId"] = operationId;
        return producer.SendAsync([data], token);
    }

    public ValueTask DisposeAsync() => producer.DisposeAsync();
}
