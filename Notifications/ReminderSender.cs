using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker.Notifications;

public sealed class ReminderSender(IServiceScopeFactory scopes, TimeProvider clock, TelegramClient telegram, TelegramOptions options)
{
    private readonly SemaphoreSlim gate = new(1);

    public async Task RecoverAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDb>();
        await db.ReminderDeliveries.Where(d => d.State == DeliveryState.Sending).ExecuteUpdateAsync(
            s => s.SetProperty(d => d.State, DeliveryState.Unknown).SetProperty(d => d.Problem, "interrupted"), token);
    }

    public async Task SendPendingAsync(CancellationToken token)
    {
        if (!telegram.Ready || !await gate.WaitAsync(0, token)) return;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDb>();
            var stamp = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var ids = await db.ReminderDeliveries.AsNoTracking()
                .Where(d => d.State == DeliveryState.Pending && d.NextAttemptAt <= stamp)
                .OrderBy(d => d.NextAttemptAt).ThenBy(d => d.Id).Take(20).Select(d => d.Id).ToListAsync(token);
            foreach (var id in ids)
            {
                if (!telegram.Ready) break;
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var row = await db.ReminderDeliveries.SingleOrDefaultAsync(d => d.Id == id && d.State == DeliveryState.Pending, token);
                if (row is null) { await transaction.CommitAsync(token); continue; }
                var settings = await db.NotificationSettings.FindAsync([row.OwnerId], token);
                var connection = await db.TelegramConnections.FindAsync([row.OwnerId], token);
                var task = row.AssignmentId is int taskId ? await db.Assignments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == taskId, token) : null;
                var now = clock.GetUtcNow(); stamp = now.ToUnixTimeMilliseconds();
                if (!ReminderPlanner.Current(row, task, settings, connection, telegram.BotId, now)) row.State = DeliveryState.Cancelled;
                else if (row.ExpiresAt <= stamp) { row.State = DeliveryState.Skipped; row.Problem = "expired"; }
                else if (connection!.LastRequestAt is long last && last + 1000 > stamp) row.NextAttemptAt = last + 1000;
                else
                {
                    var subject = task is null ? null : await db.Subjects.AsNoTracking().SingleOrDefaultAsync(s => s.Id == task.SubjectId && s.OwnerId == row.OwnerId, token);
                    var text = row.Kind == ReminderKind.Test ? "Проверочное сообщение из «Зачёта». Telegram подключён." :
                        $"Напоминание из «Зачёта»\n{task!.Title}\nПредмет: {subject?.Name}\nСдать до: {task.DueDate.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"))}";
                    if (Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out var address) && address.Scheme == "https" && !address.IsLoopback
                        && string.IsNullOrEmpty(address.UserInfo) && string.IsNullOrEmpty(address.Query) && string.IsNullOrEmpty(address.Fragment)
                        && address.AbsoluteUri.Length <= 2048) text += "\n" + address.AbsoluteUri;
                    row.State = DeliveryState.Sending;
                    row.Attempts++;
                    connection!.LastRequestAt = stamp;
                    var targetChat = connection.ChatId;
                    await db.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    var result = await telegram.CallAsync("sendMessage", new { chat_id = targetChat, text }, token);
                    await db.Entry(row).ReloadAsync(CancellationToken.None);
                    if (row.State != DeliveryState.Sending) continue;
                    if (result.Success && result.Data.ValueKind == System.Text.Json.JsonValueKind.Object
                        && result.Data.TryGetProperty("message_id", out var messageId) && messageId.TryGetInt64(out var number))
                    { row.State = DeliveryState.Sent; row.MessageId = number; row.SentAt = clock.GetUtcNow().ToUnixTimeMilliseconds(); row.Problem = null; }
                    else if (result.Code == 429)
                    {
                        row.NextAttemptAt = clock.GetUtcNow().ToUnixTimeMilliseconds() + result.RetryAfter * 1000L;
                        row.State = row.NextAttemptAt >= row.ExpiresAt ? DeliveryState.Skipped : row.Attempts <= 3 ? DeliveryState.Pending : DeliveryState.Failed;
                        row.Problem = "rate_limit";
                    }
                    else if (result.Code == 403)
                    {
                        row.State = DeliveryState.Failed; row.Problem = "blocked";
                        await db.Entry(connection).ReloadAsync(CancellationToken.None);
                        await db.Entry(settings!).ReloadAsync(CancellationToken.None);
                        if (db.Entry(connection).State != EntityState.Detached && connection.ChatId == targetChat
                            && settings!.BindingVersion == row.BindingVersion)
                        {
                            connection.Problem = "Бот не может отправлять сообщения. Переподключите Telegram.";
                            settings.Enabled = false;
                            await db.ReminderDeliveries.Where(d => d.OwnerId == row.OwnerId && d.State == DeliveryState.Pending
                                && d.BindingVersion == row.BindingVersion)
                                .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, DeliveryState.Cancelled), CancellationToken.None);
                        }
                    }
                    else if (result.Code is 400 or 401 or 404)
                    { row.State = DeliveryState.Failed; row.Problem = "rejected"; }
                    else { row.State = DeliveryState.Unknown; row.Problem = "unknown"; }
                    await db.SaveChangesAsync(CancellationToken.None);
                    continue;
                }
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
        }
        finally { gate.Release(); }
    }
}
