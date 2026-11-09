using System.Text.Json;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class GroupMembershipTests
{
    private const string Head = "head@example.test";
    private const string Ann = "ann@example.test";
    private const string Bob = "bob@example.test";

    private static async Task<ApiClient> SignedIn(TrackerFactory factory, string email)
    {
        var client = factory.NewClient();
        await client.SignIn(email);
        return client;
    }

    private static string? GroupState(ApiResponse me, string property) =>
        me.Body.GetProperty("group") is { ValueKind: JsonValueKind.Object } group ? group.GetProperty(property).GetString() : null;

    private static long Count(TrackerFactory factory, Func<TrackerDb, int> query) => factory.Scope(services => (long)query(services.GetRequiredService<TrackerDb>()));

    [Fact]
    public async Task ListShowsGroupNamesOnly()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory, Ann);
        GroupSupport.CreateGroup(factory, "Б-2");
        var first = GroupSupport.CreateGroup(factory, "А-1");

        var response = await client.Get("groups");

        Assert.Equal(200, response.Status);
        var items = response.Body.EnumerateArray().ToList();
        Assert.Equal(["А-1", "Б-2"], items.Select(i => i.GetProperty("name").GetString()));
        Assert.Equal(first, items[0].GetProperty("id").GetInt32());
        Assert.All(items, i => Assert.Equal(2, i.EnumerateObject().Count()));
    }

    [Fact]
    public async Task JoinCreatesPendingRequest()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory, Ann);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        var other = GroupSupport.CreateGroup(factory, "ИВТ-3");

        var joined = await client.Post($"groups/{group}/join");

        Assert.Equal(201, joined.Status);
        Assert.Equal("Pending", GroupState(await client.Get("auth/me"), "status"));
        Assert.Equal("Student", GroupState(await client.Get("auth/me"), "role"));
        foreach (var target in new[] { group, other })
        {
            var again = await client.Post($"groups/{target}/join");
            Assert.Equal(409, again.Status);
            Assert.Equal("Вы уже состоите в группе или подали заявку.", again.Body.GetProperty("error").GetString());
        }
        Assert.Equal(404, (await client.Post("groups/9999/join")).Status);
    }

    [Fact]
    public async Task PendingSeesNothingOfGroup()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory, Ann);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        await client.Post($"groups/{group}/join");

        Assert.Equal("Pending", GroupState(await client.Get("auth/me"), "status"));
        Assert.Equal(404, (await client.Get($"groups/{group}/members")).Status);
    }

    [Fact]
    public async Task HeadApprovesAndRejects()
    {
        await using var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        var ann = await SignedIn(factory, Ann);
        var bob = await SignedIn(factory, Bob);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Head, GroupRole.Head);
        await ann.Post($"groups/{group}/join");
        await bob.Post($"groups/{group}/join");

        var members = await head.Get($"groups/{group}/members");
        Assert.Equal(200, members.Status);
        var entries = members.Body.EnumerateArray().ToList();
        Assert.Equal([Ann, Bob, Head], entries.Select(e => e.GetProperty("email").GetString()));
        var request = entries[0];
        Assert.Equal("Pending", request.GetProperty("status").GetString());
        Assert.Equal("Student", request.GetProperty("role").GetString());
        Assert.Equal("Head", entries[2].GetProperty("role").GetString());

        var annId = request.GetProperty("userId").GetString();
        Assert.Equal(200, (await head.Post($"groups/{group}/members/{annId}/approve")).Status);
        Assert.Equal("Active", GroupState(await ann.Get("auth/me"), "status"));
        Assert.Equal(409, (await head.Post($"groups/{group}/members/{annId}/approve")).Status);

        var bobId = GroupSupport.UserId(factory, Bob);
        Assert.Equal(204, (await head.Delete($"groups/{group}/members/{bobId}")).Status);
        Assert.Equal(JsonValueKind.Null, (await bob.Get("auth/me")).Body.GetProperty("group").ValueKind);
        Assert.Equal(404, (await head.Post($"groups/{group}/members/{bobId}/approve")).Status);
        Assert.Equal(404, (await head.Delete($"groups/{group}/members/{bobId}")).Status);
    }

    [Fact]
    public async Task StudentCannotManage()
    {
        await using var factory = new TrackerFactory();
        var ann = await SignedIn(factory, Ann);
        await SignedIn(factory, Bob);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Ann);
        GroupSupport.Join(factory, group, Bob, status: MemberStatus.Pending);
        var bobId = GroupSupport.UserId(factory, Bob);

        Assert.Equal(404, (await ann.Get($"groups/{group}/members")).Status);
        Assert.Equal(404, (await ann.Post($"groups/{group}/members/{bobId}/approve")).Status);
        Assert.Equal(404, (await ann.Delete($"groups/{group}/members/{bobId}")).Status);
        Assert.Equal(1, Count(factory, db => db.GroupMembers.Count(m => m.Status == MemberStatus.Pending)));
    }

    [Fact]
    public async Task HeadOfOtherGroupGets404()
    {
        await using var factory = new TrackerFactory();
        var rival = await SignedIn(factory, Head);
        await SignedIn(factory, Ann);
        var mine = GroupSupport.CreateGroup(factory, "ИВТ-3");
        var theirs = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, mine, Head, GroupRole.Head);
        GroupSupport.Join(factory, theirs, Ann, status: MemberStatus.Pending);
        var annId = GroupSupport.UserId(factory, Ann);

        Assert.Equal(404, (await rival.Get($"groups/{theirs}/members")).Status);
        Assert.Equal(404, (await rival.Post($"groups/{theirs}/members/{annId}/approve")).Status);
        Assert.Equal(404, (await rival.Delete($"groups/{theirs}/members/{annId}")).Status);
        Assert.Equal(1, Count(factory, db => db.GroupMembers.Count(m => m.GroupId == theirs && m.Status == MemberStatus.Pending)));
    }

    [Fact]
    public async Task OwnerManagesAnyGroup()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory, "owner@example.test");
        await SignedIn(factory, Ann);
        await SignedIn(factory, Bob);
        GroupSupport.MakeOwner(factory, "owner@example.test");
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Ann, status: MemberStatus.Pending);
        GroupSupport.Join(factory, group, Bob, status: MemberStatus.Pending);

        Assert.Equal(200, (await owner.Get($"groups/{group}/members")).Status);
        Assert.Equal(200, (await owner.Post($"groups/{group}/members/{GroupSupport.UserId(factory, Ann)}/approve")).Status);
        Assert.Equal(204, (await owner.Delete($"groups/{group}/members/{GroupSupport.UserId(factory, Bob)}")).Status);
        Assert.Equal(1, Count(factory, db => db.GroupMembers.Count()));
    }

    [Fact]
    public async Task HeadCannotRemoveHead()
    {
        await using var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        var owner = await SignedIn(factory, "owner@example.test");
        await SignedIn(factory, Ann);
        GroupSupport.MakeOwner(factory, "owner@example.test");
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Head, GroupRole.Head);
        GroupSupport.Join(factory, group, Ann, GroupRole.Head);
        var annId = GroupSupport.UserId(factory, Ann);

        Assert.Equal(409, (await head.Delete($"groups/{group}/members/{annId}")).Status);
        Assert.Equal(2, Count(factory, db => db.GroupMembers.Count()));
        Assert.Equal(204, (await owner.Delete($"groups/{group}/members/{annId}")).Status);
        Assert.Equal(1, Count(factory, db => db.GroupMembers.Count()));
    }

    [Fact]
    public async Task StudentLeavesAndLosesTraces()
    {
        await using var factory = new TrackerFactory();
        var ann = await SignedIn(factory, Ann);
        await SignedIn(factory, Bob);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Ann);
        GroupSupport.Join(factory, group, Bob);
        var (_, task) = GroupSupport.AddGroupTask(factory, group);
        foreach (var email in new[] { Ann, Bob })
        {
            GroupSupport.AddProgress(factory, task, email, AssignmentStatus.Done);
            GroupSupport.AddStep(factory, task, email);
        }

        Assert.Equal(204, (await ann.Delete("group/membership")).Status);

        Assert.Equal(JsonValueKind.Null, (await ann.Get("auth/me")).Body.GetProperty("group").ValueKind);
        var annId = GroupSupport.UserId(factory, Ann);
        Assert.Equal(0, Count(factory, db => db.GroupProgress.Count(p => p.UserId == annId)));
        Assert.Equal(0, Count(factory, db => db.Subtasks.Count(s => s.UserId == annId)));
        Assert.Equal(1, Count(factory, db => db.GroupProgress.Count()));
        Assert.Equal(1, Count(factory, db => db.Subtasks.Count()));
        Assert.Equal(404, (await ann.Delete("group/membership")).Status);
    }

    [Fact]
    public async Task CancelRequestWithoutGroupGives404()
    {
        await using var factory = new TrackerFactory();
        var ann = await SignedIn(factory, Ann);

        Assert.Equal(404, (await ann.Delete("group/membership")).Status);
    }

    [Fact]
    public async Task HeadCannotLeave()
    {
        await using var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Head, GroupRole.Head);

        var response = await head.Delete("group/membership");

        Assert.Equal(409, response.Status);
        Assert.Equal("Старосту снимает владелец.", response.Body.GetProperty("error").GetString());
        Assert.Equal("Head", GroupState(await head.Get("auth/me"), "role"));
    }

    [Fact]
    public async Task GroupAdminRequiresCsrf()
    {
        await using var factory = new TrackerFactory();
        var ann = await SignedIn(factory, Ann);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");

        Assert.Equal(400, (await ann.Post($"groups/{group}/join", csrf: false)).Status);
        Assert.Equal(400, (await ann.Delete("group/membership", csrf: false)).Status);
        Assert.Equal(JsonValueKind.Null, (await ann.Get("auth/me")).Body.GetProperty("group").ValueKind);
    }

    [Fact]
    public async Task ApiTokenCannotUseGroupAddresses()
    {
        await using var factory = new TrackerFactory();
        var ann = await SignedIn(factory, Ann);
        var created = await ann.Post("tokens", new { name = "Скрипт", scopes = new[] { "tasks:read" }, days = 30 });
        var bearer = factory.NewClient();
        bearer.UseBearer(created.Body.GetProperty("token").GetString()!);

        Assert.Equal(401, (await bearer.Get("groups")).Status);
    }
}
