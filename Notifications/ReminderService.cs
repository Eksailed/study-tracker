namespace StudyTracker.Notifications;

public sealed class ReminderService(ReminderPlanner planner, ReminderSender sender, TimeProvider clock,
    TelegramOptions options, IWebHostEnvironment environment, ILogger<ReminderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsDevelopment() && options.ManualMode) return;
        await sender.RecoverAsync(stoppingToken);
        long lastPlan = long.MinValue / 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
                if (now - lastPlan >= 60000) { await planner.PlanAsync(stoppingToken); lastPlan = now; }
                await sender.SendPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Не удалось обработать очередь уведомлений. Повтор через секунду."); }
            await Task.Delay(1000, stoppingToken);
        }
    }
}
