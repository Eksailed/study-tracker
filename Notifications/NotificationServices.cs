using System.Globalization;

namespace StudyTracker.Notifications;

public sealed class NotificationClock(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}

public static class NotificationServices
{
    public static void AddNotifications(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Telegram").Get<TelegramOptions>() ?? new TelegramOptions();
        builder.Services.AddSingleton(options);
        var fixedValue = builder.Configuration["Testing:UtcNow"];
        TimeProvider clock = builder.Environment.IsDevelopment() && fixedValue is not null
            ? new NotificationClock(DateTimeOffset.Parse(fixedValue, CultureInfo.InvariantCulture)) : TimeProvider.System;
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<TelegramClient>();
        builder.Services.AddSingleton<TelegramUpdateWorker>();
        builder.Services.AddSingleton<ReminderPlanner>();
        builder.Services.AddSingleton<ReminderSender>();
        if (options.Configured)
        {
            builder.Services.AddHostedService<TelegramPollingService>();
            builder.Services.AddHostedService<ReminderService>();
        }
    }
}
