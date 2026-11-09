using System.Diagnostics;

namespace StudyTracker;

public static class RequestLogging
{
    /// <summary>
    /// Добавляет <c>X-Request-Id</c> и пишет одну строку на запрос: метод, путь без query, статус, миллисекунды, номер.
    /// Заголовки, query и тело в журнал не попадают. <c>/health*</c> не пишется. Подключается до обработчика исключений.
    /// </summary>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("StudyTracker.Requests");
        var requestId = context.TraceIdentifier;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Request-Id"] = requestId;
            return Task.CompletedTask;
        });
        var started = Stopwatch.GetTimestamp();
        using var scope = log.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId });
        var failed = false;
        try { await next(); }
        catch { failed = true; throw; }
        finally
        {
            if (!context.Request.Path.StartsWithSegments("/health"))
                log.LogInformation("{Method} {Path} {StatusCode} {ElapsedMs} мс {RequestId}", context.Request.Method,
                    context.Request.Path.Value, failed ? 500 : context.Response.StatusCode,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, requestId);
        }
    });
}
