using Microsoft.EntityFrameworkCore;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class AuthTests
{
    private static Dictionary<string, string?> Setting(string key, string value) => new() { [key] = value };

    [Fact]
    public async Task RegistrationAndLogin()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();

        Assert.Equal(201, (await client.Register("student@example.test")).Status);
        Assert.Equal(401, (await client.Get("auth/me")).Status);
        var login = await client.Login("student@example.test");
        Assert.Equal(200, login.Status);
        Assert.Equal("student@example.test", (await client.Get("auth/me")).Body.GetProperty("email").GetString());

        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("Zachet.Account="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        var hash = factory.Scope(services => services.GetRequiredService<TrackerDb>().Users.Select(u => u.PasswordHash).Single())!;
        Assert.NotEqual(ApiClient.DefaultPassword, hash);
        Assert.True(hash.Length > 50);
    }

    [Fact]
    public async Task LogoutEndsSession()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("logout@example.test");

        Assert.Equal(204, (await client.Logout()).Status);
        Assert.Equal(401, (await client.Get("auth/me")).Status);
    }

    [Fact]
    public async Task ShortPasswordIsRejected()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();

        Assert.Equal(400, (await client.Register("short@example.test", new string('a', 11))).Status);
        Assert.Equal(201, (await client.Register("valid@example.test", new string('a', 12))).Status);
        Assert.Equal(400, (await client.Register("long@example.test", new string('a', 129))).Status);
    }

    [Fact]
    public async Task LoginIsLockedAfterFiveFailures()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.Register("locked@example.test");

        for (var i = 0; i < 5; i++)
            Assert.Equal(401, (await client.Login("locked@example.test", "wrong-password")).Status);

        Assert.Equal(401, (await client.Login("locked@example.test")).Status);
        Assert.NotNull(factory.Scope(services => services.GetRequiredService<TrackerDb>().Users.Single().LockoutEnd));
    }

    [Fact]
    public async Task AuthRequiresCsrf()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();

        foreach (var path in new[] { "auth/register", "auth/login" })
            Assert.Equal(400, (await client.Post(path, new { }, csrf: false)).Status);
        await client.SignIn("csrf@example.test");
        Assert.Equal(400, (await client.Post("auth/logout", csrf: false)).Status);
    }

    [Fact]
    public async Task RegistrationCanBeDisabled()
    {
        await using var factory = new TrackerFactory(Setting("Accounts:AllowRegistration", "false"));
        var client = factory.NewClient();

        Assert.False((await client.Get("auth/options")).Body.GetProperty("allowRegistration").GetBoolean());
        Assert.Equal(403, (await client.Register("closed@example.test")).Status);
    }

    [Fact]
    public async Task AuthRateLimit()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();

        for (var i = 0; i < 10; i++)
            Assert.Equal(401, (await client.Login("missing@example.test")).Status);

        Assert.Equal(429, (await client.Login("missing@example.test")).Status);
    }

    [Fact]
    public async Task ProductionRegistrationIsClosedByDefault()
    {
        await using var factory = new TrackerFactory(environment: "Production");

        Assert.Equal(403, (await factory.NewClient().Register("prod@example.test")).Status);
    }

    [Fact]
    public async Task ProductionCookieRequiresHttps()
    {
        await using var factory = new TrackerFactory(Setting("Accounts:AllowRegistration", "true"), "Production");
        var client = factory.NewClient();

        Assert.Equal(201, (await client.Register("secure@example.test")).Status);
        var login = await client.Login("secure@example.test");

        Assert.Equal(200, login.Status);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("Zachet.Account="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(401, (await client.Get("auth/me")).Status);
    }

    [Fact]
    public async Task LoginRotatesCsrfIdentity()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.Register("rotate@example.test");
        var anonymousToken = client.Token;

        Assert.Equal(200, (await client.Login("rotate@example.test")).Status);
        var newToken = client.Token;

        client.Token = anonymousToken;
        Assert.Equal(400, (await client.Post("auth/logout")).Status);
        client.Token = newToken;
        Assert.Equal(204, (await client.Post("auth/logout")).Status);
    }
}
