using System.Text.Json;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class OwnerApiTests
{
    private const string OwnerEmail = "owner@example.test";
    private const string Ann = "ann@example.test";
    private const string Bob = "bob@example.test";

    private static async Task<ApiClient> SignedIn(TrackerFactory factory, string email)
    {
        var client = factory.NewClient();
        await client.SignIn(email);
        return client;
    }

    private static async Task<ApiClient> Owner(TrackerFactory factory)
    {
        var client = await SignedIn(factory, OwnerEmail);
        GroupSupport.MakeOwner(factory, OwnerEmail);
        return client;
    }

    private static long Count(TrackerFactory factory, Func<TrackerDb, int> query) =>
        factory.Scope(services => (long)query(services.GetRequiredService<TrackerDb>()));

    [Fact]
    public async Task NonOwnerGets404EverywhereUnderOwner()
    {
        await using var factory = new TrackerFactory();
        var student = await SignedIn(factory, Ann);
        var head = await SignedIn(factory, Bob);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Ann);
        GroupSupport.Join(factory, group, Bob, GroupRole.Head);
        var annId = GroupSupport.UserId(factory, Ann);

        foreach (var client in new[] { student, head })
        {
            Assert.Equal(404, (await client.Get("owner/groups")).Status);
            Assert.Equal(404, (await client.Post("owner/groups", new { name = "Новая" })).Status);
            Assert.Equal(404, (await client.Put($"owner/groups/{group}", new { name = "Новая" })).Status);
            Assert.Equal(404, (await client.Delete($"owner/groups/{group}")).Status);
            Assert.Equal(404, (await client.Get("owner/users?q=a")).Status);
            Assert.Equal(404, (await client.Put($"owner/groups/{group}/heads/{annId}")).Status);
            Assert.Equal(404, (await client.Delete($"owner/groups/{group}/heads/{annId}")).Status);
        }
        Assert.Equal(1, Count(factory, db => db.Groups.Count()));
        Assert.Equal("Student", factory.Scope(s => s.GetRequiredService<TrackerDb>().GroupMembers.Single(m => m.UserId == annId).Role.ToString()));
    }

    [Fact]
    public async Task OwnerCreatesRenamesAndListsGroups()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory);

        var created = await owner.Post("owner/groups", new { name = "  ИВТ-4 " });
        Assert.Equal(201, created.Status);
        Assert.Equal("ИВТ-4", created.Body.GetProperty("name").GetString());
        var id = created.Body.GetProperty("id").GetInt32();

        var duplicate = await owner.Post("owner/groups", new { name = "ивт-4" });
        Assert.Equal(409, duplicate.Status);
        Assert.Equal("Такая группа уже есть.", duplicate.Body.GetProperty("error").GetString());
        Assert.Equal(400, (await owner.Post("owner/groups", new { name = "   " })).Status);
        Assert.Equal(400, (await owner.Post("owner/groups", new { name = new string('а', 61) })).Status);
        Assert.Equal(400, (await owner.Post("owner/groups", new { })).Status);

        var other = (await owner.Post("owner/groups", new { name = "ИВТ-3" })).Body.GetProperty("id").GetInt32();
        Assert.Equal(200, (await owner.Put($"owner/groups/{id}", new { name = "ИВТ-4Б" })).Status);
        Assert.Equal(200, (await owner.Put($"owner/groups/{id}", new { name = "ивт-4б" })).Status);
        Assert.Equal(409, (await owner.Put($"owner/groups/{id}", new { name = "ивт-3" })).Status);
        Assert.Equal(400, (await owner.Put($"owner/groups/{id}", new { name = "" })).Status);
        Assert.Equal(404, (await owner.Put("owner/groups/9999", new { name = "Любая" })).Status);

        GroupSupport.Join(factory, id, OwnerEmail, GroupRole.Head);
        await SignedIn(factory, Ann);
        await SignedIn(factory, Bob);
        GroupSupport.Join(factory, id, Ann);
        GroupSupport.Join(factory, id, Bob, status: MemberStatus.Pending);

        var list = (await owner.Get("owner/groups")).Body.EnumerateArray().ToList();
        Assert.Equal(["ИВТ-3", "ивт-4б"], list.Select(g => g.GetProperty("name").GetString()));
        var first = list.Single(g => g.GetProperty("id").GetInt32() == id);
        Assert.Equal(2, first.GetProperty("members").GetInt32());
        Assert.Equal(1, first.GetProperty("pending").GetInt32());
        var heads = first.GetProperty("heads").EnumerateArray().ToList();
        Assert.Equal(OwnerEmail, Assert.Single(heads).GetProperty("email").GetString());
        Assert.Equal(0, list.Single(g => g.GetProperty("id").GetInt32() == other).GetProperty("members").GetInt32());
    }

    [Fact]
    public async Task UsersSearchFindsByEmailSubstring()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory);
        await SignedIn(factory, "Ivan.Petrov@example.test");
        await SignedIn(factory, "olga@example.test");
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, "Ivan.Petrov@example.test");

        var found = (await owner.Get("owner/users?q=IVAN")).Body.EnumerateArray().ToList();

        var user = Assert.Single(found);
        Assert.Equal("Ivan.Petrov@example.test", user.GetProperty("email").GetString());
        Assert.False(user.GetProperty("isOwner").GetBoolean());
        Assert.Equal("ИВТ-4", user.GetProperty("group").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, (await owner.Get("owner/users?q=olga")).Body.EnumerateArray().First().GetProperty("group").ValueKind);
        Assert.Equal(0, (await owner.Get("owner/users?q=%25")).Body.GetArrayLength());
    }

    [Fact]
    public async Task UsersSearchReturnsAtMostFifty()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory);
        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            for (var i = 0; i < 55; i++)
                db.Users.Add(new ApplicationUser { UserName = $"many{i:00}@example.test", Email = $"many{i:00}@example.test" });
            db.SaveChanges();
            return 0;
        });

        var found = (await owner.Get("owner/users?q=many")).Body.EnumerateArray().ToList();

        Assert.Equal(50, found.Count);
        Assert.Equal("many00@example.test", found[0].GetProperty("email").GetString());
        Assert.Equal("many49@example.test", found[^1].GetProperty("email").GetString());
    }

    [Fact]
    public async Task AssignHeadAddsOrPromotes()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory);
        foreach (var email in new[] { Ann, Bob, "cat@example.test", "dan@example.test" }) await SignedIn(factory, email);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        var elsewhere = GroupSupport.CreateGroup(factory, "ИВТ-3");
        GroupSupport.Join(factory, group, Bob);
        GroupSupport.Join(factory, group, "cat@example.test", status: MemberStatus.Pending);
        GroupSupport.Join(factory, elsewhere, "dan@example.test");

        var added = await owner.Put($"owner/groups/{group}/heads/{GroupSupport.UserId(factory, Ann)}");
        Assert.Equal(200, added.Status);
        Assert.Equal("Head", added.Body.GetProperty("role").GetString());
        Assert.Equal("Active", added.Body.GetProperty("status").GetString());
        Assert.Equal(200, (await owner.Put($"owner/groups/{group}/heads/{GroupSupport.UserId(factory, Bob)}")).Status);
        Assert.Equal(200, (await owner.Put($"owner/groups/{group}/heads/{GroupSupport.UserId(factory, "cat@example.test")}")).Status);
        Assert.Equal(3, Count(factory, db => db.GroupMembers.Count(m => m.GroupId == group && m.Role == GroupRole.Head && m.Status == MemberStatus.Active)));

        var busy = await owner.Put($"owner/groups/{group}/heads/{GroupSupport.UserId(factory, "dan@example.test")}");
        Assert.Equal(409, busy.Status);
        Assert.Equal("Состоит в другой группе.", busy.Body.GetProperty("error").GetString());
        Assert.Equal(404, (await owner.Put($"owner/groups/{group}/heads/no-such-user")).Status);
        Assert.Equal(404, (await owner.Put($"owner/groups/9999/heads/{GroupSupport.UserId(factory, Ann)}")).Status);
        Assert.Equal(1, Count(factory, db => db.GroupMembers.Count(m => m.GroupId == elsewhere)));
    }

    [Fact]
    public async Task RemoveHeadMakesStudent()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory);
        var head = await SignedIn(factory, Ann);
        await SignedIn(factory, Bob);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Ann, GroupRole.Head);
        GroupSupport.Join(factory, group, Bob);
        Assert.Equal(200, (await head.Get($"groups/{group}/members")).Status);

        var removed = await owner.Delete($"owner/groups/{group}/heads/{GroupSupport.UserId(factory, Ann)}");

        Assert.Equal(200, removed.Status);
        Assert.Equal("Student", removed.Body.GetProperty("role").GetString());
        Assert.Equal("Active", removed.Body.GetProperty("status").GetString());
        Assert.Equal(404, (await head.Get($"groups/{group}/members")).Status);
        Assert.Equal(404, (await owner.Delete($"owner/groups/{group}/heads/{GroupSupport.UserId(factory, Bob)}")).Status);
        Assert.Equal(404, (await owner.Delete($"owner/groups/{group}/heads/{GroupSupport.UserId(factory, Ann)}")).Status);
    }

    [Fact]
    public async Task DeleteGroupErasesEverything()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory);
        await SignedIn(factory, Ann);
        await SignedIn(factory, Bob);
        var doomed = GroupSupport.CreateGroup(factory, "ИВТ-4");
        var kept = GroupSupport.CreateGroup(factory, "ИВТ-3");
        GroupSupport.Join(factory, doomed, Ann);
        GroupSupport.Join(factory, kept, Bob);
        var (_, doomedTask) = GroupSupport.AddGroupTask(factory, doomed);
        var (_, keptTask) = GroupSupport.AddGroupTask(factory, kept);
        GroupSupport.AddProgress(factory, doomedTask, Ann, AssignmentStatus.Done);
        GroupSupport.AddStep(factory, doomedTask, Ann);
        GroupSupport.AddProgress(factory, keptTask, Bob, AssignmentStatus.Done);
        GroupSupport.AddStep(factory, keptTask, Bob);

        Assert.Equal(204, (await owner.Delete($"owner/groups/{doomed}")).Status);

        Assert.Equal(0, Count(factory, db => db.Groups.Count(g => g.Id == doomed)));
        Assert.Equal(0, Count(factory, db => db.GroupMembers.Count(m => m.GroupId == doomed)));
        Assert.Equal(0, Count(factory, db => db.Subjects.Count(s => s.GroupId == doomed)));
        Assert.Equal(0, Count(factory, db => db.Assignments.Count(a => a.GroupId == doomed)));
        Assert.Equal(1, Count(factory, db => db.GroupProgress.Count()));
        Assert.Equal(1, Count(factory, db => db.Subtasks.Count()));
        Assert.Equal(1, Count(factory, db => db.Groups.Count(g => g.Id == kept)));
        Assert.Equal(1, Count(factory, db => db.Assignments.Count(a => a.GroupId == kept)));
        Assert.Equal(404, (await owner.Delete($"owner/groups/{doomed}")).Status);
    }

    [Fact]
    public async Task OwnerFlagCannotBeSetThroughApi()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory);
        var student = await SignedIn(factory, Ann);
        var annId = GroupSupport.UserId(factory, Ann);

        Assert.Equal(404, (await owner.Put($"owner/users/{annId}", new { isOwner = true })).Status);
        Assert.Equal(404, (await student.Put($"owner/users/{annId}", new { isOwner = true })).Status);
        Assert.False((await student.Get("auth/me")).Body.GetProperty("isOwner").GetBoolean());
    }
}
