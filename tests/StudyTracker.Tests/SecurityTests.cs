using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class SecurityTests
{
    [Fact]
    public async Task CsrfRequiredForAllMutations()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();

        Assert.Equal(400, (await client.Post("subjects", new { name = "Тест", color = "blue" }, csrf: false)).Status);
        Assert.Equal(400, (await client.Put("assignments/1", new { }, csrf: false)).Status);
        Assert.Equal(400, (await client.Delete("subjects/1", csrf: false)).Status);
    }

    [Fact]
    public async Task StaticPageAndSecurityHeaders()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();

        var page = await client.Page("/");
        Assert.Equal(200, page.Status);
        Assert.Contains("Зачёт", page.Text);
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(200, (await client.Page("/app.js")).Status);
        Assert.True((await client.Page("/api/subjects")).Headers.CacheControl!.NoStore);
    }
}
