using Microsoft.EntityFrameworkCore;
using StudyTracker.Notifications;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class TelegramBindingTests : TelegramFixture
{
    private T Db<T>(Func<TrackerDb, T> read) => App.Scope(services => read(services.GetRequiredService<TrackerDb>()));

    private async Task<bool> Connected(ApiClient? client = null) => (await StateAsync(client)).GetProperty("connected").GetBoolean();

    [Fact]
    public async Task FixtureSendsNothingByItself()
    {
        await BindAsync();

        Assert.Equal(202, (await Client.Post("notifications/test")).Status);
        await Task.Delay(1500);

        Assert.Empty(Fake.Messages);
    }

    [Fact]
    public async Task TelegramTokenIsNotLogged()
    {
        Fake.AddUpdate(await CodeAsync());
        await PollAsync();
        await PollAsync();

        Assert.NotEmpty(Logs.Records);
        Assert.DoesNotContain(Logs.Records, record => record.Contains(FakeTelegram.Token) || record.Contains("fake-token-for-tests"));
    }

    [Fact]
    public async Task BindingCodeIsSingleUse()
    {
        var code = await CodeAsync();
        Fake.AddUpdate(code);
        await PollAsync();
        Assert.True(await Connected());

        Fake.AddUpdate(code, chat: 20000000002);
        await PollAsync();

        Assert.Equal(10000000001, Db(db => db.TelegramConnections.Single().ChatId));
        Assert.Equal(0, Db(db => db.TelegramLinkCodes.Count()));
    }

    [Fact]
    public async Task BindingCodeExpiresAfterTenMinutes()
    {
        var code = await CodeAsync();
        Assert.DoesNotContain(code, Db(db => db.TelegramLinkCodes.Single().Hash));

        await AdvanceAsync("2026-11-11T15:10:00Z");
        Fake.AddUpdate(code);
        await PollAsync();

        Assert.False(await Connected());
    }

    [Fact]
    public async Task CodeWorksAtNineMinutesFiftyNineSeconds()
    {
        var code = await CodeAsync();

        await AdvanceAsync("2026-11-11T15:09:59Z");
        Fake.AddUpdate(code);
        await PollAsync();

        Assert.True(await Connected());
    }

    [Fact]
    public async Task NewCodeInvalidatesOldCode()
    {
        var old = await CodeAsync();
        var current = await CodeAsync();

        Fake.AddUpdate(old);
        await PollAsync();
        Assert.False(await Connected());

        Fake.AddUpdate(current);
        await PollAsync();
        Assert.True(await Connected());
    }

    [Fact]
    public async Task GroupChatCannotBind()
    {
        Fake.AddUpdate(await CodeAsync(), kind: "group");
        await PollAsync();

        Assert.False(await Connected());
        Assert.Empty(Fake.Messages);
    }

    [Fact]
    public async Task ChatBoundToAnotherUserIsNotReassigned()
    {
        Fake.AddUpdate(await CodeAsync());
        await PollAsync();
        var other = App.NewClient();
        await other.SignIn("second-telegram@example.test");

        Fake.AddUpdate(await CodeAsync(other));
        await PollAsync();

        Assert.False(await Connected(other));
        Assert.True(await Connected());
    }

    [Fact]
    public async Task DuplicateUpdateIsIgnored()
    {
        Fake.AddUpdate(await CodeAsync());
        await PollAsync();
        await PollAsync();

        Assert.Single(Fake.Messages);
    }

    [Fact]
    public async Task DisconnectClearsPendingCode()
    {
        var code = await CodeAsync();
        Assert.Equal(204, (await Client.Delete("notifications/connection")).Status);

        Fake.AddUpdate(code);
        await PollAsync();

        Assert.False(await Connected());
    }

    [Fact]
    public async Task CodeIssueLimit()
    {
        for (var i = 0; i < 5; i++) await CodeAsync();

        Assert.Equal(429, (await Client.Post("notifications/code")).Status);
    }

    [Fact]
    public async Task ExistingWebhookIsNotRemoved()
    {
        Fake.Webhook = "https://example.test/existing-hook";

        Assert.False(await App.Services.GetRequiredService<TelegramClient>().InitializeAsync(CancellationToken.None));

        Assert.Contains("webhook", (await StateAsync()).GetProperty("botStatus").GetString());
        Assert.Equal(503, (await Client.Post("notifications/code")).Status);
        Assert.DoesNotContain("deleteWebhook", Fake.Calls);
    }
}
