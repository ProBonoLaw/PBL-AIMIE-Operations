using System.Net;
using System.Text.Json;
using Aimie.Shared;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aimie.Operations.Tests;

[Collection("Settings")]
public sealed class FunctionClientTests
{
    [Fact]
    public async Task Notification_posts_json_to_service_without_function_key()
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);
        configuration["TEST_NOTIFICATION_URL"] = "http://notification-service.test/api/sendNotice";
        var handler = new RecordingHandler
        {
            ResponseStatusCode = HttpStatusCode.Accepted,
            ResponseBody = ""
        };
        var client = new FunctionClient(new HttpClient(handler), NullLogger<FunctionClient>.Instance);
        var payload = new TestNotice("user-a", "ARGUMENT_BUILDER", new TestNoticeData(
            "case-a", "2-15", "Draft an argument", "session-a", "SUCCESS", null));

        await client.NotifyAsync("TEST_NOTIFICATION", payload, default);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://notification-service.test/api/sendNotice", handler.Uri?.ToString());
        Assert.Null(handler.FunctionKey);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var notice = body.RootElement;
        Assert.Equal("user-a", notice.GetProperty("oid").GetString());
        Assert.Equal("ARGUMENT_BUILDER", notice.GetProperty("notificationType").GetString());
        var data = notice.GetProperty("data");
        Assert.Equal("case-a", data.GetProperty("accountId").GetString());
        Assert.Equal("2-15", data.GetProperty("requestId").GetString());
        Assert.Equal("Draft an argument", data.GetProperty("requestText").GetString());
        Assert.Equal("session-a", data.GetProperty("sessionId").GetString());
        Assert.Equal("SUCCESS", data.GetProperty("status").GetString());
        Assert.False(data.TryGetProperty("errorMessage", out _));
    }

    [Fact]
    public async Task Notification_failure_includes_status_endpoint_and_response_body()
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);
        configuration["TEST_NOTIFICATION_URL"] = "http://notification-service.test/api/sendNotice";
        var handler = new RecordingHandler
        {
            ResponseStatusCode = HttpStatusCode.BadRequest,
            ResponseBody = "{\"error\":\"invalid notice\"}"
        };
        var client = new FunctionClient(new HttpClient(handler), NullLogger<FunctionClient>.Instance);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.NotifyAsync(
            "TEST_NOTIFICATION", new { oid = "user-a", notificationType = "UNKNOWN", data = new { invalid = true } }, default));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("TEST_NOTIFICATION", error.Message);
        Assert.Contains("http://notification-service.test/api/sendNotice", error.Message);
        Assert.Contains("400", error.Message);
        Assert.Contains("invalid notice", error.Message);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Notification_retries_transient_server_responses()
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);
        configuration["TEST_NOTIFICATION_URL"] = "http://notification-service.test/api/sendNotice";
        var handler = new RecordingHandler
        {
            ResponseStatuses = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.Accepted])
        };
        var client = new FunctionClient(new HttpClient(handler), NullLogger<FunctionClient>.Instance);

        await client.NotifyAsync("TEST_NOTIFICATION", new { oid = "user-a" }, default);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(HttpStatusCode.Accepted, handler.LastStatusCode);
    }

    [Fact]
    public async Task Missing_function_url_is_rejected_before_sending()
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);
        var handler = new RecordingHandler();
        var client = new FunctionClient(new HttpClient(handler), NullLogger<FunctionClient>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.NotifyAsync("UNCONFIGURED_FUNCTION", new { value = 1 }, default));
        Assert.Null(handler.Method);
    }

    [Fact]
    public async Task Function_call_returns_json_response_without_function_key()
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);
        configuration["TEST_CALL_URL"] = "https://functions.test/call";
        var handler = new RecordingHandler { ResponseBody = """{"answer":"done"}""" };
        var client = new FunctionClient(new HttpClient(handler), NullLogger<FunctionClient>.Instance);

        var result = await client.CallAsync("TEST_CALL", new { question = "Why?" }, wait: false, default,
            correlationId: "corr-a", operationId: "op-a");

        Assert.Equal("done", result.GetProperty("answer").GetString());
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Null(handler.FunctionKey);
        Assert.Equal("corr-a", handler.CorrelationId);
        Assert.Equal("op-a", handler.OperationId);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal("Why?", body.RootElement.GetProperty("question").GetString());
    }

    [Fact]
    public async Task Binary_function_call_returns_file_bytes_and_forwards_keys_and_correlation()
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);
        configuration["TEST_BINARY_URL"] = "https://functions.test/convert";
        configuration["TEST_BINARY_API_KEY"] = "binary-key";
        var expectedBytes = new byte[] { 0x50, 0x4b, 0x03, 0x04 };
        var handler = new RecordingHandler { ResponseBytes = expectedBytes };
        var client = new FunctionClient(new HttpClient(handler), NullLogger<FunctionClient>.Instance);

        var bytes = await client.CallBinaryAsync("TEST_BINARY", new { markdown = "# Declaration" }, default,
            correlationId: "corr-binary", operationId: "op-binary");

        Assert.Equal(expectedBytes, bytes);
        Assert.Equal("binary-key", handler.FunctionKey);
        Assert.Equal("corr-binary", handler.CorrelationId);
        Assert.Equal("op-binary", handler.OperationId);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal("# Declaration", body.RootElement.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task Binary_function_call_retries_transient_responses()
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);
        configuration["TEST_BINARY_URL"] = "https://functions.test/convert";
        var handler = new RecordingHandler
        {
            ResponseStatuses = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]),
            ResponseBytes = [0x50, 0x4b]
        };
        var client = new FunctionClient(new HttpClient(handler), NullLogger<FunctionClient>.Instance);

        var bytes = await client.CallBinaryAsync("TEST_BINARY", new { markdown = "Draft" }, default);

        Assert.Equal(new byte[] { 0x50, 0x4b }, bytes);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Binary_function_failure_includes_status_and_response_body()
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);
        configuration["TEST_BINARY_URL"] = "https://functions.test/convert";
        var handler = new RecordingHandler
        {
            ResponseStatusCode = HttpStatusCode.BadRequest,
            ResponseBody = "invalid markdown"
        };
        var client = new FunctionClient(new HttpClient(handler), NullLogger<FunctionClient>.Instance);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CallBinaryAsync("TEST_BINARY", new { markdown = "Draft" }, default));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("400", error.Message);
        Assert.Contains("invalid markdown", error.Message);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void Event_hub_transport_uses_default_valid_value_and_rejects_invalid_value()
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);

        configuration["EVENTHUB_TRANSPORT_TYPE"] = "";
        Assert.Equal(EventHubsTransportType.AmqpTcp, Settings.EventHubConnectionOptions().TransportType);
        configuration["EVENTHUB_TRANSPORT_TYPE"] = "AmqpWebSockets";
        Assert.Equal(EventHubsTransportType.AmqpWebSockets, Settings.EventHubConnectionOptions().TransportType);
        configuration["EVENTHUB_TRANSPORT_TYPE"] = "unsupported";
        Assert.Throws<InvalidOperationException>(() => Settings.EventHubConnectionOptions());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? FunctionKey { get; private set; }
        public string? CorrelationId { get; private set; }
        public string? OperationId { get; private set; }
        public string? Body { get; private set; }
        public byte[]? ResponseBytes { get; set; }
        public int RequestCount { get; private set; }
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "{}";
        public Queue<HttpStatusCode> ResponseStatuses { get; set; } = new();
        public HttpStatusCode? LastStatusCode { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            Uri = request.RequestUri;
            FunctionKey = request.Headers.TryGetValues("x-functions-key", out var values)
                ? values.Single() : null;
            CorrelationId = request.Headers.TryGetValues("x-correlation-id", out var correlations)
                ? correlations.Single() : null;
            OperationId = request.Headers.TryGetValues("x-operation-id", out var operations)
                ? operations.Single() : null;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastStatusCode = ResponseStatuses.TryDequeue(out var nextStatus) ? nextStatus : ResponseStatusCode;
            var responseContent = ResponseBytes is null
                ? new StringContent(ResponseBody)
                : new ByteArrayContent(ResponseBytes);
            return new HttpResponseMessage(LastStatusCode.Value) { Content = responseContent };
        }
    }

    private sealed record TestNotice(string Oid, string NotificationType, TestNoticeData Data);

    private sealed record TestNoticeData(string AccountId, string RequestId, string RequestText,
        string SessionId, string Status, string? ErrorMessage);
}
