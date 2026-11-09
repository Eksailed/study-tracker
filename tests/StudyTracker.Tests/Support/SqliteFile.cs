using Microsoft.Data.Sqlite;

namespace StudyTracker.Tests.Support;

/// <summary>Прямой доступ к файлу SQLite без пула: после вызова файл свободен и его можно хэшировать или удалять.</summary>
public static class SqliteFile
{
    public static void Execute(string path, string sql, bool foreignKeys = false)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, ForeignKeys = foreignKeys }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static string Sha256(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}
