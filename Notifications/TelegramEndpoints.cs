using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker.Notifications;

public record NotificationInput(bool Enabled, string? TimeZone, bool BeforeEnabled, string? BeforeTime,
    bool DueEnabled, string? DueTime);

public static class TelegramEndpoints
{
    public static readonly string[] Zones = ["Europe/Moscow", "Asia/Yekaterinburg", "Asia/Novosibirsk"];

    public static async Task<NotificationSettings> SettingsAsync(TrackerDb db, string owner)
    {
        var settings = await db.NotificationSettings.FindAsync(owner);
        if (settings is not null) return settings;
        settings = new NotificationSettings { OwnerId = owner };
        db.NotificationSettings.Add(settings);
        await db.SaveChangesAsync();
        return settings;
    }

    public static void MapNotifications(this WebApplication app)
    {
        var group = app.MapGroup("/api/notifications").RequireAuthorization();
        group.MapGet("/settings", async (TrackerDb db, ClaimsPrincipal user, TelegramOptions options) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var settings = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OwnerId == owner)
                ?? new NotificationSettings { OwnerId = owner };
            return Results.Ok(new { settings.Enabled, settings.TimeZone, settings.BeforeEnabled,
                beforeTime = FormatTime(settings.BeforeMinute), settings.DueEnabled, dueTime = FormatTime(settings.DueMinute),
                configured = options.Configured, connected = false, zones = Zones });
        });
        group.MapPut("/settings", async (NotificationInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (!Zones.Contains(input.TimeZone) || !ParseTime(input.BeforeTime, out var before) || !ParseTime(input.DueTime, out var due))
                return Results.BadRequest(new { error = "Выберите доступный часовой пояс и время в формате ЧЧ:ММ." });
            if (input.Enabled)
                return Results.Conflict(new { error = "Сначала подключите Telegram." });
            var settings = await SettingsAsync(db, user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            settings.Enabled = input.Enabled;
            settings.TimeZone = input.TimeZone!;
            settings.BeforeEnabled = input.BeforeEnabled;
            settings.BeforeMinute = before;
            settings.DueEnabled = input.DueEnabled;
            settings.DueMinute = due;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static string FormatTime(int minute) => new TimeOnly(minute / 60, minute % 60).ToString("HH:mm", CultureInfo.InvariantCulture);
    private static bool ParseTime(string? value, out int minute)
    {
        var valid = TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time);
        minute = time.Hour * 60 + time.Minute;
        return valid;
    }
}
