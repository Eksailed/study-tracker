using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudyTracker.Calendar;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class GuestTests
{
    private const string Head = "head@example.test";
    private static readonly string[] PublicFields = ["subject", "color", "title", "description", "dueDate", "priority"];

    private sealed record Reply(int Status, string Text, HttpResponseHeaders Headers, string? Type);

    private static async Task<ApiClient> SignedIn(TrackerFactory factory, string email)
    {
        var client = factory.NewClient();
        await client.SignIn(email);
        return client;
    }

    private static async Task<Reply> Get(TrackerFactory factory, string path)
    {
        using var http = factory.CreateClient();
        using var response = await http.GetAsync(path);
        return new Reply((int)response.StatusCode, await response.Content.ReadAsStringAsync(),
            response.Headers, response.Content.Headers.ContentType?.ToString());
    }

    private static Task<Reply> Page(TrackerFactory factory, string key) => Get(factory, "/public/group?key=" + Uri.EscapeDataString(key));

    private static Task<Reply> Feed(TrackerFactory factory, string key) => Get(factory, "/group.ics?key=" + Uri.EscapeDataString(key));

    private static async Task<string> Issue(ApiClient client, int group)
    {
        var created = await client.Post($"groups/{group}/guest-link");
        Assert.Equal(201, created.Status);
        return created.Body.GetProperty("key").GetString()!;
    }

    /// <summary>Староста группы «ИВТ-4» и её номер.</summary>
    private static async Task<(TrackerFactory Factory, ApiClient Head, int Group)> Setup(string name = "ИВТ-4")
    {
        var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        var group = GroupSupport.CreateGroup(factory, name);
        GroupSupport.Join(factory, group, Head, GroupRole.Head);
        return (factory, head, group);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string[] Titles(Reply reply) => JsonDocument.Parse(reply.Text).RootElement.GetProperty("assignments")
        .EnumerateArray().Select(a => a.GetProperty("title").GetString()!).ToArray();

    [Fact]
    public async Task HeadManagesGuestLink()
    {
        var (factory, head, group) = await Setup();
        await using var lifetime = factory;

        var none = await head.Get($"groups/{group}/guest-link");
        Assert.Equal(200, none.Status);
        Assert.False(none.Body.GetProperty("exists").GetBoolean());

        var first = await Issue(head, group);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first);
        var state = await head.Get($"groups/{group}/guest-link");
        Assert.True(state.Body.GetProperty("exists").GetBoolean());
        Assert.False(state.Body.TryGetProperty("key", out _));
        Assert.Equal(200, (await Page(factory, first)).Status);
        Assert.Equal(200, (await Feed(factory, first)).Status);

        var second = await Issue(head, group);
        Assert.NotEqual(first, second);
        Assert.Equal(404, (await Page(factory, first)).Status);
        Assert.Equal(404, (await Feed(factory, first)).Status);
        Assert.Equal(200, (await Page(factory, second)).Status);

        Assert.Equal(204, (await head.Delete($"groups/{group}/guest-link")).Status);
        Assert.Equal(204, (await head.Delete($"groups/{group}/guest-link")).Status);
        Assert.Equal(404, (await Page(factory, second)).Status);
        Assert.Equal(404, (await Feed(factory, second)).Status);
        Assert.False((await head.Get($"groups/{group}/guest-link")).Body.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task OnlyHeadOrOwnerManagesLink()
    {
        var (factory, head, group) = await Setup();
        await using var lifetime = factory;
        var student = await SignedIn(factory, "student@example.test");
        var rival = await SignedIn(factory, "rival@example.test");
        var owner = await SignedIn(factory, "owner@example.test");
        GroupSupport.MakeOwner(factory, "owner@example.test");
        GroupSupport.Join(factory, group, "student@example.test");
        GroupSupport.Join(factory, GroupSupport.CreateGroup(factory, "ИВТ-3"), "rival@example.test", GroupRole.Head);
        var key = await Issue(head, group);

        foreach (var client in new[] { student, rival })
        {
            Assert.Equal(404, (await client.Get($"groups/{group}/guest-link")).Status);
            Assert.Equal(404, (await client.Post($"groups/{group}/guest-link")).Status);
            Assert.Equal(404, (await client.Delete($"groups/{group}/guest-link")).Status);
        }
        Assert.Equal(200, (await Page(factory, key)).Status);

        Assert.Equal(200, (await owner.Get($"groups/{group}/guest-link")).Status);
        var reissued = await Issue(owner, group);
        Assert.Equal(404, (await Page(factory, key)).Status);
        Assert.Equal(204, (await owner.Delete($"groups/{group}/guest-link")).Status);
        Assert.Equal(404, (await Page(factory, reissued)).Status);
        Assert.Equal(404, (await owner.Get("groups/9999/guest-link")).Status);
    }

    [Fact]
    public async Task KeyIsStoredHashed()
    {
        var (factory, head, group) = await Setup();
        await using var lifetime = factory;

        var key = await Issue(head, group);

        var stored = factory.Scope(s => s.GetRequiredService<TrackerDb>().Groups.Single(g => g.Id == group));
        Assert.Matches("^[0-9a-f]{64}$", stored.GuestKeyHash);
        Assert.Equal(CalendarKeys.Hash(key), stored.GuestKeyHash);
        Assert.DoesNotContain(key, stored.GuestKeyHash);
        Assert.NotNull(stored.GuestKeyCreatedAt);
    }

    [Fact]
    public async Task GuestPageDataHasOnlyPublicFields()
    {
        var (factory, head, group) = await Setup();
        await using var lifetime = factory;
        var student = await SignedIn(factory, "student@example.test");
        GroupSupport.Join(factory, group, "student@example.test");
        var (_, task) = GroupSupport.AddGroupTask(factory, group, "Физика", "Лабораторная");
        GroupSupport.AddProgress(factory, task, "student@example.test", AssignmentStatus.Done);
        GroupSupport.AddStep(factory, task, "student@example.test", "Личный шаг");
        var key = await Issue(head, group);

        var reply = await Page(factory, key);

        Assert.Equal(200, reply.Status);
        Assert.True(reply.Headers.CacheControl!.NoStore);
        var root = JsonDocument.Parse(reply.Text).RootElement;
        Assert.Equal(["assignments", "name"], root.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("ИВТ-4", root.GetProperty("name").GetString());
        var item = root.GetProperty("assignments").EnumerateArray().Single();
        Assert.Equal(PublicFields.Order(), item.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Физика", item.GetProperty("subject").GetString());
        Assert.Equal("blue", item.GetProperty("color").GetString());
        Assert.Equal("Лабораторная", item.GetProperty("title").GetString());
        Assert.Equal("2026-12-12", item.GetProperty("dueDate").GetString());
        Assert.Equal("Normal", item.GetProperty("priority").GetString());
        Assert.DoesNotContain("example.test", reply.Text);
        Assert.DoesNotContain("Личный шаг", reply.Text);
        _ = student;
    }

    [Fact]
    public async Task WindowAndOrder()
    {
        var (factory, head, group) = await Setup();
        await using var lifetime = factory;
        GroupSupport.AddGroupTasks(factory, group,
        [
            ("Через год", Today.AddDays(365)), ("Минус восемь", Today.AddDays(-8)), ("Завтра", Today.AddDays(1)),
            ("Минус семь", Today.AddDays(-7)), ("Вчера", Today.AddDays(-1)),
        ]);
        var key = await Issue(head, group);

        var page = await Page(factory, key);
        var feed = await Feed(factory, key);

        Assert.Equal(new[] { "Минус семь", "Вчера", "Завтра", "Через год" }, Titles(page));
        var summaries = Regex.Matches(feed.Text.Replace("\r\n ", ""), @"SUMMARY:Физика: ([^\r]+)").Select(m => m.Groups[1].Value);
        Assert.Equal(new[] { "Минус семь", "Вчера", "Завтра", "Через год" }, summaries);
    }

    [Fact]
    public async Task ListIsCappedAt500()
    {
        var (factory, head, group) = await Setup();
        await using var lifetime = factory;
        GroupSupport.AddGroupTasks(factory, group, Enumerable.Range(0, 501).Select(i => ($"Задание {i:000}", Today.AddDays(1 + i / 10))));
        var key = await Issue(head, group);

        var titles = Titles(await Page(factory, key));
        var events = Regex.Matches((await Feed(factory, key)).Text, "BEGIN:VEVENT").Count;

        Assert.Equal(500, titles.Length);
        Assert.Equal("Задание 000", titles[0]);
        Assert.DoesNotContain("Задание 500", titles);
        Assert.Equal(500, events);
    }

    [Fact]
    public async Task WrongKeysAreIdentical404()
    {
        var (factory, head, group) = await Setup();
        await using var lifetime = factory;
        var key = await Issue(head, group);
        var personal = (await head.Post("calendar")).Body.GetProperty("key").GetString()!;
        var wrong = new[] { "", "x", new string('A', 43), key + "x", key.ToLowerInvariant() == key ? key.ToUpperInvariant() : key.ToLowerInvariant(), personal };

        foreach (var attempt in wrong)
        {
            var page = await Page(factory, attempt);
            var feed = await Feed(factory, attempt);
            Assert.Equal(404, page.Status);
            Assert.Equal(404, feed.Status);
            Assert.Equal("", page.Text);
            Assert.Equal("", feed.Text);
            Assert.True(page.Headers.CacheControl!.NoStore);
            Assert.True(feed.Headers.CacheControl!.NoStore);
        }
        Assert.Equal(404, (await Get(factory, "/public/group")).Status);
        Assert.Equal(404, (await Get(factory, "/group.ics")).Status);
        Assert.Equal(404, (await Get(factory, "/calendar.ics?key=" + Uri.EscapeDataString(key))).Status);
    }

    [Fact]
    public async Task GuestFeedIsIcs()
    {
        var (factory, head, group) = await Setup("ИВТ-4, 2 курс");
        await using var lifetime = factory;
        var (_, task) = GroupSupport.AddGroupTask(factory, group, "Физика", "а,б;в");
        factory.Scope(services =>
        {
            services.GetRequiredService<TrackerDb>().Assignments.Single(a => a.Id == task).Description = "Секретное описание";
            services.GetRequiredService<TrackerDb>().SaveChanges();
            return 0;
        });
        GroupSupport.AddProgress(factory, task, Head, AssignmentStatus.Done);
        var key = await Issue(head, group);

        var feed = await Feed(factory, key);

        Assert.Equal(200, feed.Status);
        Assert.Equal("text/calendar; charset=utf-8", feed.Type);
        Assert.True(feed.Headers.CacheControl!.NoStore);
        Assert.DoesNotMatch(@"(?<!\r)\n", feed.Text);
        var text = feed.Text.Replace("\r\n ", "");
        Assert.Contains($"UID:group-task-{task}@zachet\r\n", text);
        Assert.Contains(@"SUMMARY:Физика: а\,б\;в" + "\r\n", text);
        Assert.Contains(@"X-WR-CALNAME:ИВТ-4\, 2 курс" + "\r\n", text);
        Assert.Contains("DTSTART;VALUE=DATE:20261212\r\n", text);
        Assert.DoesNotContain("Секретное описание", text);
        Assert.DoesNotContain("DESCRIPTION", text);
        Assert.DoesNotContain("UID:task-", text);
    }

    [Fact]
    public async Task GuestEndpointsAreRateLimited()
    {
        await using var factory = new TrackerFactory();

        for (var i = 0; i < 60; i++) Assert.Equal(404, (await Page(factory, "x")).Status);
        var limited = await Page(factory, "x");

        Assert.Equal(429, limited.Status);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task GuestPageIsStaticAndScriptsExternal()
    {
        await using var factory = new TrackerFactory();

        var page = await Get(factory, "/g.html");
        var script = await Get(factory, "/g.js");

        Assert.Equal(200, page.Status);
        Assert.Contains("src=\"/g.js\"", page.Text);
        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)", RegexOptions.IgnoreCase), page.Text);
        Assert.DoesNotMatch(new Regex(@"\son\w+\s*=", RegexOptions.IgnoreCase), page.Text);
        Assert.Contains("no-referrer", page.Text);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(200, script.Status);
        Assert.DoesNotContain("innerHTML", script.Text);
        Assert.Contains("textContent", script.Text);
        Assert.Contains("location.hash", script.Text);
    }
}
