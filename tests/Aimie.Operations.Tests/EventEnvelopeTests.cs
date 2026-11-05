using System.Text.Json;
using Aimie.Shared;
using Xunit;

namespace Aimie.Operations.Tests;

public sealed class EventEnvelopeTests
{
    [Fact]
    public void Content_uses_content_data_when_present()
    {
        using var document = JsonDocument.Parse("""{"contentData":{"type":"qa"},"type":"other"}""");
        Assert.Equal("qa", EventEnvelope.String(EventEnvelope.Content(document.RootElement), "type"));
    }

    [Fact]
    public void Request_text_prefers_question_then_input()
    {
        using var document = JsonDocument.Parse("""{"question":"Question","input":"Fallback"}""");
        Assert.Equal("Question", EventEnvelope.RequestText(document.RootElement));
    }

    [Fact]
    public void Missing_account_and_text_are_invalid_events()
    {
        using var document = JsonDocument.Parse("""{"type":"qa"}""");
        Assert.Throws<InvalidEventException>(() => EventEnvelope.RequiredEvent(document.RootElement, "accountId"));
        Assert.Throws<InvalidEventException>(() => EventEnvelope.RequestText(document.RootElement));
    }

    [Fact]
    public void Optional_numbers_and_booleans_reject_wrong_types()
    {
        using var document = JsonDocument.Parse("""
            {"maxDocCount":"25","provideThoroughResponse":"false"}
            """);
        Assert.Null(EventEnvelope.Integer(document.RootElement, "maxDocCount"));
        Assert.Null(EventEnvelope.Boolean(document.RootElement, "provideThoroughResponse"));
    }

    [Fact]
    public void Boolean_reads_true_and_false_and_returns_null_when_missing()
    {
        using var document = JsonDocument.Parse("""
            {"yes":true,"no":false}
            """);

        Assert.True(EventEnvelope.Boolean(document.RootElement, "yes"));
        Assert.False(EventEnvelope.Boolean(document.RootElement, "no"));
        Assert.Null(EventEnvelope.Boolean(document.RootElement, "missing"));
    }
}
