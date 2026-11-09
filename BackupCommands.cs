using Microsoft.Data.Sqlite;

namespace StudyTracker;

/// <summary>Команды <c>--backup</c> и <c>--restore</c>. Копия — онлайн-снимок SQLite плюс папка ключей рядом.</summary>
public static class BackupCommands
{
    private const int BusyAttempts = 20;
    private static readonly TimeSpan BusyPause = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Создаёт <c>tracker-ГГГГММДД-ЧЧммсс.db</c> и <c>tracker-ГГГГММДД-ЧЧммсс-keys</c> в <paramref name="folder"/> (время UTC),
    /// проверяет копию и возвращает путь к <c>.db</c>. Если проверка не пройдена, оба результата удаляются.
    /// </summary>
    public static async Task<string> BackupAsync(IServiceProvider services, string folder, TimeProvider time,
        Func<string, Task<string>>? integrity = null)
    {
        var paths = services.GetRequiredService<TrackerPaths>();
        Directory.CreateDirectory(folder);
        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(folder, $"tracker-{stamp}.db");
        var keysTarget = Path.Combine(folder, $"tracker-{stamp}-keys");
        if (File.Exists(target) || Directory.Exists(keysTarget))
            throw new InvalidOperationException($"Копия {Path.GetFileName(target)} уже есть. Повторите через секунду.");
        try
        {
            await CopyDatabaseAsync(Path.Combine(paths.DataDirectory, "tracker.db"), target);
            CopyDirectory(paths.KeyDirectory, keysTarget);
            if (await (integrity ?? IntegrityCheck)(target) != "ok")
                throw new InvalidOperationException("Копия не прошла проверку целостности и удалена. Базу не меняли: повторите команду.");
            return target;
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(target)) File.Delete(target);
            if (Directory.Exists(keysTarget)) Directory.Delete(keysTarget, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// Онлайн-снимок базы. Если базу в этот момент держат (SQLITE_BUSY или SQLITE_LOCKED: контрольная точка,
    /// эксклюзивная запись), ждёт и повторяет: до 20 попыток с паузой 0,5 с, то есть не дольше 10 секунд.
    /// </summary>
    private static async Task CopyDatabaseAsync(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var from = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await using var to = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = target, Pooling = false }.ToString());
                await from.OpenAsync();
                await to.OpenAsync();
                from.BackupDatabase(to);
                await using var mode = to.CreateCommand();
                mode.CommandText = "PRAGMA journal_mode = DELETE"; // копия — один файл, без -wal и -shm
                await mode.ExecuteNonQueryAsync();
                return;
            }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6 && attempt < BusyAttempts)
            {
                if (File.Exists(target)) File.Delete(target);
                await Task.Delay(BusyPause);
            }
        }
    }

    /// <summary>
    /// Кладёт копию базы и её папку ключей (<c>X-keys</c> рядом с <c>X.db</c>) в пустую папку данных.
    /// Работающую базу не затирает: если <c>tracker.db</c> или ключи уже есть — отказ, ничего не меняется.
    /// </summary>
    public static Task RestoreAsync(string backupDb, string dataDirectory, string keyDirectory)
    {
        var keysSource = backupDb.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            ? backupDb[..^".db".Length] + "-keys" : backupDb + "-keys";
        var database = Path.Combine(dataDirectory, "tracker.db");
        if (!File.Exists(backupDb)) throw new InvalidOperationException($"Копия не найдена: {backupDb}");
        if (!Directory.Exists(keysSource))
            throw new InvalidOperationException($"Рядом с копией нет папки ключей {Path.GetFileName(keysSource)}. " +
                "Без неё не восстановить сеансы.");
        if (File.Exists(database))
            throw new InvalidOperationException("В папке данных уже есть tracker.db. Восстановление не затирает рабочую базу: " +
                "перенесите её вручную или укажите пустую папку через Tracker__DataDirectory.");
        if (Directory.Exists(keyDirectory) && Directory.EnumerateFileSystemEntries(keyDirectory).Any())
            throw new InvalidOperationException("В папке ключей уже есть файлы. Укажите пустую папку.");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            File.Copy(backupDb, database);
            CopyDirectory(keysSource, keyDirectory);
        }
        catch
        {
            if (File.Exists(database)) File.Delete(database);
            throw;
        }
        return Task.CompletedTask;
    }

    private static async Task<string> IntegrityCheck(string database)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from))
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    /// <summary>Выполняет <c>--backup</c> или <c>--restore</c> и печатает результат; возвращает код выхода (0 или 1).</summary>
    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Contains("--restore"))
            {
                var file = Option(args, "--restore", "Укажите файл копии после --restore.");
                var paths = services.GetRequiredService<TrackerPaths>();
                await RestoreAsync(file, paths.DataDirectory, paths.KeyDirectory);
                output.WriteLine($"Восстановлено в {paths.DataDirectory}. Запустите приложение; " +
                    "если копия сделана старой версией, сначала выполните --migrate.");
                return 0;
            }
            var folder = Option(args, "--backup", "Укажите папку после --backup.");
            var database = await BackupAsync(services, folder, services.GetRequiredService<TimeProvider>());
            output.WriteLine("Копия базы: " + database);
            output.WriteLine("Копия ключей: " + database[..^".db".Length] + "-keys");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or UnauthorizedAccessException or SqliteException)
        {
            error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static string Option(string[] args, string name, string message)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--"))
            throw new InvalidOperationException(message);
        return args[index + 1];
    }
}
