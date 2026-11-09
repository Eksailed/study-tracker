using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class PersistenceTests
{
    [Fact]
    public async Task DataSurvivesRestart()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();
        var subject = await client.CreateSubject("Физика");
        var task = await client.CreateTask(subject, new()
        {
            ["status"] = "InProgress", ["description"] = "Сохранить после перезапуска",
        });

        await using var restarted = factory.Restart();
        var again = restarted.NewClient();
        Assert.Equal(200, (await again.Login("api@example.test")).Status);
        var tasks = await again.Get("assignments");

        Assert.Equal(200, tasks.Status);
        var stored = tasks.Body.EnumerateArray().Single(a => a.GetProperty("id").GetInt32() == (int)task["id"]!);
        Assert.Equal("InProgress", stored.GetProperty("status").GetString());
        Assert.Equal("Сохранить после перезапуска", stored.GetProperty("description").GetString());
    }

    [Fact]
    public async Task TwoUsersSurviveRestart()
    {
        await using var factory = new TrackerFactory();
        var a = factory.NewClient();
        var b = factory.NewClient();
        await a.SignIn("a@example.test");
        await b.SignIn("b@example.test");
        var taskA = await a.CreateTask(await a.CreateSubject("Предмет А"));
        var taskB = await b.CreateTask(await b.CreateSubject("Предмет Б"));

        await using var restarted = factory.Restart();
        var a2 = restarted.NewClient();
        var b2 = restarted.NewClient();
        Assert.Equal(200, (await a2.Login("a@example.test")).Status);
        Assert.Equal(200, (await b2.Login("b@example.test")).Status);

        Assert.Equal([(int)taskA["id"]!], (await a2.Get("assignments")).Body.EnumerateArray().Select(t => t.GetProperty("id").GetInt32()));
        Assert.Equal([(int)taskB["id"]!], (await b2.Get("assignments")).Body.EnumerateArray().Select(t => t.GetProperty("id").GetInt32()));
    }
}
