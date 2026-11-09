using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyTracker.Migrations
{
    /// <inheritdoc />
    public partial class GroupScopeTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Составной внешний ключ (SubjectId, OwnerId) снят в RolesAndGroups: у предмета и задания группы OwnerId пуст.
            // Совпадение владельца и группы у задания и предмета держат триггеры. Отдельная миграция нужна, потому что
            // EF пересобирает таблицы в конце предыдущей миграции и снёс бы триггеры. Новая пересборка Assignments
            // тоже их снесёт: GroupMigrationTests.SubjectScopeTriggersExist это поймает.
            foreach (var (name, when) in new[]
            {
                ("TR_Assignments_SubjectScope_Insert", "INSERT ON Assignments"),
                ("TR_Assignments_SubjectScope_Update", "UPDATE OF SubjectId, OwnerId, GroupId ON Assignments"),
            })
                migrationBuilder.Sql($"""
                    CREATE TRIGGER {name} BEFORE {when}
                    BEGIN
                        SELECT RAISE(ABORT, 'Предмет и задание из разных владельцев или групп')
                        WHERE NOT EXISTS (SELECT 1 FROM Subjects s
                            WHERE s.Id = NEW.SubjectId AND s.OwnerId IS NEW.OwnerId AND s.GroupId IS NEW.GroupId);
                    END;
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_Assignments_SubjectScope_Insert;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_Assignments_SubjectScope_Update;");
        }
    }
}
