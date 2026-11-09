using System.Text.Json;
using StudyTracker.Notifications;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class NotificationActionsTests : TelegramFixture
{
    [Fact]
    public async Task TestMessageIsRateLimited()
    {
        await BindAsync();

        Assert.Equal(202, (await Client.Post("notifications/test")).Status);
        Assert.Equal(429, (await Client.Post("notifications/test")).Status);

        await AdvanceAsync("2026-11-11T15:00:59Z");
        Assert.Equal(429, (await Client.Post("notifications/test")).Status);

        await AdvanceAsync("2026-11-11T15:01:00Z");
        Assert.Equal(202, (await Client.Post("notifications/test")).Status);
    }

    [Fact]
    public async Task TestMessageRequiresExplicitRequest()
    {
        await BindAsync();

        await TickAsync();
        Assert.Empty(Fake.Messages);

        Assert.Equal(202, (await Client.Post("notifications/test")).Status);
        await TickAsync();

        Assert.Single(Fake.Messages);
        Assert.Equal(ReminderKind.Test, (await RowsAsync())[0].Kind);
        var settings = await StateAsync();
        Assert.False(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal("Sent", settings.GetProperty("lastDelivery").GetProperty("state").GetString());
    }

    [Fact]
    public async Task DisconnectCancelsTestMessage()
    {
        await BindAsync();
        await Client.Post("notifications/test");
        await Client.Delete("notifications/connection");

        await TickAsync();

        Assert.Equal(DeliveryState.Cancelled, (await RowsAsync())[0].State);
        Assert.Empty(Fake.Messages);
    }

    [Fact]
    public async Task TestMessageRequiresCsrf()
    {
        await BindAsync();

        Assert.Equal(400, (await Client.Post("notifications/test", csrf: false)).Status);
    }

    [Fact]
    public async Task EnablingRequiresConnection()
    {
        var values = new
        { enabled = true, timeZone = "Europe/Moscow", beforeEnabled = true, beforeTime = "19:00", dueEnabled = true, dueTime = "09:00" };

        Assert.Equal(409, (await Client.Put("notifications/settings", values)).Status);
    }

    [Fact]
    public async Task OtherAccountCannotUseConnection()
    {
        await BindAsync();
        var other = App.NewClient();
        await other.SignIn("no-chat@example.test");

        Assert.Equal(409, (await other.Post("notifications/test")).Status);
        Assert.Equal(JsonValueKind.Null, (await StateAsync(other)).GetProperty("lastDelivery").ValueKind);
    }
}
