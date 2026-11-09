using StudyTracker.Import;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class ObsidianNoteTests
{
    private const string Path = "ДЗ/ИКТ/Лабораторная работа 1.md";

    private static ParsedNote Ok(string text, DateOnly? defaultDue = null, string path = Path)
    {
        var result = ObsidianNote.Parse(path, text, defaultDue);
        Assert.True(result.Note is not null, $"Пропуск: {result.Skip}, не задание: {result.NotHomework}");
        return result.Note!;
    }

    private static string Skip(string text, DateOnly? defaultDue = null)
    {
        var result = ObsidianNote.Parse(Path, text, defaultDue);
        Assert.Null(result.Note);
        Assert.False(result.NotHomework);
        return result.Skip!;
    }

    [Fact]
    public void ParsesTypicalNote()
    {
        var note = Ok(ObsidianSamples.Typical);

        Assert.Equal("Лабораторная работа 1", note.Title);
        Assert.Equal("ИКТ", note.SubjectName);
        Assert.Equal(new DateOnly(2026, 10, 6), note.Due);
        Assert.Equal(AssignmentStatus.Done, note.Status);
        Assert.Equal("https://xn--d1asac1a.xn--p1ai/?page=homework&id=11", note.Key);
        Assert.StartsWith("Срок сдачи ± месяц.", note.Description);
        Assert.EndsWith("не требуются.", note.Description);
        Assert.DoesNotContain("Отметка «сделал»", note.Description);
        Assert.False(note.TitleCut);
        Assert.False(note.DescriptionCut);
    }

    [Fact]
    public void HandlesCrlfAndBom()
    {
        var note = Ok("\uFEFF" + ObsidianSamples.Typical.Replace("\n", "\r\n"));

        Assert.Equal("Лабораторная работа 1", note.Title);
        Assert.Equal(AssignmentStatus.Done, note.Status);
        Assert.DoesNotContain('\r', note.Description);
        Assert.StartsWith("Срок сдачи", note.Description);
    }

    [Fact]
    public void SubjectAliasLink()
    {
        Assert.Equal("ИКТ", Ok(ObsidianSamples.Typical.Replace("[[ИКТ]]\"", "[[ИКТ|Информатика]]\"")).SubjectName);
        Assert.Equal("ИКТ", Ok(ObsidianSamples.Typical.Replace("subject: \"[[ИКТ]]\"", "subject: ИКТ")).SubjectName);
    }

    [Fact]
    public void MissingSubjectIsSkipped()
    {
        Assert.Equal("не указан предмет", Skip(ObsidianSamples.Typical.Replace("subject: \"[[ИКТ]]\"\n", "")));
        Assert.Equal("не указан предмет", Skip(ObsidianSamples.Typical.Replace("subject: \"[[ИКТ]]\"", "subject:")));
    }

    [Fact]
    public void LongSubjectIsSkipped()
    {
        Assert.Equal("имя предмета длиннее 80 символов", Skip(ObsidianSamples.Note(subject: new string('я', 81))));
        Assert.Equal(new string('я', 80), Ok(ObsidianSamples.Note(subject: new string('я', 80))).SubjectName);
    }

    [Fact]
    public void EmptyDueIsSkippedWithoutDefault() =>
        Assert.Equal("нет срока", Skip(ObsidianSamples.Note(due: null)));

    [Fact]
    public void EmptyDueUsesDefault()
    {
        var note = Ok(ObsidianSamples.Note(due: null), new DateOnly(2026, 12, 31));

        Assert.Equal(new DateOnly(2026, 12, 31), note.Due);
    }

    [Theory]
    [InlineData("2026-13-40")]
    [InlineData("06.10.2026")]
    [InlineData("скоро")]
    [InlineData("1999-12-31")]
    [InlineData("2101-01-01")]
    public void BadDueIsSkipped(string due) =>
        Assert.Equal("неверный срок", Skip(ObsidianSamples.Note(due: due), new DateOnly(2026, 12, 31)));

    [Theory]
    [InlineData("2000-01-01")]
    [InlineData("2100-12-31")]
    public void DueBoundsAreAllowed(string due) =>
        Assert.Equal(DateOnly.Parse(due), Ok(ObsidianSamples.Note(due: due)).Due);

    [Theory]
    [InlineData("true", AssignmentStatus.Done)]
    [InlineData("false", AssignmentStatus.Todo)]
    [InlineData("", AssignmentStatus.Todo)]
    [InlineData("yes", AssignmentStatus.Todo)]
    public void DoneVariants(string done, AssignmentStatus expected) =>
        Assert.Equal(expected, Ok(ObsidianSamples.Note(done: done)).Status);

    [Fact]
    public void DoneKeyMissingMeansTodo() =>
        Assert.Equal(AssignmentStatus.Todo, Ok(ObsidianSamples.Typical.Replace("done: true\n", "")).Status);

    [Fact]
    public void RemovedIsSkipped() =>
        Assert.Equal("удалено на сайте", Skip(ObsidianSamples.Note(extra: "removed: true")));

    [Fact]
    public void NoTaskSectionGivesEmptyDescription() =>
        Assert.Equal("", Ok(ObsidianSamples.Note(task: null)).Description);

    [Fact]
    public void DescriptionStopsAtNextSection()
    {
        var note = Ok(ObsidianSamples.Note(task: "Первая строка.\n\nВторая строка."));

        Assert.Equal("Первая строка.\n\nВторая строка.", note.Description);
    }

    [Fact]
    public void DescriptionKeepsSubheadings()
    {
        var note = Ok(ObsidianSamples.Note(task: "Вступление.\n\n### Подзаголовок\n\nТекст."));

        Assert.Equal("Вступление.\n\n### Подзаголовок\n\nТекст.", note.Description);
    }

    [Fact]
    public void SeparatorAndHeadingInsideDescriptionAreHandled()
    {
        var note = Ok(ObsidianSamples.Note(task: "До.\n\n---\n\nПосле."));

        Assert.Equal("До.\n\n---\n\nПосле.", note.Description);
    }

    [Fact]
    public void OnlyFirstTaskSectionCounts()
    {
        var text = ObsidianSamples.Note(task: "Первое.") + "\n## Задание\n\nВторое.\n";

        Assert.Equal("Первое.", Ok(text).Description);
    }

    [Fact]
    public void FrontMatterIsTakenOnlyFromFileStart()
    {
        var text = "Лишняя строка\n" + ObsidianSamples.Typical;

        var result = ObsidianNote.Parse(Path, text, null);

        Assert.True(result.NotHomework);
    }

    [Fact]
    public void DescriptionLimit()
    {
        var exact = Ok(ObsidianSamples.Note(task: new string('а', 2000)));
        var over = Ok(ObsidianSamples.Note(task: new string('а', 2001)));

        Assert.Equal(2000, exact.Description.Length);
        Assert.False(exact.DescriptionCut);
        Assert.Equal(2000, over.Description.Length);
        Assert.EndsWith("…", over.Description);
        Assert.True(over.DescriptionCut);
    }

    [Fact]
    public void DescriptionCutKeepsSurrogatePair()
    {
        // Эмодзи занимает два char: 1998 букв + пара суррогатов даёт длину 2000, ещё один символ — обрезку по границе пары.
        var note = Ok(ObsidianSamples.Note(task: new string('а', 1998) + "😀" + "б"));

        Assert.True(note.DescriptionCut);
        Assert.True(note.Description.Length <= 2000);
        Assert.EndsWith("…", note.Description);
        Assert.False(char.IsHighSurrogate(note.Description[^2]), "Пара суррогатов разорвана");
        foreach (var rune in note.Description.EnumerateRunes()) Assert.NotEqual(0xFFFD, rune.Value);
    }

    [Fact]
    public void TitleLimit()
    {
        var exact = Ok(ObsidianSamples.Note(title: new string('я', 140)));
        var over = Ok(ObsidianSamples.Note(title: new string('я', 141)));

        Assert.Equal(140, exact.Title.Length);
        Assert.False(exact.TitleCut);
        Assert.Equal(140, over.Title.Length);
        Assert.EndsWith("…", over.Title);
        Assert.True(over.TitleCut);
    }

    [Fact]
    public void TitleFromFileNameWithoutHeading()
    {
        var text = ObsidianSamples.Typical.Replace("# Лабораторная работа 1\n", "");

        Assert.Equal("Лаба 1", Ok(text, path: "ДЗ\\ИКТ\\Лаба 1.md").Title);
        Assert.Equal("Лаба 2", Ok(text, path: "ДЗ/ИКТ/Лаба 2.md").Title);
    }

    [Fact]
    public void NotHomeworkKinds()
    {
        foreach (var text in new[]
        {
            ObsidianSamples.Note(type: "note"),
            "# Просто заметка\nТекст\n",
            "",
            "---\ntype: homework\nsubject: \"[[ИКТ]]\"\n# нет закрытия шапки\n",
        })
        {
            var result = ObsidianNote.Parse(Path, text, null);
            Assert.True(result.NotHomework, text);
            Assert.Null(result.Note);
            Assert.Null(result.Skip);
        }
    }

    [Fact]
    public void KeyFallbackWithoutSource()
    {
        var note = Ok(ObsidianSamples.Note(subject: "икт", title: "Лабораторная работа 1", source: null));

        Assert.Equal("ИКТ|ЛАБОРАТОРНАЯ РАБОТА 1|2026-10-06", note.Key);
    }

    [Fact]
    public void EmptySourceFallsBackToo() =>
        Assert.StartsWith("ИКТ|", Ok(ObsidianSamples.Note(source: "")).Key);

    [Fact]
    public void KeyIsCutTo300()
    {
        var note = Ok(ObsidianSamples.Note(source: "https://example.test/" + new string('x', 400)));

        Assert.Equal(300, note.Key.Length);
    }
}
