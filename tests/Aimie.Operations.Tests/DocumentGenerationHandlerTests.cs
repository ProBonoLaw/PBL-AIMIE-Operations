using System.Text.Json;
using Aimie.Operations;
using Aimie.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aimie.Operations.Tests;

[Collection("Settings")]
public sealed class DocumentGenerationHandlerTests
{
    public DocumentGenerationHandlerTests()
    {
        var configuration = new ConfigurationManager();
        Settings.Configure(configuration);
        configuration["SHAREPOINT_HOST"] = "sharepoint.example.test";
        configuration["SHAREPOINT_SITE_PREFIX"] = "/sites";
    }

    private readonly FakeFunctions functions = new();
    private readonly FakeResponses responses = new();
    private readonly FakeRequestStore<DocumentRequestRecord> requests = new(record => record.Id);
    private readonly FakeOutputStore outputs = new();
    private readonly FakeDeclarationOutputStore declarationOutputs = new();

    private DocumentGenerationHandler Handler(string role, string? accountFilter = null) =>
        new(role, accountFilter, "test-db", functions, responses, requests, outputs, NullLogger.Instance,
            declarationOutputs);

    private static JsonElement Content(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Declaration_writes_and_indexes_result_then_publishes_and_notifies()
    {
        functions.Returns("DECLARATION_WRITER_FUNCTION", """
            {"runtimeStatus":"Completed","output":{"finalDeclaration":"Declaration text","context":{"source":"a"}}}
            """);
        functions.Returns("EXHIBIT_INDEX", """
            {"document":"Indexed declaration","exhibits":[{"name":"Exhibit A"}]}
            """);
        var content = Content("""
            {"type":"declaration","accountId":"case-a","input":"Draft declaration",
             "sessionId":"session-a","oid":"user-a","messageId":"email-a"}
            """);

        await Handler("declaration").ProcessAsync(content, "2", 15, default);

        Assert.Equal(2, functions.Calls.Count);
        var writer = functions.Calls[0];
        Assert.Equal("DECLARATION_WRITER_FUNCTION", writer.Prefix);
        Assert.True(writer.Wait);
        Assert.Equal(100, writer.Body.GetProperty("maxDocCount").GetInt32());
        var index = functions.Calls[1];
        Assert.Equal("EXHIBIT_INDEX", index.Prefix);
        Assert.False(index.Wait);
        Assert.Equal("Declaration text", index.Body.GetProperty("document").GetString());
        Assert.Equal("a", index.Body.GetProperty("context").GetProperty("source").GetString());
        var operationId = writer.Body.GetProperty("operationId").GetString()!;
        var blob = Assert.Single(outputs.Written);
        Assert.Equal($"declaration/case-a/{operationId}/result.json", blob.Name);
        Assert.Equal("Indexed declaration", blob.Content.GetProperty("document").GetString());
        var sharePointResult = Assert.Single(declarationOutputs.Written);
        Assert.Equal("case-a", sharePointResult.AccountId);
        Assert.EndsWith(operationId, sharePointResult.FolderPath);
        var request = requests.Get("case-a", operationId)!;
        Assert.Equal("https://sharepoint.example.test/sites/case-a" + sharePointResult.FolderPath,
            request.SourceUrl);
        Assert.Equal("declaration-md-id", request.DeclarationMd);
        Assert.Equal("declaration-docx-id", Assert.Single(request.Contents!).FileId);
        var response = Assert.Single(responses.Sent);
        Assert.Equal("Generated", response.GetProperty("status").GetString());
        Assert.Equal(operationId, response.GetProperty("responseId").GetString());
        Assert.Equal("email-a", response.GetProperty("messageId").GetString());
        Assert.Equal("case-a", response.GetProperty("resultReference").GetProperty("partitionKey").GetString());
        Assert.Equal("declaration-md-id", response.GetProperty("declarationMd").GetString());
        Assert.Equal("declaration-docx-id", response.GetProperty("attachments")[0].GetProperty("fileId").GetString());
        Assert.Equal("completed", requests.Get("case-a", operationId)?.Status);
        var notice = Assert.Single(functions.Notifications);
        Assert.Equal("DECLARATION", notice.Body.GetProperty("notificationType").GetString());
        Assert.Equal("SUCCESS", notice.Body.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(request.SourceUrl,
            notice.Body.GetProperty("data").GetProperty("declarationUrl").GetString());
    }

    [Fact]
    public async Task Qa_uses_fast_qa_answer_for_exhibit_index_without_notification()
    {
        functions.Returns("FAST_QA_FUNCTION", """
            {"answer":"Answer text","context":{"source":"b"}}
            """);
        functions.Returns("EXHIBIT_INDEX", """{"document":"Indexed answer","exhibits":[]}""");
        var content = Content("""
            {"type":"qa","accountId":"case-a","question":"What happened?","sessionId":"session-a"}
            """);

        await Handler("qa").ProcessAsync(content, "0", 5, default);

        Assert.Equal(2, functions.Calls.Count);
        var qa = functions.Calls[0];
        Assert.Equal("FAST_QA_FUNCTION", qa.Prefix);
        Assert.False(qa.Wait);
        Assert.Equal("What happened?", qa.Body.GetProperty("question").GetString());
        Assert.Equal(10, qa.Body.GetProperty("maxDocCount").GetInt32());
        Assert.False(qa.Body.GetProperty("provideThoroughResponse").GetBoolean());
        Assert.Equal("Answer text", functions.Calls[1].Body.GetProperty("document").GetString());
        var operationId = qa.Body.GetProperty("operationId").GetString()!;
        Assert.Equal($"qa/case-a/{operationId}/result.json", Assert.Single(outputs.Written).Name);
        Assert.Equal("Generated", Assert.Single(responses.Sent).GetProperty("status").GetString());
        Assert.Empty(functions.Notifications);
        Assert.Equal("completed", requests.Get("case-a", operationId)?.Status);
    }

    [Fact]
    public async Task Missing_declaration_output_fails_without_writing_blob()
    {
        functions.Returns("DECLARATION_WRITER_FUNCTION", """{"runtimeStatus":"Completed","output":{}}""");
        var content = Content("""
            {"type":"declaration","accountId":"case-a","input":"Draft","oid":"user-a"}
            """);

        await Handler("declaration").ProcessAsync(content, "0", 6, default);

        var operationId = functions.Calls[0].Body.GetProperty("operationId").GetString()!;
        Assert.Equal("failed", requests.Get("case-a", operationId)?.Status);
        Assert.Empty(outputs.Written);
        Assert.Equal("Failed", Assert.Single(responses.Sent).GetProperty("status").GetString());
        Assert.Equal("FAILED", Assert.Single(functions.Notifications).Body
            .GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Missing_qa_answer_fails_without_writing_blob()
    {
        functions.Returns("FAST_QA_FUNCTION", """{"context":{}}""");
        await Handler("qa").ProcessAsync(Content("""
            {"type":"qa","accountId":"case-a","input":"Question"}
            """), "0", 7, default);

        var operationId = functions.Calls[0].Body.GetProperty("operationId").GetString()!;
        Assert.Equal("failed", requests.Get("case-a", operationId)?.Status);
        Assert.Empty(outputs.Written);
        Assert.Equal("Failed", Assert.Single(responses.Sent).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Generated_replay_skips_functions_and_blob_write()
    {
        requests.Seed("case-a", new DocumentRequestRecord("0-8", "case-a", "qa", "Question",
            "saved-session", "generated", null, DateTimeOffset.UtcNow, null,
            "https://test.blob.core.windows.net/results/saved.json", null));

        await Handler("qa").ProcessAsync(Content("""
            {"type":"qa","accountId":"case-a","input":"Question"}
            """), "0", 8, default);

        Assert.Empty(functions.Calls);
        Assert.Empty(outputs.Written);
        Assert.Equal("https://test.blob.core.windows.net/results/saved.json",
            Assert.Single(responses.Sent).GetProperty("outputUrl").GetString());
        Assert.Equal("completed", requests.Get("case-a", "0-8")?.Status);
    }

    [Fact]
    public async Task Completed_replay_does_no_work()
    {
        requests.Seed("case-a", new DocumentRequestRecord("0-9", "case-a", "qa", "Question",
            "saved-session", "completed", null, DateTimeOffset.UtcNow, null,
            "https://test.blob.core.windows.net/results/saved.json", null));

        await Handler("qa").ProcessAsync(Content("""
            {"type":"qa","accountId":"case-a","input":"Question"}
            """), "0", 9, default);

        Assert.Empty(functions.Calls);
        Assert.Empty(outputs.Written);
        Assert.Empty(responses.Sent);
        Assert.Empty(requests.Saved);
    }

    [Fact]
    public async Task Blob_failure_leaves_request_started_for_retry()
    {
        functions.Returns("FAST_QA_FUNCTION", """{"answer":"Answer","context":{}}""");
        functions.Returns("EXHIBIT_INDEX", """{"document":"Indexed","exhibits":[]}""");
        outputs.NextWriteError = new IOException("Storage unavailable");
        var content = Content("""
            {"type":"qa","accountId":"case-a","input":"Question"}
            """);

        await Assert.ThrowsAsync<IOException>(() => Handler("qa").ProcessAsync(content, "0", 10, default));

        var operationId = functions.Calls[0].Body.GetProperty("operationId").GetString()!;
        Assert.Equal("started", requests.Get("case-a", operationId)?.Status);
        Assert.Empty(responses.Sent);
    }

    [Fact]
    public async Task Failed_response_publish_reuses_saved_blob()
    {
        functions.Returns("FAST_QA_FUNCTION", """{"answer":"Answer","context":{}}""");
        functions.Returns("EXHIBIT_INDEX", """{"document":"Indexed","exhibits":[]}""");
        responses.NextSendError = new IOException("Event Hub unavailable");
        var content = Content("""
            {"type":"qa","accountId":"case-a","input":"Question"}
            """);
        var handler = Handler("qa");

        await Assert.ThrowsAsync<IOException>(() => handler.ProcessAsync(content, "0", 11, default));
        var operationId = functions.Calls[0].Body.GetProperty("operationId").GetString()!;
        Assert.Equal("generated", requests.Get("case-a", operationId)?.Status);
        await handler.ProcessAsync(content, "0", 11, default);

        Assert.Equal(2, functions.Calls.Count);
        Assert.Single(outputs.Written);
        Assert.Single(responses.Sent);
        Assert.Equal("completed", requests.Get("case-a", operationId)?.Status);
    }
}
