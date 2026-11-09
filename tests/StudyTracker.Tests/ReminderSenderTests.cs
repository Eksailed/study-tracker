using Microsoft.EntityFrameworkCore;
using StudyTracker.Notifications;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class ReminderSenderTests : TelegramFixture
{
    private async Task Prepare()
    {
        await BindAsync();
        await SettingsAsync();
        await TaskAsync();
        await AdvanceAsync("2026-11-11T16:00:00Z");
    }

    private async Task<ReminderDelivery> Row() => (await RowsAsync())[0];

    [Fact]
    public async Task SuccessfulSendRecordsMessageId()
    {
        await Prepare();

        await TickAsync();
        var row = await Row();
        Assert.Equal(DeliveryState.Sent, row.State);
        Assert.NotNull(row.MessageId);

        await TickAsync();
        Assert.Single(Fake.Messages);
    }

    [Fact]
    public async Task Respects429RetryAfter()
    {
        await Prepare();
        Fake.Responses.Enqueue(FakeReply.Error(429, retryAfter: 5));

        await TickAsync();
        Assert.Equal(DeliveryState.Pending, (await Row()).State);
        await TickAsync();
        Assert.Single(Fake.Messages);

        await AdvanceAsync("2026-11-11T16:00:05Z");
        await TickAsync();
        Assert.Equal(DeliveryState.Sent, (await Row()).State);
        Assert.Equal(2, Fake.Messages.Count);
    }

    [Fact]
    public async Task RetryLimitIsThree()
    {
        await Prepare();
        for (var i = 0; i < 4; i++) Fake.Responses.Enqueue(FakeReply.Error(429, retryAfter: 1));

        for (var second = 0; second < 4; second++)
        {
            await AdvanceAsync($"2026-11-11T16:00:0{second}Z");
            await TickAsync();
        }

        Assert.Equal(DeliveryState.Failed, (await Row()).State);
        Assert.Equal(4, Fake.Messages.Count);
    }

    [Fact]
    public async Task Forbidden403DisablesDelivery()
    {
        await Prepare();
        Fake.Responses.Enqueue(FakeReply.Error(403));

        await TickAsync();

        var settings = await StateAsync();
        Assert.False(settings.GetProperty("enabled").GetBoolean());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, settings.GetProperty("connectionProblem").ValueKind);
        Assert.Equal(DeliveryState.Failed, (await Row()).State);
    }

    [Fact]
    public async Task NetworkFailureIsUnknownWithoutAutomaticRetry()
    {
        await Prepare();
        Fake.Responses.Enqueue(FakeReply.NetworkFailure);

        await TickAsync();
        Assert.Equal(DeliveryState.Unknown, (await Row()).State);

        await AdvanceAsync("2026-11-11T16:00:05Z");
        await TickAsync();
        Assert.Single(Fake.Messages);
    }

    [Fact]
    public async Task Server500IsUnknownWithoutAutomaticRetry()
    {
        await Prepare();
        Fake.Responses.Enqueue(FakeReply.Error(500));

        await TickAsync();
        Assert.Equal(DeliveryState.Unknown, (await Row()).State);

        await TickAsync();
        Assert.Single(Fake.Messages);
    }

    [Fact]
    public async Task RestartMarksInflightSendUnknown()
    {
        await Prepare();
        await PlanAsync();
        using (var scope = App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TrackerDb>().ReminderDeliveries.ExecuteUpdateAsync(
                s => s.SetProperty(d => d.State, DeliveryState.Sending).SetProperty(d => d.Attempts, 1));

        await TickAsync();

        Assert.Equal(DeliveryState.Unknown, (await Row()).State);
        Assert.Empty(Fake.Messages);
    }

    [Fact]
    public async Task ReminderContainsOnlyAllowedFields()
    {
        await Prepare();

        await TickAsync();

        var message = Fake.Messages[0];
        var text = message.GetProperty("text").GetString()!;
        Assert.Contains("Название задания", text);
        Assert.Contains("Предмет", text);
        Assert.Contains("12 ноября 2026", text);
        Assert.DoesNotContain("Не передавать описание", text);
        Assert.DoesNotContain("telegram@example.test", text);
        Assert.False(message.TryGetProperty("parse_mode", out _));
    }

    [Fact]
    public async Task Bad400IsFailedWithoutRetry()
    {
        await Prepare();
        Fake.Responses.Enqueue(FakeReply.Error(400));

        await TickAsync();
        Assert.Equal(DeliveryState.Failed, (await Row()).State);

        await TickAsync();
        Assert.Single(Fake.Messages);
    }

    [Fact]
    public async Task RestartWithoutTokenMarksInflightUnknown()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("notify@example.test");
        var owner = (await client.Get("auth/me")).Body.GetProperty("id").GetString()!;
        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            db.ReminderDeliveries.Add(new ReminderDelivery
            { OwnerId = owner, Kind = ReminderKind.Test, State = DeliveryState.Sending, Attempts = 1 });
            return db.SaveChanges();
        });

        await using var restarted = factory.Restart();
        var again = restarted.NewClient();
        await again.Login("notify@example.test");

        var settings = (await again.Get("notifications/settings")).Body;
        Assert.Equal("Unknown", settings.GetProperty("lastDelivery").GetProperty("state").GetString());
    }
}
