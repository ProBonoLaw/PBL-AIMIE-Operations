using System.Text.Json;
using Aimie.Shared;
using Xunit;

namespace Aimie.Operations.Tests;

public sealed class EventRoutingTests
{
    [Theory]
    [InlineData("argument-builder", "{\"contentData\":{\"type\":\"argument-builder\",\"accountId\":\"a\",\"question\":\"Q\"}}", true)]
    [InlineData("timeline-builder", "{\"contentData\":{\"type\":\"timeline-builder\",\"accountId\":\"a\",\"input\":\"Q\"}}", true)]
    [InlineData("declaration", "{\"contentData\":{\"type\":\"declaration\",\"accountId\":\"a\",\"input\":\"Q\"}}", true)]
    [InlineData("qa", "{\"contentData\":{\"type\":\"qa\",\"accountId\":\"a\",\"question\":\"Q\"}}", true)]
    [InlineData("qa", "{\"contentData\":{\"type\":\"qa\",\"accountId\":\"a\",\"messageId\":\"m\"}}", false)]
    [InlineData("declaration", "{\"contentData\":{\"type\":\"declaration\",\"accountId\":\"a\",\"messageId\":\"m\"}}", false)]
    [InlineData("qa", "{\"contentData\":{\"type\":\"timeline-builder\",\"accountId\":\"a\",\"input\":\"Q\"}}", false)]
    [InlineData("other", "{\"contentData\":{\"type\":\"other\",\"accountId\":\"a\",\"input\":\"Q\"}}", false)]
    public void Routes_only_matching_normalized_requests(string role, string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, EventRouting.ShouldHandle(role, EventEnvelope.Content(document.RootElement)));
    }

    [Fact]
    public void Local_account_filter_excludes_other_accounts()
    {
        using var document = JsonDocument.Parse("""
            {"contentData":{"type":"qa","accountId":"other-case","input":"Question"}}
            """);
        var content = EventEnvelope.Content(document.RootElement);
        Assert.False(EventRouting.ShouldHandle("qa", content, "debug-case"));
        Assert.True(EventRouting.ShouldHandle("qa", content, "other-case"));
    }
}
