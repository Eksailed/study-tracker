namespace StudyTracker.Tests.Support;

/// <summary>Заметки в формате хранилища Obsidian: шапка, заголовок, служебная цитата и раздел «Задание».</summary>
public static class ObsidianSamples
{
    public const string Quote = "> Отметка «сделал» — галочка **done** в свойствах выше или в таблице на странице предмета. " +
        "После входа через Telegram (Ctrl+P → «Войти через Telegram») отметка уходит и на сайт.";

    /// <summary>Заметка как в хранилище. Пустое <paramref name="due"/> даёт строку <c>due:</c> без значения.</summary>
    public static string Note(string subject = "ИКТ", string title = "Лабораторная работа 1", string? due = "2026-10-06",
        string done = "true", string? source = "https://xn--d1asac1a.xn--p1ai/?page=homework&id=11", string? task = null,
        string type = "homework", string extra = "")
    {
        var head = $"---\ntype: {type}\nsubject: \"[[{subject}]]\"\ndue:{(due is null ? "" : " " + due)}\ndone: {done}\n";
        if (extra.Length > 0) head += extra + "\n";
        if (source is not null) head += $"source: {source}\n";
        head += "tags: [дз]\n---\n";
        var body = $"# {title}\n\n**Предмет:** [[{subject}]]  \n**Срок:** {due}\n\n{Quote}\n\n";
        if (task is not null) body += $"## Задание\n\n{task}\n\n";
        body += "## Файлы\n\n- [файл.docx](<https://example.test/file>)\n\n## Мои заметки\nличное\n";
        return head + body;
    }

    public const string Typical = "---\ntype: homework\nsubject: \"[[ИКТ]]\"\ndue: 2026-10-06\ndone: true\n" +
        "source: https://xn--d1asac1a.xn--p1ai/?page=homework&id=11\ntags: [дз]\n---\n# Лабораторная работа 1\n\n" +
        "**Предмет:** [[ИКТ]]  \n**Срок:** 06.10.2026 (просрочено)\n\n" + Quote + "\n\n## Задание\n\n" +
        "Срок сдачи ± месяц. Выставлена ориентировочная дата.\nОтветы на контрольные вопросы не требуются.\n\n" +
        "## Файлы\n\n- [файл.docx](<https://example.test/file>)\n\n## Мои заметки\nличное\n";
}
