using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class SubjectApiTests
{
    [Fact]
    public async Task SubjectValidationAndCyrillicDuplicates()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn();
        await client.CreateSubject("Программирование");

        var duplicate = await client.Post("subjects", new { name = " программирование ", color = "teal" });

        Assert.Equal(409, duplicate.Status);
        foreach (var data in new object[]
        {
            new { name = "", color = "blue" }, new { name = new string('x', 81), color = "blue" },
            new { name = "Химия", color = "red" }, new { name = (string?)null, color = "blue" },
        })
            Assert.Equal(400, (await client.Post("subjects", data)).Status);
    }
}
