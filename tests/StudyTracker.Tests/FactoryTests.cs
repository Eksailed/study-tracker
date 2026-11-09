using Microsoft.EntityFrameworkCore;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class FactoryTests
{
    [Fact]
    public async Task FactoryUsesTemporaryDataDirectory()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();

        var response = await client.Get("token");

        Assert.Equal(200, response.Status);
        var source = factory.Scope(services => services.GetRequiredService<TrackerDb>().Database.GetConnectionString())!;
        Assert.Contains(factory.DataDirectory, source);
        Assert.True(File.Exists(Path.Combine(factory.DataDirectory, "tracker.db")));
    }

    [Fact]
    public async Task DisposeReleasesDataDirectory()
    {
        var factory = new TrackerFactory();
        await factory.NewClient().Register("dispose@example.test");
        var directory = factory.DataDirectory;
        Assert.True(Directory.Exists(directory));

        await factory.DisposeAsync();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task RegisterAndLoginWork()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();

        var registered = await client.Register("first@example.test");
        var login = await client.Login("first@example.test");
        var me = await client.Get("auth/me");

        Assert.Equal(201, registered.Status);
        Assert.Equal(200, login.Status);
        Assert.Equal(200, me.Status);
        Assert.Equal("first@example.test", me.Body.GetProperty("email").GetString());
    }
}
