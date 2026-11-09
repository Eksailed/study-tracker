using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using StudyTracker.Notifications;

namespace StudyTracker.Tests.Support;

/// <summary>
/// Основа для тестов уведомлений: хост с подменой Telegram и ручными часами. Фоновые службы в
/// <c>ManualMode</c> ничего не делают, поэтому планировщик, отправщик и опрос вызываются только из теста.
/// </summary>
public abstract class TelegramFixture : IAsyncLifetime
{
    public TrackerFactory App { get; private set; } = null!;
    public FakeTelegram Fake { get; } = new();
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-11-11T15:00:00Z"));
    public LogCollector Logs { get; } = new();
    public ApiClient Client { get; private set; } = null!;

    private static readonly CancellationToken Never = CancellationToken.None;

    public async Task InitializeAsync()
    {
        App = new TrackerFactory(
            new Dictionary<string, string?> { ["Telegram:BotToken"] = FakeTelegram.Token, ["Telegram:ManualMode"] = "true" },
            services: services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<TelegramHandlerFactory>(() => Fake);
                services.AddSingleton<ILoggerProvider>(Logs);
            });
        Assert.True(await App.Services.GetRequiredService<TelegramClient>().InitializeAsync(Never), "Подмена Telegram не подключилась");
        Client = App.NewClient();
        await Client.SignIn("telegram@example.test");
    }

    public async Task DisposeAsync() => await App.DisposeAsync();

    public async Task<string> CodeAsync(ApiClient? client = null)
    {
        var response = await (client ?? Client).Post("notifications/code");
        Assert.True(response.Status == 200, $"Код: {response.Status} {response.Body}");
        var url = new Uri(response.Body.GetProperty("url").GetString()!);
        return QueryHelpers.ParseQuery(url.Query)["start"].ToString();
    }

    public Task PollAsync() => App.Services.GetRequiredService<TelegramUpdateWorker>().PollAsync(Never, timeout: 0);

    public async Task<JsonElement> StateAsync(ApiClient? client = null) => (await (client ?? Client).Get("notifications/settings")).Body;

    public async Task BindAsync()
    {
        Fake.AddUpdate(await CodeAsync());
        await PollAsync();
        Assert.True((await StateAsync()).GetProperty("connected").GetBoolean());
        Fake.Messages.Clear();
    }

    public async Task SettingsAsync(Func<JsonObject, JsonObject>? change = null)
    {
        var settings = new JsonObject
        {
            ["enabled"] = true, ["timeZone"] = "Europe/Moscow", ["beforeEnabled"] = true,
            ["beforeTime"] = "19:00", ["dueEnabled"] = true, ["dueTime"] = "09:00",
        };
        var response = await Client.Put("notifications/settings", change is null ? settings : change(settings));
        Assert.True(response.Status == 204, $"Настройки: {response.Status} {response.Body}");
    }

    public async Task<JsonElement> TaskAsync()
    {
        var subject = await Client.Post("subjects", new { name = "Предмет", color = "teal" });
        var task = await Client.Post("assignments", new
        {
            title = "Название задания", description = "Не передавать описание",
            subjectId = subject.Body.GetProperty("id").GetInt32(), dueDate = "2026-11-12", status = "Todo",
        });
        Assert.Equal(201, task.Status);
        return task.Body;
    }

    public async Task<List<ReminderDelivery>> RowsAsync()
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TrackerDb>().ReminderDeliveries
            .AsNoTracking().OrderBy(d => d.Id).ToListAsync();
    }

    public Task PlanAsync() => App.Services.GetRequiredService<ReminderPlanner>().PlanAsync(Never);

    /// <summary>Один проход так же, как команда <c>--notifications-tick</c>: восстановление, планирование, отправка.</summary>
    public async Task TickAsync()
    {
        var sender = App.Services.GetRequiredService<ReminderSender>();
        await sender.RecoverAsync(Never);
        await PlanAsync();
        await sender.SendPendingAsync(Never);
    }

    public Task AdvanceAsync(string utc)
    {
        Clock.SetUtcNow(DateTimeOffset.Parse(utc));
        return Task.CompletedTask;
    }
}
