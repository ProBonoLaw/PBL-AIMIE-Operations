using System.Text.Json;

namespace Aimie.Shared;

public static class EventRouting
{
    public static bool ShouldHandle(string role, JsonElement content, string? accountFilter = null)
    {
        if (accountFilter is not null && EventEnvelope.String(content, "accountId") != accountFilter)
            return false;
        if (EventEnvelope.String(content, "type") != role) return false;
        if (role is "declaration" or "qa")
            return !string.IsNullOrWhiteSpace(EventEnvelope.String(content, "input")) ||
                   !string.IsNullOrWhiteSpace(EventEnvelope.String(content, "question"));
        return role is "argument-builder" or "timeline-builder";
    }
}
