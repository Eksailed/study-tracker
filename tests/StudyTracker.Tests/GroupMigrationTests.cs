using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class GroupMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261109112320_SubtasksAndPriority";

    private readonly string directory = Directory.CreateTempSubdirectory("zachet-groups-").FullName;

    private string DatabasePath => Path.Combine(directory, "tracker.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private TrackerDb Open() => new(new DbContextOptionsBuilder<TrackerDb>().UseSqlite(
        new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true, Pooling = false }.ToString()).Options);

    private void Migrate(string? target = null)
    {
        using var db = Open();
        if (target is null) db.Database.Migrate(); else db.GetService<IMigrator>().Migrate(target);
    }

    [Fact]
    public void MigrationKeepsOldDataAndAddsGroups()
    {
        Migrate(PreviousMigration);
        SqliteFile.Execute(DatabasePath, """
            INSERT INTO AspNetUsers (Id, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
                VALUES ('owner',0,0,0,0,0);
            INSERT INTO Subjects (Id, Name, NameKey, Color, OwnerId) VALUES (1, 'Сохранить', 'СОХРАНИТЬ', 'blue', 'owner');
            INSERT INTO Assignments (Id, Title, Description, SubjectId, DueDate, Status, Priority, CreatedAt, OwnerId, ImportKey)
                VALUES (7, 'Старое задание', '', 1, '2026-12-12', 1, 2, '2026-11-06 10:00:00', 'owner', 'k-1');
            INSERT INTO Subtasks (Id, AssignmentId, Title, Done, Position) VALUES (3, 7, 'Шаг', 1, 0);
            """, foreignKeys: true);

        Migrate();

        Assert.Equal(1, Count("SELECT COUNT(*) FROM Subjects WHERE Id=1 AND Name='Сохранить' AND OwnerId='owner' AND GroupId IS NULL"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM Assignments WHERE Id=7 AND Title='Старое задание' AND OwnerId='owner' AND GroupId IS NULL AND Status=1 AND Priority=2 AND ImportKey='k-1'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM Subtasks WHERE Id=3 AND AssignmentId=7 AND Done=1 AND UserId IS NULL"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM pragma_foreign_key_check"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM AspNetUsers WHERE Id='owner' AND IsOwner=0"));
        foreach (var table in new[] { "Groups", "GroupMembers", "GroupProgress" })
            Assert.Equal(1, Count($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM pragma_foreign_key_list('Subtasks') WHERE \"table\"='Assignments' AND on_delete='CASCADE'"));
    }

    [Fact]
    public void RollbackKeepsPersonalDataAndDropsGroupData()
    {
        Migrate();
        Seed();
        Run("INSERT INTO Subjects (Id, Name, NameKey, Color, OwnerId) VALUES (1, 'Личный', 'ЛИЧНЫЙ', 'blue', 'u1')");
        Run("INSERT INTO Subjects (Id, Name, NameKey, Color, GroupId) VALUES (2, 'Групповой', 'ГРУППОВОЙ', 'blue', 1)");
        Run("INSERT INTO Assignments (Id, Title, Description, SubjectId, DueDate, Status, Priority, CreatedAt, OwnerId) VALUES (5, 'Личное', '', 1, '2026-12-12', 0, 0, '2026-11-06', 'u1')");
        Run("INSERT INTO Assignments (Id, Title, Description, SubjectId, DueDate, Status, Priority, CreatedAt, GroupId) VALUES (6, 'Групповое', '', 2, '2026-12-12', 0, 0, '2026-11-06', 1)");

        Migrate(PreviousMigration);

        Assert.Equal(1, Count("SELECT COUNT(*) FROM Subjects WHERE Id=1 AND OwnerId='u1'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM Assignments WHERE Id=5 AND OwnerId='u1'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM Subjects WHERE Id=2"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM Assignments WHERE Id=6"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM pragma_foreign_key_check"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Groups', 'TMP_Subjects_Id_OwnerId')"));
    }

    [Fact]
    public void CheckRejectsNeitherOrBothOwnerAndGroup()
    {
        Migrate();
        Seed();

        Assert.Throws<SqliteException>(() => Run("INSERT INTO Subjects (Name, NameKey, Color) VALUES ('Без', 'БЕЗ', 'blue')"));
        Assert.Throws<SqliteException>(() => Run("INSERT INTO Subjects (Name, NameKey, Color, OwnerId, GroupId) VALUES ('Оба', 'ОБА', 'blue', 'u1', 1)"));
        Run("INSERT INTO Subjects (Name, NameKey, Color, OwnerId) VALUES ('Личный', 'ЛИЧНЫЙ', 'blue', 'u1')");
        Run("INSERT INTO Subjects (Name, NameKey, Color, GroupId) VALUES ('Групповой', 'ГРУППОВОЙ', 'blue', 1)");

        var subject = Count("SELECT Id FROM Subjects WHERE NameKey='ЛИЧНЫЙ'");
        var shared = Count("SELECT Id FROM Subjects WHERE NameKey='ГРУППОВОЙ'");
        Assert.Throws<SqliteException>(() => Run($"INSERT INTO Assignments (Title, Description, SubjectId, DueDate, Status, Priority, CreatedAt) VALUES ('Без', '', {subject}, '2026-12-12', 0, 0, '2026-11-06')"));
        Assert.Throws<SqliteException>(() => Run($"INSERT INTO Assignments (Title, Description, SubjectId, DueDate, Status, Priority, CreatedAt, OwnerId, GroupId) VALUES ('Оба', '', {subject}, '2026-12-12', 0, 0, '2026-11-06', 'u1', 1)"));
        Run($"INSERT INTO Assignments (Title, Description, SubjectId, DueDate, Status, Priority, CreatedAt, OwnerId) VALUES ('Личное', '', {subject}, '2026-12-12', 0, 0, '2026-11-06', 'u1')");
        Run($"INSERT INTO Assignments (Title, Description, SubjectId, DueDate, Status, Priority, CreatedAt, GroupId) VALUES ('Групповое', '', {shared}, '2026-12-12', 0, 0, '2026-11-06', 1)");
    }

    [Fact]
    public void GroupAssignmentCannotUseForeignSubject()
    {
        Migrate();
        Seed();
        Run("INSERT INTO Subjects (Name, NameKey, Color, GroupId) VALUES ('Чужой', 'ЧУЖОЙ', 'blue', 2)");
        Run("INSERT INTO Subjects (Name, NameKey, Color, OwnerId) VALUES ('Личный', 'ЛИЧНЫЙ', 'blue', 'u1')");
        var other = Count("SELECT Id FROM Subjects WHERE NameKey='ЧУЖОЙ'");
        var personal = Count("SELECT Id FROM Subjects WHERE NameKey='ЛИЧНЫЙ'");
        const string columns = "(Title, Description, SubjectId, DueDate, Status, Priority, CreatedAt, {0})";
        const string tail = "'2026-12-12', 0, 0, '2026-11-06'";

        Assert.Throws<SqliteException>(() => Run($"INSERT INTO Assignments {string.Format(columns, "GroupId")} VALUES ('A', '', {other}, {tail}, 1)"));
        Assert.Throws<SqliteException>(() => Run($"INSERT INTO Assignments {string.Format(columns, "GroupId")} VALUES ('B', '', {personal}, {tail}, 1)"));
        Assert.Throws<SqliteException>(() => Run($"INSERT INTO Assignments {string.Format(columns, "OwnerId")} VALUES ('C', '', {personal}, {tail}, 'u2')"));
        Assert.Throws<SqliteException>(() => Run($"INSERT INTO Assignments {string.Format(columns, "OwnerId")} VALUES ('D', '', {other}, {tail}, 'u1')"));
    }

    [Fact]
    public void SubjectScopeTriggersExist()
    {
        Migrate();
        Assert.Equal(2, Count("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Assignments_SubjectScope_%'"));
    }

    [Fact]
    public void GuestKeyAndNameAreUnique()
    {
        Migrate();
        Assert.Throws<SqliteException>(() => Run("INSERT INTO Groups (Name, NameKey) VALUES ('ивт-4', 'ИВТ-4'), ('ИВТ-4', 'ИВТ-4')"));
        Run("INSERT INTO Groups (Name, NameKey, GuestKeyHash) VALUES ('А', 'А', NULL), ('Б', 'Б', NULL), ('В', 'В', 'h')");
        Assert.Throws<SqliteException>(() => Run("INSERT INTO Groups (Name, NameKey, GuestKeyHash) VALUES ('Г', 'Г', 'h')"));
    }

    [Fact]
    public void OneMembershipPerUser()
    {
        Migrate();
        Seed();
        Run("INSERT INTO GroupMembers (GroupId, UserId, Role, Status, CreatedAt) VALUES (1, 'u1', 0, 1, '2026-11-06')");
        Assert.Throws<SqliteException>(() => Run("INSERT INTO GroupMembers (GroupId, UserId, Role, Status, CreatedAt) VALUES (2, 'u1', 0, 0, '2026-11-06')"));
    }

    [Fact]
    public void GroupSubjectNamesAreUniquePerGroup()
    {
        Migrate();
        Seed();
        Run("INSERT INTO Subjects (Name, NameKey, Color, GroupId) VALUES ('Физика', 'ФИЗИКА', 'blue', 1)");
        Assert.Throws<SqliteException>(() => Run("INSERT INTO Subjects (Name, NameKey, Color, GroupId) VALUES ('физика', 'ФИЗИКА', 'blue', 1)"));
        Run("INSERT INTO Subjects (Name, NameKey, Color, GroupId) VALUES ('Физика', 'ФИЗИКА', 'blue', 2)");
        Run("INSERT INTO Subjects (Name, NameKey, Color, OwnerId) VALUES ('Физика', 'ФИЗИКА', 'blue', 'u1')");
    }

    private void Seed() => Run("""
        INSERT INTO AspNetUsers (Id, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
            VALUES ('u1',0,0,0,0,0), ('u2',0,0,0,0,0);
        INSERT INTO Groups (Id, Name, NameKey) VALUES (1, 'ИВТ-4', 'ИВТ-4'), (2, 'ИВТ-3', 'ИВТ-3');
        """);

    private void Run(string sql) => SqliteFile.Execute(DatabasePath, sql, foreignKeys: true);

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
