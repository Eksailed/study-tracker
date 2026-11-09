using System.Text.Json;

namespace StudyTracker.Tests.Support;

/// <summary>Ответ подмены на <c>sendMessage</c>: обычный успех, ошибка Bot API или обрыв сети.</summary>
public sealed class FakeReply
{
    public int Status { get; private init; } = 200;
    public string Body { get; private init; } = "";
    public bool Fails { get; private init; }

    public static FakeReply Error(int code, int? retryAfter = null)
    {
        // Как настоящий Bot API: поля parameters нет, пока Telegram не просит подождать.
        var body = new Dictionary<string, object> { ["ok"] = false, ["error_code"] = code, ["description"] = "error" };
        if (retryAfter is not null) body["parameters"] = new { retry_after = retryAfter };
        return new() { Status = code, Body = JsonSerializer.Serialize(body) };
    }

    public static FakeReply NetworkFailure => new() { Fails = true };
}

/// <summary>Локальная подмена Bot API без сети: вместо запросов к Telegram хост получает эти ответы.</summary>
public sealed class FakeTelegram : HttpMessageHandler
{
    public const string Token = "123456:fake-token-for-tests";

    private readonly object gate = new();
    private readonly List<JsonElement> updates = [];

    /// <summary>Тела всех вызовов <c>sendMessage</c>, включая те, что подмена завершила ошибкой.</summary>
    public List<JsonElement> Messages { get; } = [];
    public List<string> Calls { get; } = [];
    /// <summary>Ответы на ближайшие <c>sendMessage</c> по порядку; пока очередь пуста, все отправки успешны.</summary>
    public Queue<FakeReply> Responses { get; } = new();
    public string Webhook { get; set; } = "";

    public void AddUpdate(string code, long chat = 10000000001, string kind = "private", long? updateId = null)
    {
        lock (gate)
        {
            var number = updateId ?? updates.Count + 1;
            updates.Add(JsonSerializer.SerializeToElement(new
            {
                update_id = number,
                message = new { from = new { id = chat, is_bot = false }, chat = new { id = chat, type = kind }, text = "/start " + code },
            }));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.RequestUri!.AbsolutePath.Split('/')[^1];
        var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var data = JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
        lock (gate)
        {
            Calls.Add(method);
            switch (method)
            {
                case "getMe":
                    return Ok(new { id = 123456, is_bot = true, username = "zachet_test_bot" });
                case "getWebhookInfo":
                    return Ok(new { url = Webhook });
                case "getUpdates":
                    var offset = data.TryGetProperty("offset", out var value) ? value.GetInt64() : 0;
                    return Ok(updates.Where(u => u.GetProperty("update_id").GetInt64() >= offset).ToList());
                case "sendMessage":
                    Messages.Add(data);
                    if (Responses.TryDequeue(out var reply))
                    {
                        if (reply.Fails) throw new HttpRequestException("Сеть недоступна (подмена).");
                        return Reply(reply.Status, reply.Body);
                    }
                    return Ok(new
                    {
                        message_id = Messages.Count,
                        chat = new { id = data.GetProperty("chat_id").GetInt64(), type = "private" },
                        text = data.GetProperty("text").GetString(),
                    });
                default:
                    return Reply(404, """{"ok":false,"error_code":404}""");
            }
        }
    }

    private static HttpResponseMessage Ok(object result) =>
        Reply(200, JsonSerializer.Serialize(new { ok = true, result }));

    private static HttpResponseMessage Reply(int status, string body) => new((System.Net.HttpStatusCode)status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };
}
