using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class BackupTests : IDisposable
{
    private static readonly DateTimeOffset Moment = DateTimeOffset.Parse("2026-11-10T08:09:10Z");
    private readonly string folder = Directory.CreateTempSubdirectory("zachet-backup-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }

    private static async Task<string> Seed(TrackerFactory factory)
    {
        var client = factory.NewClient();
        await client.SignIn("backup@example.test");
        await client.CreateTask(await client.CreateSubject("Предмет"));
        return (await client.Get("auth/me")).Body.GetProperty("id").GetString()!;
    }

    private static string Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    [Fact]
    public async Task BackupCreatesOpenableCopyWithRecords()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);

        var path = await BackupCommands.BackupAsync(factory.Services, folder, new FakeTimeProvider(Moment));

        Assert.Equal(Path.Combine(folder, "tracker-20261110-080910.db"), path);
        Assert.Equal("1", Scalar(path, "SELECT COUNT(*) FROM Subjects"));
        Assert.Equal("1", Scalar(path, "SELECT COUNT(*) FROM Assignments"));
        var keys = Path.Combine(folder, "tracker-20261110-080910-keys");
        Assert.True(Directory.Exists(keys));
        Assert.NotEmpty(Directory.GetFiles(keys));
    }

    [Fact]
    public async Task BackupPassesIntegrityCheck()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);

        var path = await BackupCommands.BackupAsync(factory.Services, folder, new FakeTimeProvider(Moment));

        Assert.Equal("ok", Scalar(path, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task BackupIsSingleSelfContainedFile()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);

        var path = await BackupCommands.BackupAsync(factory.Services, folder, new FakeTimeProvider(Moment));

        Assert.Equal([path], Directory.GetFiles(folder));
    }

    [Fact]
    public async Task BackupWhileConnectionOpen()
    {
        await using var factory = new TrackerFactory();
        var owner = await Seed(factory);
        await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(factory.DataDirectory, "tracker.db"), Pooling = false }.ToString());
        await writer.OpenAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var insert = writer.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO Subjects (Name, NameKey, Color, OwnerId) VALUES ('Скрытый', 'СКРЫТЫЙ', 'blue', '{owner}')";
            await insert.ExecuteNonQueryAsync();
        }

        var path = await BackupCommands.BackupAsync(factory.Services, folder, new FakeTimeProvider(Moment));

        Assert.Equal("ok", Scalar(path, "PRAGMA integrity_check"));
        Assert.Equal("1", Scalar(path, "SELECT COUNT(*) FROM Subjects"));
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task FailedIntegrityRemovesBackup()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BackupCommands.BackupAsync(
            factory.Services, folder, new FakeTimeProvider(Moment), _ => Task.FromResult("row 1 missing from index")));

        Assert.Contains("проверку", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Theory]
    [InlineData("--backup")]
    [InlineData("--backup", "--migrate")]
    public async Task MissingFolderArgumentFails(params string[] args)
    {
        await using var factory = new TrackerFactory();
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await BackupCommands.RunAsync(args, factory.Services, output, error);

        Assert.Equal(1, code);
        Assert.Contains("Укажите папку после --backup", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task RunPrintsPathsAndReturnsZero()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);
        var output = new StringWriter();

        var code = await BackupCommands.RunAsync(["--backup", folder], factory.Services, output, new StringWriter());

        Assert.Equal(0, code);
        var copy = Assert.Single(Directory.GetFiles(folder, "tracker-*.db"));
        Assert.Contains(copy, output.ToString());
        Assert.Contains("-keys", output.ToString());
    }

    [Fact]
    public async Task OldBackupsAreKept()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);
        var time = new FakeTimeProvider(Moment);

        await BackupCommands.BackupAsync(factory.Services, folder, time);
        time.Advance(TimeSpan.FromSeconds(1));
        await BackupCommands.BackupAsync(factory.Services, folder, time);

        Assert.Equal(2, Directory.GetFiles(folder, "tracker-*.db").Length);
        Assert.Equal(2, Directory.GetDirectories(folder, "tracker-*-keys").Length);
    }
}
