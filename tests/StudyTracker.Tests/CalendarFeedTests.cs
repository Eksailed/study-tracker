using System.Text;
using System.Text.RegularExpressions;
using StudyTracker.Calendar;

namespace StudyTracker.Tests;

public class CalendarFeedTests
{
    private static Assignment Task(int id, string title, string due, AssignmentStatus status = AssignmentStatus.Todo,
        string subject = "Физика") => new()
    {
        Id = id, Title = title, OwnerId = "owner", SubjectId = 1, DueDate = DateOnly.Parse(due), Status = status,
        Subject = new Subject { Id = 1, Name = subject, NameKey = subject.ToUpperInvariant(), Color = "blue", OwnerId = "owner" },
        CreatedAt = new DateTime(2026, 11, 1, 10, 30, 0, DateTimeKind.Utc),
    };

    private static string Unfold(string feed) => feed.Replace("\r\n ", "");

    [Fact]
    public void EventIsAllDayOnDueDate()
    {
        var feed = CalendarFeed.Build([Task(5, "Лабораторная работа 3", "2026-11-20")]);

        Assert.Contains("BEGIN:VEVENT\r\n", feed);
        Assert.Contains("UID:task-5@zachet\r\n", feed);
        Assert.Contains("DTSTAMP:20261101T103000Z\r\n", feed);
        Assert.Contains("DTSTART;VALUE=DATE:20261120\r\n", feed);
        Assert.Contains("DTEND;VALUE=DATE:20261121\r\n", feed);
        Assert.Contains("SUMMARY:Физика: Лабораторная работа 3\r\n", Unfold(feed));
    }

    [Fact]
    public void EndOfYearRollsOver()
    {
        var feed = CalendarFeed.Build([Task(1, "Отчёт", "2026-12-31")]);

        Assert.Contains("DTSTART;VALUE=DATE:20261231\r\n", feed);
        Assert.Contains("DTEND;VALUE=DATE:20270101\r\n", feed);
    }

    [Fact]
    public void DoneTasksAreSkipped()
    {
        var feed = CalendarFeed.Build([
            Task(1, "Сделано", "2026-11-20", AssignmentStatus.Done),
            Task(2, "В работе", "2026-11-21", AssignmentStatus.InProgress),
            Task(3, "Нужно", "2026-11-22")]);

        Assert.DoesNotContain("task-1@zachet", feed);
        Assert.Contains("task-2@zachet", feed);
        Assert.Contains("task-3@zachet", feed);
    }

    [Fact]
    public void EventsGoByDueDateThenId()
    {
        var feed = CalendarFeed.Build([Task(9, "Позже", "2026-11-25"), Task(8, "Раньше", "2026-11-20"), Task(7, "Тоже раньше", "2026-11-20")]);

        var ids = Regex.Matches(feed, @"UID:task-(\d+)@").Select(m => m.Groups[1].Value);
        Assert.Equal(["7", "8", "9"], ids);
    }

    [Fact]
    public void TextIsEscaped()
    {
        var feed = CalendarFeed.Build([Task(1, "а,б;в\\г\nд\r\nе", "2026-11-20")]);

        Assert.Contains(@"SUMMARY:Физика: а\,б\;в\\г\nд\nе", Unfold(feed));
    }

    [Fact]
    public void LongLinesAreFolded()
    {
        var title = string.Concat(Enumerable.Repeat("Лабораторная 🧪 ", 12));
        var feed = CalendarFeed.Build([Task(1, title, "2026-11-20")]);

        var lines = feed.Split("\r\n");
        Assert.All(lines, line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"Строка длиннее 75 байт: {line}"));
        Assert.Contains(lines, line => line.StartsWith(' '));
        var summary = Unfold(feed).Split("\r\n").Single(l => l.StartsWith("SUMMARY:"));
        Assert.Equal("SUMMARY:Физика: " + title.Replace("\n", "\\n"), summary);
        Assert.DoesNotContain('�', feed);
    }

    [Fact]
    public void LinesEndWithCrlf()
    {
        var feed = CalendarFeed.Build([Task(1, "Раз\nдва", "2026-11-20")]);

        Assert.DoesNotMatch(@"(?<!\r)\n", feed);
        Assert.EndsWith("\r\n", feed);
    }

    [Fact]
    public void EmptyFeedIsValid()
    {
        var feed = CalendarFeed.Build([]);

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", feed);
        Assert.Contains("PRODID:-//Zachet//Study Tracker//RU\r\n", feed);
        Assert.Contains("X-WR-CALNAME:Зачёт\r\n", feed);
        Assert.EndsWith("END:VCALENDAR\r\n", feed);
        Assert.DoesNotContain("BEGIN:VEVENT", feed);
    }

    [Fact]
    public void KeyIsRandomAndHashed()
    {
        var first = CalendarKeys.Create();
        var second = CalendarKeys.Create();

        Assert.NotEqual(first, second);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first);
        var hash = CalendarKeys.Hash(first);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, CalendarKeys.Hash(first));
        Assert.NotEqual(hash, CalendarKeys.Hash(second));
        Assert.DoesNotContain(first, hash);
    }
}
