using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace MarkdownConverter;

public static class Api
{
    public const string DocxRoute = "/api/markdowntodocxconverter";
    public const string IndexRoute = "/api/markdowndocumentexhibitindexer";
    public const string HtmlRoute = "/api/htmltomarkdownconverter";
    public const string DocxMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var appSettingsFile = Environment.GetEnvironmentVariable("APPSETTINGS_FILE") ?? "/mnt/keyvault/appsettings.json";
        builder.Configuration.AddJsonFile(appSettingsFile, optional: true, reloadOnChange: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);
        builder.Logging.Configure(options => options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        builder.Services.AddSingleton<MarkdownConversionService>();
        builder.Services.AddSingleton<DocumentIndexer>();

        var origins = (builder.Configuration["CORS_ALLOWED_ORIGINS"] ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (origins.Length > 0)
            builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
                .WithOrigins(origins)
                .WithMethods("POST", "GET", "OPTIONS")
                .WithHeaders("Content-Type", "X-Correlation-Id", "X-Operation-Id")
                .WithExposedHeaders("X-Correlation-Id", "X-Operation-Id")));

        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 25 * 1024 * 1024);

        var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<CorrelationLoggingMiddleware>();
        if (origins.Length > 0) app.UseCors();
        app.MapGet("/healthz", () => Results.Ok(new { status = "Healthy" }));
        app.MapPost(DocxRoute, ConvertMarkdownAsync);
        app.MapPost(IndexRoute, IndexDocument);
        app.MapPost(HtmlRoute, ConvertHtml);

        return app;
    }

    private static async Task<IResult> ConvertMarkdownAsync(MarkdownRequest? request,
        MarkdownConversionService converter, CancellationToken cancellationToken)
    {
        if (request?.Markdown is null or "")
            return Results.Text("Please provide Markdown content.", statusCode: StatusCodes.Status400BadRequest);

        var bytes = await converter.ToDocxAsync(request.Markdown, cancellationToken);
        return Results.File(bytes, DocxMediaType,
            $"converted-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.docx");
    }

    private static IResult IndexDocument(DocumentIndexRequest? request, DocumentIndexer indexer)
    {
        if (request?.Document is null or "")
            return Results.Text("Please provide Markdown content.", statusCode: StatusCodes.Status400BadRequest);
        return Results.Json(indexer.Index(request));
    }

    private static IResult ConvertHtml(HtmlRequest? request, MarkdownConversionService converter)
    {
        if (request?.Html is null or "")
            return Results.Text("Please provide HTML content.", statusCode: StatusCodes.Status400BadRequest);
        var markdown = converter.ToMarkdown(request.Html);
        return Results.File(System.Text.Encoding.UTF8.GetBytes(markdown), "text/markdown",
            $"converted-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.md");
    }
}
