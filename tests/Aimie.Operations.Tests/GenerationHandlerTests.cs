using System.Text.Json;
using Aimie.Operations;
using Aimie.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aimie.Operations.Tests;

public sealed class GenerationHandlerTests
{
    private readonly FakeFunctions functions = new();
    private readonly FakeResponses responses = new();
    private readonly FakeRequestStore<RequestRecord> requests = new(record => record.Id);
    private readonly FakeSharePointOutputStore outputs = new();

    private GenerationHandler Handler(string role, string? accountFilter = null) =>
        new(role, accountFilter, "test-db", functions, responses, requests, outputs, NullLogger.Instance);

    private static JsonElement Content(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Argument_builder_sends_expected_payload_and_publishes_result()
    {
        functions.Returns("ARGUMENT_BUILDER_FUNCTION", """{"runtimeStatus":"Completed","output":{"argument":"Draft"}}""");
        var content = Content("""
            {"type":"argument-builder","accountId":"case-a","question":"Make an argument",
             "sessionId":"session-a","oid":"user-a","conversationId":"conversation-a"}
            """);

        await Handler("argument-builder").ProcessAsync(content, "3", 27, default);

        var call = Assert.Single(functions.Calls);
        Assert.Equal("ARGUMENT_BUILDER_FUNCTION", call.Prefix);
        Assert.True(call.Wait);
        Assert.Equal("Make an argument", call.Body.GetProperty("question").GetString());
        Assert.Equal(200, call.Body.GetProperty("maxDocCount").GetInt32());
        Assert.True(call.Body.GetProperty("provideThoroughResponse").GetBoolean());
        var operationId = call.Body.GetProperty("operationId").GetString()!;
        var response = Assert.Single(responses.Sent);
        Assert.Equal("Generated", response.GetProperty("status").GetString());
        Assert.Equal(operationId, response.GetProperty("responseId").GetString());
        Assert.Equal("test-db", response.GetProperty("resultReference").GetProperty("databaseId").GetString());
        Assert.Equal("case-a", response.GetProperty("resultReference").GetProperty("partitionKey").GetString());
        Assert.Equal("completed", requests.Get("case-a", operationId)?.Status);
        var savedOutput = Assert.Single(outputs.Written);
        Assert.Equal("case-a", savedOutput.AccountId);
        Assert.Equal(requests.Get("case-a", operationId)?.SharePointPath, savedOutput.FolderPath);
        Assert.Equal("Completed", savedOutput.Content.GetProperty("runtimeStatus").GetString());
        Assert.True(requests.Get("case-a", operationId)?.ResultStored);
        Assert.Equal("https://sharepoint.example.test/sites/case-a" + savedOutput.FolderPath,
            requests.Get("case-a", operationId)?.SourceUrl);
        var notice = Assert.Single(functions.Notifications);
        Assert.Equal("ARGUMENT_BUILDER", notice.Body.GetProperty("notificationType").GetString());
        Assert.Equal("SUCCESS", notice.Body.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Timeline_builder_uses_input_and_custom_document_limit()
    {
        functions.Returns("TIMELINE_BUILDER_FUNCTION", """{"runtimeStatus":"Completed","output":{"timeline":["Event A"]}}""");
        var content = Content("""
            {"type":"timeline-builder","accountId":"case-a","input":"Build timeline",
             "sessionId":"session-a","maxDocCount":42}
            """);

        await Handler("timeline-builder").ProcessAsync(content, "1", 8, default);

        var call = Assert.Single(functions.Calls);
        Assert.Equal("TIMELINE_BUILDER_FUNCTION", call.Prefix);
        Assert.Equal("Build timeline", call.Body.GetProperty("input").GetString());
        Assert.Equal(42, call.Body.GetProperty("maxDocCount").GetInt32());
        Assert.False(call.Body.TryGetProperty("question", out _));
        var operationId = call.Body.GetProperty("operationId").GetString()!;
        Assert.Empty(functions.Notifications);
        Assert.Equal("completed", requests.Get("case-a", operationId)?.Status);
        Assert.Equal("Event A", Assert.Single(outputs.Written).Content.GetProperty("output")
            .GetProperty("timeline")[0].GetString());
    }

    [Fact]
    public async Task Reused_session_id_still_gets_unique_request_ids_and_result_folders()
    {
        functions.Returns("TIMELINE_BUILDER_FUNCTION", """{"runtimeStatus":"Completed","output":{"timeline":[]}}""");
        var content = Content("""
            {"type":"timeline-builder","accountId":"case-a","input":"Build timeline","sessionId":"session/shared"}
            """);
        var handler = Handler("timeline-builder");

        await handler.ProcessAsync(content, "2", 20, default);
        await handler.ProcessAsync(content, "2", 21, default);

        var operationIds = functions.Calls.Select(call => call.Body.GetProperty("operationId").GetString()).ToArray();
        var responseIds = responses.Sent.Select(response => response.GetProperty("responseId").GetString()).ToArray();
        var folderPaths = outputs.Written.Select(output => output.FolderPath).ToArray();
        Assert.Equal(2, operationIds.Distinct().Count());
        Assert.Equal(operationIds, responseIds);
        Assert.Equal(2, folderPaths.Distinct().Count());
        Assert.All(folderPaths, path => Assert.StartsWith("/Shared Documents/_GeneratedDocuments/session_shared", path));
        Assert.All(Enumerable.Range(0, 2), index => Assert.Contains(operationIds[index]!, folderPaths[index]));
    }

    [Fact]
    public async Task Legacy_sharepoint_paths_get_the_operation_id_only_once()
    {
        functions.Returns("TIMELINE_BUILDER_FUNCTION", """{"runtimeStatus":"Completed","output":{"timeline":[]}}""");
        var handler = Handler("timeline-builder");
        var legacyPath = "/Shared Documents/_GeneratedDocuments/session-a2026-01-01T10.00.00";
        var firstContent = Content("""
            {"type":"timeline-builder","accountId":"case-a","input":"Build timeline","sessionId":"session-a"}
            """);
        requests.Seed("case-a", new RequestRecord("0-22", "case-a", "session-a", "Build timeline",
            "timeline-builder", "started", null, DateTimeOffset.UtcNow, null, legacyPath,
            null, null, null));

        await handler.ProcessAsync(firstContent, "0", 22, default);

        var migrated = requests.Get("case-a", "0-22")!;
        Assert.Equal($"{legacyPath}-{migrated.OperationId}", migrated.SharePointPath);
        Assert.EndsWith(migrated.OperationId!, migrated.SharePointPath);

        var secondContent = Content("""
            {"type":"timeline-builder","accountId":"case-a","input":"Build timeline","sessionId":"session-a"}
            """);
        var operationId = OperationCorrelation.Create("timeline-builder", "0", 23, secondContent).OperationId;
        var alreadyUniquePath = $"{legacyPath}-{operationId}";
        requests.Seed("case-a", new RequestRecord("0-23", "case-a", "session-a", "Build timeline",
            "timeline-builder", "started", null, DateTimeOffset.UtcNow, null, alreadyUniquePath,
            null, null, null, false, operationId));

        await handler.ProcessAsync(secondContent, "0", 23, default);

        Assert.Equal(alreadyUniquePath, requests.Get("case-a", "0-23")?.SharePointPath);
    }

    [Fact]
    public async Task Incomplete_function_result_marks_request_failed_and_notifies()
    {
        functions.Returns("ARGUMENT_BUILDER_FUNCTION", """{"runtimeStatus":"Failed"}""");
        var content = Content("""
            {"type":"argument-builder","accountId":"case-a","input":"Argue","oid":"user-a"}
            """);

        await Handler("argument-builder").ProcessAsync(content, "0", 1, default);

        var operationId = functions.Calls[0].Body.GetProperty("operationId").GetString()!;
        Assert.Equal("failed", requests.Get("case-a", operationId)?.Status);
        var response = Assert.Single(responses.Sent);
        Assert.Equal("Failed", response.GetProperty("status").GetString());
        Assert.Equal(operationId, response.GetProperty("responseId").GetString());
        Assert.Equal("FAILED", Assert.Single(functions.Notifications).Body
            .GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Generated_replay_skips_function_and_completes_delivery()
    {
        requests.Seed("case-a", new RequestRecord("0-2", "case-a", "saved-session", "Prompt",
            "argument-builder", "generated", null, DateTimeOffset.UtcNow, null,
            "/saved-path", "https://saved/path", "See file rawoutput.json", "rawoutput.json", true));

        await Handler("argument-builder").ProcessAsync(Content("""
            {"type":"argument-builder","accountId":"case-a","input":"Prompt"}
            """), "0", 2, default);

        Assert.Empty(functions.Calls);
        Assert.Single(responses.Sent);
        Assert.Equal("completed", requests.Get("case-a", "0-2")?.Status);
        Assert.Equal("https://saved/path", requests.Get("case-a", "0-2")?.SourceUrl);
    }

    [Fact]
    public async Task Completed_replay_does_no_work()
    {
        requests.Seed("case-a", new RequestRecord("0-3", "case-a", "saved-session", "Prompt",
            "timeline-builder", "completed", null, DateTimeOffset.UtcNow, null,
            "/saved-path", "https://saved/path", "See file rawoutput.json", "rawoutput.json", true));

        await Handler("timeline-builder").ProcessAsync(Content("""
            {"type":"timeline-builder","accountId":"case-a","input":"Prompt"}
            """), "0", 3, default);

        Assert.Empty(functions.Calls);
        Assert.Empty(responses.Sent);
        Assert.Empty(requests.Saved);
    }

    [Fact]
    public async Task Transient_function_error_stays_uncompleted_for_retry()
    {
        functions.NextCallError = new HttpRequestException("Temporary failure");
        var content = Content("""
            {"type":"argument-builder","accountId":"case-a","input":"Argue"}
            """);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Handler("argument-builder").ProcessAsync(content, "0", 4, default));

        var operationId = functions.Calls[0].Body.GetProperty("operationId").GetString()!;
        Assert.Equal("started", requests.Get("case-a", operationId)?.Status);
        Assert.Empty(responses.Sent);
    }

    [Fact]
    public async Task Failed_response_publish_replays_without_rerunning_function()
    {
        functions.Returns("TIMELINE_BUILDER_FUNCTION", """{"runtimeStatus":"Completed","output":{"timeline":["Event A"]}}""");
        responses.NextSendError = new IOException("Event Hub unavailable");
        var content = Content("""
            {"type":"timeline-builder","accountId":"case-a","input":"Build timeline"}
            """);
        var handler = Handler("timeline-builder");

        await Assert.ThrowsAsync<IOException>(() => handler.ProcessAsync(content, "0", 5, default));
        var operationId = functions.Calls[0].Body.GetProperty("operationId").GetString()!;
        Assert.Equal("generated", requests.Get("case-a", operationId)?.Status);
        await handler.ProcessAsync(content, "0", 5, default);

        Assert.Single(functions.Calls);
        Assert.Single(responses.Sent);
        Assert.Equal("completed", requests.Get("case-a", operationId)?.Status);
    }
}
