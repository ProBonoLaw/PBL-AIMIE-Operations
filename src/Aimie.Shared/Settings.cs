using Azure.Identity;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.Configuration;

namespace Aimie.Shared;

public static class Settings
{
    private static IConfiguration? configuration;

    public static void Configure(IConfigurationManager manager)
    {
        var file = Environment.GetEnvironmentVariable("APPSETTINGS_FILE") ?? "/mnt/keyvault/appsettings.json";
        manager.AddJsonFile(file, optional: true, reloadOnChange: true);
        manager.AddEnvironmentVariables();
        configuration = manager;
    }

    public static string? Get(string name) => configuration?[name] ?? Environment.GetEnvironmentVariable(name);
    public static string Required(string name) => !string.IsNullOrWhiteSpace(Get(name))
        ? Get(name)! : throw new InvalidOperationException($"Missing required setting {name}");

    public static Azure.Core.TokenCredential Credential { get; } = new DefaultAzureCredential();

    public static EventHubConnectionOptions EventHubConnectionOptions()
    {
        var value = Get("EVENTHUB_TRANSPORT_TYPE");
        if (string.IsNullOrWhiteSpace(value)) return new EventHubConnectionOptions();
        if (!Enum.TryParse<EventHubsTransportType>(value, true, out var transport) ||
            !Enum.IsDefined(transport))
            throw new InvalidOperationException("EVENTHUB_TRANSPORT_TYPE must be AmqpTcp or AmqpWebSockets");
        return new EventHubConnectionOptions { TransportType = transport };
    }
}
