using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker.Calendar;

public static class CalendarEndpoints
{
    public const string LimiterPolicy = "calendar";

    public static IServiceCollection AddCalendar(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy(LimiterPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        return services;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Управление ссылкой: только вошедшему по cookie, токены API действуют лишь на <c>/api/v1</c>.</summary>
    public static void MapCalendar(this RouteGroupBuilder group)
    {
        group.MapGet("/calendar", async (TrackerDb db, ClaimsPrincipal user) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var link = await db.CalendarLinks.AsNoTracking().SingleOrDefaultAsync(l => l.OwnerId == owner);
            return Results.Ok(link is null ? new { exists = false, createdAt = (DateTime?)null }
                : new { exists = true, createdAt = (DateTime?)Utc(link.CreatedAt) });
        });

        group.MapPost("/calendar", async (TrackerDb db, ClaimsPrincipal user) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var key = CalendarKeys.Create();
            var link = await db.CalendarLinks.SingleOrDefaultAsync(l => l.OwnerId == owner);
            if (link is null) db.CalendarLinks.Add(link = new CalendarLink { OwnerId = owner, KeyHash = CalendarKeys.Hash(key) });
            else { link.KeyHash = CalendarKeys.Hash(key); link.CreatedAt = DateTime.UtcNow; }
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Ссылка уже создаётся. Повторите через минуту." }); }
            return Results.Created("/api/calendar", new { key, createdAt = Utc(link.CreatedAt) });
        });

        group.MapDelete("/calendar", async (TrackerDb db, ClaimsPrincipal user) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await db.CalendarLinks.Where(l => l.OwnerId == owner).ExecuteDeleteAsync();
            return Results.NoContent();
        });
    }

    /// <summary>Сама лента. Секрет в query, а не в пути: журнал запросов пишет только путь.</summary>
    public static void MapCalendarFeed(this WebApplication app) =>
        app.MapGet("/calendar.ics", async (string? key, HttpContext context, TrackerDb db) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (string.IsNullOrEmpty(key)) return Results.NotFound();
            var hash = CalendarKeys.Hash(key);
            var link = await db.CalendarLinks.AsNoTracking().SingleOrDefaultAsync(l => l.KeyHash == hash);
            if (link is null) return Results.NotFound();
            var tasks = await db.Assignments.AsNoTracking().Include(a => a.Subject)
                .Where(a => a.OwnerId == link.OwnerId && a.Status != AssignmentStatus.Done).ToListAsync();
            return Results.Text(CalendarFeed.Build(tasks), "text/calendar; charset=utf-8");
        }).AllowAnonymous().RequireRateLimiting(LimiterPolicy);
}
