using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class RequestLoggingTests
{
    private const string Category = "StudyTracker.Requests";

    private static TrackerFactory Host(LogCollector logs) =>
        new(services: services => services.AddSingleton<ILoggerProvider>(logs));

    private static string[] Lines(LogCollector logs) =>
        logs.Records.Where(r => r.StartsWith(Category + ":")).ToArray();

    [Fact]
    public async Task ResponseHasRequestId()
    {
        await using var factory = Host(new LogCollector());
        var client = factory.NewClient();

        var first = await client.Get("token");
        var second = await client.Get("token");

        var firstId = first.Headers.GetValues("X-Request-Id").Single();
        var secondId = second.Headers.GetValues("X-Request-Id").Single();
        Assert.False(string.IsNullOrWhiteSpace(firstId));
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public async Task RequestLineHasMethodPathStatusAndId()
    {
        var logs = new LogCollector();
        await using var factory = Host(logs);

        var response = await factory.NewClient().Get("subjects");

        var id = response.Headers.GetValues("X-Request-Id").Single();
        var line = Assert.Single(Lines(logs), l => l.Contains("/api/subjects"));
        Assert.Contains("GET", line);
        Assert.Contains("/api/subjects", line);
        Assert.Contains("401", line);
        Assert.Contains(id, line);
    }

    [Fact]
    public async Task QueryIsNotLogged()
    {
        var logs = new LogCollector();
        await using var factory = Host(logs);

        await factory.NewClient().Get("subjects?secret=abc123");

        Assert.NotEmpty(Lines(logs));
        Assert.DoesNotContain(logs.Records, r => r.Contains("secret") || r.Contains("abc123"));
    }

    [Fact]
    public async Task HealthIsNotLogged()
    {
        var logs = new LogCollector();
        await using var factory = Host(logs);
        var client = factory.NewClient();

        await client.Page("/health");
        await client.Page("/health/ready");

        Assert.Empty(Lines(logs));
    }

    [Fact]
    public async Task ServerErrorIsLoggedAs500()
    {
        var logs = new LogCollector();
        await using var factory = Host(logs);
        var client = factory.NewClient();
        await client.SignIn("log@example.test");
        factory.Scope(services => services.GetRequiredService<TrackerDb>().Database.ExecuteSqlRaw("DROP TABLE Assignments"));

        var response = await client.Get("assignments");

        Assert.Equal(500, response.Status);
        var id = response.Headers.GetValues("X-Request-Id").Single();
        var line = Lines(logs).Single(l => l.Contains("/api/assignments"));
        Assert.Contains("500", line);
        Assert.Contains(id, line);
    }
}
