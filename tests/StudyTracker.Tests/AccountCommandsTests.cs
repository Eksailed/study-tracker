using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class AccountCommandsTests
{
    [Fact]
    public async Task CliCreatesAccountWithoutOpeningRegistration()
    {
        var settings = new Dictionary<string, string?> { ["Accounts:AllowRegistration"] = "false" };
        await using var factory = new TrackerFactory(settings);
        var client = factory.NewClient();

        await AccountCommands.CreateAsync(factory.Services, "cli@example.test", () => "cli-test-password-2026");

        Assert.Equal(200, (await client.Login("cli@example.test", "cli-test-password-2026")).Status);
        Assert.False((await client.Get("auth/options")).Body.GetProperty("allowRegistration").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountCommands.CreateAsync(factory.Services, "cli@example.test", () => "other-test-password"));
        Assert.Equal(200, (await client.Login("cli@example.test", "cli-test-password-2026")).Status);
    }

    [Fact]
    public async Task CliRejectsShortPassword()
    {
        await using var factory = new TrackerFactory();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountCommands.CreateAsync(factory.Services, "short-cli@example.test", () => "short"));

        Assert.Equal(401, (await factory.NewClient().Login("short-cli@example.test")).Status);
    }
}
