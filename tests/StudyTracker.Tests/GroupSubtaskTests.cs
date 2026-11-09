using System.Text.Json;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class GroupSubtaskTests
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

    private static async Task<int> AddStep(ApiClient client, int task, string title)
    {
        var response = await client.Post($"assignments/{task}/subtasks", new { title });
        Assert.True(response.Status == 201, "Шаг не создан: " + response.Status + " " + response.Body);
        return response.Body.GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> Steps(ApiClient client, int task) =>
        (await client.Get("assignments")).Body.EnumerateArray().Single(a => a.GetProperty("id").GetInt32() == task).GetProperty("subtasks");

    private static int[] Ids(JsonElement steps) => steps.EnumerateArray().Select(s => s.GetProperty("id").GetInt32()).ToArray();

    private static int[] Positions(JsonElement steps) => steps.EnumerateArray().Select(s => s.GetProperty("position").GetInt32()).ToArray();

    /// <summary>Группа с двумя студентами и старостой, одно задание группы.</summary>
    private static async Task<(TrackerFactory Factory, ApiClient Ann, ApiClient Bob, ApiClient Head, int Group, int Task)> Setup()
    {
        var factory = new TrackerFactory();
        var ann = await SignedIn(factory, Ann);
        var bob = await SignedIn(factory, Bob);
        var head = await SignedIn(factory, Head);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Ann);
        GroupSupport.Join(factory, group, Bob);
        GroupSupport.Join(factory, group, Head, GroupRole.Head);
        var (_, task) = GroupSupport.AddGroupTask(factory, group);
        return (factory, ann, bob, head, group, task);
    }

    [Fact]
    public async Task StudentAddsOwnStepsToGroupTask()
    {
        var (factory, ann, bob, head, _, task) = await Setup();
        await using var lifetime = factory;

        var created = await ann.Post($"assignments/{task}/subtasks", new { title = "Прочитать" });

        Assert.Equal(201, created.Status);
        Assert.False(created.Body.TryGetProperty("userId", out _));
        Assert.Equal(new[] { "Прочитать" }, (await Steps(ann, task)).EnumerateArray().Select(s => s.GetProperty("title").GetString()));
        Assert.Empty((await Steps(bob, task)).EnumerateArray());
        Assert.Empty((await Steps(head, task)).EnumerateArray());
        var ownerId = GroupSupport.UserId(factory, Ann);
        Assert.Equal(1, factory.Scope(s => s.GetRequiredService<TrackerDb>().Subtasks.Count(x => x.UserId == ownerId)));
    }

    [Fact]
    public async Task OtherStudentsStepIsNotFound()
    {
        var (factory, ann, bob, _, _, task) = await Setup();
        await using var lifetime = factory;
        var annStep = await AddStep(ann, task, "Шаг Ани");
        var bobStep = await AddStep(bob, task, "Шаг Бори");

        Assert.Equal(404, (await bob.Put($"assignments/{task}/subtasks/{annStep}", new { done = true })).Status);
        Assert.Equal(404, (await bob.Put($"assignments/{task}/subtasks/{annStep}", new { title = "Чужое" })).Status);
        Assert.Equal(404, (await bob.Delete($"assignments/{task}/subtasks/{annStep}")).Status);
        var order = await bob.Put($"assignments/{task}/subtasks/order", new { ids = new[] { annStep } });
        Assert.Equal(400, order.Status);
        Assert.Equal(400, (await bob.Put($"assignments/{task}/subtasks/order", new { ids = new[] { bobStep, annStep } })).Status);

        var kept = (await Steps(ann, task)).EnumerateArray().Single();
        Assert.Equal("Шаг Ани", kept.GetProperty("title").GetString());
        Assert.False(kept.GetProperty("done").GetBoolean());
        Assert.Equal(200, (await ann.Put($"assignments/{task}/subtasks/{annStep}", new { done = true })).Status);
        Assert.Equal(204, (await ann.Delete($"assignments/{task}/subtasks/{annStep}")).Status);
        Assert.Single((await Steps(bob, task)).EnumerateArray());
    }

    [Fact]
    public async Task LimitCountsOwnSteps()
    {
        var (factory, ann, bob, _, _, task) = await Setup();
        await using var lifetime = factory;
        for (var i = 0; i < SubtaskEndpoints.MaxSubtasks; i++) await AddStep(ann, task, $"Шаг {i}");

        var extra = await ann.Post($"assignments/{task}/subtasks", new { title = "Лишний" });

        Assert.Equal(400, extra.Status);
        Assert.Equal("У задания не больше 30 шагов.", extra.Body.GetProperty("error").GetString());
        Assert.Equal(201, (await bob.Post($"assignments/{task}/subtasks", new { title = "Первый у Бори" })).Status);
        Assert.Equal(SubtaskEndpoints.MaxSubtasks, (await Steps(ann, task)).GetArrayLength());
        Assert.Equal(1, (await Steps(bob, task)).GetArrayLength());
    }

    [Fact]
    public async Task OutsidersCannotAddSteps()
    {
        await using var factory = new TrackerFactory();
        var pending = await SignedIn(factory, "pending@example.test");
        var outsider = await SignedIn(factory, "outsider@example.test");
        var neighbour = await SignedIn(factory, "neighbour@example.test");
        var target = GroupSupport.CreateGroup(factory, "ИВТ-4");
        var other = GroupSupport.CreateGroup(factory, "ИВТ-3");
        GroupSupport.Join(factory, target, "pending@example.test", status: MemberStatus.Pending);
        GroupSupport.Join(factory, other, "neighbour@example.test");
        var (_, task) = GroupSupport.AddGroupTask(factory, target);

        foreach (var client in new[] { pending, outsider, neighbour })
        {
            Assert.Equal(404, (await client.Post($"assignments/{task}/subtasks", new { title = "Шаг" })).Status);
            Assert.Equal(404, (await client.Put($"assignments/{task}/subtasks/order", new { ids = new[] { 1 } })).Status);
        }
        Assert.Equal(0, factory.Scope(s => s.GetRequiredService<TrackerDb>().Subtasks.Count()));
    }

    [Fact]
    public async Task PositionsAreIndependent()
    {
        var (factory, ann, bob, _, _, task) = await Setup();
        await using var lifetime = factory;
        var a1 = await AddStep(ann, task, "А1");
        var b1 = await AddStep(bob, task, "Б1");
        var a2 = await AddStep(ann, task, "А2");
        var b2 = await AddStep(bob, task, "Б2");
        var a3 = await AddStep(ann, task, "А3");

        Assert.Equal(new[] { 0, 1, 2 }, Positions(await Steps(ann, task)));
        Assert.Equal(new[] { 0, 1 }, Positions(await Steps(bob, task)));

        Assert.Equal(204, (await ann.Delete($"assignments/{task}/subtasks/{a1}")).Status);
        Assert.Equal(new[] { a2, a3 }, Ids(await Steps(ann, task)));
        Assert.Equal(new[] { 0, 1 }, Positions(await Steps(ann, task)));
        Assert.Equal(new[] { b1, b2 }, Ids(await Steps(bob, task)));
        Assert.Equal(new[] { 0, 1 }, Positions(await Steps(bob, task)));

        var reordered = await bob.Put($"assignments/{task}/subtasks/order", new { ids = new[] { b2, b1 } });
        Assert.Equal(200, reordered.Status);
        Assert.Equal(new[] { b2, b1 }, Ids(reordered.Body));
        Assert.Equal(new[] { a2, a3 }, Ids(await Steps(ann, task)));
        Assert.Equal(new[] { 0, 1 }, Positions(await Steps(ann, task)));
    }

    [Fact]
    public async Task LeavingGroupRemovesSteps()
    {
        var (factory, ann, _, head, group, task) = await Setup();
        await using var lifetime = factory;
        await AddStep(ann, task, "Шаг");

        Assert.Equal(204, (await ann.Delete("group/membership")).Status);
        Assert.Equal(201, (await ann.Post($"groups/{group}/join")).Status);
        Assert.Equal(200, (await head.Post($"groups/{group}/members/{GroupSupport.UserId(factory, Ann)}/approve")).Status);

        Assert.Empty((await Steps(ann, task)).EnumerateArray());
        Assert.Equal(0, factory.Scope(s => s.GetRequiredService<TrackerDb>().Subtasks.Count()));
    }

    [Fact]
    public async Task HeadHasOwnStepsToo()
    {
        var (factory, ann, _, head, _, task) = await Setup();
        await using var lifetime = factory;

        await AddStep(head, task, "Шаг старосты");

        Assert.Single((await Steps(head, task)).EnumerateArray());
        Assert.Empty((await Steps(ann, task)).EnumerateArray());
    }
}
