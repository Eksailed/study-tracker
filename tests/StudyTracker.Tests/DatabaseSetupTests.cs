using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class DatabaseSetupTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("zachet-setup-").FullName;

    private string DatabasePath => Path.Combine(directory, "tracker.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private async Task Prepare(bool migrate)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TrackerDb>(options => options.UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true, Pooling = false }.ToString()));
        await using var provider = services.BuildServiceProvider();
        await DatabaseSetup.PrepareAsync(provider, migrate);
    }

    private static long Count(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task MigrationsCreateEmptyDatabase()
    {
        await Prepare(migrate: true);

        Assert.Equal(0, Count(DatabasePath, "SELECT COUNT(*) FROM Subjects"));
        Assert.Equal(0, Count(DatabasePath, "SELECT COUNT(*) FROM Assignments"));
        Assert.True(Count(DatabasePath, "SELECT COUNT(*) FROM __EFMigrationsHistory") > 0);
    }

    [Fact]
    public async Task MigrationsAreRepeatable()
    {
        await Prepare(migrate: true);
        SqliteFile.Execute(DatabasePath, """
            INSERT INTO AspNetUsers (Id, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
                VALUES ('test',0,0,0,0,0);
            INSERT INTO Subjects (Name, NameKey, Color, OwnerId) VALUES ('Test', 'TEST', 'blue', 'test');
            """);

        await Prepare(migrate: true);

        Assert.Equal(1, Count(DatabasePath, "SELECT COUNT(*) FROM Subjects WHERE Name='Test'"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyDatabaseIsNotModified(bool withRecords)
    {
        SqliteFile.Execute(DatabasePath, "CREATE TABLE Subjects (Id INTEGER PRIMARY KEY, Name TEXT)");
        if (withRecords) SqliteFile.Execute(DatabasePath, "INSERT INTO Subjects (Name) VALUES ('Old')");
        var before = SqliteFile.Sha256(DatabasePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Prepare(migrate: true));

        Assert.Equal(before, SqliteFile.Sha256(DatabasePath));
    }

    [Fact]
    public async Task SharedMigratedDatabaseIsNotModified()
    {
        SqliteFile.Execute(DatabasePath, """
            CREATE TABLE Subjects (Id INTEGER PRIMARY KEY,Name TEXT,NameKey TEXT,Color TEXT);
            CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY,ProductVersion TEXT);
            INSERT INTO Subjects VALUES (1,'Общий предмет','ОБЩИЙ ПРЕДМЕТ','blue');
            INSERT INTO __EFMigrationsHistory VALUES ('old_schema','10.0.14');
            """);
        var before = SqliteFile.Sha256(DatabasePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Prepare(migrate: true));

        Assert.Equal(before, SqliteFile.Sha256(DatabasePath));
    }

    [Fact]
    public async Task NotificationMigrationPreservesAccounts()
    {
        await Prepare(migrate: true);
        SqliteFile.Execute(DatabasePath, """
            INSERT INTO AspNetUsers (Id, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
                VALUES ('owner',0,0,0,0,0);
            INSERT INTO Subjects (Id, Name, NameKey, Color, OwnerId) VALUES (1, 'Сохранить', 'СОХРАНИТЬ', 'blue', 'owner');
            INSERT INTO Assignments (Id, Title, Description, SubjectId, DueDate, Status, CreatedAt, OwnerId)
                VALUES (7, 'Сохранить задание', '', 1, '2026-12-12', 0, '2026-11-06 10:00:00', 'owner');
            DROP INDEX IX_Assignments_OwnerId_ImportKey;
            ALTER TABLE Assignments DROP COLUMN ImportKey;
            CREATE INDEX IX_Assignments_OwnerId ON Assignments (OwnerId);
            DROP TABLE ApiTokens;
            DROP TABLE ReminderDeliveries;
            DROP TABLE TelegramLinkCodes;
            DROP TABLE TelegramConnections;
            DROP TABLE NotificationSettings;
            DROP TABLE TelegramUpdateStates;
            DELETE FROM __EFMigrationsHistory WHERE MigrationId >=
                (SELECT MigrationId FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_Notifications');
            """);

        await Prepare(migrate: true);

        Assert.Equal(1, Count(DatabasePath, "SELECT COUNT(*) FROM AspNetUsers WHERE Id='owner'"));
        Assert.Equal(1, Count(DatabasePath, "SELECT COUNT(*) FROM Assignments WHERE Id=7 AND OwnerId='owner'"));
        Assert.Equal(0, Count(DatabasePath, "SELECT COUNT(*) FROM ReminderDeliveries"));
        Assert.Equal(0, Count(DatabasePath, "SELECT COUNT(*) FROM NotificationSettings"));
    }
}
