using Aimie.Operations;
using Aimie.Shared;
using Microsoft.Azure.Cosmos;

var builder = Host.CreateApplicationBuilder(args);
Settings.Configure(builder.Configuration);
if (Settings.Get("LOCAL_DEBUG") == "1" && string.IsNullOrWhiteSpace(Settings.Get("LOCAL_ACCOUNT_ID")))
    throw new InvalidOperationException("LOCAL_ACCOUNT_ID is required for local debug sessions");
builder.Services.AddHttpClient<FunctionClient>(client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient("SharePointOutputStore", client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddTransient(services => new SharePointOutputStore(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("SharePointOutputStore")));
builder.Services.AddSingleton(_ => new CosmosClient(Settings.Required("COSMOS_ENDPOINT"), Settings.Credential));
builder.Services.AddSingleton(_ => new EventsPublisher(Settings.Get("RESPONSE_EVENTHUB_NAME") ?? "aimie-response-queue"));
if (Settings.Get("ROLE") is "argument-builder" or "timeline-builder")
    builder.Services.AddHostedService<GenerationWorker>();
else if (Settings.Get("ROLE") is "declaration" or "qa")
    builder.Services.AddHostedService<DocumentGenerationWorker>();
else
    throw new InvalidOperationException("ROLE must be declaration, qa, argument-builder, or timeline-builder");
await builder.Build().RunAsync();
