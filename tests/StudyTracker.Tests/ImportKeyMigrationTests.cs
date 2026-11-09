using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class ImportKeyMigrationTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("zachet-importkey-").FullName;

    private string DatabasePath => Path.Combine(directory, "tracker.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private static string OwnerId(TrackerFactory factory, string email) =>
        factory.Scope(services => services.GetRequiredService<TrackerDb>().Users.Single(u => u.Email == email).Id);

    private static void AddTask(TrackerFactory factory, string ownerId, string title, string? key)
    {
        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            var subject = db.Subjects.FirstOrDefault(s => s.OwnerId == ownerId);
            if (subject is null)
            {
                subject = new Subject { Name = "ИКТ", NameKey = "ИКТ", Color = "blue", OwnerId = ownerId };
                db.Subjects.Add(subject);
                db.SaveChanges();
            }
            db.Assignments.Add(new Assignment
            {
                Title = title, SubjectId = subject.Id, OwnerId = ownerId,
                DueDate = new DateOnly(2026, 12, 1), ImportKey = key,
            });
            db.SaveChanges();
            return 0;
        });
    }

    [Fact]
    public async Task ImportKeyIsUniquePerOwner()
    {
        await using var factory = new TrackerFactory();
        await factory.NewClient().SignIn("first@example.test");
        await factory.NewClient().SignIn("second@example.test");
        var first = OwnerId(factory, "first@example.test");
        var second = OwnerId(factory, "second@example.test");

        AddTask(factory, first, "Первое", "ключ-1");
        AddTask(factory, second, "У другого владельца", "ключ-1");
        AddTask(factory, first, "Без ключа", null);
        AddTask(factory, first, "Тоже без ключа", null);

        Assert.Throws<DbUpdateException>(() => AddTask(factory, first, "Повтор", "ключ-1"));
    }

    [Fact]
    public async Task ApiDoesNotShowImportKey()
    {
        await using var factory = new TrackerFactory();
        var client = factory.NewClient();
        await client.SignIn("owner@example.test");
        AddTask(factory, OwnerId(factory, "owner@example.test"), "С ключом", "секретный-ключ");

        var own = await client.Get("assignments");
        var v1 = await client.Get("v1/assignments");

        Assert.Equal(200, own.Status);
        Assert.Equal(200, v1.Status);
        foreach (var response in new[] { own, v1 })
        {
            var item = Assert.Single(response.Body.EnumerateArray());
            Assert.Equal("С ключом", item.GetProperty("title").GetString());
            Assert.False(item.TryGetProperty("importKey", out _), "Ключ импорта виден в ответе");
            Assert.DoesNotContain("секретный-ключ", response.Body.GetRawText());
        }
    }

    [Fact]
    public async Task MigrationKeepsOldData()
    {
        await Prepare();
        SqliteFile.Execute(DatabasePath, """
            INSERT INTO AspNetUsers (Id, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
                VALUES ('owner',0,0,0,0,0);
            INSERT INTO Subjects (Id, Name, NameKey, Color, OwnerId) VALUES (1, 'Сохранить', 'СОХРАНИТЬ', 'blue', 'owner');
            INSERT INTO Assignments (Id, Title, Description, SubjectId, DueDate, Status, Priority, CreatedAt, OwnerId)
                VALUES (7, 'Старое задание', '', 1, '2026-12-12', 0, 0, '2026-11-06 10:00:00', 'owner');
            DROP INDEX IX_Assignments_OwnerId_ImportKey;
            ALTER TABLE Assignments DROP COLUMN ImportKey;
            CREATE INDEX IX_Assignments_OwnerId ON Assignments (OwnerId);
            DELETE FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_ImportKey';
            """);

        await Prepare();

        Assert.Equal(1, Count("SELECT COUNT(*) FROM Assignments WHERE Id=7 AND Title='Старое задание' AND ImportKey IS NULL"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_ImportKey'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM pragma_index_list('Assignments') WHERE name='IX_Assignments_OwnerId_ImportKey' AND \"unique\"=1 AND partial=1"));
    }

    private async Task Prepare()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TrackerDb>(options => options.UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true, Pooling = false }.ToString()));
        await using var provider = services.BuildServiceProvider();
        await DatabaseSetup.PrepareAsync(provider, migrate: true);
    }

    private long Count(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
