using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class LegacyImportTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("zachet-legacy-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string LegacyDatabase(int status = 1)
    {
        var path = Path.Combine(directory, "old.db");
        SqliteFile.Execute(path, $"""
            CREATE TABLE Subjects(Id INTEGER PRIMARY KEY,Name TEXT,NameKey TEXT,Color TEXT);
            CREATE TABLE Assignments(Id INTEGER PRIMARY KEY,Title TEXT,Description TEXT,SubjectId INTEGER,
                DueDate TEXT,Status INTEGER,CreatedAt TEXT);
            INSERT INTO Subjects VALUES (12,'Физика','ФИЗИКА','teal');
            INSERT INTO Assignments VALUES (23,'Опыт','Описание',12,'2026-12-12',{status},'2026-11-06 10:00:00');
            """);
        return path;
    }

    private static async Task<(ApiClient A, ApiClient B)> TwoUsers(TrackerFactory factory)
    {
        var a = factory.NewClient();
        var b = factory.NewClient();
        await a.SignIn("a@example.test");
        await b.SignIn("b@example.test");
        return (a, b);
    }

    [Fact]
    public async Task LegacyImportPreservesRecords()
    {
        await using var factory = new TrackerFactory();
        var (a, b) = await TwoUsers(factory);
        var source = LegacyDatabase();
        var before = SqliteFile.Sha256(source);

        await LegacyImport.RunAsync(factory.Services, source, "a@example.test");

        Assert.Equal(before, SqliteFile.Sha256(source));
        var tasks = (await a.Get("assignments")).Body;
        Assert.Equal(1, tasks.GetArrayLength());
        Assert.Equal("Описание", tasks[0].GetProperty("description").GetString());
        Assert.Equal("2026-12-12", tasks[0].GetProperty("dueDate").GetString());
        Assert.Equal("InProgress", tasks[0].GetProperty("status").GetString());
        Assert.StartsWith("2026-11-06T10:00:00", tasks[0].GetProperty("createdAt").GetString());
        Assert.Equal(0, (await b.Get("assignments")).Body.GetArrayLength());
        await Assert.ThrowsAsync<InvalidOperationException>(() => LegacyImport.RunAsync(factory.Services, source, "a@example.test"));
        Assert.Equal(1, (await a.Get("assignments")).Body.GetArrayLength());
    }

    [Fact]
    public async Task LegacyImportRollsBackInvalidData()
    {
        await using var factory = new TrackerFactory();
        var (a, _) = await TwoUsers(factory);
        var source = LegacyDatabase(status: 99);

        await Assert.ThrowsAsync<InvalidOperationException>(() => LegacyImport.RunAsync(factory.Services, source, "a@example.test"));

        Assert.Equal(0, (await a.Get("subjects")).Body.GetArrayLength());
        Assert.Equal(0, (await a.Get("assignments")).Body.GetArrayLength());
    }

    [Fact]
    public async Task LegacyImportRejectsUnknownOwner()
    {
        await using var factory = new TrackerFactory();
        var source = LegacyDatabase();

        await Assert.ThrowsAsync<InvalidOperationException>(() => LegacyImport.RunAsync(factory.Services, source, "missing@example.test"));
    }
}
