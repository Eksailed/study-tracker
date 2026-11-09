using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace StudyTracker.Tests.Support;

/// <summary>
/// Перед ближайшим сохранением вставляет в ту же базу задание предмета «ИКТ» с ключом импорта <c>s-2</c>
/// отдельным подключением: так выглядит гонка двух одновременных импортов. Срабатывает один раз.
/// </summary>
public sealed class ConflictingInsert(bool armed = true) : SaveChangesInterceptor
{
    public const string Key = "s-2";

    public bool Armed { get; set; } = armed;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!Armed) return result;
        Armed = false;
        await using var connection = new SqliteConnection(eventData.Context!.Database.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO Assignments (Title, Description, SubjectId, DueDate, Status, CreatedAt, OwnerId, ImportKey)
            SELECT 'Вставлено перехватчиком', '', Id, '2026-11-20', 0, '2026-11-09 10:00:00', OwnerId, '{Key}'
            FROM Subjects WHERE NameKey = 'ИКТ' LIMIT 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }
}
