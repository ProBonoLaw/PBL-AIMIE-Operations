using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aimie.Shared;

public sealed record OperationCorrelation(string OperationId, string CorrelationId)
{
    public static OperationCorrelation Create(string operation, string partition, long sequence,
        JsonElement content, string? storedOperationId = null, string? storedCorrelationId = null,
        string? eventCorrelationId = null)
    {
        var operationId = IsSafe(storedOperationId) ? storedOperationId! :
            CreateOperationId(operation, partition, sequence);
        var requested = EventEnvelope.String(content, "correlationId");
        var correlationId = IsSafe(requested) ? requested! :
            IsSafe(eventCorrelationId) ? eventCorrelationId! :
            IsSafe(storedCorrelationId) ? storedCorrelationId! : operationId;
        return new OperationCorrelation(operationId, correlationId);
    }

    private static string CreateOperationId(string operation, string partition, long sequence)
    {
        // Event Hubs sequence numbers are unique only within one partition of one hub.
        // Include the source and worker role so IDs remain distinct across all generation paths.
        var identity = string.Join('\n', Settings.Get("EVENTHUB_NAMESPACE") ?? "unknown-namespace",
            Settings.Get("EVENTHUB_NAME") ?? "unknown-hub", operation, partition,
            sequence.ToString(CultureInfo.InvariantCulture));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var prefix = string.Concat(operation.Take(48).Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-'));
        return $"{(string.IsNullOrWhiteSpace(prefix) ? "operation" : prefix)}-{digest}";
    }

    private static bool IsSafe(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/');
}
