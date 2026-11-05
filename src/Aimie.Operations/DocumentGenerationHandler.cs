using System.Text.Json;
using Aimie.Shared;

namespace Aimie.Operations;

public sealed class DocumentGenerationHandler(
    string role, string? accountFilter, string database, IFunctionClient functions,
    IEventsPublisher responses, IRequestStore<DocumentRequestRecord> requests,
    IOutputStore outputs, ILogger logger, IDeclarationOutputStore? declarationOutputs = null)
{
    public async Task ProcessAsync(JsonElement content, string partition, long sequence, CancellationToken token,
        string? sourceCorrelationId = null)
    {
        // Email normalization and delivery are owned by the separate email repository.
        if (!EventRouting.ShouldHandle(role, content, accountFilter)) return;
        var accountId = EventEnvelope.RequiredEvent(content, "accountId");
        var input = EventEnvelope.RequestText(content);
        var correlation = OperationCorrelation.Create(role, partition, sequence, content,
            eventCorrelationId: sourceCorrelationId);
        var requestId = correlation.OperationId;
        var legacyRequestId = $"{partition}-{sequence}";
        DocumentRequestRecord? existing;
        try
        {
            existing = await requests.ReadAsync(accountId, requestId, token);
            if (existing is null && legacyRequestId != requestId)
                existing = await requests.ReadAsync(accountId, legacyRequestId, token);
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        {
            logger.LogError(error, "Could not read request {RequestId}; correlation {CorrelationId}, operation {OperationId}",
                requestId, correlation.CorrelationId, correlation.OperationId);
            throw;
        }
        if (existing is not null) requestId = existing.Id;
        if (existing?.Status == "completed")
        {
            logger.LogInformation("Request {RequestId} was already completed; correlation {CorrelationId}, operation {OperationId}",
                requestId, existing.CorrelationId, existing.OperationId);
            return;
        }
        correlation = OperationCorrelation.Create(role, partition, sequence, content,
            existing?.OperationId, existing?.CorrelationId, sourceCorrelationId);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlation.CorrelationId,
            ["OperationId"] = correlation.OperationId
        });
        try
        {
            var sessionId = existing?.SessionId ?? EventEnvelope.String(content, "sessionId") ?? Guid.NewGuid().ToString();
            var sharePointPath = role == "declaration" && string.IsNullOrWhiteSpace(existing?.SharePointPath)
                ? NewSharePointPath(sessionId, correlation.OperationId)
                : existing?.SharePointPath;
            var record = existing ?? new DocumentRequestRecord(requestId, accountId, role, input, sessionId,
                "started", EventEnvelope.String(content, "oid"), DateTimeOffset.UtcNow, null, null, null,
                correlation.OperationId, correlation.CorrelationId, sharePointPath);
            var correlationUpdated = existing is not null &&
                                     (existing.OperationId is null || existing.CorrelationId is null ||
                                      existing.SharePointPath != sharePointPath);
            if (correlationUpdated)
                record = record with
                {
                    OperationId = correlation.OperationId,
                    CorrelationId = correlation.CorrelationId,
                    SharePointPath = sharePointPath
                };
            if (existing is null || correlationUpdated) await requests.SaveAsync(accountId, record, token);

            var outputUrl = record.OutputUrl;
            var characters = 0;
            var declarationOutputMissing = role == "declaration" &&
                (string.IsNullOrWhiteSpace(record.SourceUrl) || record.Contents is null ||
                 string.IsNullOrWhiteSpace(record.DeclarationMd));
            if (record.Status != "generated" || string.IsNullOrWhiteSpace(outputUrl) || declarationOutputMissing)
            {
                JsonElement indexed;
                try
                {
                    if (role == "declaration")
                    {
                        var writer = await functions.CallAsync("DECLARATION_WRITER_FUNCTION", new
                        {
                            accountId,
                            indexPostfix = "general",
                            input,
                            maxDocCount = 100,
                            sessionId,
                            correlationId = correlation.CorrelationId,
                            operationId = correlation.OperationId
                        }, wait: true, token, correlation.CorrelationId, correlation.OperationId);
                        if (EventEnvelope.String(writer, "runtimeStatus") != "Completed")
                            throw new InvalidDataException("Declaration writer did not complete");
                        var output = EventEnvelope.Property(writer, "output") ??
                            throw new InvalidDataException("Declaration writer returned no output");
                        indexed = await functions.CallAsync("EXHIBIT_INDEX", new
                        {
                            accountId,
                            context = EventEnvelope.Property(output, "context"),
                            document = EventEnvelope.Required(output, "finalDeclaration"),
                            correlationId = correlation.CorrelationId,
                            operationId = correlation.OperationId
                        }, wait: false, token, correlation.CorrelationId, correlation.OperationId);
                    }
                    else
                    {
                        var answer = await functions.CallAsync("FAST_QA_FUNCTION", new
                        {
                            accountId,
                            indexPostfix = "general",
                            maxDocCount = 10,
                            provideThoroughResponse = false,
                            question = input,
                            sessionId,
                            correlationId = correlation.CorrelationId,
                            operationId = correlation.OperationId
                        }, wait: false, token, correlation.CorrelationId, correlation.OperationId);
                        indexed = await functions.CallAsync("EXHIBIT_INDEX", new
                        {
                            context = EventEnvelope.Property(answer, "context"),
                            document = EventEnvelope.Required(answer, "answer"),
                            correlationId = correlation.CorrelationId,
                            operationId = correlation.OperationId
                        }, wait: false, token, correlation.CorrelationId, correlation.OperationId);
                    }
                    characters = EventEnvelope.Required(indexed, "document").Length;
                }
                catch (InvalidDataException error)
                {
                    await FailAsync(record, content, error, token);
                    return;
                }
                var blobName = $"{role}/{accountId}/{correlation.OperationId}/result.json";
                DeclarationStoredOutput? declarationOutput = null;
                if (role == "declaration")
                {
                    try
                    {
                        declarationOutput = await (declarationOutputs ?? throw new InvalidOperationException(
                            "Declaration output storage is not configured")).WriteAsync(accountId,
                            sharePointPath ?? throw new InvalidOperationException("Declaration SharePoint path is missing"),
                            indexed, token, correlation.CorrelationId, correlation.OperationId);
                    }
                    catch (InvalidDataException error)
                    {
                        await FailAsync(record, content, error, token);
                        return;
                    }
                }
                outputUrl = await outputs.WriteAsync(blobName, indexed, token,
                    correlation.CorrelationId, correlation.OperationId);
                record = record with
                {
                    Status = "generated",
                    OutputUrl = outputUrl,
                    SourceUrl = declarationOutput?.SourceUrl ?? record.SourceUrl,
                    Contents = declarationOutput?.Attachments ?? record.Contents,
                    DeclarationMd = declarationOutput?.DeclarationMarkdownId ?? record.DeclarationMd
                };
                await requests.SaveAsync(accountId, record, token);
            }
            var response = new Dictionary<string, object?>
            {
                ["accountId"] = accountId,
                ["type"] = role,
                ["status"] = "Generated",
                ["input"] = input,
                ["sessionId"] = sessionId,
                ["responseId"] = requestId,
                ["outputUrl"] = outputUrl,
                ["correlationId"] = correlation.CorrelationId,
                ["operationId"] = correlation.OperationId,
                ["conversationId"] = EventEnvelope.String(content, "conversationId"),
                ["emailAddresses"] = EventEnvelope.Property(content, "emailAddresses"),
                ["emailSubject"] = EventEnvelope.String(content, "emailSubject"),
                ["messageId"] = EventEnvelope.String(content, "messageId"),
                ["oid"] = EventEnvelope.String(content, "oid"),
                ["resultReference"] = new
                {
                    databaseId = database,
                    containerId = "requests",
                    id = requestId,
                    itemId = requestId,
                    partitionKey = accountId
                }
            };
            if (role == "declaration")
            {
                response["sourceUrl"] = record.SourceUrl;
                response["declarationMd"] = record.DeclarationMd;
                response["attachments"] = record.Contents ?? [];
            }
            await responses.SendAsync(response, token, correlation.CorrelationId, correlation.OperationId);
            if (role == "declaration" && !string.IsNullOrWhiteSpace(record.Oid))
                await NotifyAsync(record, "SUCCESS", token);
            record = record with { Status = "completed", EndedAt = DateTimeOffset.UtcNow };
            await requests.SaveAsync(accountId, record, token);
            logger.LogInformation("Generated {Role} request {RequestId} ({Characters} characters); correlation {CorrelationId}, operation {OperationId}",
                role, requestId, characters, correlation.CorrelationId, correlation.OperationId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Operation {Role} failed for request {RequestId}; correlation {CorrelationId}, operation {OperationId}",
                role, requestId, correlation.CorrelationId, correlation.OperationId);
            throw;
        }
    }

    private async Task FailAsync(DocumentRequestRecord record, JsonElement content,
        Exception error, CancellationToken token)
    {
        logger.LogError(error, "Generation failed for {RequestId}; correlation {CorrelationId}, operation {OperationId}",
            record.Id, record.CorrelationId, record.OperationId);
        await requests.SaveAsync(record.AccountId, record with { Status = "failed", EndedAt = DateTimeOffset.UtcNow }, token);
        await responses.SendAsync(new
        {
            accountId = record.AccountId,
            type = role,
            status = "Failed",
            record.Input,
            record.SessionId,
            responseId = record.Id,
            operationId = record.OperationId,
            correlationId = record.CorrelationId,
            messageId = EventEnvelope.String(content, "messageId"),
            emailSubject = EventEnvelope.String(content, "emailSubject")
        }, token, record.CorrelationId, record.OperationId);
        if (role == "declaration" && !string.IsNullOrWhiteSpace(record.Oid))
            await NotifyAsync(record, "FAILED", token);
    }

    private Task NotifyAsync(DocumentRequestRecord record, string status,
        CancellationToken token) => functions.NotifyAsync("NOTIFICATION_SERVICE", new
        {
            oid = record.Oid,
            notificationType = "DECLARATION",
            data = new
            {
                accountId = record.AccountId,
                requestId = record.Id,
                requestText = record.Input,
                sessionId = record.SessionId,
                status,
                // Keep the previous Logic App behavior: link to the account's generated SharePoint folder.
                declarationUrl = DeclarationUrl(record)
            }
        }, token, record.CorrelationId, record.OperationId);

    private static string DeclarationUrl(DocumentRequestRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.SourceUrl)) return record.SourceUrl;
        var host = Settings.Required("SHAREPOINT_HOST");
        var sitePrefix = Settings.Required("SHAREPOINT_SITE_PREFIX").TrimEnd('/');
        var account = Uri.EscapeDataString(record.AccountId);
        var path = string.Join('/', (record.SharePointPath ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return $"https://{host}{sitePrefix}/{account}/{path}";
    }

    private static string NewSharePointPath(string sessionId, string operationId) =>
        $"/Shared Documents/_GeneratedDocuments/{SafeSegment(sessionId)}{DateTimeOffset.UtcNow:yyyy-MM-ddTHH.mm.ss}-{operationId}";

    private static string SafeSegment(string value)
    {
        var safe = string.Concat(value.Select(character => char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' ? character : '_'));
        return safe.Length <= 80 ? safe : safe[..80];
    }
}
