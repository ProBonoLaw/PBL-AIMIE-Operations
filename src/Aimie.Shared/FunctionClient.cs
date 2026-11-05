using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DaatPhoenix.HttpOrchestration;
using Microsoft.Extensions.Logging;

namespace Aimie.Shared;

public interface IFunctionClient
{
    Task<JsonElement> CallAsync(string prefix, object body, bool wait, CancellationToken token,
        string? correlationId = null, string? operationId = null);
    Task<byte[]> CallBinaryAsync(string prefix, object body, CancellationToken token,
        string? correlationId = null, string? operationId = null);
    Task NotifyAsync(string prefix, object body, CancellationToken token,
        string? correlationId = null, string? operationId = null);
}

public sealed class FunctionClient(HttpClient client, ILogger<FunctionClient> logger) : IFunctionClient
{
    private const int FunctionAttemptLimit = 3;
    private const int NotificationAttemptLimit = 3;
    private const int MaxNotificationErrorBodyLength = 4096;
    private static readonly TimeSpan FunctionRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NotificationRetryDelay = TimeSpan.FromSeconds(2);
    // Match PostAsJsonAsync casing and omit null optional fields rejected by strict notice schemas.
    private static readonly JsonSerializerOptions NotificationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpOrchestrationClient orchestration = new(client, logger,
        new HttpOrchestrationOptions { Timeout = TimeSpan.FromHours(2) });

    public async Task<JsonElement> CallAsync(string prefix, object body, bool wait, CancellationToken token,
        string? correlationId = null, string? operationId = null)
    {
        var (endpoint, headers) = RequestConfiguration(prefix, correlationId, operationId);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["OperationId"] = operationId
        });
        logger.LogInformation("Calling HTTP endpoint {EndpointPrefix}; operation {OperationId}, correlation {CorrelationId}",
            prefix, operationId, correlationId);
        var result = wait
            ? await orchestration.SendAndWaitForCompletionAsync(HttpMethod.Post, endpoint, body, prefix, headers,
                token, retryCount: 3, retryDelay: TimeSpan.FromSeconds(2))
            : await orchestration.SendAsync(HttpMethod.Post, endpoint, body, prefix, headers,
                token, retryCount: 3, retryDelay: TimeSpan.FromSeconds(2));
        var completed = result ?? throw new InvalidDataException($"{prefix} returned an empty response");
        logger.LogInformation("HTTP endpoint {EndpointPrefix} returned status {RuntimeStatus}, instance {InstanceId}; operation {OperationId}, correlation {CorrelationId}",
            prefix, EventEnvelope.String(completed, "runtimeStatus"),
            EventEnvelope.String(completed, "instanceId") ?? EventEnvelope.String(completed, "id"),
            operationId, correlationId);
        return completed;
    }

    public async Task<byte[]> CallBinaryAsync(string prefix, object body, CancellationToken token,
        string? correlationId = null, string? operationId = null)
    {
        var (endpoint, headers) = RequestConfiguration(prefix, correlationId, operationId);
        var json = JsonSerializer.Serialize(body);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["OperationId"] = operationId
        });

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (headers is not null)
                foreach (var header in headers)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsByteArrayAsync(token);
                    if (content.Length == 0)
                        throw new InvalidDataException($"HTTP endpoint {prefix} returned an empty binary response");
                    logger.LogInformation("Binary HTTP endpoint {EndpointPrefix} returned HTTP {StatusCode}; operation {OperationId}, correlation {CorrelationId}",
                        prefix, (int)response.StatusCode, operationId, correlationId);
                    return content;
                }

                var status = response.StatusCode;
                var detail = await response.Content.ReadAsStringAsync(token);
                if (IsTransient(status) && attempt < FunctionAttemptLimit)
                {
                    logger.LogWarning("Binary HTTP endpoint {EndpointPrefix} returned HTTP {StatusCode} on attempt {Attempt}/{AttemptLimit}; retrying. Operation {OperationId}, correlation {CorrelationId}",
                        prefix, (int)status, attempt, FunctionAttemptLimit, operationId, correlationId);
                    await Task.Delay(FunctionRetryDelay, token);
                    continue;
                }

                throw new HttpRequestException(
                    $"HTTP endpoint {prefix} at {endpoint} failed with HTTP {(int)status} ({status}). " +
                    $"OperationId={operationId ?? "unknown"}; CorrelationId={correlationId ?? "unknown"}. " +
                    $"Response body: {Truncate(detail, MaxNotificationErrorBodyLength)}", null, status);
            }
            catch (HttpRequestException error) when (IsTransient(error.StatusCode) &&
                                                       attempt < FunctionAttemptLimit)
            {
                logger.LogWarning(error,
                    "Binary HTTP endpoint {EndpointPrefix} failed on attempt {Attempt}/{AttemptLimit}; retrying. Operation {OperationId}, correlation {CorrelationId}",
                    prefix, attempt, FunctionAttemptLimit, operationId, correlationId);
                await Task.Delay(FunctionRetryDelay, token);
            }
        }
    }

    public async Task NotifyAsync(string prefix, object body, CancellationToken token,
        string? correlationId = null, string? operationId = null)
    {
        var endpoint = new Uri(Settings.Required(prefix + "_URL"), UriKind.Absolute);
        var json = JsonSerializer.Serialize(body, NotificationJsonOptions);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["OperationId"] = operationId
        });

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddCorrelationHeaders(request, correlationId, operationId);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (HttpRequestException error) when (IsTransient(error.StatusCode) &&
                                                       attempt < NotificationAttemptLimit)
            {
                logger.LogWarning(error,
                    "Notification {Prefix} to {Endpoint} failed on attempt {Attempt}/{AttemptLimit}; retrying. Operation {OperationId}, correlation {CorrelationId}",
                    prefix, endpoint, attempt, NotificationAttemptLimit, operationId, correlationId);
                await Task.Delay(NotificationRetryDelay, token);
                continue;
            }
            catch (HttpRequestException error)
            {
                var status = error.StatusCode is { } statusCode
                    ? $"HTTP {(int)statusCode} ({statusCode})" : "no HTTP response";
                throw new HttpRequestException(
                    $"Notification {prefix} to {endpoint} failed on attempt {attempt}/{NotificationAttemptLimit} ({status}). " +
                    $"OperationId={operationId ?? "unknown"}; CorrelationId={correlationId ?? "unknown"}.",
                    error, error.StatusCode);
            }

            using (response)
            {
                var responseBody = await response.Content.ReadAsStringAsync(token);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("Notification {Prefix} to {Endpoint} succeeded with HTTP {StatusCode}; operation {OperationId}, correlation {CorrelationId}",
                        prefix, endpoint, (int)response.StatusCode, operationId, correlationId);
                    return;
                }

                if (IsTransient(response.StatusCode) && attempt < NotificationAttemptLimit)
                {
                    logger.LogWarning(
                        "Notification {Prefix} to {Endpoint} returned HTTP {StatusCode} on attempt {Attempt}/{AttemptLimit}; retrying. Operation {OperationId}, correlation {CorrelationId}",
                        prefix, endpoint, (int)response.StatusCode, attempt, NotificationAttemptLimit,
                        operationId, correlationId);
                    await Task.Delay(NotificationRetryDelay, token);
                    continue;
                }

                var diagnosticBody = Truncate(responseBody, MaxNotificationErrorBodyLength);
                throw new HttpRequestException(
                    $"Notification {prefix} to {endpoint} failed with HTTP {(int)response.StatusCode} " +
                    $"({response.StatusCode}) after {attempt} attempt(s). " +
                    $"OperationId={operationId ?? "unknown"}; CorrelationId={correlationId ?? "unknown"}. " +
                    $"Response body: {diagnosticBody}",
                    null, response.StatusCode);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode? statusCode) =>
        statusCode is null || statusCode == HttpStatusCode.TooManyRequests || (int)statusCode.Value >= 500;

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength
        ? value : value[..maxLength] + " [truncated]";

    private static (Uri Endpoint, IReadOnlyDictionary<string, string>? Headers) RequestConfiguration(
        string prefix, string? correlationId, string? operationId)
    {
        var endpoint = new Uri(Settings.Required(prefix + "_URL"), UriKind.Absolute);
        var key = Settings.Get(prefix + "_API_KEY");
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(key)) headers["x-functions-key"] = key;
        if (!string.IsNullOrWhiteSpace(correlationId)) headers["x-correlation-id"] = correlationId;
        if (!string.IsNullOrWhiteSpace(operationId)) headers["x-operation-id"] = operationId;
        return (endpoint, headers.Count == 0 ? null : headers);
    }

    private static void AddCorrelationHeaders(HttpRequestMessage request, string? correlationId,
        string? operationId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId))
            request.Headers.TryAddWithoutValidation("x-correlation-id", correlationId);
        if (!string.IsNullOrWhiteSpace(operationId))
            request.Headers.TryAddWithoutValidation("x-operation-id", operationId);
    }
}
