using System.Security.Cryptography;
using System.Text;

namespace StudyTracker.Calendar;

/// <summary>Ссылка-секрет на календарную ленту аккаунта. Сам секрет не хранится: только его SHA-256.</summary>
public class CalendarLink
{
    public int Id { get; set; }
    public required string OwnerId { get; set; }
    public required string KeyHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class CalendarKeys
{
    /// <summary>32 случайных байта в base64url без заполнителей: 43 символа.</summary>
    public static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
