using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

public static class SubjectEndpoints
{
    public static readonly string[] Colors = ["blue", "teal", "purple", "orange", "pink", "slate"];

    /// <summary>
    /// Группы доступны только по адресам <c>/api</c>: токен API (<c>prefix</c> другой) видит и меняет лишь личные предметы.
    /// </summary>
    public static void MapSubjects(this RouteGroupBuilder group, string prefix = "/api")
    {
        var groups = prefix == "/api";

        group.MapGet("/subjects", async (TrackerDb db, ClaimsPrincipal user) =>
        {
            if (!groups)
                return Results.Ok(await db.Subjects.Where(s => s.OwnerId == user.FindFirstValue(ClaimTypes.NameIdentifier))
                    .OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, s.Color }).ToListAsync());
            var access = await GroupAccess.LoadAsync(db, user);
            var visible = GroupAssignments.VisibleGroup(access);
            return Results.Ok(await db.Subjects.AsNoTracking()
                .Where(s => s.OwnerId == access.UserId || (visible != null && s.GroupId == visible))
                .OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, s.Color, s.GroupId }).ToListAsync());
        });

        group.MapPost("/subjects", async (SubjectInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (input.GroupId is not null && !groups) return Results.BadRequest(new { error = GroupAssignments.ApiTokenRefusal });
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (input.GroupId is { } groupId && !GroupAssignments.Leads(await GroupAccess.LoadAsync(db, user), groupId))
                return Results.NotFound();
            var name = input.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 80 || !Colors.Contains(input.Color))
                return Results.BadRequest(new { error = "Укажите название предмета до 80 символов и выберите цвет." });
            var subject = new Subject { Name = name, NameKey = name.ToUpperInvariant(), Color = input.Color! };
            if (input.GroupId is { } id) subject.GroupId = id;
            else subject.OwnerId = owner;
            db.Subjects.Add(subject);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Такой предмет уже есть." }); }
            object created = groups ? new { subject.Id, subject.Name, subject.Color, subject.GroupId } : new { subject.Id, subject.Name, subject.Color };
            return Results.Created($"{prefix}/subjects/{subject.Id}", created);
        });

        group.MapDelete("/subjects/{id:int}", async (int id, TrackerDb db, ClaimsPrincipal user) =>
        {
            var subject = await db.Subjects.SingleOrDefaultAsync(s => s.Id == id);
            if (subject is null) return Results.NotFound();
            if (subject.GroupId is { } groupId)
            {
                if (!groups || !GroupAssignments.Leads(await GroupAccess.LoadAsync(db, user), groupId)) return Results.NotFound();
            }
            else if (subject.OwnerId != user.FindFirstValue(ClaimTypes.NameIdentifier)) return Results.NotFound();
            if (await db.Assignments.AnyAsync(a => a.SubjectId == id))
                return Results.Conflict(new { error = "Сначала удалите задания предмета или перенесите их в другой предмет." });
            db.Subjects.Remove(subject);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "У предмета появились задания. Обновите страницу." }); }
            return Results.NoContent();
        });
    }
}
