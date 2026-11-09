using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class GroupAssignmentTests
{
    private const string Head = "head@example.test";
    private const string Ann = "ann@example.test";
    private const string Bob = "bob@example.test";
    private const string Rival = "rival@example.test";
    private const string Moved = "Нельзя перенести задание между личным списком и группой.";

    private static async Task<ApiClient> SignedIn(TrackerFactory factory, string email)
    {
        var client = factory.NewClient();
        await client.SignIn(email);
        return client;
    }

    private static object Body(string title, int subjectId, int? groupId = null, string status = "Todo", string due = "2026-11-20") =>
        new { title, description = "", subjectId, dueDate = due, status, groupId };

    private static List<JsonElement> Items(ApiResponse response)
    {
        Assert.Equal(200, response.Status);
        return response.Body.EnumerateArray().ToList();
    }

    private static JsonElement Find(List<JsonElement> items, int id) => items.Single(i => i.GetProperty("id").GetInt32() == id);

    private static int? GroupOf(JsonElement item) =>
        item.GetProperty("groupId") is { ValueKind: JsonValueKind.Number } group ? group.GetInt32() : null;

    private static string StatusOf(List<JsonElement> items, int id) => Find(items, id).GetProperty("status").GetString()!;

    private static int Count(TrackerFactory factory, Func<TrackerDb, int> count) =>
        factory.Scope(services => count(services.GetRequiredService<TrackerDb>()));

    [Fact]
    public async Task HeadCreatesGroupSubjectAndTask()
    {
        await using var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        var ann = await SignedIn(factory, Ann);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Head, GroupRole.Head);
        GroupSupport.Join(factory, group, Ann);
        var personalHead = await head.CreateSubject("Личное старосты");
        var personalAnn = await ann.CreateSubject("Личное студента");
        await head.CreateTask(personalHead);

        var subject = await head.Post("subjects", new { name = "Физика", color = "blue", groupId = group });
        Assert.Equal(201, subject.Status);
        Assert.Equal(group, subject.Body.GetProperty("groupId").GetInt32());
        var subjectId = subject.Body.GetProperty("id").GetInt32();
        var task = await head.Post("assignments", Body("Лаба", subjectId, group));

        Assert.Equal(201, task.Status);
        Assert.Equal(group, task.Body.GetProperty("groupId").GetInt32());
        var taskId = task.Body.GetProperty("id").GetInt32();
        foreach (var client in new[] { head, ann })
        {
            var subjects = Items(await client.Get("subjects"));
            Assert.Equal(group, GroupOf(Find(subjects, subjectId)));
            var tasks = Items(await client.Get("assignments"));
            Assert.Equal(group, GroupOf(Find(tasks, taskId)));
        }
        var headSubjects = Items(await head.Get("subjects"));
        Assert.Null(GroupOf(Find(headSubjects, personalHead)));
        Assert.Equal(2, headSubjects.Count);
        Assert.Equal(2, Items(await head.Get("assignments")).Count);
        var annSubjects = Items(await ann.Get("subjects"));
        Assert.Null(GroupOf(Find(annSubjects, personalAnn)));
        Assert.Equal(2, annSubjects.Count);
        Assert.Single(Items(await ann.Get("assignments")));
    }

    [Fact]
    public async Task StudentCannotCreateOrEditGroupItems()
    {
        await using var factory = new TrackerFactory();
        var ann = await SignedIn(factory, Ann);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Ann);
        var (subjectId, taskId) = GroupSupport.AddGroupTask(factory, group);

        Assert.Equal(404, (await ann.Post("subjects", new { name = "Химия", color = "teal", groupId = group })).Status);
        Assert.Equal(404, (await ann.Post("assignments", Body("Новое", subjectId, group))).Status);
        Assert.Equal(404, (await ann.Put($"assignments/{taskId}", Body("Правка", subjectId, group))).Status);
        Assert.Equal(404, (await ann.Delete($"assignments/{taskId}")).Status);
        Assert.Equal(404, (await ann.Delete($"subjects/{subjectId}")).Status);

        Assert.Equal(1, Count(factory, db => db.Subjects.Count(s => s.GroupId == group)));
        Assert.Equal(1, Count(factory, db => db.Assignments.Count(a => a.GroupId == group && a.Title == "Лабораторная")));
    }

    [Fact]
    public async Task HeadOfOtherGroupCannotTouchGroupItems()
    {
        await using var factory = new TrackerFactory();
        var rival = await SignedIn(factory, Rival);
        var mine = GroupSupport.CreateGroup(factory, "ИВТ-3");
        var theirs = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, mine, Rival, GroupRole.Head);
        var (subjectId, taskId) = GroupSupport.AddGroupTask(factory, theirs);

        Assert.Equal(404, (await rival.Post("subjects", new { name = "Химия", color = "teal", groupId = theirs })).Status);
        Assert.Equal(404, (await rival.Post("assignments", Body("Новое", subjectId, theirs))).Status);
        Assert.Equal(404, (await rival.Put($"assignments/{taskId}", Body("Правка", subjectId, theirs))).Status);
        Assert.Equal(404, (await rival.Delete($"assignments/{taskId}")).Status);
        Assert.Equal(404, (await rival.Delete($"subjects/{subjectId}")).Status);
        Assert.Equal(404, (await rival.Put($"assignments/{taskId}/progress", new { status = "Done" })).Status);

        Assert.Equal(1, Count(factory, db => db.Assignments.Count(a => a.GroupId == theirs && a.Title == "Лабораторная")));
        Assert.Equal(0, Count(factory, db => db.GroupProgress.Count()));
    }

    [Fact]
    public async Task GroupTaskNeedsSubjectOfSameGroup()
    {
        await using var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        var mine = GroupSupport.CreateGroup(factory, "ИВТ-4");
        var other = GroupSupport.CreateGroup(factory, "ИВТ-3");
        GroupSupport.Join(factory, mine, Head, GroupRole.Head);
        var (ownSubject, ownTask) = GroupSupport.AddGroupTask(factory, mine);
        var (otherSubject, _) = GroupSupport.AddGroupTask(factory, other, "Химия");
        var personal = await head.CreateSubject("Личное");

        Assert.Equal(404, (await head.Post("assignments", Body("Чужой предмет", otherSubject, mine))).Status);
        Assert.Equal(404, (await head.Post("assignments", Body("Личный предмет", personal, mine))).Status);
        Assert.Equal(404, (await head.Post("assignments", Body("Нет предмета", 9999, mine))).Status);
        Assert.Equal(404, (await head.Post("assignments", Body("Личное с групповым предметом", ownSubject))).Status);
        Assert.Equal(404, (await head.Put($"assignments/{ownTask}", Body("Перенос", otherSubject, mine))).Status);
        Assert.Equal(404, (await head.Put($"assignments/{ownTask}", Body("Перенос", personal, mine))).Status);

        Assert.Equal(1, Count(factory, db => db.Assignments.Count(a => a.GroupId == mine)));
        Assert.Equal(0, Count(factory, db => db.Assignments.Count(a => a.OwnerId != null)));
        Assert.Equal(201, (await head.Post("assignments", Body("Свой предмет", ownSubject, mine))).Status);
    }

    [Fact]
    public async Task GroupSubjectNamesAreUniquePerGroup()
    {
        await using var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        var mine = GroupSupport.CreateGroup(factory, "ИВТ-4");
        var other = GroupSupport.CreateGroup(factory, "ИВТ-3");
        GroupSupport.Join(factory, mine, Head, GroupRole.Head);
        GroupSupport.AddGroupTask(factory, other, "Физика");

        Assert.Equal(201, (await head.Post("subjects", new { name = "Физика", color = "blue", groupId = mine })).Status);
        var again = await head.Post("subjects", new { name = "физика", color = "teal", groupId = mine });

        Assert.Equal(409, again.Status);
        Assert.Equal("Такой предмет уже есть.", again.Body.GetProperty("error").GetString());
        Assert.Equal(201, (await head.Post("subjects", new { name = "Физика", color = "blue" })).Status);
        Assert.Equal(1, Count(factory, db => db.Subjects.Count(s => s.GroupId == mine)));
        Assert.Equal(1, Count(factory, db => db.Subjects.Count(s => s.GroupId == other)));
    }

    [Fact]
    public async Task PendingAndOutsidersSeeNoGroupItems()
    {
        await using var factory = new TrackerFactory();
        var pending = await SignedIn(factory, Ann);
        var outsider = await SignedIn(factory, Bob);
        var neighbour = await SignedIn(factory, Rival);
        var mine = GroupSupport.CreateGroup(factory, "ИВТ-4");
        var other = GroupSupport.CreateGroup(factory, "ИВТ-3");
        GroupSupport.Join(factory, mine, Ann, status: MemberStatus.Pending);
        GroupSupport.Join(factory, other, Rival);
        GroupSupport.AddGroupTask(factory, mine);

        Assert.Empty(Items(await pending.Get("subjects")));
        Assert.Empty(Items(await pending.Get("assignments")));
        Assert.Empty(Items(await outsider.Get("subjects")));
        Assert.Empty(Items(await outsider.Get("assignments")));
        Assert.Empty(Items(await neighbour.Get("subjects")));
        Assert.Empty(Items(await neighbour.Get("assignments")));
    }

    [Fact]
    public async Task ProgressIsPerStudent()
    {
        await using var factory = new TrackerFactory();
        var ann = await SignedIn(factory, Ann);
        var bob = await SignedIn(factory, Bob);
        var head = await SignedIn(factory, Head);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Ann);
        GroupSupport.Join(factory, group, Bob);
        GroupSupport.Join(factory, group, Head, GroupRole.Head);
        var (_, task) = GroupSupport.AddGroupTask(factory, group);

        var done = await ann.Put($"assignments/{task}/progress", new { status = "Done" });

        Assert.Equal(200, done.Status);
        Assert.Equal("Done", done.Body.GetProperty("status").GetString());
        Assert.Equal(task, done.Body.GetProperty("id").GetInt32());
        Assert.Equal("Done", StatusOf(Items(await ann.Get("assignments")), task));
        Assert.Equal("Todo", StatusOf(Items(await bob.Get("assignments")), task));
        Assert.Equal("Todo", StatusOf(Items(await head.Get("assignments")), task));

        Assert.Equal(200, (await ann.Put($"assignments/{task}/progress", new { status = "InProgress" })).Status);
        Assert.Equal("InProgress", StatusOf(Items(await ann.Get("assignments")), task));
        Assert.Equal(1, Count(factory, db => db.GroupProgress.Count(p => p.AssignmentId == task)));

        Assert.Equal(200, (await head.Put($"assignments/{task}/progress", new { status = "Done" })).Status);
        Assert.Equal("Done", StatusOf(Items(await head.Get("assignments")), task));
        Assert.Equal("Todo", StatusOf(Items(await bob.Get("assignments")), task));
        Assert.Equal(2, Count(factory, db => db.GroupProgress.Count(p => p.AssignmentId == task)));
        Assert.Equal(AssignmentStatus.Todo, factory.Scope(s => s.GetRequiredService<TrackerDb>().Assignments.Single(a => a.Id == task).Status));
    }

    [Fact]
    public async Task ProgressRejectsPersonalAndOutsiders()
    {
        await using var factory = new TrackerFactory();
        var ann = await SignedIn(factory, Ann);
        var bob = await SignedIn(factory, Bob);
        var pending = await SignedIn(factory, Rival);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Ann);
        GroupSupport.Join(factory, group, Rival, status: MemberStatus.Pending);
        var (_, task) = GroupSupport.AddGroupTask(factory, group);
        var personal = (await ann.CreateTask(await ann.CreateSubject("Личное")))["id"]!.GetValue<int>();

        Assert.Equal(404, (await ann.Put($"assignments/{personal}/progress", new { status = "Done" })).Status);
        Assert.Equal(404, (await bob.Put($"assignments/{task}/progress", new { status = "Done" })).Status);
        Assert.Equal(404, (await pending.Put($"assignments/{task}/progress", new { status = "Done" })).Status);
        Assert.Equal(404, (await ann.Put("assignments/9999/progress", new { status = "Done" })).Status);
        Assert.Equal(400, (await ann.Put($"assignments/{task}/progress", new { status = "Finished" })).Status);
        Assert.Equal(400, (await ann.Put($"assignments/{task}/progress", new { status = 7 })).Status);
        Assert.Equal(400, (await ann.Put($"assignments/{task}/progress", new { })).Status);
        Assert.Equal(0, Count(factory, db => db.GroupProgress.Count()));
        Assert.Equal(AssignmentStatus.Todo, factory.Scope(s => s.GetRequiredService<TrackerDb>().Assignments.Single(a => a.Id == personal).Status));
    }

    [Fact]
    public async Task PutKeepsProgressAndIgnoresStatus()
    {
        await using var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        await SignedIn(factory, Ann);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Head, GroupRole.Head);
        GroupSupport.Join(factory, group, Ann);
        var (subject, task) = GroupSupport.AddGroupTask(factory, group);
        GroupSupport.AddProgress(factory, task, Ann, AssignmentStatus.Done);

        var response = await head.Put($"assignments/{task}", Body("Новое название", subject, group, "Done", "2026-12-01"));

        Assert.Equal(200, response.Status);
        Assert.Equal("Новое название", response.Body.GetProperty("title").GetString());
        Assert.Equal("2026-12-01", response.Body.GetProperty("dueDate").GetString());
        Assert.Equal("Todo", response.Body.GetProperty("status").GetString());
        Assert.Equal(AssignmentStatus.Todo, factory.Scope(s => s.GetRequiredService<TrackerDb>().Assignments.Single(a => a.Id == task).Status));
        Assert.Equal(AssignmentStatus.Done, factory.Scope(s => s.GetRequiredService<TrackerDb>().GroupProgress.Single().Status));
        var withoutGroupId = await head.Put($"assignments/{task}", new { title = "Без поля группы", description = "", subjectId = subject, dueDate = "2026-12-02", status = "Todo" });
        Assert.Equal(200, withoutGroupId.Status);
        Assert.Equal(group, withoutGroupId.Body.GetProperty("groupId").GetInt32());
    }

    [Fact]
    public async Task MovingBetweenScopesIsRejected()
    {
        await using var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        var mine = GroupSupport.CreateGroup(factory, "ИВТ-4");
        var other = GroupSupport.CreateGroup(factory, "ИВТ-3");
        GroupSupport.Join(factory, mine, Head, GroupRole.Head);
        var (subject, task) = GroupSupport.AddGroupTask(factory, mine);
        var personalSubject = await head.CreateSubject("Личное");
        var personal = (await head.CreateTask(personalSubject))["id"]!.GetValue<int>();

        foreach (var response in new[]
        {
            await head.Put($"assignments/{task}", Body("Из группы в другую", subject, other)),
            await head.Put($"assignments/{personal}", Body("Из личного в группу", personalSubject, mine)),
        })
        {
            Assert.Equal(400, response.Status);
            Assert.Equal(Moved, response.Body.GetProperty("error").GetString());
        }
        Assert.Equal(mine, Count(factory, db => db.Assignments.Single(a => a.Id == task).GroupId ?? 0));
        Assert.Null(factory.Scope(s => s.GetRequiredService<TrackerDb>().Assignments.Single(a => a.Id == personal).GroupId));
    }

    [Fact]
    public async Task DeletingGroupTaskRemovesProgress()
    {
        await using var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        await SignedIn(factory, Ann);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Head, GroupRole.Head);
        GroupSupport.Join(factory, group, Ann);
        var (subject, task) = GroupSupport.AddGroupTask(factory, group);
        GroupSupport.AddProgress(factory, task, Ann, AssignmentStatus.Done);
        GroupSupport.AddStep(factory, task, Ann);

        Assert.Equal(409, (await head.Delete($"subjects/{subject}")).Status);
        Assert.Equal(204, (await head.Delete($"assignments/{task}")).Status);

        Assert.Equal(0, Count(factory, db => db.GroupProgress.Count()));
        Assert.Equal(0, Count(factory, db => db.Subtasks.Count()));
        Assert.Equal(204, (await head.Delete($"subjects/{subject}")).Status);
        Assert.Equal(0, Count(factory, db => db.Subjects.Count()));
    }

    [Fact]
    public async Task GroupItemsStayOutOfPersonalSurfaces()
    {
        await using var factory = new TrackerFactory();
        var ann = await SignedIn(factory, Ann);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Ann);
        var (_, groupTask) = GroupSupport.AddGroupTask(factory, group, title: "Групповая лаба");
        var personalSubject = await ann.CreateSubject("Личное");
        await ann.CreateTask(personalSubject);
        var token = (await ann.Post("tokens", new { name = "Скрипт", scopes = new[] { "tasks:read", "tasks:write" }, days = 30 }))
            .Body.GetProperty("token").GetString()!;
        var bearer = factory.NewClient();
        bearer.UseBearer(token);

        var v1Tasks = Items(await bearer.Get("v1/assignments"));
        var v1Subjects = Items(await bearer.Get("v1/subjects"));
        Assert.Single(v1Tasks);
        Assert.DoesNotContain(v1Tasks, t => t.GetProperty("id").GetInt32() == groupTask);
        Assert.Single(v1Subjects);
        var rejected = await bearer.Post("v1/assignments", Body("Из токена", personalSubject, group));
        Assert.Equal(400, rejected.Status);
        Assert.Equal("В токене API группы не поддерживаются.", rejected.Body.GetProperty("error").GetString());
        var rejectedSubject = await bearer.Post("v1/subjects", new { name = "Химия", color = "blue", groupId = group });
        Assert.Equal(400, rejectedSubject.Status);
        Assert.Equal("В токене API группы не поддерживаются.", rejectedSubject.Body.GetProperty("error").GetString());
        Assert.Equal(404, (await bearer.Put($"v1/assignments/{groupTask}", Body("Правка", personalSubject))).Status);
        Assert.Equal(404, (await bearer.Delete($"v1/assignments/{groupTask}")).Status);
        Assert.Equal(1, Count(factory, db => db.Assignments.Count(a => a.GroupId == group)));

        var key = (await ann.Post("calendar", new { })).Body.GetProperty("key").GetString()!;
        using var http = factory.CreateClient();
        var feed = await http.GetStringAsync("/calendar.ics?key=" + key);
        Assert.Contains("Лабораторная № 2", feed);
        Assert.DoesNotContain("SUMMARY:Лабораторная\r", feed.Replace("\n", "\r"));

        var note = ObsidianSamples.Note("Физика", "Лаба физика", "2026-11-20", "false", "s-1");
        var import = await ann.Post("import/obsidian", new { files = new[] { new { path = "ДЗ/Физика/Лаба.md", text = note } }, defaultDue = (string?)null });
        Assert.Equal(200, import.Status);
        Assert.Equal(1, Count(factory, db => db.Assignments.Count(a => a.GroupId == group)));
        Assert.Equal(1, Count(factory, db => db.Subjects.Count(s => s.GroupId == group)));
        Assert.Equal(2, Count(factory, db => db.Subjects.Count(s => s.OwnerId != null)));
    }

    [Fact]
    public async Task RoleLossHidesGroupInstantly()
    {
        await using var factory = new TrackerFactory();
        var head = await SignedIn(factory, Head);
        var ann = await SignedIn(factory, Ann);
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, Head, GroupRole.Head);
        GroupSupport.Join(factory, group, Ann);
        var (subject, task) = GroupSupport.AddGroupTask(factory, group);
        Assert.Equal(201, (await head.Post("assignments", Body("Пока староста", subject, group))).Status);
        Assert.Equal(2, Items(await ann.Get("assignments")).Count);

        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            db.GroupMembers.Single(m => m.Role == GroupRole.Head).Role = GroupRole.Student;
            db.SaveChanges();
            return 0;
        });
        Assert.Equal(404, (await head.Post("assignments", Body("Уже студент", subject, group))).Status);
        Assert.Equal(404, (await head.Post("subjects", new { name = "Химия", color = "blue", groupId = group })).Status);
        Assert.Equal(404, (await head.Delete($"assignments/{task}")).Status);

        Assert.Equal(204, (await ann.Delete("group/membership")).Status);
        Assert.Empty(Items(await ann.Get("assignments")));
        Assert.Empty(Items(await ann.Get("subjects")));
    }

    [Fact]
    public async Task OwnerWithoutMembershipCannotCreateGroupItems()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory, "owner@example.test");
        GroupSupport.MakeOwner(factory, "owner@example.test");
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        var (subject, task) = GroupSupport.AddGroupTask(factory, group);

        Assert.Equal(404, (await owner.Post("subjects", new { name = "Химия", color = "blue", groupId = group })).Status);
        Assert.Equal(404, (await owner.Post("assignments", Body("Новое", subject, group))).Status);
        Assert.Equal(404, (await owner.Put($"assignments/{task}", Body("Правка", subject, group))).Status);
        Assert.Equal(404, (await owner.Delete($"assignments/{task}")).Status);
        Assert.Equal(404, (await owner.Put($"assignments/{task}/progress", new { status = "Done" })).Status);
        Assert.Empty(Items(await owner.Get("assignments")));
    }

    [Fact]
    public async Task OwnerWhoIsHeadCan()
    {
        await using var factory = new TrackerFactory();
        var owner = await SignedIn(factory, "owner@example.test");
        GroupSupport.MakeOwner(factory, "owner@example.test");
        var group = GroupSupport.CreateGroup(factory, "ИВТ-4");
        GroupSupport.Join(factory, group, "owner@example.test", GroupRole.Head);

        var subject = await owner.Post("subjects", new { name = "Физика", color = "blue", groupId = group });
        Assert.Equal(201, subject.Status);
        var task = await owner.Post("assignments", Body("Лаба", subject.Body.GetProperty("id").GetInt32(), group));
        Assert.Equal(201, task.Status);
        Assert.Equal(204, (await owner.Delete($"assignments/{task.Body.GetProperty("id").GetInt32()}")).Status);
    }
}
