using Microsoft.EntityFrameworkCore;

namespace StudyTracker.Notifications;

public sealed class ReminderPlanner(IServiceScopeFactory scopes, TimeProvider clock, TelegramClient telegram)
{
    public static long ScheduledAt(DateOnly date, ReminderKind kind, NotificationSettings settings)
    {
        var day = kind == ReminderKind.Before ? date.AddDays(-1) : date;
        var minute = kind == ReminderKind.Before ? settings.BeforeMinute : settings.DueMinute;
        var local = day.ToDateTime(new TimeOnly(minute / 60, minute % 60), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone)))
            .ToUnixTimeMilliseconds();
    }

    public static bool Current(ReminderDelivery delivery, Assignment? task, NotificationSettings? settings,
        TelegramConnection? connection, long botId, DateTimeOffset now)
    {
        if (settings is null || connection is null || connection.Problem is not null || connection.BotId != botId
            || delivery.BindingVersion != settings.BindingVersion) return false;
        if (delivery.Kind == ReminderKind.Test) return true;
        if (!settings.Enabled || task is null || task.OwnerId != delivery.OwnerId || task.Status == AssignmentStatus.Done
            || task.DueDate != delivery.DueDate) return false;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone)).DateTime);
        return task.DueDate >= today && (delivery.Kind == ReminderKind.Before ? settings.BeforeEnabled : settings.DueEnabled)
            && delivery.ScheduledAt == ScheduledAt(task.DueDate, delivery.Kind, settings);
    }

    public async Task PlanAsync(CancellationToken token)
    {
        if (!telegram.Ready) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDb>();
        var now = clock.GetUtcNow();
        var stamp = now.ToUnixTimeMilliseconds();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var settings = await db.NotificationSettings.ToDictionaryAsync(s => s.OwnerId, token);
        var connections = await db.TelegramConnections.ToDictionaryAsync(c => c.OwnerId, token);
        var tasks = await db.Assignments.AsNoTracking().ToDictionaryAsync(a => a.Id, token);
        var deliveries = await db.ReminderDeliveries.Where(d => d.State == DeliveryState.Pending).ToListAsync(token);
        foreach (var delivery in deliveries)
        {
            tasks.TryGetValue(delivery.AssignmentId ?? -1, out var task);
            settings.TryGetValue(delivery.OwnerId, out var setting);
            connections.TryGetValue(delivery.OwnerId, out var connection);
            if (!Current(delivery, task, setting, connection, telegram.BotId, now)) delivery.State = DeliveryState.Cancelled;
            else if (delivery.ExpiresAt <= stamp) delivery.State = DeliveryState.Skipped;
        }
        foreach (var setting in settings.Values.Where(s => s.Enabled))
        {
            if (!connections.TryGetValue(setting.OwnerId, out var connection) || connection.Problem is not null || connection.BotId != telegram.BotId) continue;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(setting.TimeZone)).DateTime);
            foreach (var task in tasks.Values.Where(a => a.OwnerId == setting.OwnerId && a.Status != AssignmentStatus.Done && a.DueDate >= today))
            foreach (var kind in new[] { ReminderKind.Before, ReminderKind.Due })
            {
                if (!(kind == ReminderKind.Before ? setting.BeforeEnabled : setting.DueEnabled)) continue;
                var scheduled = ScheduledAt(task.DueDate, kind, setting);
                if (scheduled > stamp) continue;
                var row = await db.ReminderDeliveries.SingleOrDefaultAsync(d => d.AssignmentId == task.Id && d.DueDate == task.DueDate && d.Kind == kind, token);
                if (row is not null && row.State is not (DeliveryState.Cancelled or DeliveryState.Skipped)) continue;
                if (row is null)
                {
                    row = new ReminderDelivery { OwnerId = setting.OwnerId, AssignmentId = task.Id, DueDate = task.DueDate, Kind = kind };
                    db.ReminderDeliveries.Add(row);
                }
                row.ScheduledAt = scheduled;
                row.NextAttemptAt = scheduled;
                row.ExpiresAt = scheduled + 900000;
                row.BindingVersion = setting.BindingVersion;
                row.State = row.ExpiresAt > stamp ? DeliveryState.Pending : DeliveryState.Skipped;
                row.Problem = row.State == DeliveryState.Skipped ? "expired" : null;
            }
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
}

public sealed class ReminderPlanningService(ReminderPlanner planner, TelegramOptions options, IWebHostEnvironment environment,
    ILogger<ReminderPlanningService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsDevelopment() && options.ManualMode) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await planner.PlanAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Не удалось обновить очередь напоминаний; повтор через минуту."); }
            await Task.Delay(60000, stoppingToken);
        }
    }
}
