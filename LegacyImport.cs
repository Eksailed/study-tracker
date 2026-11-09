using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

public static class LegacyImport
{
    public static async Task RunAsync(IServiceProvider services, string path, string email)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("Исходная база не найдена.");
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDb>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await users.FindByEmailAsync(email.Trim())
            ?? throw new InvalidOperationException("Сначала создайте аккаунт владельца.");
        var destination = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        if (Path.GetFullPath(path).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Исходная и новая база должны различаться.");
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await source.OpenAsync();
        using var snapshot = source.BeginTransaction(deferred: true);
        await using var command = source.CreateCommand();
        command.Transaction = snapshot;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Subjects') WHERE name='OwnerId'";
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
            throw new InvalidOperationException("Эта база уже содержит владельцев. Нужна старая общая база.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (await db.Subjects.AnyAsync(s => s.OwnerId == owner.Id) || await db.Assignments.AnyAsync(a => a.OwnerId == owner.Id))
            throw new InvalidOperationException("У владельца уже есть записи. Импорт разрешён только в пустой аккаунт.");
        var subjects = new Dictionary<int, Subject>();
        command.CommandText = "SELECT Id, Name, Color FROM Subjects ORDER BY Id";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(1).Trim();
                var color = reader.GetString(2);
                if (name.Length is < 1 or > 80 || !SubjectEndpoints.Colors.Contains(color))
                    throw new InvalidOperationException("В старой базе некорректный предмет.");
                var subject = new Subject { Name = name, NameKey = name.ToUpperInvariant(), Color = color, OwnerId = owner.Id };
                db.Subjects.Add(subject);
                subjects.Add(reader.GetInt32(0), subject);
            }
        }
        await db.SaveChangesAsync();
        var count = 0;
        command.CommandText = "SELECT Title, Description, SubjectId, DueDate, Status, CreatedAt FROM Assignments ORDER BY Id";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var title = reader.GetString(0);
                var description = reader.GetString(1);
                var due = DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var status = (AssignmentStatus)reader.GetInt32(4);
                if (string.IsNullOrWhiteSpace(title) || title.Length > 140 || description.Length > 2000 ||
                    due < new DateOnly(2000, 1, 1) || due > new DateOnly(2100, 12, 31) || !Enum.IsDefined(status) ||
                    !subjects.TryGetValue(reader.GetInt32(2), out var subject))
                    throw new InvalidOperationException("В старой базе некорректное задание.");
                db.Assignments.Add(new Assignment { Title = title, Description = description, SubjectId = subject.Id,
                    OwnerId = owner.Id, DueDate = due, Status = status,
                    CreatedAt = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) });
                count++;
            }
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        Console.WriteLine($"Перенесено предметов: {subjects.Count}; заданий: {count}. Исходная база не изменена.");
    }
}
