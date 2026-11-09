using Microsoft.Data.Sqlite;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

/// <summary>Настройки уведомлений на обычном хосте без токена бота.</summary>
public class NotificationSettingsTests
{
    private static object Settings(string timeZone = "Europe/Moscow", string dueTime = "09:00") => new
    { enabled = false, timeZone, beforeEnabled = true, beforeTime = "19:00", dueEnabled = true, dueTime };

    [Fact]
    public async Task NotificationDefaults()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("notify@example.test");

        var response = await client.Get("notifications/settings");

        Assert.Equal(200, response.Status);
        Assert.False(response.Body.GetProperty("enabled").GetBoolean());
        Assert.False(response.Body.GetProperty("configured").GetBoolean());
        Assert.Equal("Europe/Moscow", response.Body.GetProperty("timeZone").GetString());
        Assert.Equal("19:00", response.Body.GetProperty("beforeTime").GetString());
        Assert.Equal("09:00", response.Body.GetProperty("dueTime").GetString());
    }

    [Fact]
    public async Task SettingsArePrivate()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        var other = factory.NewClient();
        await client.SignIn("notify@example.test");
        await other.SignIn("other-notify@example.test");

        var changed = new { enabled = false, timeZone = "Asia/Yekaterinburg", beforeEnabled = true, beforeTime = "18:30", dueEnabled = false, dueTime = "08:00" };
        Assert.Equal(204, (await client.Put("notifications/settings", changed)).Status);

        Assert.Equal("Europe/Moscow", (await other.Get("notifications/settings")).Body.GetProperty("timeZone").GetString());
        Assert.Equal("Asia/Yekaterinburg", (await client.Get("notifications/settings")).Body.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task NotificationSettingsRequireCsrf()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("notify@example.test");

        Assert.Equal(400, (await client.Put("notifications/settings", new { }, csrf: false)).Status);
        Assert.Equal(401, (await factory.NewClient().Get("notifications/settings")).Status);
    }

    [Fact]
    public async Task InvalidTimezoneIsRejected()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("notify@example.test");

        Assert.Equal(400, (await client.Put("notifications/settings", Settings(timeZone: "Unknown/Zone"))).Status);
        Assert.Equal(400, (await client.Put("notifications/settings", Settings(dueTime: "25:00"))).Status);
    }

    [Fact]
    public async Task DuplicateReminderIsRejected()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("notify@example.test");
        var owner = (await client.Get("auth/me")).Body.GetProperty("id").GetString();
        var database = Path.Combine(factory.DataDirectory, "tracker.db");
        var insert = $"""
            INSERT INTO ReminderDeliveries(OwnerId,AssignmentId,DueDate,Kind,ScheduledAt,NextAttemptAt,ExpiresAt,BindingVersion,State,Attempts)
            VALUES ('{owner}',123,'2026-12-12',0,0,0,0,0,0,0)
            """;

        SqliteFile.Execute(database, insert);

        Assert.Throws<SqliteException>(() => SqliteFile.Execute(database, insert));
    }
}
