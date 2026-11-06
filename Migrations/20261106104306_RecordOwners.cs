using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyTracker.Migrations;

public partial class RecordOwners : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Общие записи переносятся явным импортом. Проверка действует и при прямом вызове dotnet ef.
        // SQLite требует создать составной ключ предмета раньше внешнего ключа задания.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE OwnerMigrationGuard (Count INTEGER NOT NULL CHECK (Count = 0));
            INSERT INTO OwnerMigrationGuard VALUES ((SELECT COUNT(*) FROM Subjects) + (SELECT COUNT(*) FROM Assignments));
            DROP TABLE OwnerMigrationGuard;
            DROP TABLE Assignments;
            DROP TABLE Subjects;
            CREATE TABLE Subjects (
                Id INTEGER NOT NULL CONSTRAINT PK_Subjects PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL, NameKey TEXT NOT NULL, Color TEXT NOT NULL, OwnerId TEXT NOT NULL,
                CONSTRAINT AK_Subjects_Id_OwnerId UNIQUE (Id, OwnerId),
                CONSTRAINT FK_Subjects_AspNetUsers_OwnerId FOREIGN KEY (OwnerId) REFERENCES AspNetUsers (Id) ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IX_Subjects_OwnerId_NameKey ON Subjects (OwnerId, NameKey);
            CREATE TABLE Assignments (
                Id INTEGER NOT NULL CONSTRAINT PK_Assignments PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL, Description TEXT NOT NULL, SubjectId INTEGER NOT NULL,
                OwnerId TEXT NOT NULL, DueDate TEXT NOT NULL, Status INTEGER NOT NULL, CreatedAt TEXT NOT NULL,
                CONSTRAINT FK_Assignments_AspNetUsers_OwnerId FOREIGN KEY (OwnerId) REFERENCES AspNetUsers (Id) ON DELETE RESTRICT,
                CONSTRAINT FK_Assignments_Subjects_SubjectId_OwnerId FOREIGN KEY (SubjectId, OwnerId) REFERENCES Subjects (Id, OwnerId) ON DELETE RESTRICT
            );
            CREATE INDEX IX_Assignments_OwnerId ON Assignments (OwnerId);
            CREATE INDEX IX_Assignments_SubjectId_OwnerId ON Assignments (SubjectId, OwnerId);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Схему с владельцами нельзя автоматически вернуть к общим данным. Восстановите резервную копию.");
}
