using System.Text.Json;

namespace Aimie.Shared;

public static class EventProcessing
{
    public static async Task ExecuteAsync(ReadOnlyMemory<byte> body,
        Func<JsonElement, Task> handle, Func<InvalidEventException, Task> retainInvalid,
        Func<Task> checkpoint)
    {
        try
        {
            JsonDocument parsed;
            try { parsed = JsonDocument.Parse(body); }
            catch (JsonException) { throw new InvalidEventException("Event is not valid JSON"); }
            using var document = parsed;
            await handle(EventEnvelope.Content(document.RootElement));
        }
        catch (InvalidEventException error)
        {
            await retainInvalid(error);
        }
        await checkpoint();
    }
}
