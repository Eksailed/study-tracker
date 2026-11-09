namespace StudyTracker;

public static class GroupNames
{
    public const int MaxLength = 60;

    /// <summary>Имя группы после обрезки пробелов и ключ для сравнения без учёта регистра; <c>null</c>, если имя пустое или длиннее 60 символов.</summary>
    public static (string Name, string Key)? Normalize(string? raw)
    {
        var name = raw?.Trim() ?? "";
        return name.Length is 0 or > MaxLength ? null : (name, name.ToUpperInvariant());
    }
}
