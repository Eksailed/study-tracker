using System.Text;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker.Import;

public record ImportFile(string? Path, string? Text);
public record ImportRequest(List<ImportFile>? Files, DateOnly? DefaultDue);
public sealed record ImportSkip(string Path, string Reason);
public sealed record ImportItem(string Path, string Title, string Subject, DateOnly Due, AssignmentStatus Status);
public sealed record ImportReport(int NewCount, int ExistingCount, int NotHomework, int Truncated,
    IReadOnlyList<string> NewSubjects, IReadOnlyList<ImportSkip> Skipped, IReadOnlyList<ImportItem> Items);

/// <summary>Запрос импорта не годится целиком: пустой, слишком большой или со сроком вне диапазона.</summary>
public sealed class ImportException(string message) : Exception(message);

/// <summary>Предпросмотр и добавление заданий из заметок Obsidian. Существующие задания не меняются.</summary>
public sealed class ObsidianImport(TrackerDb db)
{
    public const int MaxFiles = 200, MaxFileBytes = 100 * 1024, MaxTotalBytes = 2 * 1024 * 1024, MaxPath = 260;
    private static readonly DateOnly FirstDue = new(2000, 1, 1), LastDue = new(2100, 12, 31);

    public Task<ImportReport> PreviewAsync(string ownerId, ImportRequest request, CancellationToken token = default) =>
        RunAsync(ownerId, request, save: false, token);

    public Task<ImportReport> ApplyAsync(string ownerId, ImportRequest request, CancellationToken token = default) =>
        RunAsync(ownerId, request, save: true, token);

    private async Task<ImportReport> RunAsync(string ownerId, ImportRequest request, bool save, CancellationToken token)
    {
        var files = Validate(request);
        var skipped = new List<ImportSkip>();
        var notes = new List<ParsedNote>();
        var notHomework = 0;
        foreach (var file in files)
        {
            var path = file?.Path ?? "";
            if (path.Length is 0 or > MaxPath)
            {
                skipped.Add(new ImportSkip(path.Length > MaxPath ? path[..MaxPath] : path, "неверный путь"));
                continue;
            }
            var text = file!.Text ?? "";
            if (text.Length == 0) { notHomework++; continue; }
            if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) { skipped.Add(new ImportSkip(path, "файл больше 100 КБ")); continue; }
            var result = ObsidianNote.Parse(path, text, request.DefaultDue);
            if (result.NotHomework) notHomework++;
            else if (result.Skip is not null) skipped.Add(new ImportSkip(path, result.Skip));
            else notes.Add(result.Note!);
        }

        var keys = notes.Select(n => n.Key).Distinct().ToList();
        var existing = (await db.Assignments
            .Where(a => a.OwnerId == ownerId && a.ImportKey != null && keys.Contains(a.ImportKey))
            .Select(a => a.ImportKey!).ToListAsync(token)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var existingCount = 0;
        var fresh = new List<ParsedNote>();
        foreach (var note in notes)
        {
            if (!seen.Add(note.Key)) skipped.Add(new ImportSkip(note.Path, "повтор в загрузке"));
            else if (existing.Contains(note.Key)) existingCount++;
            else fresh.Add(note);
        }

        var subjects = (await db.Subjects.Where(s => s.OwnerId == ownerId).ToListAsync(token))
            .ToDictionary(s => s.NameKey, StringComparer.Ordinal);
        var created = new SortedDictionary<string, Subject>(StringComparer.Ordinal);
        foreach (var note in fresh)
        {
            var key = note.SubjectName.ToUpperInvariant();
            if (subjects.ContainsKey(key) || created.ContainsKey(key)) continue;
            created[key] = new Subject { Name = note.SubjectName, NameKey = key, Color = "", OwnerId = ownerId };
        }
        var colorIndex = 0;
        foreach (var subject in created.Values) subject.Color = SubjectEndpoints.Colors[colorIndex++ % SubjectEndpoints.Colors.Length];

        var items = new List<ImportItem>();
        var assignments = new List<Assignment>();
        foreach (var note in fresh)
        {
            var key = note.SubjectName.ToUpperInvariant();
            var subject = subjects.GetValueOrDefault(key) ?? created[key];
            items.Add(new ImportItem(note.Path, note.Title, subject.Name, note.Due, note.Status));
            assignments.Add(new Assignment
            {
                Title = note.Title, Description = note.Description, Subject = subject, OwnerId = ownerId,
                DueDate = note.Due, Status = note.Status, CreatedAt = DateTime.UtcNow, ImportKey = note.Key,
            });
        }

        if (save)
        {
            db.Subjects.AddRange(created.Values);
            db.Assignments.AddRange(assignments);
            await db.SaveChangesAsync(token);
        }
        return new ImportReport(fresh.Count, existingCount, notHomework, fresh.Count(n => n.TitleCut || n.DescriptionCut),
            created.Values.Select(s => s.Name).ToList(), skipped, items);
    }

    private static List<ImportFile> Validate(ImportRequest request)
    {
        var files = request.Files;
        if (files is null || files.Count == 0) throw new ImportException("Выберите файлы заметок.");
        if (files.Count > MaxFiles) throw new ImportException("Слишком много файлов. Выберите папку с заданиями.");
        if (request.DefaultDue is { } due && (due < FirstDue || due > LastDue))
            throw new ImportException("Выберите срок между 2000 и 2100 годом.");
        long total = 0;
        foreach (var file in files)
            if (file?.Text is { } text) total += Encoding.UTF8.GetByteCount(text);
        if (total > MaxTotalBytes) throw new ImportException("Слишком большая загрузка. Выберите папку с заданиями.");
        return files;
    }
}
