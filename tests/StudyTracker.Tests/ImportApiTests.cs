using Microsoft.EntityFrameworkCore;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class ImportApiTests
{
    private const string Preview = "import/obsidian/preview";
    private const string Apply = "import/obsidian";

    private static readonly string Physics = ObsidianSamples.Note("Физика", "Лаба физика", "2026-11-20", "false", "s-1", "Описание физики");
    private static readonly string Ict = ObsidianSamples.Note("ИКТ", "Лаба икт", "2026-11-21", "true", "s-2", "Описание ИКТ");
    private static readonly string Math = ObsidianSamples.Note("Математика", "ДЗ математика", "2026-12-01", "false", "s-3");
    private static readonly string NoDue = ObsidianSamples.Note("ИКТ", "Без срока", null, "false", "s-4");

    private static object Body(string? defaultDue = null, params string[] texts) => new
    {
        files = texts.Select((text, i) => new { path = $"ДЗ/Предмет/Заметка {i}.md", text }).ToArray(),
        defaultDue,
    };

    private static async Task<ApiClient> SignedIn(TrackerFactory factory, string email = "owner@example.test")
    {
        var client = factory.NewClient();
        await client.SignIn(email);
        return client;
    }

    private static int Count(TrackerFactory factory, Func<TrackerDb, int> count) =>
        factory.Scope(services => count(services.GetRequiredService<TrackerDb>()));

    [Fact]
    public async Task PreviewReturnsReport()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);

        var response = await client.Post(Preview, Body(null, Physics, Ict, Math, NoDue, "# Просто заметка\n"));

        Assert.Equal(200, response.Status);
        Assert.Equal(3, response.Body.GetProperty("newCount").GetInt32());
        Assert.Equal(0, response.Body.GetProperty("existingCount").GetInt32());
        Assert.Equal(1, response.Body.GetProperty("notHomework").GetInt32());
        Assert.Equal(0, response.Body.GetProperty("truncated").GetInt32());
        Assert.Equal(["ИКТ", "Математика", "Физика"],
            response.Body.GetProperty("newSubjects").EnumerateArray().Select(e => e.GetString()));
        var skipped = Assert.Single(response.Body.GetProperty("skipped").EnumerateArray());
        Assert.Equal("нет срока", skipped.GetProperty("reason").GetString());
        Assert.Equal("ДЗ/Предмет/Заметка 3.md", skipped.GetProperty("path").GetString());
        var items = response.Body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("Todo", items[0].GetProperty("status").GetString());
        Assert.Equal("Done", items[1].GetProperty("status").GetString());
        Assert.Equal("2026-11-21", items[1].GetProperty("due").GetString());
        Assert.Equal("ИКТ", items[1].GetProperty("subject").GetString());
        Assert.Equal(0, Count(factory, db => db.Subjects.Count()));
        Assert.Equal(0, Count(factory, db => db.Assignments.Count()));
    }

    [Fact]
    public async Task ApplyThenRepeat()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);

        var first = await client.Post(Apply, Body(null, Physics, Ict, Math));
        var listed = await client.Get("assignments");
        var second = await client.Post(Apply, Body(null, Physics, Ict, Math));

        Assert.Equal(200, first.Status);
        Assert.Equal(3, first.Body.GetProperty("newCount").GetInt32());
        Assert.Equal(3, listed.Body.GetArrayLength());
        Assert.Equal(200, second.Status);
        Assert.Equal(0, second.Body.GetProperty("newCount").GetInt32());
        Assert.Equal(3, second.Body.GetProperty("existingCount").GetInt32());
        Assert.Equal(3, Count(factory, db => db.Assignments.Count()));
    }

    [Fact]
    public async Task RequiresLogin()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();

        Assert.Equal(401, (await client.Post(Preview, Body(null, Ict), csrf: false)).Status);
        Assert.Equal(401, (await client.Post(Apply, Body(null, Ict), csrf: false)).Status);
    }

    [Fact]
    public async Task TokenIsRejected()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var created = await owner.Post("tokens", new { name = "Скрипт", scopes = new[] { "tasks:read", "tasks:write" } });
        Assert.Equal(201, created.Status);
        var bearer = factory.NewClient();
        bearer.UseBearer(created.Body.GetProperty("token").GetString()!);

        Assert.Equal(401, (await bearer.Post(Preview, Body(null, Ict), csrf: false)).Status);
        Assert.Equal(401, (await bearer.Post(Apply, Body(null, Ict), csrf: false)).Status);
        Assert.Equal(0, Count(factory, db => db.Assignments.Count()));
    }

    [Fact]
    public async Task RequiresCsrf()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);

        var response = await client.Post(Apply, Body(null, Ict), csrf: false);

        Assert.Equal(400, response.Status);
        Assert.StartsWith("Обновите страницу", response.Body.GetProperty("error").GetString());
        Assert.Equal(0, Count(factory, db => db.Assignments.Count()));
    }

    [Fact]
    public async Task BadRequests()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);

        var empty = await client.Post(Preview, new { files = Array.Empty<object>(), defaultDue = (string?)null });
        var noFiles = await client.Post(Preview, new { });
        var early = await client.Post(Preview, Body("1999-12-31", Ict));
        var notDate = await client.Post(Preview, Body("не дата", Ict));
        var notJson = await client.Post(Apply, "это не объект");

        Assert.Equal(400, empty.Status);
        Assert.Equal("Выберите файлы заметок.", empty.Body.GetProperty("error").GetString());
        Assert.Equal(400, noFiles.Status);
        Assert.Equal(400, early.Status);
        Assert.Equal("Выберите срок между 2000 и 2100 годом.", early.Body.GetProperty("error").GetString());
        Assert.Equal(400, notDate.Status);
        Assert.Equal(400, notJson.Status);
        Assert.Equal(0, Count(factory, db => db.Assignments.Count()));
    }

    [Fact]
    public async Task RateLimit()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var other = await SignedIn(factory, "other@example.test");

        for (var i = 0; i < 10; i++)
            Assert.Equal(200, (await client.Post(Preview, Body(null, Ict))).Status);
        var limited = await client.Post(Preview, Body(null, Ict));
        var otherAccount = await other.Post(Preview, Body(null, Ict));

        Assert.Equal(429, limited.Status);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(200, otherAccount.Status);
    }

    [Fact]
    public async Task ApplyConflictGives409()
    {
        var conflict = new ConflictingInsert(armed: false);
        await using var factory = new TrackerFactory(services: services =>
            services.ConfigureDbContext<TrackerDb>(options => options.AddInterceptors(conflict)));
        var client = await SignedIn(factory);
        await client.CreateSubject("ИКТ");
        conflict.Armed = true;

        var response = await client.Post(Apply, Body(null, Ict, Physics));

        Assert.Equal(409, response.Status);
        Assert.Contains("Импорт уже выполняется", response.Body.GetProperty("error").GetString());
        Assert.Equal(1, Count(factory, db => db.Subjects.Count()));
        Assert.Equal(1, Count(factory, db => db.Assignments.Count()));
    }

    [Fact]
    public async Task OtherAccountSeesNothing()
    {
        await using var factory = new TrackerFactory();
        var first = await SignedIn(factory, "first@example.test");
        var second = await SignedIn(factory, "second@example.test");

        await first.Post(Apply, Body(null, Physics, Ict, Math));

        Assert.Equal(3, (await first.Get("assignments")).Body.GetArrayLength());
        Assert.Equal(0, (await second.Get("assignments")).Body.GetArrayLength());
        Assert.Equal(0, (await second.Get("subjects")).Body.GetArrayLength());
    }
}
