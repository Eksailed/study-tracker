using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class ApiV1Tests
{
    private static readonly DateTimeOffset Moment = DateTimeOffset.Parse("2026-11-10T08:09:10Z");
    private static readonly string[] Read = ["tasks:read"];
    private static readonly string[] Write = ["tasks:write"];
    private static readonly string[] Both = ["tasks:read", "tasks:write"];

    private static async Task<ApiClient> SignedIn(TrackerFactory factory, string email = "owner@example.test")
    {
        var client = factory.NewClient();
        await client.SignIn(email);
        return client;
    }

    private static async Task<(int Id, string Token)> Issue(ApiClient owner, string[] scopes, int? days = null, string name = "Скрипт")
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["scopes"] = scopes };
        if (days is not null) body["days"] = days;
        var created = await owner.Post("tokens", body);
        Assert.True(created.Status == 201, "Токен не выпущен: " + created.Body);
        return (created.Body.GetProperty("id").GetInt32(), created.Body.GetProperty("token").GetString()!);
    }

    private static ApiClient Bearer(TrackerFactory factory, string token)
    {
        var client = factory.NewClient();
        client.UseBearer(token);
        return client;
    }

    private static object NewTask(int subjectId) =>
        new { title = "Через токен", description = "", subjectId, dueDate = "2026-11-12", status = "Todo" };

    private static ApiToken Stored(TrackerFactory factory, int id) =>
        factory.Scope(services => services.GetRequiredService<TrackerDb>().ApiTokens.AsNoTracking().Single(t => t.Id == id));

    [Fact]
    public async Task TokenReadsSubjectsAndAssignments()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        await owner.CreateTask(await owner.CreateSubject("Предмет"));
        var bearer = Bearer(factory, (await Issue(owner, Read)).Token);

        var subjects = await bearer.Get("v1/subjects");
        var assignments = await bearer.Get("v1/assignments");

        Assert.Equal(200, subjects.Status);
        Assert.Equal("Предмет", Assert.Single(subjects.Body.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(200, assignments.Status);
        Assert.Equal("Лабораторная № 2", Assert.Single(assignments.Body.EnumerateArray()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task TokenWritesWithoutCsrf()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var bearer = Bearer(factory, (await Issue(owner, Both)).Token);

        var subject = await bearer.Post("v1/subjects", new { name = "Новый", color = "teal" }, csrf: false);
        var subjectId = subject.Body.GetProperty("id").GetInt32();
        var created = await bearer.Post("v1/assignments", NewTask(subjectId), csrf: false);
        var taskId = created.Body.GetProperty("id").GetInt32();
        var updated = await bearer.Put($"v1/assignments/{taskId}", new
        { title = "Изменено", description = "", subjectId, dueDate = "2026-11-13", status = "Done" }, csrf: false);
        var deleted = await bearer.Delete($"v1/assignments/{taskId}", csrf: false);

        Assert.Equal(201, subject.Status);
        Assert.Equal($"/api/v1/subjects/{subjectId}", subject.Headers.Location!.ToString());
        Assert.Equal(201, created.Status);
        Assert.Equal($"/api/v1/assignments/{taskId}", created.Headers.Location!.ToString());
        Assert.Equal(200, updated.Status);
        Assert.Equal("Изменено", updated.Body.GetProperty("title").GetString());
        Assert.Equal(204, deleted.Status);
        Assert.Equal(204, (await bearer.Delete($"v1/subjects/{subjectId}", csrf: false)).Status);
    }

    [Fact]
    public async Task ReadScopeCannotWrite()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var bearer = Bearer(factory, (await Issue(owner, Read)).Token);

        var response = await bearer.Post("v1/subjects", new { name = "Нельзя", color = "teal" }, csrf: false);

        Assert.Equal(403, response.Status);
        Assert.Equal("Токену не хватает прав для этого действия.", response.Body.GetProperty("error").GetString());
        Assert.Equal(0, (await owner.Get("subjects")).Body.GetArrayLength());
    }

    [Fact]
    public async Task WriteScopeCannotRead()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var bearer = Bearer(factory, (await Issue(owner, Write)).Token);

        Assert.Equal(403, (await bearer.Get("v1/subjects")).Status);
        Assert.Equal(403, (await bearer.Get("v1/assignments")).Status);
        Assert.Equal(201, (await bearer.Post("v1/subjects", new { name = "Можно", color = "teal" }, csrf: false)).Status);
    }

    [Fact]
    public async Task CookieOnV1StillNeedsCsrf()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);

        var without = await owner.Post("v1/subjects", new { name = "Без токена CSRF", color = "teal" }, csrf: false);
        var with = await owner.Post("v1/subjects", new { name = "С токеном CSRF", color = "teal" });

        Assert.Equal(400, without.Status);
        Assert.Equal(201, with.Status);
        Assert.Equal($"/api/v1/subjects/{with.Body.GetProperty("id").GetInt32()}", with.Headers.Location!.ToString());
        Assert.Equal(1, (await owner.Get("v1/subjects")).Body.GetArrayLength());
    }

    [Fact]
    public async Task OldApiLocationUnchanged()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);

        var response = await owner.Post("subjects", new { name = "Старый адрес", color = "teal" });

        Assert.Equal(201, response.Status);
        Assert.Equal($"/api/subjects/{response.Body.GetProperty("id").GetInt32()}", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task TokenIsRejectedOutsideV1()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var (id, token) = await Issue(owner, Both);
        var bearer = Bearer(factory, token);

        foreach (var path in new[] { "subjects", "assignments", "auth/me", "tokens", "notifications/settings" })
            Assert.True((await bearer.Get(path)).Status == 401, "GET /api/" + path);
        Assert.Equal(401, (await bearer.Delete($"tokens/{id}", csrf: false)).Status);
        Assert.Null(Stored(factory, id).RevokedAt);
        Assert.Equal(200, (await bearer.Get("v1/subjects")).Status);
    }

    [Fact]
    public async Task GarbageTokenDoesNotFallBackToCookie()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        Assert.Equal(200, (await owner.Get("v1/subjects")).Status);
        owner.UseBearer("zt_garbage");

        var response = await owner.Get("v1/subjects");

        Assert.Equal(401, response.Status);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal("Неверный или просроченный токен.", response.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RevokedTokenIsRejected()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var (id, token) = await Issue(owner, Read);
        var bearer = Bearer(factory, token);
        Assert.Equal(200, (await bearer.Get("v1/subjects")).Status);

        Assert.Equal(204, (await owner.Delete($"tokens/{id}")).Status);

        Assert.Equal(401, (await bearer.Get("v1/subjects")).Status);
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        var time = new FakeTimeProvider(Moment);
        await using var factory = new TrackerFactory(services: TrackerFactory.WithTime(time));
        var owner = await SignedIn(factory);
        var bearer = Bearer(factory, (await Issue(owner, Read, days: 2)).Token);

        time.Advance(TimeSpan.FromDays(2) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(200, (await bearer.Get("v1/subjects")).Status);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(401, (await bearer.Get("v1/subjects")).Status);
    }

    [Fact]
    public async Task UnknownAndMalformedTokensAreRejected()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        await Issue(owner, Both);

        foreach (var header in new[] { "Bearer", "Bearer abc", "Bearer zt_", "Bearer zt_" + new string('a', 43), "Basic abc" })
        {
            var client = factory.NewClient();
            client.DefaultHeaders.TryAddWithoutValidation("Authorization", header);
            Assert.True((await client.Get("v1/subjects")).Status == 401, header);
        }
    }

    [Fact]
    public async Task OwnersAreIsolated()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory, "first@example.test");
        var other = await SignedIn(factory, "second@example.test");
        var otherId = (await other.Get("auth/me")).Body.GetProperty("id").GetString()!;
        var foreignSubject = await other.CreateSubject("Чужой предмет");
        await other.CreateTask(foreignSubject);
        await owner.CreateSubject("Свой предмет");
        var bearer = Bearer(factory, (await Issue(owner, Both)).Token);

        var subjects = await bearer.Get($"v1/subjects?ownerId={otherId}");
        var assignments = await bearer.Get($"v1/assignments?ownerId={otherId}");

        Assert.Equal("Свой предмет", Assert.Single(subjects.Body.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(0, assignments.Body.GetArrayLength());
        Assert.Equal(404, (await bearer.Post("v1/assignments", NewTask(foreignSubject), csrf: false)).Status);
        Assert.Equal(404, (await bearer.Delete($"v1/subjects/{foreignSubject}", csrf: false)).Status);
    }

    [Fact]
    public async Task RateLimitPerToken()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var first = Bearer(factory, (await Issue(owner, Read, name: "первый")).Token);
        var second = Bearer(factory, (await Issue(owner, Read, name: "второй")).Token);

        for (var i = 0; i < 120; i++) Assert.True((await first.Get("v1/subjects")).Status == 200, "запрос " + (i + 1));
        var limited = await first.Get("v1/subjects");

        Assert.Equal(429, limited.Status);
        var wait = limited.Headers.RetryAfter?.Delta ?? throw new Xunit.Sdk.XunitException("Нет Retry-After");
        Assert.InRange(wait.TotalSeconds, 1, 60);
        Assert.Equal(200, (await second.Get("v1/subjects")).Status);
        for (var i = 0; i < 125; i++) Assert.True((await owner.Get("v1/subjects")).Status == 200, "cookie, запрос " + (i + 1));
    }

    [Fact]
    public async Task LastUsedIsUpdatedAtMostEveryFiveMinutes()
    {
        var time = new FakeTimeProvider(Moment);
        await using var factory = new TrackerFactory(services: TrackerFactory.WithTime(time));
        var owner = await SignedIn(factory);
        var (id, token) = await Issue(owner, Read);
        var bearer = Bearer(factory, token);
        Assert.Null(Stored(factory, id).LastUsedAt);

        await bearer.Get("v1/subjects");
        var first = Stored(factory, id).LastUsedAt;
        time.Advance(TimeSpan.FromMinutes(4));
        await bearer.Get("v1/subjects");
        var afterFour = Stored(factory, id).LastUsedAt;
        time.Advance(TimeSpan.FromMinutes(1));
        await bearer.Get("v1/subjects");
        var afterFive = Stored(factory, id).LastUsedAt;

        Assert.Equal(Moment.ToUnixTimeMilliseconds(), first);
        Assert.Equal(first, afterFour);
        Assert.Equal(Moment.AddMinutes(5).ToUnixTimeMilliseconds(), afterFive);
    }

    [Fact]
    public async Task TokenAndQueryAreNotLogged()
    {
        var logs = new LogCollector();
        await using var factory = new TrackerFactory(services: services => services.AddSingleton<ILoggerProvider>(logs));
        var owner = await SignedIn(factory);
        var (_, token) = await Issue(owner, Read);

        await Bearer(factory, token).Get("v1/subjects?token=СЕКРЕТНЫЙ-ЗАПРОС");
        await Bearer(factory, "zt_garbage-секрет").Get("v1/subjects?token=СЕКРЕТНЫЙ-ЗАПРОС");
        await Bearer(factory, token).Get("subjects");

        var text = string.Join('\n', logs.Records);
        Assert.Contains("/api/v1/subjects", text);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(token[3..], text);
        Assert.DoesNotContain("zt_", text);
        Assert.DoesNotContain("garbage", text);
        Assert.DoesNotContain("СЕКРЕТНЫЙ", text);
        Assert.DoesNotContain("Authorization", text);
    }

    [Fact]
    public async Task RestoreKeepsToken()
    {
        var folder = Directory.CreateTempSubdirectory("zachet-backup-").FullName;
        var restored = Directory.CreateTempSubdirectory("zachet-restore-").FullName;
        try
        {
            string copy, token;
            await using (var source = new TrackerFactory())
            {
                var owner = await SignedIn(source);
                await owner.CreateSubject("Переживёт копию");
                token = (await Issue(owner, Read)).Token;
                copy = await BackupCommands.BackupAsync(source.Services, folder, new FakeTimeProvider(Moment));
            }

            await BackupCommands.RestoreAsync(copy, restored, Path.Combine(restored, "keys"));

            await using var factory = TrackerFactory.OnDirectory(restored);
            var response = await Bearer(factory, token).Get("v1/subjects");
            Assert.Equal(200, response.Status);
            Assert.Equal("Переживёт копию", Assert.Single(response.Body.EnumerateArray()).GetProperty("name").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
            Directory.Delete(restored, recursive: true);
        }
    }
}
