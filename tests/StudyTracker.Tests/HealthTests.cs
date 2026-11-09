using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using StudyTracker.Tests.Support;
using System.Data.Common;

namespace StudyTracker.Tests;

public class HealthTests
{
    /// <summary>Перехватчик подключений: по флагу бросает исключение при открытии, как недоступная база.</summary>
    private sealed class FailingConnections : DbConnectionInterceptor
    {
        public volatile bool Fail;

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => Fail ? throw new InvalidOperationException("секретный путь C:\\hidden\\tracker.db") : result;

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
            => Fail ? throw new InvalidOperationException("секретный путь C:\\hidden\\tracker.db") : ValueTask.FromResult(result);
    }

    [Fact]
    public async Task LiveAlwaysOk()
    {
        await using var factory = new TrackerFactory();
        var response = await Probe(factory.NewClient(), "/health");

        Assert.Equal(200, response.Status);
        Assert.Equal("ok", response.Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReadyOnHealthyHost()
    {
        await using var factory = new TrackerFactory();
        var response = await Ready(factory);

        Assert.Equal(200, response.Status);
        Assert.Equal("ok", response.Body.GetProperty("status").GetString());
        var checks = response.Body.GetProperty("checks");
        Assert.Equal("ok", checks.GetProperty("database").GetString());
        Assert.Equal("ok", checks.GetProperty("schema").GetString());
        Assert.Equal("ok", checks.GetProperty("keys").GetString());
        Assert.Equal("disabled", checks.GetProperty("telegram").GetString());
    }

    [Fact]
    public async Task ReadyFailsWhenKeysDirectoryIsGone()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        Assert.Equal(200, (await Probe(client, "/health/ready")).Status);
        Directory.Delete(Path.Combine(factory.DataDirectory, "keys"), recursive: true);

        var response = await Probe(client, "/health/ready");

        Assert.Equal(503, response.Status);
        Assert.Equal("fail", response.Body.GetProperty("status").GetString());
        Assert.Equal("fail", response.Body.GetProperty("checks").GetProperty("keys").GetString());
        Assert.Equal("ok", response.Body.GetProperty("checks").GetProperty("database").GetString());
    }

    [Fact]
    public async Task ReadyFailsOnPendingMigration()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        Assert.Equal(200, (await Probe(client, "/health/ready")).Status);
        factory.Scope(services => services.GetRequiredService<TrackerDb>().Database.ExecuteSqlRaw(
            "DELETE FROM __EFMigrationsHistory WHERE MigrationId = (SELECT MAX(MigrationId) FROM __EFMigrationsHistory)"));

        var response = await Probe(client, "/health/ready");

        Assert.Equal(503, response.Status);
        Assert.Equal("fail", response.Body.GetProperty("checks").GetProperty("schema").GetString());
    }

    [Fact]
    public async Task ReadyFailsWhenDatabaseUnavailable()
    {
        var connections = new FailingConnections();
        await using var factory = new TrackerFactory(services: Swap(connections));
        var client = factory.NewClient();
        Assert.Equal(200, (await Probe(client, "/health/ready")).Status);
        connections.Fail = true;

        var response = await Probe(client, "/health/ready");

        Assert.Equal(503, response.Status);
        Assert.Equal("fail", response.Body.GetProperty("checks").GetProperty("database").GetString());
    }

    [Fact]
    public async Task ReadyHidesExceptionDetails()
    {
        var connections = new FailingConnections();
        await using var factory = new TrackerFactory(services: Swap(connections));
        var client = factory.NewClient();
        await Probe(client, "/health/ready");
        connections.Fail = true;
        Directory.Delete(Path.Combine(factory.DataDirectory, "keys"), recursive: true);

        var response = await client.Page("/health/ready");

        Assert.Equal(503, response.Status);
        Assert.DoesNotContain("Exception", response.Text);
        Assert.DoesNotContain("Sqlite", response.Text);
        Assert.DoesNotContain("hidden", response.Text);
        Assert.DoesNotContain(factory.DataDirectory, response.Text);
        Assert.DoesNotContain("zachet-tests", response.Text);
    }

    [Fact]
    public async Task TelegramStateDoesNotChangeCode()
    {
        await using var factory = new TrackerFactory(new Dictionary<string, string?>
        { ["Telegram:BotToken"] = FakeTelegram.Token, ["Telegram:ManualMode"] = "true" });

        var response = await Ready(factory);

        Assert.Equal(200, response.Status);
        Assert.Equal("error", response.Body.GetProperty("checks").GetProperty("telegram").GetString());
    }

    private static Task<ApiResponse> Ready(TrackerFactory factory) => Probe(factory.NewClient(), "/health/ready");

    private static async Task<ApiResponse> Probe(ApiClient client, string path)
    {
        var page = await client.Page(path);
        var body = page.Text.Length == 0 ? default : System.Text.Json.JsonDocument.Parse(page.Text).RootElement.Clone();
        return new ApiResponse(page.Status, body, page.Headers);
    }

    /// <summary>Заменяет настройки базы на те же, но с перехватчиком подключений.</summary>
    private static Action<IServiceCollection> Swap(FailingConnections connections) => services =>
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(DbContextOptions<TrackerDb>));
        services.Remove(descriptor);
        services.AddDbContext<TrackerDb>((provider, options) =>
        {
            var dataDirectory = provider.GetRequiredService<IConfiguration>()["Tracker:DataDirectory"]!;
            options.UseSqlite(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(dataDirectory, "tracker.db"), ForeignKeys = true }.ToString())
                .AddInterceptors(connections);
        });
    };
}
