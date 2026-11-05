using System.Text.Json;
using Aimie.Shared;
using Xunit;

namespace Aimie.Operations.Tests;

public sealed class OperationCorrelationTests
{
    [Fact]
    public void Operation_ids_are_stable_per_source_position_and_distinct_between_operations()
    {
        using var document = JsonDocument.Parse("{}");
        var timeline = OperationCorrelation.Create("timeline-builder", "2", 17, document.RootElement);
        var replay = OperationCorrelation.Create("timeline-builder", "2", 17, document.RootElement);
        var argument = OperationCorrelation.Create("argument-builder", "2", 17, document.RootElement);
        var nextEvent = OperationCorrelation.Create("timeline-builder", "2", 18, document.RootElement);

        Assert.Equal(timeline.OperationId, replay.OperationId);
        Assert.NotEqual(timeline.OperationId, argument.OperationId);
        Assert.NotEqual(timeline.OperationId, nextEvent.OperationId);
        Assert.Equal(timeline.OperationId, timeline.CorrelationId);
        Assert.StartsWith("timeline-builder-", timeline.OperationId);
    }

    [Fact]
    public void Caller_correlation_takes_precedence_and_unsafe_values_fall_back_safely()
    {
        using var callerDocument = JsonDocument.Parse("""
            {"correlationId":"caller:case-17"}
            """);
        using var invalidDocument = JsonDocument.Parse("""
            {"correlationId":"not a safe value"}
            """);
        using var emptyDocument = JsonDocument.Parse("{}");

        var caller = OperationCorrelation.Create("builder_name.test/path", "0", 1,
            callerDocument.RootElement, eventCorrelationId: "event-correlation");
        var eventValue = OperationCorrelation.Create("timeline-builder", "0", 2,
            invalidDocument.RootElement, eventCorrelationId: "event-correlation");
        var storedValue = OperationCorrelation.Create("timeline-builder", "0", 3,
            invalidDocument.RootElement, storedCorrelationId: "stored-correlation", eventCorrelationId: "bad value");
        var fallback = OperationCorrelation.Create("timeline-builder", "0", 4,
            invalidDocument.RootElement, storedCorrelationId: "bad value", eventCorrelationId: "also bad");
        var storedOperation = OperationCorrelation.Create("timeline-builder", "0", 5,
            emptyDocument.RootElement, storedOperationId: "stored-operation", storedCorrelationId: "stored-correlation");
        var unsafeStoredOperation = OperationCorrelation.Create("timeline-builder", "0", 6,
            emptyDocument.RootElement, storedOperationId: "unsafe/operation id");

        Assert.Equal("caller:case-17", caller.CorrelationId);
        Assert.StartsWith("builder_name.test-path-", caller.OperationId);
        Assert.Equal("event-correlation", eventValue.CorrelationId);
        Assert.Equal("stored-correlation", storedValue.CorrelationId);
        Assert.Equal(fallback.OperationId, fallback.CorrelationId);
        Assert.Equal("stored-operation", storedOperation.OperationId);
        Assert.Equal("stored-correlation", storedOperation.CorrelationId);
        Assert.StartsWith("timeline-builder-", unsafeStoredOperation.OperationId);
    }
}
