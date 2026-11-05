using System.Text.Json;
using Aimie.Shared;

namespace Aimie.Operations;

public sealed class GenerationHandler(
    string role, string? accountFilter, string database, IFunctionClient functions,
    IEventsPublisher responses, IRequestStore<RequestRecord> requests,
    ISharePointOutputStore outputs, ILogger logger)
{
    public async Task ProcessAsync(JsonElement content, string partition, long sequence, CancellationToken token,
        string? sourceCorrelationId = null)
    {
        if (!EventRouting.ShouldHandle(role, content, accountFilter)) return;
        var type = EventEnvelope.String(content, "type")!;
        var accountId = EventEnvelope.RequiredEvent(content, "accountId");
        var input = EventEnvelope.RequestText(content);
        var correlation = OperationCorrelation.Create(type, partition, sequence, content,
            eventCorrelationId: sourceCorrelationId);
        var requestId = correlation.OperationId;
        var legacyRequestId = $"{partition}-{sequence}";
        RequestRecord? existing;
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
        if (existing?.Status == "completed" && existing.ResultStored)
        {
            logger.LogInformation("Request {RequestId} was already completed; correlation {CorrelationId}, operation {OperationId}",
                requestId, existing.CorrelationId, existing.OperationId);
            return;
        }
        correlation = OperationCorrelation.Create(type, partition, sequence, content,
            existing?.OperationId, existing?.CorrelationId, sourceCorrelationId);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlation.CorrelationId,
            ["OperationId"] = correlation.OperationId
        });
        try
        {
            var sessionId = existing?.SessionId ?? EventEnvelope.String(content, "sessionId") ?? Guid.NewGuid().ToString();
            var sharePointPath = existing?.SharePointPath ?? string.Empty;
            if (existing is null)
                sharePointPath = NewSharePointPath(sessionId, correlation.OperationId);
            else if (!existing.ResultStored)
                sharePointPath = EnsureUniqueSharePointPath(sharePointPath, correlation.OperationId);
            var record = existing ?? new RequestRecord(requestId, accountId, sessionId, input, type, "started",
                EventEnvelope.String(content, "oid"), DateTimeOffset.UtcNow, null, sharePointPath, null, null, null,
                false, correlation.OperationId, correlation.CorrelationId);
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

            var prefix = type == "argument-builder" ? "ARGUMENT_BUILDER_FUNCTION" : "TIMELINE_BUILDER_FUNCTION";
            var notificationType = type == "argument-builder" ? "ARGUMENT_BUILDER" : "TIMELINE_BUILDER";
            if (record.Status != "generated" || !record.ResultStored)
            {
                try
                {
                    var payload = type == "argument-builder" ? (object)new
                    {
                        accountId,
                        question = input,
                        sessionId,
                        maxDocCount = EventEnvelope.Integer(content, "maxDocCount") ?? 200,
                        provideThoroughResponse = EventEnvelope.Boolean(content, "provideThoroughResponse") ?? true,
                        correlationId = correlation.CorrelationId,
                        operationId = correlation.OperationId
                    } : new
                    {
                        accountId,
                        input,
                        sessionId,
                        maxDocCount = EventEnvelope.Integer(content, "maxDocCount") ?? 150,
                        correlationId = correlation.CorrelationId,
                        operationId = correlation.OperationId
                    };
                    var result = await functions.CallAsync(prefix, payload, wait: true, token,
                        correlation.CorrelationId, correlation.OperationId);
                    if (EventEnvelope.String(result, "runtimeStatus") != "Completed")
                        throw new InvalidDataException($"{type} returned no Completed runtime status");
                    var sourceUrl = await outputs.WriteAsync(accountId, sharePointPath, result, token,
                        correlation.CorrelationId, correlation.OperationId);
                    record = record with
                    {
                        Status = "generated",
                        ResultStored = true,
                        SourceUrl = sourceUrl,
                        Contents = $"See file rawoutput.json in SharePoint folder {sharePointPath}",
                        RawOutputJson = "rawoutput.json"
                    };
                }
                catch (InvalidDataException error)
                {
                    logger.LogError(error, "Failed {Type} request {RequestId}; correlation {CorrelationId}, operation {OperationId}",
                        type, requestId, correlation.CorrelationId, correlation.OperationId);
                    await requests.SaveAsync(accountId, record with { Status = "failed", EndedAt = DateTimeOffset.UtcNow }, token);
                    await responses.SendAsync(new
                    {
                        accountId,
                        emailSubject = EventEnvelope.String(content, "emailSubject"),
                        input,
                        messageId = EventEnvelope.String(content, "messageId"),
                        sessionId,
                        responseId = requestId,
                        status = "Failed",
                        type,
                        correlationId = correlation.CorrelationId,
                        operationId = correlation.OperationId
                    }, token, correlation.CorrelationId, correlation.OperationId);
                    if (!string.IsNullOrWhiteSpace(record.Oid))
                        await NotifyAsync(notificationType, record, "FAILED", token);
                    return;
                }
                record = record with { EndedAt = DateTimeOffset.UtcNow };
                await requests.SaveAsync(accountId, record, token);
            }
            await responses.SendAsync(new
            {
                accountId,
                conversationId = EventEnvelope.String(content, "conversationId"),
                emailAddresses = EventEnvelope.Property(content, "emailAddresses"),
                emailSubject = EventEnvelope.String(content, "emailSubject"),
                input,
                messageId = EventEnvelope.String(content, "messageId"),
                oid = EventEnvelope.String(content, "oid"),
                responseId = requestId,
                correlationId = correlation.CorrelationId,
                operationId = correlation.OperationId,
                resultReference = new
                {
                    databaseId = database,
                    containerId = "requests",
                    id = requestId,
                    itemId = requestId,
                    partitionKey = accountId
                },
                sessionId,
                status = "Generated",
                type
            }, token, correlation.CorrelationId, correlation.OperationId);
            if (!string.IsNullOrWhiteSpace(record.Oid))
                await NotifyAsync(notificationType, record, "SUCCESS", token);
            await requests.SaveAsync(accountId, record with { Status = "completed", EndedAt = DateTimeOffset.UtcNow }, token);
            logger.LogInformation("Completed {Type} request {RequestId}; correlation {CorrelationId}, operation {OperationId}",
                type, requestId, correlation.CorrelationId, correlation.OperationId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Operation {Type} failed for request {RequestId}; correlation {CorrelationId}, operation {OperationId}",
                type, requestId, correlation.CorrelationId, correlation.OperationId);
            throw;
        }
    }

    private Task NotifyAsync(string notificationType, RequestRecord record, string status,
        CancellationToken token) => functions.NotifyAsync("NOTIFICATION_SERVICE", new
        {
            oid = record.Oid,
            notificationType,
            // Builder notice schemas reject unmapped fields and do not define sourceUrl.
            data = new
            {
                accountId = record.AccountId,
                requestId = record.Id,
                requestText = record.Input,
                sessionId = record.SessionId,
                status
            }
        }, token, record.CorrelationId, record.OperationId);

    private static string NewSharePointPath(string sessionId, string operationId) =>
        $"/Shared Documents/_GeneratedDocuments/{SafeSegment(sessionId)}{DateTimeOffset.UtcNow:yyyy-MM-ddTHH.mm.ss}-{operationId}";

    private static string EnsureUniqueSharePointPath(string existingPath, string operationId)
    {
        var suffix = $"-{operationId}";
        return existingPath.EndsWith(suffix, StringComparison.Ordinal)
            ? existingPath : existingPath.TrimEnd('/') + suffix;
    }

    private static string SafeSegment(string value) =>
        string.Concat(value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
            ? character : '_'));
}
