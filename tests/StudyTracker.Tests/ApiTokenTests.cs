using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using StudyTracker.ApiTokens;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class ApiTokenTests
{
    private static readonly DateTimeOffset Moment = DateTimeOffset.Parse("2026-11-10T08:09:10Z");

    private static Task<ApiResponse> Issue(ApiClient client, string name = "Скрипт", string[]? scopes = null, int? days = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["scopes"] = scopes ?? ["tasks:read"] };
        if (days is not null) body["days"] = days;
        return client.Post("tokens", body);
    }

    private static List<ApiToken> Stored(TrackerFactory factory) =>
        factory.Scope(services => services.GetRequiredService<TrackerDb>().ApiTokens.AsNoTracking().OrderBy(t => t.Id).ToList());

    [Fact]
    public async Task CreateReturnsTokenOnceAndStoresOnlyHash()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();

        var created = await Issue(client, "Скрипт", ["tasks:read", "tasks:write"], days: 30);

        Assert.Equal(201, created.Status);
        var token = created.Body.GetProperty("token").GetString()!;
        Assert.StartsWith("zt_", token);
        Assert.Equal(46, token.Length);
        var id = created.Body.GetProperty("id").GetInt32();
        Assert.Equal($"/api/tokens/{id}", created.Headers.Location!.ToString());
        Assert.Equal(["tasks:read", "tasks:write"], created.Body.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        var list = await client.Get("tokens");
        Assert.Equal(200, list.Status);
        Assert.DoesNotContain(token, list.Body.GetRawText());
        Assert.False(list.Body[0].TryGetProperty("token", out _));
        Assert.False(list.Body[0].TryGetProperty("hash", out _));
        var stored = Assert.Single(Stored(factory));
        Assert.NotEqual(token, stored.Hash);
        Assert.Equal(TokenGenerator.Hash(token), stored.Hash);
        Assert.Equal(token[..8], stored.Prefix);
        Assert.Equal(token[..8], list.Body[0].GetProperty("prefix").GetString());
    }

    [Fact]
    public void GeneratorMakesDistinctTokensWithMatchingHash()
    {
        var (first, firstHash, firstPrefix) = TokenGenerator.Create();
        var (second, _, _) = TokenGenerator.Create();

        Assert.NotEqual(first, second);
        Assert.Equal(TokenGenerator.Hash(first), firstHash);
        Assert.Equal(first[..8], firstPrefix);
        Assert.Matches("^zt_[A-Za-z0-9_-]{43}$", first);
    }

    [Fact]
    public async Task ListShowsOwnTokensOnly()
    {
        await using var factory = new TrackerFactory();
        var owner = factory.NewClient();
        await owner.SignIn("owner@example.test");
        var other = factory.NewClient();
        await other.SignIn("other@example.test");
        var id = (await Issue(owner)).Body.GetProperty("id").GetInt32();

        Assert.Equal(1, (await owner.Get("tokens")).Body.GetArrayLength());
        Assert.Equal(0, (await other.Get("tokens")).Body.GetArrayLength());
        Assert.Equal(404, (await other.Delete($"tokens/{id}")).Status);
        Assert.Null(Assert.Single(Stored(factory)).RevokedAt);
    }

    [Fact]
    public async Task RevokeIsIdempotent()
    {
        var time = new FakeTimeProvider(Moment);
        await using var factory = new TrackerFactory(services: TrackerFactory.WithTime(time));
        var client = factory.NewClient();
        await client.SignIn();
        var id = (await Issue(client)).Body.GetProperty("id").GetInt32();

        Assert.Equal(204, (await client.Delete($"tokens/{id}")).Status);
        var revokedAt = Assert.Single(Stored(factory)).RevokedAt;
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(204, (await client.Delete($"tokens/{id}")).Status);

        Assert.Equal(Moment.ToUnixTimeMilliseconds(), revokedAt);
        Assert.Equal(revokedAt, Assert.Single(Stored(factory)).RevokedAt);
        Assert.True((await client.Get("tokens")).Body[0].GetProperty("revoked").GetBoolean());
        Assert.Equal(404, (await client.Delete("tokens/9999")).Status);
    }

    [Fact]
    public async Task ValidationRejectsBadInput()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();

        Assert.Equal(400, (await Issue(client, "")).Status);
        Assert.Equal(400, (await Issue(client, "   ")).Status);
        Assert.Equal(400, (await Issue(client, new string('н', 61))).Status);
        Assert.Equal(400, (await Issue(client, scopes: ["tasks:read", "admin"])).Status);
        Assert.Equal(400, (await Issue(client, scopes: [])).Status);
        Assert.Equal(400, (await Issue(client, days: 0)).Status);
        Assert.Equal(400, (await Issue(client, days: 366)).Status);
        var bad = await client.Post("tokens", new { name = "x", scopes = new[] { "tasks:read" }, days = 1.5 });
        Assert.Equal(400, bad.Status);
        Assert.Equal(400, (await client.Post("tokens", new { name = "без областей" })).Status);
        Assert.Empty(Stored(factory));

        Assert.Equal(201, (await Issue(client, new string('н', 60))).Status);
        Assert.Equal(201, (await Issue(client, "один день", days: 1)).Status);
        Assert.Equal(201, (await Issue(client, "год", days: 365)).Status);
        Assert.Equal(201, (await Issue(client, "  с пробелами  ", scopes: ["tasks:write", "tasks:write"])).Status);
        var last = Stored(factory).Last();
        Assert.Equal("с пробелами", last.Name);
        Assert.Equal(2, last.Scopes);
    }

    [Fact]
    public async Task DefaultDaysIs90()
    {
        var time = new FakeTimeProvider(Moment);
        await using var factory = new TrackerFactory(services: TrackerFactory.WithTime(time));
        var client = factory.NewClient();
        await client.SignIn();

        var created = await Issue(client);

        var expected = Moment.AddDays(90).ToUnixTimeMilliseconds();
        Assert.Equal(expected, created.Body.GetProperty("expiresAt").GetInt64());
        Assert.Equal(expected, Assert.Single(Stored(factory)).ExpiresAt);
        Assert.Equal(Moment.ToUnixTimeMilliseconds(), Assert.Single(Stored(factory)).CreatedAt);
    }

    [Fact]
    public async Task LimitOfTenActiveTokens()
    {
        var time = new FakeTimeProvider(Moment);
        await using var factory = new TrackerFactory(services: TrackerFactory.WithTime(time));
        var client = factory.NewClient();
        await client.SignIn();
        var ids = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            var created = await Issue(client, $"токен {i}", days: 1);
            Assert.Equal(201, created.Status);
            ids.Add(created.Body.GetProperty("id").GetInt32());
        }

        Assert.Equal(409, (await Issue(client, "лишний", days: 1)).Status);
        Assert.Equal(204, (await client.Delete($"tokens/{ids[0]}")).Status);
        Assert.Equal(201, (await Issue(client, "вместо отозванного", days: 1)).Status);
        Assert.Equal(409, (await Issue(client, "снова лишний", days: 1)).Status);

        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(201, (await Issue(client, "после срока")).Status);
        var list = await client.Get("tokens");
        Assert.Equal(12, list.Body.GetArrayLength());
        Assert.Equal(11, list.Body.EnumerateArray().Count(t => t.GetProperty("expired").GetBoolean()));
    }

    [Fact]
    public async Task LimitCountsOnlyOwnTokens()
    {
        await using var factory = new TrackerFactory();
        var first = factory.NewClient();
        await first.SignIn("first@example.test");
        for (var i = 0; i < 10; i++) Assert.Equal(201, (await Issue(first, $"токен {i}")).Status);
        var second = factory.NewClient();
        await second.SignIn("second@example.test");

        Assert.Equal(201, (await Issue(second)).Status);
    }

    [Fact]
    public async Task TokenCannotManageTokens()
    {
        await using var factory = new TrackerFactory();
        var owner = factory.NewClient();
        await owner.SignIn();
        var token = (await Issue(owner, scopes: ["tasks:read", "tasks:write"])).Body.GetProperty("token").GetString()!;
        var id = Stored(factory).Single().Id;
        var bearer = factory.NewClient();
        bearer.DefaultHeaders.Add("Authorization", "Bearer " + token);

        Assert.Equal(401, (await bearer.Get("tokens")).Status);
        Assert.Equal(401, (await bearer.Post("tokens", new { name = "x", scopes = new[] { "tasks:read" } })).Status);
        Assert.Equal(401, (await bearer.Delete($"tokens/{id}")).Status);
        Assert.Null(Assert.Single(Stored(factory)).RevokedAt);
    }

    [Fact]
    public async Task ManagementNeedsCsrf()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();
        var created = await Issue(client);
        var id = created.Body.GetProperty("id").GetInt32();

        var post = await client.Post("tokens", new { name = "без csrf", scopes = new[] { "tasks:read" } }, csrf: false);
        var delete = await client.Delete($"tokens/{id}", csrf: false);

        Assert.Equal(400, post.Status);
        Assert.Equal(400, delete.Status);
        Assert.Null(Assert.Single(Stored(factory)).RevokedAt);
    }

    [Fact]
    public async Task ManagementNeedsSignIn()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();

        Assert.Equal(401, (await client.Get("tokens")).Status);
        Assert.Equal(401, (await client.Post("tokens", new { name = "x", scopes = new[] { "tasks:read" } })).Status);
    }

    [Fact]
    public async Task MigrationKeepsExistingData()
    {
        var directory = Directory.CreateTempSubdirectory("zachet-tokens-").FullName;
        try
        {
            var path = Path.Combine(directory, "tracker.db");
            var options = new DbContextOptionsBuilder<TrackerDb>().UseSqlite(
                new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString()).Options;
            await using (var db = new TrackerDb(options))
                db.GetService<IMigrator>().Migrate("20261106114506_DeliveryThrottle");
            SqliteFile.Execute(path, """
                INSERT INTO AspNetUsers (Id, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
                    VALUES ('owner',0,0,0,0,0);
                INSERT INTO Subjects (Id, Name, NameKey, Color, OwnerId) VALUES (1, 'Сохранить', 'СОХРАНИТЬ', 'blue', 'owner');
                INSERT INTO Assignments (Id, Title, Description, SubjectId, DueDate, Status, CreatedAt, OwnerId)
                    VALUES (7, 'Сохранить задание', '', 1, '2026-12-12', 0, '2026-11-06 10:00:00', 'owner');
                """);

            await using (var db = new TrackerDb(options)) await db.Database.MigrateAsync();

            await using var check = new TrackerDb(options);
            Assert.Equal(1, await check.Users.CountAsync(u => u.Id == "owner"));
            Assert.Equal(1, await check.Assignments.CountAsync(a => a.Id == 7 && a.OwnerId == "owner"));
            Assert.Equal(0, await check.ApiTokens.CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeletingAccountDeletesTokens()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("gone@example.test");
        await Issue(client);
        var keeper = factory.NewClient();
        await keeper.SignIn("keeper@example.test");
        await Issue(keeper);
        Assert.Equal(2, Stored(factory).Count);

        factory.Scope(services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = users.FindByEmailAsync("gone@example.test").GetAwaiter().GetResult()!;
            Assert.True(users.DeleteAsync(user).GetAwaiter().GetResult().Succeeded);
            return 0;
        });

        var left = Assert.Single(Stored(factory));
        Assert.Equal("keeper@example.test",
            factory.Scope(services => services.GetRequiredService<UserManager<ApplicationUser>>()
                .FindByIdAsync(left.OwnerId).GetAwaiter().GetResult()!.Email));
    }
}
