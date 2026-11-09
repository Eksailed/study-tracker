using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StudyTracker.Import;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class ObsidianImportTests
{
    private static readonly string Physics = ObsidianSamples.Note("Физика", "Лаба физика", "2026-11-20", "false", "s-1", "Описание физики");
    private static readonly string Ict = ObsidianSamples.Note("ИКТ", "Лаба икт", "2026-11-21", "true", "s-2", "Описание ИКТ");
    private static readonly string Math = ObsidianSamples.Note("Математика", "ДЗ математика", "2026-12-01", "false", "s-3");

    private static ImportRequest Request(DateOnly? defaultDue = null, params string[] texts) =>
        new(texts.Select((text, i) => new ImportFile($"ДЗ/Предмет/Заметка {i}.md", text)).ToList(), defaultDue);

    private static ImportRequest Three() => Request(null, Physics, Ict, Math);

    private static async Task<string> Owner(TrackerFactory factory, string email)
    {
        await factory.NewClient().SignIn(email);
        return factory.Scope(services => services.GetRequiredService<TrackerDb>().Users.Single(u => u.Email == email).Id);
    }

    private static T Run<T>(TrackerFactory factory, Func<ObsidianImport, Task<T>> action) =>
        factory.Scope(services => action(new ObsidianImport(services.GetRequiredService<TrackerDb>())).GetAwaiter().GetResult());

    private static T Db<T>(TrackerFactory factory, Func<TrackerDb, T> read) =>
        factory.Scope(services => read(services.GetRequiredService<TrackerDb>()));

    private static string Pad(string text, int bytes)
    {
        var missing = bytes - Encoding.UTF8.GetByteCount(text);
        Assert.True(missing >= 0, "Заметка длиннее нужного размера");
        return text + new string('x', missing);
    }

    private static ImportFile File(string path, string text) => new(path, text);

    [Fact]
    public async Task PreviewWritesNothing()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");

        var report = Run(factory, import => import.PreviewAsync(owner, Three()));

        Assert.Equal(3, report.NewCount);
        Assert.Equal(["ИКТ", "Математика", "Физика"], report.NewSubjects);
        Assert.Equal(3, report.Items.Count);
        Assert.Equal(0, Db(factory, db => db.Subjects.Count()));
        Assert.Equal(0, Db(factory, db => db.Assignments.Count()));
    }

    [Fact]
    public async Task ApplyCreatesSubjectsAndTasks()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");

        var report = Run(factory, import => import.ApplyAsync(owner, Three()));

        Assert.Equal(3, report.NewCount);
        var subjects = Db(factory, db => db.Subjects.AsNoTracking().OrderBy(s => s.NameKey).ToList());
        Assert.Equal(["ИКТ", "Математика", "Физика"], subjects.Select(s => s.Name));
        Assert.Equal(["blue", "teal", "purple"], subjects.Select(s => s.Color));
        var tasks = Db(factory, db => db.Assignments.AsNoTracking().Include(a => a.Subject).OrderBy(a => a.ImportKey).ToList());
        Assert.Equal(["s-1", "s-2", "s-3"], tasks.Select(t => t.ImportKey));
        Assert.Equal(["Лаба физика", "Лаба икт", "ДЗ математика"], tasks.Select(t => t.Title));
        Assert.Equal(["Физика", "ИКТ", "Математика"], tasks.Select(t => t.Subject!.Name));
        Assert.Equal([AssignmentStatus.Todo, AssignmentStatus.Done, AssignmentStatus.Todo], tasks.Select(t => t.Status));
        Assert.Equal([new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 21), new DateOnly(2026, 12, 1)], tasks.Select(t => t.DueDate));
        Assert.Equal(["Описание физики", "Описание ИКТ", ""], tasks.Select(t => t.Description));
        Assert.All(tasks, t => Assert.Equal(owner, t.OwnerId));
    }

    [Fact]
    public async Task SecondApplyAddsNothing()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        Run(factory, import => import.ApplyAsync(owner, Three()));
        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            db.Assignments.Single(a => a.ImportKey == "s-1").Title = "Моё название";
            db.SaveChanges();
            return 0;
        });

        var report = Run(factory, import => import.ApplyAsync(owner, Three()));

        Assert.Equal(0, report.NewCount);
        Assert.Equal(3, report.ExistingCount);
        Assert.Empty(report.NewSubjects);
        Assert.Equal(3, Db(factory, db => db.Assignments.Count()));
        Assert.Equal(3, Db(factory, db => db.Subjects.Count()));
        Assert.Equal("Моё название", Db(factory, db => db.Assignments.Single(a => a.ImportKey == "s-1").Title));
    }

    [Fact]
    public async Task DeletedTaskComesBackOnNextImport()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        Run(factory, import => import.ApplyAsync(owner, Three()));
        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            db.Assignments.Remove(db.Assignments.Single(a => a.ImportKey == "s-2"));
            db.SaveChanges();
            return 0;
        });

        var report = Run(factory, import => import.ApplyAsync(owner, Three()));

        Assert.Equal(1, report.NewCount);
        Assert.Equal(2, report.ExistingCount);
        Assert.Equal(3, Db(factory, db => db.Assignments.Count()));
        Assert.Equal(["Лаба икт"], Db(factory, db => db.Assignments.Where(a => a.ImportKey == "s-2").Select(a => a.Title).ToList()));
    }

    [Fact]
    public async Task ExistingSubjectIsReused()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            db.Subjects.Add(new Subject { Name = "икт", NameKey = "ИКТ", Color = "orange", OwnerId = owner });
            db.SaveChanges();
            return 0;
        });

        var report = Run(factory, import => import.ApplyAsync(owner, Request(null, Ict)));

        Assert.Equal(1, report.NewCount);
        Assert.Empty(report.NewSubjects);
        var subject = Db(factory, db => db.Subjects.AsNoTracking().Single());
        Assert.Equal("икт", subject.Name);
        Assert.Equal("orange", subject.Color);
        Assert.Equal(subject.Id, Db(factory, db => db.Assignments.Single().SubjectId));
    }

    [Fact]
    public async Task SubjectsDifferingInCaseAreOne()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        var upper = ObsidianSamples.Note("ИКТ", "Первая", "2026-11-20", "false", "c-1");
        var lower = ObsidianSamples.Note("икт", "Вторая", "2026-11-21", "false", "c-2");

        var report = Run(factory, import => import.ApplyAsync(owner, Request(null, upper, lower)));

        Assert.Equal(2, report.NewCount);
        Assert.Equal(["ИКТ"], report.NewSubjects);
        Assert.Equal("ИКТ", Db(factory, db => db.Subjects.Single().Name));
        Assert.Equal(2, Db(factory, db => db.Assignments.Count()));
    }

    [Fact]
    public async Task DuplicateKeyInUploadIsSkipped()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        var first = ObsidianSamples.Note("ИКТ", "Первая", "2026-11-20", "false", "same");
        var second = ObsidianSamples.Note("ИКТ", "Вторая", "2026-11-21", "false", "same");

        var report = Run(factory, import => import.ApplyAsync(owner, Request(null, first, second)));

        Assert.Equal(1, report.NewCount);
        Assert.Equal("Первая", Assert.Single(report.Items).Title);
        var skipped = Assert.Single(report.Skipped);
        Assert.Equal("повтор в загрузке", skipped.Reason);
        Assert.Equal("ДЗ/Предмет/Заметка 1.md", skipped.Path);
        Assert.Equal(1, Db(factory, db => db.Assignments.Count()));
    }

    [Fact]
    public async Task ManualTaskWithSameTitleIsNotMatched()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            var subject = new Subject { Name = "ИКТ", NameKey = "ИКТ", Color = "blue", OwnerId = owner };
            db.Subjects.Add(subject);
            db.SaveChanges();
            db.Assignments.Add(new Assignment { Title = "Лаба икт", SubjectId = subject.Id, OwnerId = owner, DueDate = new DateOnly(2026, 11, 21) });
            db.SaveChanges();
            return 0;
        });

        var report = Run(factory, import => import.ApplyAsync(owner, Request(null, Ict)));

        Assert.Equal(1, report.NewCount);
        Assert.Equal(0, report.ExistingCount);
        Assert.Equal(2, Db(factory, db => db.Assignments.Count(a => a.Title == "Лаба икт")));
    }

    [Fact]
    public async Task OtherOwnersAreSeparate()
    {
        await using var factory = new TrackerFactory();
        var first = await Owner(factory, "first@example.test");
        var second = await Owner(factory, "second@example.test");
        Run(factory, import => import.ApplyAsync(first, Three()));

        var preview = Run(factory, import => import.PreviewAsync(second, Three()));
        var report = Run(factory, import => import.ApplyAsync(second, Three()));

        Assert.Equal(3, preview.NewCount);
        Assert.Equal(0, preview.ExistingCount);
        Assert.Equal(3, report.NewCount);
        Assert.Equal(3, Db(factory, db => db.Assignments.Count(a => a.OwnerId == first)));
        Assert.Equal(3, Db(factory, db => db.Assignments.Count(a => a.OwnerId == second)));
        Assert.Equal(6, Db(factory, db => db.Subjects.Count()));
    }

    [Fact]
    public async Task LimitsAreChecked()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        ImportFile Numbered(int i) => File($"ДЗ/{i}.md", ObsidianSamples.Note("ИКТ", $"Заметка {i}", "2026-11-20", "false", $"n-{i}"));
        Task<ImportReport> Preview(ImportRequest request) =>
            Task.FromResult(Run(factory, import => import.PreviewAsync(owner, request)));

        var many = Enumerable.Range(0, 200).Select(Numbered).ToList();
        Assert.Equal(200, (await Preview(new(many, null))).NewCount);
        many.Add(Numbered(200));
        await Assert.ThrowsAsync<ImportException>(() => Preview(new(many, null)));
        await Assert.ThrowsAsync<ImportException>(() => Preview(new([], null)));
        await Assert.ThrowsAsync<ImportException>(() => Preview(new(null, null)));

        var exact = Pad(ObsidianSamples.Note("ИКТ", "Ровно", "2026-11-20", "false", "exact"), 100 * 1024);
        var over = Pad(ObsidianSamples.Note("ИКТ", "Больше", "2026-11-20", "false", "over"), 100 * 1024 + 1);
        var sizes = await Preview(new([File("a.md", exact), File("b.md", over)], null));
        Assert.Equal(1, sizes.NewCount);
        var big = Assert.Single(sizes.Skipped);
        Assert.Equal(("b.md", "файл больше 100 КБ"), (big.Path, big.Reason));

        List<ImportFile> Total(int lastBytes)
        {
            var files = Enumerable.Range(0, 20).Select(i =>
                File($"t{i}.md", Pad(ObsidianSamples.Note("ИКТ", $"Т{i}", "2026-11-20", "false", $"t-{i}"), 100 * 1024))).ToList();
            files.Add(File("last.md", Pad(ObsidianSamples.Note("ИКТ", "Последняя", "2026-11-20", "false", "t-last"), lastBytes)));
            return files;
        }
        Assert.Equal(21, (await Preview(new(Total(49152), null))).NewCount);
        await Assert.ThrowsAsync<ImportException>(() => Preview(new(Total(49153), null)));

        var longPath = new string('п', 261);
        var paths = await Preview(new([File(new string('п', 260), Ict), File(longPath, Math), File("", Physics)], null));
        Assert.Equal(1, paths.NewCount);
        Assert.Equal(["неверный путь", "неверный путь"], paths.Skipped.Select(s => s.Reason));
        Assert.All(paths.Skipped, s => Assert.True(s.Path.Length <= 260));

        await Assert.ThrowsAsync<ImportException>(() => Preview(new([Numbered(0)], new DateOnly(1999, 12, 31))));
        await Assert.ThrowsAsync<ImportException>(() => Preview(new([Numbered(0)], new DateOnly(2101, 1, 1))));
    }

    [Fact]
    public async Task OnlyNotHomeworkGivesZeros()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        var request = Request(null, "# Просто заметка\n", ObsidianSamples.Note(type: "note"), "");

        var report = Run(factory, import => import.PreviewAsync(owner, request));

        Assert.Equal(0, report.NewCount);
        Assert.Equal(3, report.NotHomework);
        Assert.Empty(report.Skipped);
        Assert.Empty(report.Items);
    }

    [Fact]
    public async Task TruncatedIsCounted()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        var request = Request(null, ObsidianSamples.Note(source: "long", task: new string('а', 2001)), Ict);

        var report = Run(factory, import => import.PreviewAsync(owner, request));

        Assert.Equal(2, report.NewCount);
        Assert.Equal(1, report.Truncated);
    }

    [Fact]
    public async Task DefaultDueFillsEmptyDue()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        var noDue = ObsidianSamples.Note("ИКТ", "Без срока", null, "false", "nd");

        var without = Run(factory, import => import.PreviewAsync(owner, Request(null, noDue)));
        var with = Run(factory, import => import.ApplyAsync(owner, Request(new DateOnly(2026, 12, 31), noDue)));

        Assert.Equal(0, without.NewCount);
        Assert.Equal("нет срока", Assert.Single(without.Skipped).Reason);
        Assert.Equal(1, with.NewCount);
        Assert.Equal(new DateOnly(2026, 12, 31), Db(factory, db => db.Assignments.Single().DueDate));
    }

    [Fact]
    public async Task ApplyIsAtomic()
    {
        await using var factory = new TrackerFactory();
        var owner = await Owner(factory, "owner@example.test");
        factory.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            db.Subjects.Add(new Subject { Name = "ИКТ", NameKey = "ИКТ", Color = "blue", OwnerId = owner });
            db.SaveChanges();
            return 0;
        });
        var path = Path.Combine(factory.DataDirectory, "tracker.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString();
        var options = new DbContextOptionsBuilder<TrackerDb>().UseSqlite(connectionString)
            .AddInterceptors(new ConflictingInsert()).Options;
        await using var db = new TrackerDb(options);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new ObsidianImport(db).ApplyAsync(owner, Request(null, Ict, Physics)));

        SqliteConnection.ClearAllPools();
        Assert.Equal(1, Db(factory, d => d.Subjects.Count()));
        var only = Db(factory, d => d.Assignments.AsNoTracking().Single());
        Assert.Equal("Вставлено перехватчиком", only.Title);
    }
}
