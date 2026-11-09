using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

public static class AssignmentEndpoints
{
    /// <summary>
    /// Группы и личные отметки доступны только по адресам <c>/api</c>: токен API (<c>prefix</c> другой) видит и меняет лишь личные задания.
    /// </summary>
    public static void MapAssignments(this RouteGroupBuilder group, string prefix = "/api")
    {
        var groups = prefix == "/api";

        group.MapGet("/assignments", async (TrackerDb db, ClaimsPrincipal user) =>
        {
            if (groups) return Results.Ok(await GroupAssignments.ListAsync(db, await GroupAccess.LoadAsync(db, user)));
            return Results.Ok(await db.Assignments.AsNoTracking().Include(a => a.Subtasks.OrderBy(s => s.Position))
                .Where(a => a.OwnerId == user.FindFirstValue(ClaimTypes.NameIdentifier))
                .OrderBy(a => a.DueDate).ThenBy(a => a.Id).ToListAsync());
        }).Produces<List<Assignment>>();

        group.MapPost("/assignments", async (AssignmentInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (input.GroupId is not null && !groups) return Results.BadRequest(new { error = GroupAssignments.ApiTokenRefusal });
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (input.GroupId is { } groupId)
            {
                if (!GroupAssignments.Leads(await GroupAccess.LoadAsync(db, user), groupId)) return Results.NotFound();
                if (!await db.Subjects.AnyAsync(s => s.Id == input.SubjectId && s.GroupId == groupId)) return Results.NotFound();
            }
            else if (!await db.Subjects.AnyAsync(s => s.Id == input.SubjectId && s.OwnerId == owner)) return Results.NotFound();
            var error = AssignmentValidator.Validate(input);
            if (error is not null) return Results.BadRequest(new { error });
            var assignment = new Assignment { Title = input.Title!.Trim(), Description = input.Description?.Trim() ?? "",
                SubjectId = input.SubjectId, DueDate = input.DueDate, Priority = input.Priority ?? AssignmentPriority.Normal };
            if (input.GroupId is { } id) assignment.GroupId = id;
            else
            {
                assignment.OwnerId = owner;
                assignment.Status = input.Status;
            }
            db.Assignments.Add(assignment);
            await db.SaveChangesAsync();
            return Results.Created($"{prefix}/assignments/{assignment.Id}", assignment);
        }).Produces<Assignment>(StatusCodes.Status201Created);

        group.MapPut("/assignments/{id:int}", async (int id, AssignmentInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (input.GroupId is not null && !groups) return Results.BadRequest(new { error = GroupAssignments.ApiTokenRefusal });
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var assignment = await db.Assignments.Include(a => a.Subtasks.OrderBy(s => s.Position)).SingleOrDefaultAsync(a => a.Id == id);
            if (assignment is null) return Results.NotFound();
            Access? access = null;
            if (assignment.GroupId is { } groupId)
            {
                access = await GroupAccess.LoadAsync(db, user);
                if (!groups || !GroupAssignments.Leads(access, groupId)) return Results.NotFound();
            }
            else if (assignment.OwnerId != owner) return Results.NotFound();
            if (input.GroupId is not null && input.GroupId != assignment.GroupId)
                return Results.BadRequest(new { error = GroupAssignments.MoveRefusal });
            var subjectFits = assignment.GroupId is { } own
                ? await db.Subjects.AnyAsync(s => s.Id == input.SubjectId && s.GroupId == own)
                : await db.Subjects.AnyAsync(s => s.Id == input.SubjectId && s.OwnerId == owner);
            if (!subjectFits) return Results.NotFound();
            var error = AssignmentValidator.Validate(input);
            if (error is not null) return Results.BadRequest(new { error });
            assignment.Title = input.Title!.Trim();
            assignment.Description = input.Description?.Trim() ?? "";
            assignment.SubjectId = input.SubjectId;
            assignment.DueDate = input.DueDate;
            if (access is null) assignment.Status = input.Status;
            if (input.Priority is { } priority) assignment.Priority = priority;
            await db.SaveChangesAsync();
            return access is null ? Results.Ok(assignment) : Results.Ok(await GroupAssignments.ViewAsync(db, access, id));
        }).Produces<Assignment>();

        if (groups)
            group.MapPut("/assignments/{id:int}/progress", async (int id, ProgressInput input, TrackerDb db, ClaimsPrincipal user) =>
            {
                var access = await GroupAccess.LoadAsync(db, user);
                var groupId = await db.Assignments.Where(a => a.Id == id).Select(a => a.GroupId).SingleOrDefaultAsync();
                if (groupId is not { } target || !access.IsActiveIn(target)) return Results.NotFound();
                if (input.Status is not { } status || !Enum.IsDefined(status)) return Results.BadRequest(new { error = GroupAssignments.BadStatus });
                await GroupAssignments.SetProgressAsync(db, id, access.UserId, status);
                return Results.Ok(await GroupAssignments.ViewAsync(db, access, id));
            }).Produces<Assignment>();

        group.MapDelete("/assignments/{id:int}", async (int id, TrackerDb db, ClaimsPrincipal user) =>
        {
            var assignment = await db.Assignments.SingleOrDefaultAsync(a => a.Id == id);
            if (assignment is null) return Results.NotFound();
            if (assignment.GroupId is { } groupId)
            {
                if (!groups || !GroupAssignments.Leads(await GroupAccess.LoadAsync(db, user), groupId)) return Results.NotFound();
            }
            else if (assignment.OwnerId != user.FindFirstValue(ClaimTypes.NameIdentifier)) return Results.NotFound();
            db.Assignments.Remove(assignment);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
