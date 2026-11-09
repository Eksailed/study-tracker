using System.Text;

namespace StudyTracker.Calendar;

/// <summary>Лента iCalendar (RFC 5545): задания как события на весь день срока.</summary>
public static class CalendarFeed
{
    private const int MaxLineBytes = 75;

    /// <summary>
    /// По умолчанию личная лента: «Зачёт», <c>UID:task-…</c>, выполненные пропускаются. Лента группы для гостей
    /// передаёт название группы, свой префикс <c>UID</c> и <paramref name="skipDone"/> = <c>false</c> (отметок гость не видит).
    /// </summary>
    public static string Build(IEnumerable<Assignment> tasks, string calendarName = "Зачёт", string uidPrefix = "task", bool skipDone = true)
    {
        var text = new StringBuilder();
        AppendLine(text, "BEGIN:VCALENDAR");
        AppendLine(text, "VERSION:2.0");
        AppendLine(text, "PRODID:-//Zachet//Study Tracker//RU");
        AppendLine(text, "CALSCALE:GREGORIAN");
        AppendLine(text, "X-WR-CALNAME:" + Escape(calendarName));
        foreach (var task in tasks.Where(t => !skipDone || t.Status != AssignmentStatus.Done).OrderBy(t => t.DueDate).ThenBy(t => t.Id))
        {
            var created = task.CreatedAt.Kind == DateTimeKind.Local ? task.CreatedAt.ToUniversalTime() : task.CreatedAt;
            AppendLine(text, "BEGIN:VEVENT");
            AppendLine(text, $"UID:{uidPrefix}-{task.Id}@zachet");
            AppendLine(text, $"DTSTAMP:{created:yyyyMMdd'T'HHmmss'Z'}");
            AppendLine(text, $"DTSTART;VALUE=DATE:{task.DueDate:yyyyMMdd}");
            AppendLine(text, $"DTEND;VALUE=DATE:{task.DueDate.AddDays(1):yyyyMMdd}");
            AppendLine(text, "SUMMARY:" + Escape($"{task.Subject?.Name}: {task.Title}"));
            AppendLine(text, "END:VEVENT");
        }
        AppendLine(text, "END:VCALENDAR");
        return text.ToString();
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
        .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>Строка не длиннее 75 байт UTF-8; продолжение начинается с пробела, символы не режутся.</summary>
    private static void AppendLine(StringBuilder text, string line)
    {
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > MaxLineBytes)
            {
                text.Append("\r\n ");
                bytes = 1;
            }
            text.Append(rune.ToString());
            bytes += size;
        }
        text.Append("\r\n");
    }
}
