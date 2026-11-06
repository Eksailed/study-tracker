using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker.Notifications;

public sealed class TelegramUpdateWorker(IServiceScopeFactory scopes, TelegramClient client, TimeProvider clock)
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task PollAsync(CancellationToken token, int timeout = 30)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDb>();
        var state = await db.TelegramUpdateStates.FindAsync([1], token);
        if (state is null) { state = new TelegramUpdateState { BotId = client.BotId }; db.Add(state); }
        if (state.BotId != client.BotId) { state.BotId = client.BotId; state.NextOffset = 0; }
        await db.SaveChangesAsync(token);
        var result = await client.CallAsync("getUpdates", new { offset = state.NextOffset, timeout, allowed_updates = new[] { "message" } }, token, polling: true);
        if (!result.Success || result.Data.ValueKind != JsonValueKind.Array) { await Task.Delay(1000, token); return; }
        foreach (var update in result.Data.EnumerateArray())
        {
            if (!update.TryGetProperty("update_id", out var value) || !value.TryGetInt64(out var id)) continue;
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Entry(state).ReloadAsync(token);
            if (id < state.NextOffset) { await transaction.CommitAsync(token); continue; }
            var reply = await BindAsync(db, update, token);
            state.NextOffset = id + 1;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            if (reply is not null)
                await client.CallAsync("sendMessage", new { chat_id = reply.Value.Chat, text = reply.Value.Text }, token);
        }
    }

    private async Task<(long Chat, string Text)?> BindAsync(TrackerDb db, JsonElement update, CancellationToken token)
    {
        if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("chat", out var chat)
            || !chat.TryGetProperty("type", out var type) || type.GetString() != "private"
            || !chat.TryGetProperty("id", out var chatValue) || !chatValue.TryGetInt64(out var chatId)
            || !message.TryGetProperty("from", out var sender) || !sender.TryGetProperty("id", out var senderId)
            || !senderId.TryGetInt64(out var senderNumber) || senderNumber != chatId
            || !message.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return null;
        var command = text.GetString()!;
        if (!command.StartsWith("/start ", StringComparison.Ordinal)) return null;
        var code = command[7..].Trim();
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var hash = Hash(code);
        var link = await db.TelegramLinkCodes.SingleOrDefaultAsync(c => c.Hash == hash && c.ExpiresAt > now, token);
        if (link is null) return (chatId, "Код недействителен или истёк. Получите новую ссылку в настройках «Зачёта».");
        var occupied = await db.TelegramConnections.SingleOrDefaultAsync(c => c.ChatId == chatId || c.OwnerId == link.OwnerId, token);
        if (occupied is not null && (occupied.OwnerId != link.OwnerId || occupied.ChatId != chatId))
            return (chatId, "Чат или аккаунт уже подключён. Сначала удалите прежнюю привязку в «Зачёте».");
        var settings = await TelegramEndpoints.SettingsAsync(db, link.OwnerId);
        settings.BindingVersion++;
        settings.Enabled = false;
        if (occupied is null) db.TelegramConnections.Add(new TelegramConnection { OwnerId = link.OwnerId, ChatId = chatId, BotId = client.BotId });
        else { occupied.BotId = client.BotId; occupied.Problem = null; }
        db.TelegramLinkCodes.Remove(link);
        await db.ReminderDeliveries.Where(d => d.OwnerId == settings.OwnerId && d.State == DeliveryState.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, DeliveryState.Cancelled), token);
        return (chatId, "Чат подключён. Включите напоминания в разделе «Уведомления» в «Зачёте».");
    }
}

public sealed class TelegramPollingService(TelegramClient client, TelegramUpdateWorker updates, TelegramOptions options,
    IWebHostEnvironment environment, ILogger<TelegramPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.Ready && !await client.InitializeAsync(stoppingToken)) { await Task.Delay(5000, stoppingToken); continue; }
                if (environment.IsDevelopment() && options.ManualMode) return;
                await updates.PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Не удалось обработать команды Telegram; повтор через 5 секунд."); await Task.Delay(5000, stoppingToken); }
        }
    }
}
