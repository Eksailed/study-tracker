using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StudyTracker.Notifications;

public record TelegramResult(int Code, JsonElement Data, int RetryAfter = 0)
{
    public bool Success => Code == 200;
}

// Собственные долгоживущие клиенты не подключают журналирование URL HttpClientFactory:
// адрес Telegram содержит секрет бота. Исключения и ответы наружу не передаются.
public sealed class TelegramClient : IDisposable
{
    private readonly HttpClient send = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient poll = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) };
    private readonly TelegramOptions options;
    private readonly Uri apiBase;
    public long BotId { get; private set; }
    public string? Username { get; private set; }
    public bool Ready { get; private set; }
    public string Status { get; private set; } = "Бот не настроен.";

    public TelegramClient(TelegramOptions options, IWebHostEnvironment environment)
    {
        this.options = options;
        apiBase = environment.IsDevelopment() && options.ApiBase is not null
            ? new Uri(options.ApiBase.TrimEnd('/') + "/") : new Uri("https://api.telegram.org/");
        if (options.Configured) Status = "Проверяем подключение к Telegram.";
    }

    public async Task<TelegramResult> CallAsync(string method, object payload, CancellationToken token, bool polling = false)
    {
        if (!options.Configured) return new(0, default);
        try
        {
            using var body = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            using var response = await (polling ? poll : send).PostAsync(
                new Uri(apiBase, $"./bot{options.BotToken}/{method}"), body, token);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            var root = document.RootElement;
            if (response.IsSuccessStatusCode && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("result", out var result)) return new(200, result.Clone());
            var code = root.TryGetProperty("error_code", out var error) && error.TryGetInt32(out var number)
                ? number : response.IsSuccessStatusCode ? 0 : (int)response.StatusCode;
            var retry = root.TryGetProperty("parameters", out var parameters) && parameters.TryGetProperty("retry_after", out var delay)
                && delay.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 1, 86400) : 1;
            return new(code, default, retry);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        { return new(0, default); }
    }

    public async Task<bool> InitializeAsync(CancellationToken token)
    {
        Ready = false;
        if (!options.Configured) return false;
        var me = await CallAsync("getMe", new { }, token);
        var webhook = await CallAsync("getWebhookInfo", new { }, token);
        if (!me.Success || !webhook.Success || !me.Data.TryGetProperty("id", out var id) || !id.TryGetInt64(out var botId)
            || !me.Data.TryGetProperty("username", out var name) || name.ValueKind != JsonValueKind.String
            || !Regex.IsMatch(name.GetString()!, "^[A-Za-z0-9_]{5,32}$"))
        { Status = "Не удалось подключить бота. Проверьте его настройки."; return false; }
        if (webhook.Data.TryGetProperty("url", out var url) && !string.IsNullOrEmpty(url.GetString()))
        { Status = "У бота уже настроен webhook. Отключите его вручную либо используйте другого бота."; return false; }
        BotId = botId; Username = name.GetString(); Status = "Бот подключён."; Ready = true;
        return true;
    }

    public void Dispose() { send.Dispose(); poll.Dispose(); }
}
