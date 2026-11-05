using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MarkdownConverter;

public sealed record RequestCorrelation(string CorrelationId, string OperationId, string TraceId)
{
    private static readonly Regex SafeIdentifier = new(
        "^[a-zA-Z0-9._:/-]{1,128}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static RequestCorrelation From(HttpContext context)
    {
        var operationId = ReadIdentifier(context, "X-Operation-Id") ?? Guid.NewGuid().ToString("N");
        var correlationId = ReadIdentifier(context, "X-Correlation-Id") ?? operationId;
        var traceId = Activity.Current?.TraceId.ToString();
        if (string.IsNullOrWhiteSpace(traceId)) traceId = context.TraceIdentifier;
        return new RequestCorrelation(correlationId, operationId, traceId);
    }

    private static string? ReadIdentifier(HttpContext context, string headerName)
    {
        var value = context.Request.Headers[headerName].ToString();
        return SafeIdentifier.IsMatch(value) ? value : null;
    }
}

public sealed class CorrelationLoggingMiddleware(RequestDelegate next, ILogger<CorrelationLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlation = RequestCorrelation.From(context);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Correlation-Id"] = correlation.CorrelationId;
            context.Response.Headers["X-Operation-Id"] = correlation.OperationId;
            return Task.CompletedTask;
        });

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlation.CorrelationId,
            ["OperationId"] = correlation.OperationId,
            ["TraceId"] = correlation.TraceId
        });

        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "HTTP request started {Method} {Path}; correlation {CorrelationId}, operation {OperationId}, trace {TraceId}",
            context.Request.Method, context.Request.Path, correlation.CorrelationId, correlation.OperationId,
            correlation.TraceId);

        try
        {
            await next(context);
            logger.LogInformation(
                "HTTP request completed {Method} {Path} with status {StatusCode} in {ElapsedMilliseconds} ms; correlation {CorrelationId}, operation {OperationId}, trace {TraceId}",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, stopwatch.ElapsedMilliseconds,
                correlation.CorrelationId, correlation.OperationId, correlation.TraceId);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(
                "HTTP request was canceled {Method} {Path} after {ElapsedMilliseconds} ms; correlation {CorrelationId}, operation {OperationId}, trace {TraceId}",
                context.Request.Method, context.Request.Path, stopwatch.ElapsedMilliseconds,
                correlation.CorrelationId, correlation.OperationId, correlation.TraceId);
            throw;
        }
        catch (Exception error)
        {
            logger.LogError(error,
                "HTTP request failed {Method} {Path} after {ElapsedMilliseconds} ms; correlation {CorrelationId}, operation {OperationId}, trace {TraceId}",
                context.Request.Method, context.Request.Path, stopwatch.ElapsedMilliseconds,
                correlation.CorrelationId, correlation.OperationId, correlation.TraceId);
            throw;
        }
    }
}
