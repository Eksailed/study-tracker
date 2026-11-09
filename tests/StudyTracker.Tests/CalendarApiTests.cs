using System.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StudyTracker.Calendar;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class CalendarApiTests
{
    private static async Task<ApiClient> SignedIn(TrackerFactory factory, string email = "owner@example.test")
    {
        var client = factory.NewClient();
        await client.SignIn(email);
        return client;
    }

    private static async Task<string> Issue(ApiClient client)
    {
        var created = await client.Post("calendar", new { });
        Assert.Equal(201, created.Status);
        return created.Body.GetProperty("key").GetString()!;
    }

    private static async Task AddTask(ApiClient client, int subject, string title, string due, string status = "Todo")
    {
        var response = await client.Post("assignments", new { title, description = "", subjectId = subject, dueDate = due, status });
        Assert.Equal(201, response.Status);
    }

    private sealed record FeedResponse(int Status, string Text, HttpResponseHeaders Headers, string? Type);

    private static async Task<FeedResponse> Feed(TrackerFactory factory, string query)
    {
        using var http = factory.CreateClient();
        using var response = await http.GetAsync("/calendar.ics" + query);
        return new FeedResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(),
            response.Headers, response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task FeedFollowsLifecycle()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var subject = await client.CreateSubject("Физика");
        await AddTask(client, subject, "Лаба", "2026-11-20");
        await AddTask(client, subject, "Готовое", "2026-11-21", "Done");

        Assert.False((await client.Get("calendar")).Body.GetProperty("exists").GetBoolean());
        var first = await Issue(client);
        var status = await client.Get("calendar");
        var feed = await Feed(factory, "?key=" + first);

        Assert.True(status.Body.GetProperty("exists").GetBoolean());
        Assert.Equal(200, feed.Status);
        Assert.Contains("SUMMARY:Физика: Лаба", feed.Text);
        Assert.DoesNotContain("Готовое", feed.Text);

        var second = await Issue(client);
        Assert.NotEqual(first, second);
        Assert.Equal(404, (await Feed(factory, "?key=" + first)).Status);
        Assert.Equal(200, (await Feed(factory, "?key=" + second)).Status);
        var third = await Issue(client);
        Assert.Equal(404, (await Feed(factory, "?key=" + second)).Status);
        Assert.Equal(200, (await Feed(factory, "?key=" + third)).Status);

        Assert.Equal(204, (await client.Delete("calendar")).Status);
        Assert.Equal(404, (await Feed(factory, "?key=" + third)).Status);
        Assert.False((await client.Get("calendar")).Body.GetProperty("exists").GetBoolean());
        Assert.Equal(204, (await client.Delete("calendar")).Status);
    }

    [Fact]
    public async Task WrongKeyIsAlways404()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var revoked = await Issue(client);
        await Issue(client);

        foreach (var query in new[] { "", "?key=", "?key=" + new string('x', 43), "?key=" + revoked, "?other=" + revoked })
        {
            var feed = await Feed(factory, query);
            Assert.Equal(404, feed.Status);
            Assert.Equal("", feed.Text);
        }
    }

    [Fact]
    public async Task KeyMustMatchExactly()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var key = await Issue(client);

        Assert.Equal(404, (await Feed(factory, "?key=" + key.ToUpperInvariant())).Status);
        Assert.Equal(404, (await Feed(factory, "?key=" + key + "%20")).Status);
        Assert.Equal(404, (await Feed(factory, $"?key={key}&key=чужой")).Status);
        Assert.Equal(200, (await Feed(factory, $"?key={key}&utm=1")).Status);
    }

    [Fact]
    public async Task OnlyOwnTasks()
    {
        await using var factory = new TrackerFactory();
        var first = await SignedIn(factory, "first@example.test");
        var second = await SignedIn(factory, "second@example.test");
        await AddTask(first, await first.CreateSubject("Первый"), "Задание первого", "2026-11-20");
        await AddTask(second, await second.CreateSubject("Второй"), "Задание второго", "2026-11-20");

        var firstFeed = await Feed(factory, "?key=" + await Issue(first));
        var secondFeed = await Feed(factory, "?key=" + await Issue(second));

        Assert.Contains("Задание первого", firstFeed.Text);
        Assert.DoesNotContain("Задание второго", firstFeed.Text);
        Assert.Contains("Задание второго", secondFeed.Text);
        Assert.DoesNotContain("Задание первого", secondFeed.Text);
    }

    [Fact]
    public async Task KeyStoredOnlyAsHash()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var key = await Issue(client);

        var stored = factory.Scope(services => services.GetRequiredService<TrackerDb>().CalendarLinks.AsNoTracking().Single());
        var status = await client.Get("calendar");

        Assert.Equal(CalendarKeys.Hash(key), stored.KeyHash);
        Assert.NotEqual(key, stored.KeyHash);
        Assert.DoesNotContain(key, status.Body.GetRawText());
        Assert.False(status.Body.TryGetProperty("key", out _));
    }

    [Fact]
    public async Task ManagementNeedsCookieLogin()
    {
        await using var factory = new TrackerFactory();
        var anonymous = factory.NewClient();
        Assert.Equal(401, (await anonymous.Get("calendar")).Status);
        Assert.Equal(401, (await anonymous.Post("calendar", new { }, csrf: false)).Status);

        var owner = await SignedIn(factory);
        var token = await owner.Post("tokens", new { name = "Скрипт", scopes = new[] { "tasks:read", "tasks:write" } });
        var bearer = factory.NewClient();
        bearer.UseBearer(token.Body.GetProperty("token").GetString()!);
        Assert.Equal(401, (await bearer.Get("calendar")).Status);
        Assert.Equal(401, (await bearer.Post("calendar", new { }, csrf: false)).Status);

        Assert.Equal(400, (await owner.Post("calendar", new { }, csrf: false)).Status);
        Assert.False((await owner.Get("calendar")).Body.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task FeedHeaders()
    {
        await using var factory = new TrackerFactory();
        var feed = await Feed(factory, "?key=" + await Issue(await SignedIn(factory)));

        Assert.Equal("text/calendar; charset=utf-8", feed.Type);
        Assert.True(feed.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task FeedRateLimit()
    {
        await using var factory = new TrackerFactory();

        for (var i = 0; i < 60; i++) Assert.Equal(404, (await Feed(factory, "?key=x")).Status);
        var limited = await Feed(factory, "?key=x");

        Assert.Equal(429, limited.Status);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task LogDoesNotContainKey()
    {
        var logs = new LogCollector();
        await using var factory = new TrackerFactory(services: services => services.AddSingleton<ILoggerProvider>(logs));
        var key = await Issue(await SignedIn(factory));

        await Feed(factory, "?key=" + key);

        Assert.Contains(logs.Records, r => r.Contains("/calendar.ics"));
        Assert.DoesNotContain(logs.Records, r => r.Contains(key));
    }

    [Fact]
    public async Task DeletingAccountRemovesLink()
    {
        await using var factory = new TrackerFactory();
        await Issue(await SignedIn(factory, "gone@example.test"));
        await Issue(await SignedIn(factory, "keeper@example.test"));

        factory.Scope(services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = users.FindByEmailAsync("gone@example.test").GetAwaiter().GetResult()!;
            Assert.True(users.DeleteAsync(user).GetAwaiter().GetResult().Succeeded);
            return 0;
        });

        Assert.Equal(1, factory.Scope(services => services.GetRequiredService<TrackerDb>().CalendarLinks.Count()));
    }
}
