using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

public static class SubjectEndpoints
{
    public static readonly string[] Colors = ["blue", "teal", "purple", "orange", "pink", "slate"];

    public static void MapSubjects(this RouteGroupBuilder group, string prefix = "/api")
    {
        group.MapGet("/subjects", async (TrackerDb db, ClaimsPrincipal user) =>
            Results.Ok(await db.Subjects.Where(s => s.OwnerId == user.FindFirstValue(ClaimTypes.NameIdentifier))
                .OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, s.Color }).ToListAsync()));

        group.MapPost("/subjects", async (SubjectInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            var name = input.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 80 || !Colors.Contains(input.Color))
                return Results.BadRequest(new { error = "Укажите название предмета до 80 символов и выберите цвет." });
            var subject = new Subject { Name = name, NameKey = name.ToUpperInvariant(), Color = input.Color!,
                OwnerId = user.FindFirstValue(ClaimTypes.NameIdentifier)! };
            db.Subjects.Add(subject);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Такой предмет уже есть." }); }
            return Results.Created($"{prefix}/subjects/{subject.Id}", new { subject.Id, subject.Name, subject.Color });
        });

        group.MapDelete("/subjects/{id:int}", async (int id, TrackerDb db, ClaimsPrincipal user) =>
        {
            var subject = await db.Subjects.SingleOrDefaultAsync(s => s.Id == id && s.OwnerId == user.FindFirstValue(ClaimTypes.NameIdentifier));
            if (subject is null) return Results.NotFound();
            if (await db.Assignments.AnyAsync(a => a.SubjectId == id))
                return Results.Conflict(new { error = "Сначала удалите задания предмета или перенесите их в другой предмет." });
            db.Subjects.Remove(subject);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "У предмета появились задания. Обновите страницу." }); }
            return Results.NoContent();
        });
    }
}
