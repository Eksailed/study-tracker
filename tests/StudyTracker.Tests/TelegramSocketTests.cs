using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using StudyTracker.Notifications;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

/// <summary>Таймаут и обрыв ответа на настоящем сокете: настоящий <c>SocketsHttpHandler</c>, без подмены обработчика.</summary>
public class TelegramSocketTests
{
    private static TrackerFactory Host(SocketTelegram telegram) => new(new Dictionary<string, string?>
    {
        ["Telegram:BotToken"] = FakeTelegram.Token,
        ["Telegram:ApiBase"] = telegram.BaseUrl,
        ["Telegram:SendTimeoutSeconds"] = "1",
        ["Telegram:ManualMode"] = "true",
        ["Testing:UtcNow"] = "2026-11-11T16:00:00Z",
    });

    /// <summary>Аккаунт с привязанным чатом (прямой записью в базу, без опроса) и заданием, по которому пора напомнить.</summary>
    private static async Task Prepare(TrackerFactory factory)
    {
        Assert.True(await factory.Services.GetRequiredService<TelegramClient>().InitializeAsync(CancellationToken.None));
        var client = factory.NewClient();
        await client.SignIn("socket@example.test");
        var owner = (await client.Get("auth/me")).Body.GetProperty("id").GetString()!;
        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            db.TelegramConnections.Add(new TelegramConnection { OwnerId = owner, ChatId = 10000000001, BotId = 123456 });
            return db.SaveChanges();
        });
        var settings = new
        { enabled = true, timeZone = "Europe/Moscow", beforeEnabled = true, beforeTime = "19:00", dueEnabled = true, dueTime = "09:00" };
        Assert.Equal(204, (await client.Put("notifications/settings", settings)).Status);
        await client.CreateTask(await client.CreateSubject("Предмет"));
    }

    private static async Task Tick(TrackerFactory factory)
    {
        var sender = factory.Services.GetRequiredService<ReminderSender>();
        await sender.RecoverAsync(CancellationToken.None);
        await factory.Services.GetRequiredService<ReminderPlanner>().PlanAsync(CancellationToken.None);
        await sender.SendPendingAsync(CancellationToken.None);
    }

    private static DeliveryState State(TrackerFactory factory) => factory.Scope(services =>
        services.GetRequiredService<TrackerDb>().ReminderDeliveries.AsNoTracking().OrderBy(d => d.Id).First().State);

    [Fact]
    public async Task TimeoutIsUnknownWithoutAutomaticRetry()
    {
        await using var telegram = await SocketTelegram.StartAsync(SocketMode.Hang);
        await using var factory = Host(telegram);
        await Prepare(factory);

        var clock = Stopwatch.StartNew();
        await Tick(factory);
        clock.Stop();

        Assert.Equal(DeliveryState.Unknown, State(factory));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Отправка длилась {clock.Elapsed}: таймаут 1 секунда не применился");
        await Tick(factory);
        Assert.Equal(1, telegram.SendCount);
    }

    [Fact]
    public async Task TruncatedResponseIsUnknownWithoutAutomaticRetry()
    {
        await using var telegram = await SocketTelegram.StartAsync(SocketMode.Truncated);
        await using var factory = Host(telegram);
        await Prepare(factory);

        await Tick(factory);

        Assert.Equal(DeliveryState.Unknown, State(factory));
        await Tick(factory);
        Assert.Equal(1, telegram.SendCount);
    }
}
