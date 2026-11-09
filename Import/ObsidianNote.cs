using System.Globalization;

namespace StudyTracker.Import;

public sealed record ParsedNote(string Path, string Title, string SubjectName, DateOnly Due,
    AssignmentStatus Status, string Description, bool TitleCut, bool DescriptionCut, string Key);

/// <summary>Либо Note, либо Skip (причина), либо NotHomework (заметка не задание, в список пропусков не попадает).</summary>
public sealed record NoteResult(ParsedNote? Note, string? Skip, bool NotHomework);

public static class ObsidianNote
{
    public const int MaxTitle = 140, MaxDescription = 2000, MaxSubject = 80, MaxKey = 300;
    private static readonly DateOnly FirstDue = new(2000, 1, 1), LastDue = new(2100, 12, 31);

    public static NoteResult Parse(string path, string text, DateOnly? defaultDue)
    {
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n');
        var header = Header(lines, out var bodyStart);
        if (header is null || header.GetValueOrDefault("type") != "homework") return new(null, null, true);
        if (string.Equals(header.GetValueOrDefault("removed"), "true", StringComparison.OrdinalIgnoreCase))
            return Skipped("удалено на сайте");

        var subject = Subject(header.GetValueOrDefault("subject") ?? "");
        if (subject.Length == 0) return Skipped("не указан предмет");
        if (subject.Length > MaxSubject) return Skipped("имя предмета длиннее 80 символов");

        var dueText = header.GetValueOrDefault("due") ?? "";
        DateOnly due;
        if (dueText.Length == 0)
        {
            if (defaultDue is null) return Skipped("нет срока");
            due = defaultDue.Value;
        }
        else if (!DateOnly.TryParseExact(dueText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out due)
            || due < FirstDue || due > LastDue)
            return Skipped("неверный срок");

        var body = lines[bodyStart..];
        var title = body.FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim();
        if (string.IsNullOrEmpty(title)) title = FileTitle(path);
        var description = Description(body);
        var source = header.GetValueOrDefault("source") ?? "";
        var key = source.Length > 0
            ? source[..Math.Min(source.Length, MaxKey)]
            : $"{subject.ToUpperInvariant()}|{title.ToUpperInvariant()}|{due:yyyy-MM-dd}";
        var status = header.GetValueOrDefault("done") == "true" ? AssignmentStatus.Done : AssignmentStatus.Todo;
        var shortTitle = Cut(title, MaxTitle);
        var shortDescription = Cut(description, MaxDescription);
        return new(new ParsedNote(path, shortTitle, subject, due, status, shortDescription,
            shortTitle != title, shortDescription != description, key), null, false);
    }

    private static NoteResult Skipped(string reason) => new(null, reason, false);

    /// <summary>Шапка: блок между <c>---</c> в начале файла. Null, если файл не начинается с шапки или она не закрыта.</summary>
    private static Dictionary<string, string>? Header(string[] lines, out int bodyStart)
    {
        bodyStart = 0;
        if (lines.Length == 0 || lines[0].TrimEnd() != "---") return null;
        var header = new Dictionary<string, string>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---") { bodyStart = i + 1; return header; }
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            header.TryAdd(lines[i][..colon].Trim(), Unquote(lines[i][(colon + 1)..].Trim()));
        }
        return null;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'' ? value[1..^1].Trim() : value;

    private static string Subject(string value)
    {
        if (value.StartsWith("[[") && value.EndsWith("]]") && value.Length >= 4) value = value[2..^2];
        var bar = value.IndexOf('|');
        return (bar >= 0 ? value[..bar] : value).Trim();
    }

    private static string FileTitle(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }

    private static string Description(string[] body)
    {
        var start = Array.FindIndex(body, l => l.TrimEnd() == "## Задание");
        if (start < 0) return "";
        var lines = body[(start + 1)..].TakeWhile(l => !l.StartsWith("## "));
        return string.Join("\n", lines).Trim();
    }

    /// <summary>Обрезает до <paramref name="max"/> символов вместе с многоточием, не разрывая пару суррогатов.</summary>
    private static string Cut(string value, int max)
    {
        if (value.Length <= max) return value;
        var length = max - 1;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + "…";
    }
}
