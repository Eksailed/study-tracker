using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class BackupTests : IDisposable
{
    private static readonly DateTimeOffset Moment = DateTimeOffset.Parse("2026-11-10T08:09:10Z");
    private readonly string folder = Directory.CreateTempSubdirectory("zachet-backup-").FullName;

    private readonly string restored = Directory.CreateTempSubdirectory("zachet-restore-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
        Directory.Delete(restored, recursive: true);
    }

    private string RestoredKeys => Path.Combine(restored, "keys");

    private ServiceProvider RestoreServices() =>
        new ServiceCollection().AddSingleton(new TrackerPaths(restored, RestoredKeys)).BuildServiceProvider();

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
    public async Task RestoreBringsBackAccountAndTask()
    {
        string copy;
        await using (var source = new TrackerFactory())
        {
            await Seed(source);
            copy = await BackupCommands.BackupAsync(source.Services, folder, new FakeTimeProvider(Moment));
        }

        await BackupCommands.RestoreAsync(copy, restored, RestoredKeys);

        await using var factory = TrackerFactory.OnDirectory(restored);
        var client = factory.NewClient();
        Assert.Equal(200, (await client.Login("backup@example.test")).Status);
        var tasks = await client.Get("assignments");
        Assert.Equal(1, tasks.Body.GetArrayLength());
        Assert.Equal("Лабораторная № 2", tasks.Body[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task RestoreKeepsSessions()
    {
        string copy, cookie;
        await using (var source = new TrackerFactory())
        {
            var client = source.NewClient();
            await client.Register("session@example.test");
            var login = await client.Login("session@example.test");
            cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("Zachet.Account=")).Split(';')[0];
            Assert.Equal(200, (await client.Get("auth/me")).Status);
            copy = await BackupCommands.BackupAsync(source.Services, folder, new FakeTimeProvider(Moment));
        }

        await BackupCommands.RestoreAsync(copy, restored, RestoredKeys);

        await using var factory = TrackerFactory.OnDirectory(restored);
        var other = factory.NewClient();
        other.DefaultHeaders.Add("Cookie", cookie);
        var me = await other.Get("auth/me");
        Assert.Equal(200, me.Status);
        Assert.Equal("session@example.test", me.Body.GetProperty("email").GetString());
    }

    [Fact]
    public async Task RestoreRefusesNonEmptyDirectory()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);
        var copy = await BackupCommands.BackupAsync(factory.Services, folder, new FakeTimeProvider(Moment));
        var live = new FileInfo(Path.Combine(factory.DataDirectory, "tracker.db"));
        var size = live.Length;
        var written = live.LastWriteTimeUtc;

        // Папка ключей чистая: отказ должен случиться именно из-за tracker.db, а не из-за ключей.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BackupCommands.RestoreAsync(copy, factory.DataDirectory, RestoredKeys));

        Assert.Contains("tracker.db", error.Message);
        live.Refresh();
        Assert.Equal(size, live.Length);
        Assert.Equal(written, live.LastWriteTimeUtc);
        Assert.False(Directory.Exists(RestoredKeys) && Directory.EnumerateFileSystemEntries(RestoredKeys).Any());
    }

    [Fact]
    public async Task RestoreRefusesNonEmptyKeyDirectory()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);
        var copy = await BackupCommands.BackupAsync(factory.Services, folder, new FakeTimeProvider(Moment));
        Directory.CreateDirectory(RestoredKeys);
        await File.WriteAllTextAsync(Path.Combine(RestoredKeys, "key-old.xml"), "<key/>");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BackupCommands.RestoreAsync(copy, restored, RestoredKeys));

        Assert.Contains("папке ключей", error.Message);
        Assert.False(File.Exists(Path.Combine(restored, "tracker.db")));
        Assert.Equal(["key-old.xml"], Directory.GetFiles(RestoredKeys).Select(Path.GetFileName));
    }

    [Fact]
    public async Task RestoreRefusesMissingKeys()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);
        var copy = await BackupCommands.BackupAsync(factory.Services, folder, new FakeTimeProvider(Moment));
        Directory.Delete(copy[..^".db".Length] + "-keys", recursive: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BackupCommands.RestoreAsync(copy, restored, RestoredKeys));

        Assert.Contains("ключ", error.Message);
        Assert.False(File.Exists(Path.Combine(restored, "tracker.db")));
    }

    [Fact]
    public async Task RestoreRefusesMissingBackup()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BackupCommands.RestoreAsync(Path.Combine(folder, "нет.db"), restored, RestoredKeys));

        Assert.Contains("не найдена", error.Message);
        Assert.False(File.Exists(Path.Combine(restored, "tracker.db")));
    }

    [Fact]
    public async Task RestoreRunSucceedsOnEmptyDirectory()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);
        var copy = await BackupCommands.BackupAsync(factory.Services, folder, new FakeTimeProvider(Moment));
        await using var services = RestoreServices();
        var output = new StringWriter();

        var code = await BackupCommands.RunAsync(["--restore", copy], services, output, new StringWriter());

        Assert.Equal(0, code);
        Assert.Contains("Восстановлено", output.ToString());
        Assert.Equal("1", Scalar(Path.Combine(restored, "tracker.db"), "SELECT COUNT(*) FROM Assignments"));
    }

    [Fact]
    public async Task RestoreRunReturnsOneOnNonEmptyDirectory()
    {
        await using var factory = new TrackerFactory();
        await Seed(factory);
        var copy = await BackupCommands.BackupAsync(factory.Services, folder, new FakeTimeProvider(Moment));
        var error = new StringWriter();

        var code = await BackupCommands.RunAsync(["--restore", copy], factory.Services, new StringWriter(), error);

        Assert.Equal(1, code);
        Assert.Contains("уже есть", error.ToString());
    }

    [Fact]
    public async Task RestoreRunWithoutFileFails()
    {
        await using var services = RestoreServices();
        var error = new StringWriter();

        var code = await BackupCommands.RunAsync(["--restore"], services, new StringWriter(), error);

        Assert.Equal(1, code);
        Assert.Contains("Укажите файл копии после --restore", error.ToString());
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
