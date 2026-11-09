using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class CalendarMigrationTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("zachet-calendar-").FullName;

    private string DatabasePath => Path.Combine(directory, "tracker.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task MigrationKeepsOldDataAndAddsTable()
    {
        await Prepare();
        SqliteFile.Execute(DatabasePath, """
            INSERT INTO AspNetUsers (Id, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
                VALUES ('owner',0,0,0,0,0);
            INSERT INTO Subjects (Id, Name, NameKey, Color, OwnerId) VALUES (1, 'Сохранить', 'СОХРАНИТЬ', 'blue', 'owner');
            INSERT INTO Assignments (Id, Title, Description, SubjectId, DueDate, Status, Priority, CreatedAt, OwnerId)
                VALUES (7, 'Старое задание', '', 1, '2026-12-12', 0, 0, '2026-11-06 10:00:00', 'owner');
            DROP TABLE CalendarLinks;
            DELETE FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_CalendarLinks';
            """);

        await Prepare();

        Assert.Equal(1, Count("SELECT COUNT(*) FROM Assignments WHERE Id=7 AND Title='Старое задание'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_CalendarLinks'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM CalendarLinks"));
        Assert.Equal(2, Count("SELECT COUNT(*) FROM pragma_index_list('CalendarLinks') WHERE \"unique\"=1 AND origin='c'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM pragma_foreign_key_list('CalendarLinks') WHERE on_delete='CASCADE'"));
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
