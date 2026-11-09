using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace StudyTracker.Tests.Support;

public enum SocketMode
{
    /// <summary>Принять запрос и не отвечать, пока клиент сам не оборвёт соединение.</summary>
    Hang,
    /// <summary>Отправить заголовки с <c>Content-Length: 100</c> и начало тела, затем закрыть соединение.</summary>
    Truncated,
}

/// <summary>
/// Маленький настоящий HTTP-сервер вместо Bot API. Нужен там, где важно поведение сокета (таймаут, обрыв ответа),
/// которое заглушка <see cref="FakeTelegram"/> на уровне <c>HttpMessageHandler</c> воспроизвести не может.
/// </summary>
public sealed class SocketTelegram : IAsyncDisposable
{
    private readonly WebApplication app;
    private int sendCount;

    public SocketMode Mode { get; }
    public string BaseUrl { get; }
    public int SendCount => Volatile.Read(ref sendCount);

    private SocketTelegram(WebApplication app, SocketMode mode, string baseUrl)
    {
        this.app = app;
        Mode = mode;
        BaseUrl = baseUrl;
    }

    public static async Task<SocketTelegram> StartAsync(SocketMode mode)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        SocketTelegram? self = null;
        app.MapPost("/{**path}", context => self!.Handle(context));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return self = new SocketTelegram(app, mode, address);
    }

    private async Task Handle(HttpContext context)
    {
        switch (context.Request.Path.Value!.Split('/')[^1])
        {
            case "getMe":
                await Json(context, """{"ok":true,"result":{"id":123456,"is_bot":true,"username":"zachet_test_bot"}}""");
                break;
            case "getWebhookInfo":
                await Json(context, """{"ok":true,"result":{"url":""}}""");
                break;
            case "sendMessage":
                Interlocked.Increment(ref sendCount);
                if (Mode == SocketMode.Hang)
                {
                    try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
                    catch (OperationCanceledException) { }
                }
                else
                {
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength = 100;
                    await context.Response.WriteAsync("""{"ok":""");
                    await context.Response.Body.FlushAsync();
                    context.Abort();
                }
                break;
            default:
                context.Response.StatusCode = 404;
                break;
        }
    }

    private static Task Json(HttpContext context, string body)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(body);
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
