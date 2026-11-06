using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

public static class DatabaseSetup
{
    public static async Task PrepareAsync(IServiceProvider services, bool migrate)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDb>();
        var source = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        if (File.Exists(source))
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            var tables = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            if (tables.Count > 0 && !tables.Contains("__EFMigrationsHistory"))
                throw new InvalidOperationException("Найдена старая база без истории миграций. Она не изменена. " +
                    "Укажите новую папку через Tracker__DataDirectory и выполните --migrate; " +
                    "старые записи перенесите отдельной командой импорта.");
            await reader.DisposeAsync();
            if (tables.Contains("Subjects"))
            {
                command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Subjects') WHERE name='OwnerId'";
                var hasOwner = Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
                command.CommandText = "SELECT COUNT(*) FROM Subjects";
                if (!hasOwner && Convert.ToInt64(await command.ExecuteScalarAsync()) > 0)
                    throw new InvalidOperationException("В базе есть общие предметы без владельца. Сохраните её, " +
                        "создайте новую базу через Tracker__DataDirectory и перенесите записи командой --import-legacy с --owner.");
            }
        }
        if (migrate)
        {
            await db.Database.MigrateAsync();
            Console.WriteLine("Миграции применены. База готова.");
        }
        else if ((await db.Database.GetPendingMigrationsAsync()).Any())
            throw new InvalidOperationException("Схема базы не обновлена. Сначала выполните: dotnet run -- --migrate");
    }
}
