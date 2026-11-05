using System.Text;
using Aimie.Shared;
using Xunit;

namespace Aimie.Operations.Tests;

public sealed class EventProcessingTests
{
    [Fact]
    public async Task Valid_event_is_handled_then_checkpointed()
    {
        var steps = new List<string>();
        await EventProcessing.ExecuteAsync(Encoding.UTF8.GetBytes("""{"contentData":{"type":"qa"}}"""),
            content => { steps.Add(content.GetProperty("type").GetString()!); return Task.CompletedTask; },
            _ => { steps.Add("retained"); return Task.CompletedTask; },
            () => { steps.Add("checkpointed"); return Task.CompletedTask; });

        Assert.Equal(["qa", "checkpointed"], steps);
    }

    [Fact]
    public async Task Malformed_json_is_retained_before_checkpoint()
    {
        var steps = new List<string>();
        await EventProcessing.ExecuteAsync(Encoding.UTF8.GetBytes("{bad-json"),
            _ => { steps.Add("handled"); return Task.CompletedTask; },
            error => { steps.Add(error.Message); return Task.CompletedTask; },
            () => { steps.Add("checkpointed"); return Task.CompletedTask; });

        Assert.Equal(["Event is not valid JSON", "checkpointed"], steps);
    }

    [Fact]
    public async Task Invalid_request_is_retained_before_checkpoint()
    {
        var steps = new List<string>();
        await EventProcessing.ExecuteAsync(Encoding.UTF8.GetBytes("""{"type":"qa"}"""),
            _ => throw new InvalidEventException("Event is missing accountId"),
            error => { steps.Add(error.Message); return Task.CompletedTask; },
            () => { steps.Add("checkpointed"); return Task.CompletedTask; });

        Assert.Equal(["Event is missing accountId", "checkpointed"], steps);
    }

    [Fact]
    public async Task Transient_failure_does_not_checkpoint()
    {
        var checkpointed = false;
        await Assert.ThrowsAsync<IOException>(() => EventProcessing.ExecuteAsync(
            Encoding.UTF8.GetBytes("""{"type":"qa"}"""),
            _ => throw new IOException("temporarily unavailable"),
            _ => Task.CompletedTask,
            () => { checkpointed = true; return Task.CompletedTask; }));

        Assert.False(checkpointed);
    }

    [Fact]
    public async Task Failed_invalid_event_retention_does_not_checkpoint()
    {
        var checkpointed = false;
        await Assert.ThrowsAsync<IOException>(() => EventProcessing.ExecuteAsync(
            Encoding.UTF8.GetBytes("bad"),
            _ => Task.CompletedTask,
            _ => throw new IOException("Blob unavailable"),
            () => { checkpointed = true; return Task.CompletedTask; }));

        Assert.False(checkpointed);
    }
}
