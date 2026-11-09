using Microsoft.EntityFrameworkCore;
using StudyTracker.Notifications;

namespace StudyTracker;

/// <summary>Папки данных и ключей, как их выбрал <c>Program.cs</c>.</summary>
public sealed record TrackerPaths(string DataDirectory, string KeyDirectory);

public static class Health
{
    public static void MapHealth(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health/ready", async (TrackerDb db, TrackerPaths paths, TelegramOptions telegram,
            TelegramClient client, ILogger<TrackerDb> log, CancellationToken token) =>
        {
            var database = await Check(() => db.Database.ExecuteSqlRawAsync("SELECT 1", token), "database", log);
            var schema = database && await Check(async () =>
            {
                if ((await db.Database.GetPendingMigrationsAsync(token)).Any())
                    throw new InvalidOperationException("Применены не все миграции.");
            }, "schema", log);
            var keys = await Check(() =>
            {
                var probe = Path.Combine(paths.KeyDirectory, ".ready-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return Task.CompletedTask;
            }, "keys", log);
            var ok = database && schema && keys;
            var state = !telegram.Configured ? "disabled" : client.Ready ? "ready" : "error";
            return Results.Json(new
            {
                status = ok ? "ok" : "fail",
                checks = new
                {
                    database = database ? "ok" : "fail",
                    schema = schema ? "ok" : "fail",
                    keys = keys ? "ok" : "fail",
                    telegram = state,
                },
            }, statusCode: ok ? 200 : 503);
        });
    }

    /// <summary>Выполняет проверку; причина сбоя уходит только в журнал, в ответ не попадает.</summary>
    private static async Task<bool> Check(Func<Task> probe, string name, ILogger log)
    {
        try { await probe(); return true; }
        catch (Exception error)
        {
            log.LogWarning(error, "Проверка готовности {Check} не прошла", name);
            return false;
        }
    }
}
