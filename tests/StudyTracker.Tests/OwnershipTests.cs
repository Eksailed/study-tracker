using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class OwnershipTests
{
    private static async Task<(ApiClient A, ApiClient B)> TwoUsers(TrackerFactory factory)
    {
        var a = factory.NewClient();
        var b = factory.NewClient();
        await a.SignIn("a@example.test");
        await b.SignIn("b@example.test");
        return (a, b);
    }

    private static Task<JsonObject> NewTask(ApiClient client, int subject) => client.CreateTask(subject, new JsonObject
    {
        ["title"] = "Контрольная", ["description"] = "Подготовиться", ["dueDate"] = "2026-12-10", ["status"] = "InProgress",
    });

    [Theory]
    [InlineData("assignments", "GET", false)]
    [InlineData("subjects", "GET", false)]
    [InlineData("assignments", "POST", true)]
    [InlineData("assignments/1", "PUT", true)]
    [InlineData("assignments/1", "DELETE", false)]
    [InlineData("subjects/1", "DELETE", false)]
    public async Task AnonymousCannotAccessAssignments(string path, string method, bool withBody)
    {
        await using var factory = new TrackerFactory();
        var anonymous = factory.NewClient();

        var response = await anonymous.Send(new HttpMethod(method), path, withBody ? new { } : null);

        Assert.Equal(401, response.Status);
    }

    [Fact]
    public async Task UsersSeeOnlyOwnRecords()
    {
        await using var factory = new TrackerFactory();
        var (a, b) = await TwoUsers(factory);
        await NewTask(a, await a.CreateSubject("Математика"));

        Assert.Equal(0, (await b.Get("subjects")).Body.GetArrayLength());
        Assert.Equal(0, (await b.Get("assignments")).Body.GetArrayLength());
        Assert.Equal(1, (await a.Get("assignments")).Body.GetArrayLength());
    }

    [Fact]
    public async Task ForeignAssignmentCannotBeChangedOrDeleted()
    {
        await using var factory = new TrackerFactory();
        var (a, b) = await TwoUsers(factory);
        var subject = await a.CreateSubject("Математика");
        var task = await NewTask(a, subject);

        task["title"] = "Чужое";
        Assert.Equal(404, (await b.Put($"assignments/{task["id"]}", task)).Status);
        Assert.Equal(404, (await b.Delete($"assignments/{task["id"]}")).Status);
        Assert.Equal(404, (await b.Delete($"subjects/{subject}")).Status);
        Assert.Equal("Контрольная", (await a.Get("assignments")).Body[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ForeignSubjectCannotBeUsed()
    {
        await using var factory = new TrackerFactory();
        var (a, b) = await TwoUsers(factory);
        var subjectA = await a.CreateSubject("Математика");
        var subjectB = await b.CreateSubject("Математика");

        Assert.Equal(404, (await b.Post("assignments", new
        { title = "Контрольная", subjectId = subjectA, dueDate = "2026-12-10", status = "Todo" })).Status);
        var task = await NewTask(b, subjectB);
        task["subjectId"] = subjectA;
        Assert.Equal(404, (await b.Put($"assignments/{task["id"]}", task)).Status);

        var database = Path.Combine(factory.DataDirectory, "tracker.db");
        Assert.Throws<SqliteException>(() => SqliteFile.Execute(database,
            $"UPDATE Assignments SET SubjectId={subjectA} WHERE Id={task["id"]}", foreignKeys: true));
    }

    [Fact]
    public async Task SameSubjectNameForDifferentUsers()
    {
        await using var factory = new TrackerFactory();
        var (a, b) = await TwoUsers(factory);
        await a.CreateSubject("Математика");
        await b.CreateSubject("Математика");

        Assert.Equal(409, (await a.Post("subjects", new { name = "математика", color = "teal" })).Status);
    }

    [Fact]
    public async Task CrossAccountCsrfIsRejected()
    {
        await using var factory = new TrackerFactory();
        var (a, b) = await TwoUsers(factory);
        var tokenB = b.Token;

        b.Token = a.Token;
        Assert.Equal(400, (await b.Post("subjects", new { name = "Чужой токен", color = "blue" })).Status);
        b.Token = tokenB;

        Assert.Equal(0, (await b.Get("subjects")).Body.GetArrayLength());
    }

    [Fact]
    public async Task OwnerIdFromBodyIsIgnored()
    {
        await using var factory = new TrackerFactory();
        var (a, b) = await TwoUsers(factory);
        var ownerB = (await b.Get("auth/me")).Body.GetProperty("id").GetString();

        var subject = await a.Post("subjects", new { name = "Мой предмет", color = "blue", ownerId = ownerB });
        Assert.Equal(201, subject.Status);
        var task = await a.Post("assignments", new
        {
            title = "Моё задание", subjectId = subject.Body.GetProperty("id").GetInt32(),
            dueDate = "2026-12-10", status = "Todo", ownerId = ownerB,
        });

        Assert.Equal(201, task.Status);
        var ownerOfTask = task.Body.GetProperty("ownerId").GetString();
        Assert.NotEqual(ownerB, ownerOfTask);
        Assert.Equal(0, (await b.Get("subjects")).Body.GetArrayLength());
        Assert.Equal(ownerOfTask, task.Headers.GetValues("X-Account-Id").Single());
    }
}
