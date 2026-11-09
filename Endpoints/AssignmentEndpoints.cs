using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

public static class AssignmentEndpoints
{
    public static void MapAssignments(this RouteGroupBuilder group)
    {
        group.MapGet("/assignments", async (TrackerDb db, ClaimsPrincipal user) =>
            Results.Ok(await db.Assignments.AsNoTracking().Where(a => a.OwnerId == user.FindFirstValue(ClaimTypes.NameIdentifier))
                .OrderBy(a => a.DueDate).ThenBy(a => a.Id).ToListAsync()));

        group.MapPost("/assignments", async (AssignmentInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (!await db.Subjects.AnyAsync(s => s.Id == input.SubjectId && s.OwnerId == owner)) return Results.NotFound();
            var error = AssignmentValidator.Validate(input);
            if (error is not null) return Results.BadRequest(new { error });
            var assignment = new Assignment { Title = input.Title!.Trim(), Description = input.Description?.Trim() ?? "",
                SubjectId = input.SubjectId, OwnerId = owner, DueDate = input.DueDate, Status = input.Status };
            db.Assignments.Add(assignment);
            await db.SaveChangesAsync();
            return Results.Created($"/api/assignments/{assignment.Id}", assignment);
        });

        group.MapPut("/assignments/{id:int}", async (int id, AssignmentInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var assignment = await db.Assignments.SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == owner);
            if (assignment is null) return Results.NotFound();
            if (!await db.Subjects.AnyAsync(s => s.Id == input.SubjectId && s.OwnerId == owner)) return Results.NotFound();
            var error = AssignmentValidator.Validate(input);
            if (error is not null) return Results.BadRequest(new { error });
            assignment.Title = input.Title!.Trim();
            assignment.Description = input.Description?.Trim() ?? "";
            assignment.SubjectId = input.SubjectId;
            assignment.DueDate = input.DueDate;
            assignment.Status = input.Status;
            await db.SaveChangesAsync();
            return Results.Ok(assignment);
        });

        group.MapDelete("/assignments/{id:int}", async (int id, TrackerDb db, ClaimsPrincipal user) =>
        {
            var assignment = await db.Assignments.SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == user.FindFirstValue(ClaimTypes.NameIdentifier));
            if (assignment is null) return Results.NotFound();
            db.Assignments.Remove(assignment);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
