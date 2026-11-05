using System.Text.Json;

namespace Aimie.Shared;

public sealed class InvalidEventException(string message) : Exception(message);

public static class EventEnvelope
{
    public static JsonElement Content(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("contentData", out var content)
            ? content : root;

    public static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    public static string Required(JsonElement value, string name) =>
        !string.IsNullOrWhiteSpace(String(value, name)) ? String(value, name)!
            : throw new InvalidDataException($"Object is missing {name}");

    public static string RequiredEvent(JsonElement value, string name) =>
        !string.IsNullOrWhiteSpace(String(value, name)) ? String(value, name)!
            : throw new InvalidEventException($"Event is missing {name}");

    public static string RequestText(JsonElement value)
    {
        var question = String(value, "question");
        if (!string.IsNullOrWhiteSpace(question)) return question;
        var input = String(value, "input");
        if (!string.IsNullOrWhiteSpace(input)) return input;
        throw new InvalidEventException("Event is missing input or question");
    }

    public static int? Integer(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) ? number : null;

    public static bool? Boolean(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
        item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : null;

    public static JsonElement? Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) ? item : null;
}
