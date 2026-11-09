using Microsoft.Extensions.DependencyInjection;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class PriorityApiTests
{
    private static async Task<ApiClient> SignedIn(TrackerFactory factory, string email = "owner@example.test")
    {
        var client = factory.NewClient();
        await client.SignIn(email);
        return client;
    }

    private static object Body(int subject, string? priority = null) => priority is null
        ? new { title = "Задание", description = "", subjectId = subject, dueDate = "2026-11-12", status = "Todo" }
        : new { title = "Задание", description = "", subjectId = subject, dueDate = "2026-11-12", status = "Todo", priority };

    private static string Priority(ApiResponse response) => response.Body.GetProperty("priority").GetString()!;

    [Fact]
    public async Task PriorityDefaultsToNormal()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);

        var created = await client.Post("assignments", Body(await client.CreateSubject("Физика")));

        Assert.Equal(201, created.Status);
        Assert.Equal("Normal", Priority(created));
        Assert.Equal("Normal", (await client.Get("assignments")).Body[0].GetProperty("priority").GetString());
    }

    [Fact]
    public async Task PriorityRoundTrip()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var subject = await client.CreateSubject("Физика");

        var created = await client.Post("assignments", Body(subject, "Urgent"));
        Assert.Equal("Urgent", Priority(created));
        var id = created.Body.GetProperty("id").GetInt32();

        var changed = await client.Put($"assignments/{id}", Body(subject, "Important"));
        Assert.Equal(200, changed.Status);
        Assert.Equal("Important", Priority(changed));
        var listed = (await client.Get("assignments")).Body[0];
        Assert.Equal("Important", listed.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task PriorityKeptWhenOmitted()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var subject = await client.CreateSubject("Физика");
        var id = (await client.Post("assignments", Body(subject, "Urgent"))).Body.GetProperty("id").GetInt32();

        var edited = await client.Put($"assignments/{id}", Body(subject));

        Assert.Equal(200, edited.Status);
        Assert.Equal("Urgent", Priority(edited));
        Assert.Equal("Urgent", (await client.Get("assignments")).Body[0].GetProperty("priority").GetString());
    }

    [Fact]
    public async Task BadPriorityIs400()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var subject = await client.CreateSubject("Физика");
        var id = (await client.Post("assignments", Body(subject, "Important"))).Body.GetProperty("id").GetInt32();

        var epic = await client.Post("assignments", Body(subject, "Epic"));
        var seven = await client.Post("assignments", new
        { title = "Задание", description = "", subjectId = subject, dueDate = "2026-11-12", status = "Todo", priority = 7 });
        var editEpic = await client.Put($"assignments/{id}", Body(subject, "Epic"));

        Assert.Equal(400, epic.Status);
        Assert.Equal(400, seven.Status);
        Assert.Equal(400, editEpic.Status);
        Assert.Equal("Выберите важность из списка.", seven.Body.GetProperty("error").GetString());
        var all = (await client.Get("assignments")).Body;
        Assert.Equal(1, all.GetArrayLength());
        Assert.Equal("Important", all[0].GetProperty("priority").GetString());
    }

    [Fact]
    public async Task V1ShowsSubtasksAndPriority()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var subject = await owner.CreateSubject("Физика");
        var task = (await owner.Post("assignments", Body(subject, "Urgent"))).Body.GetProperty("id").GetInt32();
        await owner.Post($"assignments/{task}/subtasks", new { title = "Шаг" });
        var bearer = Bearer(factory, await Issue(owner));

        var listed = await bearer.Get("v1/assignments");

        Assert.Equal(200, listed.Status);
        var first = listed.Body[0];
        Assert.Equal("Urgent", first.GetProperty("priority").GetString());
        Assert.Equal("Шаг", Assert.Single(first.GetProperty("subtasks").EnumerateArray()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task V1SetsPriorityButCannotEditSubtasks()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory);
        var subject = await owner.CreateSubject("Физика");
        var bearer = Bearer(factory, await Issue(owner));

        var created = await bearer.Post("v1/assignments", Body(subject, "Important"), csrf: false);
        Assert.Equal(201, created.Status);
        Assert.Equal("Important", Priority(created));
        var id = created.Body.GetProperty("id").GetInt32();
        var edited = await bearer.Put($"v1/assignments/{id}", Body(subject, "Urgent"), csrf: false);
        Assert.Equal("Urgent", Priority(edited));

        var step = await bearer.Post($"v1/assignments/{id}/subtasks", new { title = "Шаг" }, csrf: false);

        Assert.Equal(404, step.Status);
        Assert.Equal(0, factory.Scope(services => services.GetRequiredService<TrackerDb>().Subtasks.Count()));
    }

    [Fact]
    public async Task ImportKeepsNormalPriority()
    {
        await using var factory = new TrackerFactory();
        var client = await SignedIn(factory);
        var note = ObsidianSamples.Note("Физика", "Лаба физика", "2026-11-20", "false", "s-1", "Описание");

        var imported = await client.Post("import/obsidian", new { files = new[] { new { path = "ДЗ/Физика/Лаба.md", text = note } } });

        Assert.Equal(200, imported.Status);
        Assert.Equal(1, imported.Body.GetProperty("newCount").GetInt32());
        var listed = (await client.Get("assignments")).Body;
        Assert.Equal("Normal", Assert.Single(listed.EnumerateArray()).GetProperty("priority").GetString());
        Assert.Empty(listed[0].GetProperty("subtasks").EnumerateArray());
    }

    private static async Task<string> Issue(ApiClient owner)
    {
        var created = await owner.Post("tokens", new { name = "Скрипт", scopes = new[] { "tasks:read", "tasks:write" } });
        Assert.Equal(201, created.Status);
        return created.Body.GetProperty("token").GetString()!;
    }

    private static ApiClient Bearer(TrackerFactory factory, string token)
    {
        var client = factory.NewClient();
        client.UseBearer(token);
        return client;
    }
}
