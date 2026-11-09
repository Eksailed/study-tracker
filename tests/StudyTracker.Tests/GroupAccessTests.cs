using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class GroupAccessTests
{
    [Fact]
    public async Task MakeOwnerSetsFlag()
    {
        await using var factory = new TrackerFactory();
        var owner = factory.NewClient();
        var other = factory.NewClient();
        await owner.SignIn("owner@example.test");
        await other.SignIn("other@example.test");

        await AccountCommands.MakeOwnerAsync(factory.Services, "owner@example.test");
        await AccountCommands.MakeOwnerAsync(factory.Services, "owner@example.test");

        Assert.True((await owner.Get("auth/me")).Body.GetProperty("isOwner").GetBoolean());
        Assert.False((await other.Get("auth/me")).Body.GetProperty("isOwner").GetBoolean());
    }

    [Fact]
    public async Task MakeOwnerUnknownEmailFails()
    {
        await using var factory = new TrackerFactory();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountCommands.MakeOwnerAsync(factory.Services, "nobody@example.test"));

        Assert.Equal("Аккаунт не найден.", error.Message);
    }

    [Fact]
    public async Task MeShowsMembership()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("head@example.test");
        Assert.Equal(JsonValueKind.Null, (await client.Get("auth/me")).Body.GetProperty("group").ValueKind);

        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, "head@example.test", GroupRole.Head);

        var me = (await client.Get("auth/me")).Body.GetProperty("group");
        Assert.Equal(group, me.GetProperty("id").GetInt32());
        Assert.Equal("ИВТ-4", me.GetProperty("name").GetString());
        Assert.Equal("Head", me.GetProperty("role").GetString());
        Assert.Equal("Active", me.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RoleChangesApplyWithoutRelogin()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("head@example.test");
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, "head@example.test", GroupRole.Head);
        Assert.Equal("Head", (await client.Get("auth/me")).Body.GetProperty("group").GetProperty("role").GetString());

        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            db.GroupMembers.Single().Role = GroupRole.Student;
            db.SaveChanges();
            return 0;
        });

        Assert.Equal("Student", (await client.Get("auth/me")).Body.GetProperty("group").GetProperty("role").GetString());
    }

    [Fact]
    public async Task AccessRulesFollowMembership()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("student@example.test");
        var mine = GroupSupport.CreateGroup(factory, "ИВТ-4");
        var other = GroupSupport.CreateGroup(factory, "ИВТ-3");
        GroupSupport.Join(factory, mine, "student@example.test", GroupRole.Head, MemberStatus.Pending);

        var pending = await Load(factory, "student@example.test");
        Assert.False(pending.CanSeeGroup(mine));
        Assert.False(pending.CanManage(mine));
        Assert.False(pending.IsActiveIn(mine));

        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            db.GroupMembers.Single().Status = MemberStatus.Active;
            db.SaveChanges();
            return 0;
        });
        var head = await Load(factory, "student@example.test");
        Assert.True(head.CanSeeGroup(mine));
        Assert.True(head.CanManage(mine));
        Assert.False(head.CanSeeGroup(other));
        Assert.False(head.CanManage(other));

        await AccountCommands.MakeOwnerAsync(factory.Services, "student@example.test");
        var owner = await Load(factory, "student@example.test");
        Assert.True(owner.CanManage(other));
        Assert.False(owner.CanSeeGroup(other));
    }

    [Theory]
    [InlineData("  ИВТ-4 ", "ИВТ-4", "ИВТ-4")]
    [InlineData("ивт-4", "ивт-4", "ИВТ-4")]
    [InlineData("ИВТ-4", "ИВТ-4", "ИВТ-4")]
    public void GroupNamesNormalize(string raw, string name, string key)
    {
        Assert.Equal((name, key), GroupNames.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GroupNamesRejectEmpty(string? raw) => Assert.Null(GroupNames.Normalize(raw));

    [Fact]
    public void GroupNamesLimitLength()
    {
        Assert.NotNull(GroupNames.Normalize(new string('а', 60)));
        Assert.Null(GroupNames.Normalize(new string('а', 61)));
    }

    private static Task<Access> Load(TrackerFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDb>();
        var user = db.Users.AsNoTracking().Single(u => u.Email == email);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "test"));
        return GroupAccess.LoadAsync(db, principal);
    }
}
