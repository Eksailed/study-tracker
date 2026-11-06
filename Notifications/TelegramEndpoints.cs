using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;

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
        group.MapGet("/settings", async (TrackerDb db, ClaimsPrincipal user, TelegramOptions options, TelegramClient client) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var settings = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OwnerId == owner)
                ?? new NotificationSettings { OwnerId = owner };
            var connection = await db.TelegramConnections.AsNoTracking().SingleOrDefaultAsync(c => c.OwnerId == owner);
            var pending = await db.TelegramLinkCodes.AsNoTracking().SingleOrDefaultAsync(c => c.OwnerId == owner);
            return Results.Ok(new { settings.Enabled, settings.TimeZone, settings.BeforeEnabled,
                beforeTime = FormatTime(settings.BeforeMinute), settings.DueEnabled, dueTime = FormatTime(settings.DueMinute),
                configured = options.Configured, ready = client.Ready, botStatus = client.Status,
                connected = connection is not null && connection.BotId == client.BotId,
                connectionProblem = connection?.Problem, codeExpiresAt = pending?.ExpiresAt, zones = Zones });
        });
        group.MapPut("/settings", async (NotificationInput input, TrackerDb db, ClaimsPrincipal user, TelegramClient client) =>
        {
            if (!Zones.Contains(input.TimeZone) || !ParseTime(input.BeforeTime, out var before) || !ParseTime(input.DueTime, out var due))
                return Results.BadRequest(new { error = "Выберите доступный часовой пояс и время в формате ЧЧ:ММ." });
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await using var transaction = await db.Database.BeginTransactionAsync();
            var connection = await db.TelegramConnections.FindAsync(owner);
            if (input.Enabled && (connection is null || connection.Problem is not null || !client.Ready || connection.BotId != client.BotId))
                return Results.Conflict(new { error = "Сначала подключите работающего бота Telegram." });
            var settings = await SettingsAsync(db, owner);
            settings.Enabled = input.Enabled;
            settings.TimeZone = input.TimeZone!;
            settings.BeforeEnabled = input.BeforeEnabled;
            settings.BeforeMinute = before;
            settings.DueEnabled = input.DueEnabled;
            settings.DueMinute = due;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        });
        group.MapPost("/code", async (TrackerDb db, ClaimsPrincipal user, TelegramClient client, TimeProvider clock) =>
        {
            if (!client.Ready) return Results.Json(new { error = client.Status }, statusCode: 503);
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var settings = await SettingsAsync(db, owner);
            if (now >= settings.CodeWindowStartedAt + 600000) { settings.CodeWindowStartedAt = now; settings.CodeIssueCount = 0; }
            if (settings.CodeIssueCount >= 5) return Results.Json(new { error = "Получить код можно 5 раз за 10 минут." }, statusCode: 429);
            settings.CodeIssueCount++;
            var code = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
            var link = await db.TelegramLinkCodes.FindAsync(owner);
            if (link is null) { link = new TelegramLinkCode { OwnerId = owner, Hash = "" }; db.Add(link); }
            link.Hash = TelegramUpdateWorker.Hash(code);
            link.ExpiresAt = now + 600000;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.Ok(new { url = $"https://t.me/{client.Username}?start={code}", expiresAt = link.ExpiresAt });
        });
        group.MapDelete("/connection", async (TrackerDb db, ClaimsPrincipal user) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await using var transaction = await db.Database.BeginTransactionAsync();
            var settings = await SettingsAsync(db, owner);
            settings.Enabled = false;
            settings.BindingVersion++;
            await db.TelegramConnections.Where(c => c.OwnerId == owner).ExecuteDeleteAsync();
            await db.TelegramLinkCodes.Where(c => c.OwnerId == owner).ExecuteDeleteAsync();
            await db.ReminderDeliveries.Where(d => d.OwnerId == owner && d.State == DeliveryState.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, DeliveryState.Cancelled));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
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
