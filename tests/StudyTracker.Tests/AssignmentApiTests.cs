using System.Text.Json.Nodes;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class AssignmentApiTests
{
    [Fact]
    public async Task TaskLifecycleAndSubjectProtection()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();
        var subject = await client.CreateSubject("Базы данных");
        var task = await client.CreateTask(subject);

        foreach (var status in new[] { "InProgress", "Done", "Todo" })
        {
            task["status"] = status;
            var updated = await client.Put($"assignments/{task["id"]}", task);
            Assert.Equal(200, updated.Status);
            Assert.Equal(status, updated.Body.GetProperty("status").GetString());
            task = JsonNode.Parse(updated.Body.GetRawText())!.AsObject();
        }

        Assert.Equal(409, (await client.Delete($"subjects/{subject}")).Status);
        Assert.Equal(204, (await client.Delete($"assignments/{task["id"]}")).Status);
        Assert.Equal(404, (await client.Delete($"assignments/{task["id"]}")).Status);
        Assert.Equal(204, (await client.Delete($"subjects/{subject}")).Status);
    }

    public static TheoryData<string> InvalidAssignments => new()
    {
        """{"title":"  "}""",
        $$"""{"title":"{{new string('a', 141)}}"}""",
        $$"""{"description":"{{new string('a', 2001)}}"}""",
        """{"dueDate":"1999-01-01"}""",
        """{"dueDate":"2101-01-01"}""",
        """{"status":99}""",
        """{"dueDate":"ошибка"}""",
        """{"status":"Unknown"}""",
    };

    [Theory]
    [MemberData(nameof(InvalidAssignments))]
    public async Task ValidationRejectsInvalidAssignments(string invalidJson)
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();
        var subject = await client.CreateSubject("Математика");
        var data = new JsonObject
        {
            ["title"] = "Задачи", ["subjectId"] = subject, ["dueDate"] = "2026-11-15", ["status"] = "Todo",
        };
        foreach (var (key, value) in JsonNode.Parse(invalidJson)!.AsObject()) data[key] = value?.DeepClone();

        Assert.Equal(400, (await client.Post("assignments", data)).Status);
    }

    [Fact]
    public async Task UnknownSubjectReturns404()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();
        await client.CreateSubject("Математика");
        var data = new { title = "Задачи", subjectId = 999999, dueDate = "2026-11-15", status = "Todo" };

        Assert.Equal(404, (await client.Post("assignments", data)).Status);
    }

    [Fact]
    public async Task MissingRecordsReturn404()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();

        Assert.Equal(404, (await client.Delete("assignments/999999")).Status);
        Assert.Equal(404, (await client.Delete("subjects/999999")).Status);
        Assert.Equal(404, (await client.Put("assignments/999999", new { })).Status);
    }
}
