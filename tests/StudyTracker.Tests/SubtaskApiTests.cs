using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class SubtaskApiTests
{
    private static async Task<ApiClient> SignedIn(TrackerFactory factory, string email = "owner@example.test")
    {
        var client = factory.NewClient();
        await client.SignIn(email);
        return client;
    }

    private static async Task<int> NewTask(ApiClient client, string subject = "Физика") =>
        (await client.CreateTask(await client.CreateSubject(subject)))["id"]!.GetValue<int>();

    private static async Task<int> AddStep(ApiClient client, int task, string title)
    {
        var response = await client.Post($"assignments/{task}/subtasks", new { title });
        Assert.True(response.Status == 201, "Шаг не создан: " + response.Body);
        return response.Body.GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> Steps(ApiClient client, int task)
    {
        var all = await client.Get("assignments");
        return all.Body.EnumerateArray().Single(a => a.GetProperty("id").GetInt32() == task).GetProperty("subtasks");
    }

    private static int[] Order(JsonElement steps) => steps.EnumerateArray().Select(s => s.GetProperty("id").GetInt32()).ToArray();

    private static int StoredCount(TrackerFactory factory) =>
        factory.Scope(services => services.GetRequiredService<TrackerDb>().Subtasks.Count());

    [Fact]
    public async Task AddSubtasksInOrder()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var task = await NewTask(client);

        var first = await client.Post($"assignments/{task}/subtasks", new { title = "Прочитать" });
        var second = await client.Post($"assignments/{task}/subtasks", new { title = "Посчитать" });
        var third = await client.Post($"assignments/{task}/subtasks", new { title = "Оформить" });

        Assert.All(new[] { first, second, third }, r => Assert.Equal(201, r.Status));
        Assert.Equal(new[] { 0, 1, 2 }, new[] { first, second, third }.Select(r => r.Body.GetProperty("position").GetInt32()));
        Assert.False(first.Body.GetProperty("done").GetBoolean());
        Assert.False(first.Body.TryGetProperty("assignmentId", out _));
        var steps = await Steps(client, task);
        Assert.Equal(new[] { "Прочитать", "Посчитать", "Оформить" },
            steps.EnumerateArray().Select(s => s.GetProperty("title").GetString()));
        Assert.All(steps.EnumerateArray(), s => Assert.False(s.GetProperty("done").GetBoolean()));
    }

    [Fact]
    public async Task RenameAndCheck()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var task = await NewTask(client);
        var step = await AddStep(client, task, "Старое");

        var checkedOff = await client.Put($"assignments/{task}/subtasks/{step}", new { done = true });
        Assert.Equal(200, checkedOff.Status);
        Assert.True(checkedOff.Body.GetProperty("done").GetBoolean());
        Assert.Equal("Старое", checkedOff.Body.GetProperty("title").GetString());

        var renamed = await client.Put($"assignments/{task}/subtasks/{step}", new { title = "  Новое  " });
        Assert.Equal(200, renamed.Status);
        Assert.Equal("Новое", renamed.Body.GetProperty("title").GetString());
        Assert.True(renamed.Body.GetProperty("done").GetBoolean());

        var empty = await client.Put($"assignments/{task}/subtasks/{step}", new { });
        Assert.Equal(400, empty.Status);
        Assert.Equal("Передайте название или отметку.", empty.Body.GetProperty("error").GetString());
        Assert.Equal(400, (await client.Put($"assignments/{task}/subtasks/{step}", new { done = "да" })).Status);
        Assert.Equal(400, (await client.Put($"assignments/{task}/subtasks/{step}", new { title = "   " })).Status);
        Assert.Equal("Новое", (await Steps(client, task))[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task TitleValidation()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var task = await NewTask(client);

        foreach (var title in new[] { "", "   " })
        {
            var blank = await client.Post($"assignments/{task}/subtasks", new { title });
            Assert.Equal(400, blank.Status);
            Assert.Equal("Введите название шага.", blank.Body.GetProperty("error").GetString());
        }
        var missing = await client.Post($"assignments/{task}/subtasks", new { });
        Assert.Equal(400, missing.Status);
        Assert.Equal("Введите название шага.", missing.Body.GetProperty("error").GetString());

        var tooLong = await client.Post($"assignments/{task}/subtasks", new { title = new string('я', 141) });
        Assert.Equal(400, tooLong.Status);
        Assert.Equal("Название шага не длиннее 140 символов.", tooLong.Body.GetProperty("error").GetString());
        Assert.Equal(201, (await client.Post($"assignments/{task}/subtasks", new { title = new string('я', 140) })).Status);
        Assert.Equal(201, (await client.Post($"assignments/{task}/subtasks", new { title = " " + new string('ю', 140) + " " })).Status);
        Assert.Equal(2, StoredCount(factory));

        var renameTooLong = await client.Put($"assignments/{task}/subtasks/{(await Steps(client, task))[0].GetProperty("id").GetInt32()}",
            new { title = new string('я', 141) });
        Assert.Equal(400, renameTooLong.Status);
    }

    [Fact]
    public async Task LimitIs30()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var task = await NewTask(client);

        for (var i = 1; i <= 30; i++) await AddStep(client, task, "Шаг " + i);
        var extra = await client.Post($"assignments/{task}/subtasks", new { title = "Лишний" });

        Assert.Equal(400, extra.Status);
        Assert.Equal("У задания не больше 30 шагов.", extra.Body.GetProperty("error").GetString());
        Assert.Equal(30, StoredCount(factory));
        var other = await NewTask(client, "Химия");
        Assert.Equal(201, (await client.Post($"assignments/{other}/subtasks", new { title = "Свой лимит" })).Status);
    }

    [Fact]
    public async Task DeleteRenumbers()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var task = await NewTask(client);
        var a = await AddStep(client, task, "А");
        var b = await AddStep(client, task, "Б");
        var c = await AddStep(client, task, "В");

        Assert.Equal(204, (await client.Delete($"assignments/{task}/subtasks/{b}")).Status);

        var steps = await Steps(client, task);
        Assert.Equal(new[] { a, c }, Order(steps));
        Assert.Equal(new[] { 0, 1 }, steps.EnumerateArray().Select(s => s.GetProperty("position").GetInt32()));
        Assert.Equal(404, (await client.Delete($"assignments/{task}/subtasks/{b}")).Status);
        var next = await client.Post($"assignments/{task}/subtasks", new { title = "Г" });
        Assert.Equal(2, next.Body.GetProperty("position").GetInt32());
    }

    [Fact]
    public async Task ReorderNeedsExactIds()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var task = await NewTask(client);
        var a = await AddStep(client, task, "А");
        var b = await AddStep(client, task, "Б");
        var c = await AddStep(client, task, "В");
        var foreign = await AddStep(client, await NewTask(client, "Химия"), "Чужой шаг");

        var reordered = await client.Put($"assignments/{task}/subtasks/order", new { ids = new[] { c, a, b } });
        Assert.Equal(200, reordered.Status);
        Assert.Equal(new[] { c, a, b }, Order(reordered.Body));
        Assert.Equal(new[] { 0, 1, 2 }, reordered.Body.EnumerateArray().Select(s => s.GetProperty("position").GetInt32()));
        Assert.Equal(new[] { c, a, b }, Order(await Steps(client, task)));

        var bad = new object?[]
        {
            new { ids = new[] { c, a } },
            new { ids = new[] { c, a, b, foreign } },
            new { ids = new[] { c, c, a } },
            new { ids = new[] { c, a, foreign } },
            new { ids = Array.Empty<int>() },
            new { ids = (int[]?)null },
            new { },
        };
        foreach (var body in bad)
        {
            var response = await client.Put($"assignments/{task}/subtasks/order", body);
            Assert.Equal(400, response.Status);
            Assert.Equal("Укажите все шаги задания по одному разу.", response.Body.GetProperty("error").GetString());
            Assert.Equal(new[] { c, a, b }, Order(await Steps(client, task)));
        }
    }

    [Fact]
    public async Task OtherAccountGets404()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var task = await NewTask(owner);
        var step = await AddStep(owner, task, "Мой шаг");
        var stranger = await SignedIn(factory, "stranger@example.test");
        var own = await NewTask(stranger, "Своё");
        var ownStep = await AddStep(stranger, own, "Свой шаг");

        Assert.Equal(404, (await stranger.Post($"assignments/{task}/subtasks", new { title = "Подсунуть" })).Status);
        Assert.Equal(404, (await stranger.Put($"assignments/{task}/subtasks/{step}", new { done = true })).Status);
        Assert.Equal(404, (await stranger.Delete($"assignments/{task}/subtasks/{step}")).Status);
        Assert.Equal(404, (await stranger.Put($"assignments/{task}/subtasks/order", new { ids = new[] { step } })).Status);
        // Шаг одного своего задания через адрес другого своего задания.
        Assert.Equal(404, (await stranger.Put($"assignments/{own}/subtasks/{step}", new { done = true })).Status);
        Assert.Equal(404, (await owner.Put($"assignments/{task}/subtasks/{ownStep}", new { done = true })).Status);
        Assert.Equal(404, (await owner.Delete($"assignments/{task}/subtasks/{ownStep}")).Status);

        var steps = await Steps(owner, task);
        Assert.Equal(new[] { step }, Order(steps));
        Assert.False(steps[0].GetProperty("done").GetBoolean());
        Assert.Equal(2, StoredCount(factory));
    }

    [Fact]
    public async Task DeletingTaskDeletesSubtasks()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var task = await NewTask(client);
        await AddStep(client, task, "А");
        await AddStep(client, task, "Б");
        Assert.Equal(2, StoredCount(factory));

        Assert.Equal(204, (await client.Delete($"assignments/{task}")).Status);

        Assert.Equal(0, StoredCount(factory));
    }

    [Fact]
    public async Task EditingTaskKeepsSubtasks()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var subject = await client.CreateSubject("Физика");
        var task = (await client.CreateTask(subject))["id"]!.GetValue<int>();
        var step = await AddStep(client, task, "Остаётся");
        await client.Put($"assignments/{task}/subtasks/{step}", new { done = true });

        var edited = await client.Put($"assignments/{task}", new
        { title = "Новое название", description = "", subjectId = subject, dueDate = "2026-12-01", status = "InProgress" });

        Assert.Equal(200, edited.Status);
        var inAnswer = Assert.Single(edited.Body.GetProperty("subtasks").EnumerateArray());
        Assert.Equal("Остаётся", inAnswer.GetProperty("title").GetString());
        Assert.True(inAnswer.GetProperty("done").GetBoolean());
        Assert.Equal(new[] { step }, Order(await Steps(client, task)));
    }

    [Fact]
    public async Task NewTaskComesWithEmptySubtasks()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);

        var created = await client.CreateTask(await client.CreateSubject("Физика"));

        Assert.Empty(created["subtasks"]!.AsArray());
    }

    [Fact]
    public async Task RequiresLoginAndCsrf()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var task = await NewTask(owner);
        var step = await AddStep(owner, task, "Шаг");
        var anonymous = factory.NewClient();
        var token = await owner.Post("tokens", new { name = "Скрипт", scopes = new[] { "tasks:read", "tasks:write" } });
        var bearer = factory.NewClient();
        bearer.UseBearer(token.Body.GetProperty("token").GetString()!);

        foreach (var client in new[] { anonymous, bearer })
        {
            Assert.Equal(401, (await client.Post($"assignments/{task}/subtasks", new { title = "Х" }, csrf: false)).Status);
            Assert.Equal(401, (await client.Put($"assignments/{task}/subtasks/{step}", new { done = true }, csrf: false)).Status);
            Assert.Equal(401, (await client.Put($"assignments/{task}/subtasks/order", new { ids = new[] { step } }, csrf: false)).Status);
            Assert.Equal(401, (await client.Delete($"assignments/{task}/subtasks/{step}", csrf: false)).Status);
        }

        Assert.Equal(400, (await owner.Post($"assignments/{task}/subtasks", new { title = "Х" }, csrf: false)).Status);
        Assert.Equal(400, (await owner.Put($"assignments/{task}/subtasks/{step}", new { done = true }, csrf: false)).Status);
        Assert.Equal(400, (await owner.Delete($"assignments/{task}/subtasks/{step}", csrf: false)).Status);
        Assert.Equal(1, StoredCount(factory));
        Assert.False((await Steps(owner, task))[0].GetProperty("done").GetBoolean());
    }
}
