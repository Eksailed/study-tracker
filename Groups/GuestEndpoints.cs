using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using StudyTracker.Calendar;

namespace StudyTracker;

/// <summary>
/// Гость без аккаунта видит задания группы по ссылке-секрету: страницу <c>/g.html#ключ</c> и ленту <c>/group.ics?key=</c>.
/// Секрет лежит в базе только как SHA-256, ссылку выпускает староста группы или владелец.
/// </summary>
public static class GuestEndpoints
{
    /// <summary>Задания показываются от «сегодня минус 7 дней», не больше 500 штук.</summary>
    public const int WindowDays = 7;
    public const int MaxItems = 500;

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Управление ссылкой: староста своей группы и владелец; всем остальным — <c>404</c>.</summary>
    public static void MapGuestLink(this RouteGroupBuilder group)
    {
        group.MapGet("/groups/{gid:int}/guest-link", async (int gid, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (await GroupEndpoints.ManagerOfAsync(db, user, gid) is null) return Results.NotFound();
            var created = await db.Groups.AsNoTracking().Where(g => g.Id == gid && g.GuestKeyHash != null)
                .Select(g => g.GuestKeyCreatedAt).SingleOrDefaultAsync();
            return Results.Ok(created is { } at ? new { exists = true, createdAt = (DateTime?)Utc(at) }
                : new { exists = false, createdAt = (DateTime?)null });
        });

        group.MapPost("/groups/{gid:int}/guest-link", async (int gid, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (await GroupEndpoints.ManagerOfAsync(db, user, gid) is null) return Results.NotFound();
            var key = CalendarKeys.Create();
            var now = DateTime.UtcNow;
            var hash = CalendarKeys.Hash(key);
            var changed = await db.Groups.Where(g => g.Id == gid).ExecuteUpdateAsync(set => set
                .SetProperty(g => g.GuestKeyHash, hash).SetProperty(g => g.GuestKeyCreatedAt, now));
            return changed == 0 ? Results.NotFound() : Results.Created($"/api/groups/{gid}/guest-link", new { key, createdAt = Utc(now) });
        });

        group.MapDelete("/groups/{gid:int}/guest-link", async (int gid, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (await GroupEndpoints.ManagerOfAsync(db, user, gid) is null) return Results.NotFound();
            await db.Groups.Where(g => g.Id == gid).ExecuteUpdateAsync(set => set
                .SetProperty(g => g.GuestKeyHash, (string?)null).SetProperty(g => g.GuestKeyCreatedAt, (DateTime?)null));
            return Results.NoContent();
        });
    }

    private sealed record GuestView(string Name, List<Assignment> Items);

    /// <summary>Группа по ключу и окно её заданий; неверный, пустой или отозванный ключ даёт <c>null</c>.</summary>
    private static async Task<GuestView?> LoadAsync(TrackerDb db, string? key, TimeProvider clock)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var hash = CalendarKeys.Hash(key);
        var found = await db.Groups.AsNoTracking().Where(g => g.GuestKeyHash == hash).Select(g => new { g.Id, g.Name }).SingleOrDefaultAsync();
        if (found is null) return null;
        var from = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-WindowDays);
        var items = await db.Assignments.AsNoTracking().Include(a => a.Subject)
            .Where(a => a.GroupId == found.Id && a.DueDate >= from)
            .OrderBy(a => a.DueDate).ThenBy(a => a.Id).Take(MaxItems).ToListAsync();
        return new GuestView(found.Name, items);
    }

    /// <summary>Публичные адреса без входа. Секрет в query, а не в пути: журнал запросов пишет только путь.</summary>
    public static void MapGuest(this WebApplication app)
    {
        app.MapGet("/public/group", async (string? key, HttpContext context, TrackerDb db, TimeProvider clock) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (await LoadAsync(db, key, clock) is not { } view) return Results.NotFound();
            return Results.Ok(new
            {
                name = view.Name,
                assignments = view.Items.Select(a => new
                {
                    subject = a.Subject!.Name, color = a.Subject.Color, title = a.Title,
                    description = a.Description, dueDate = a.DueDate, priority = a.Priority,
                }),
            });
        }).AllowAnonymous().RequireRateLimiting(CalendarEndpoints.LimiterPolicy);

        app.MapGet("/group.ics", async (string? key, HttpContext context, TrackerDb db, TimeProvider clock) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (await LoadAsync(db, key, clock) is not { } view) return Results.NotFound();
            return Results.Text(CalendarFeed.Build(view.Items, view.Name, "group-task", skipDone: false), "text/calendar; charset=utf-8");
        }).AllowAnonymous().RequireRateLimiting(CalendarEndpoints.LimiterPolicy);
    }
}
