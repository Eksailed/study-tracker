using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

/// <summary>
/// Шаги задания. Только для вошедшего по cookie: в <c>/api/v1</c> их нет. Шаги личного задания принадлежат его владельцу
/// (<c>UserId</c> пуст), шаги задания группы — каждому студенту свои: выборки, правки, нумерация и предел 30 считаются по его строкам.
/// </summary>
public static class SubtaskEndpoints
{
    public const int MaxSubtasks = 30;
    public const int MaxTitleLength = 140;

    private static string? CheckTitle(string? title) => string.IsNullOrWhiteSpace(title) ? "Введите название шага."
        : title.Trim().Length > MaxTitleLength ? $"Название шага не длиннее {MaxTitleLength} символов." : null;

    public static void MapSubtasks(this RouteGroupBuilder group)
    {
        group.MapPost("/assignments/{id:int}/subtasks", async (int id, SubtaskInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (await ScopeAsync(db, user, id) is not { } scope) return Results.NotFound();
            var error = CheckTitle(input.Title);
            if (error is not null) return Results.BadRequest(new { error });
            var count = await db.Subtasks.CountAsync(s => s.AssignmentId == id && s.UserId == scope.StepOwner);
            if (count >= MaxSubtasks) return Results.BadRequest(new { error = $"У задания не больше {MaxSubtasks} шагов." });
            var subtask = new Subtask { AssignmentId = id, Title = input.Title!.Trim(), Position = count, UserId = scope.StepOwner };
            db.Subtasks.Add(subtask);
            await db.SaveChangesAsync();
            return Results.Created($"/api/assignments/{id}/subtasks/{subtask.Id}", subtask);
        });

        group.MapPut("/assignments/{id:int}/subtasks/{subId:int}", async (int id, int subId, SubtaskInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            var subtask = await Find(db, user, id, subId);
            if (subtask is null) return Results.NotFound();
            if (input.Title is null && input.Done is null) return Results.BadRequest(new { error = "Передайте название или отметку." });
            if (input.Title is not null)
            {
                var error = CheckTitle(input.Title);
                if (error is not null) return Results.BadRequest(new { error });
                subtask.Title = input.Title.Trim();
            }
            if (input.Done is { } done) subtask.Done = done;
            await db.SaveChangesAsync();
            return Results.Ok(subtask);
        });

        group.MapDelete("/assignments/{id:int}/subtasks/{subId:int}", async (int id, int subId, TrackerDb db, ClaimsPrincipal user) =>
        {
            var subtask = await Find(db, user, id, subId);
            if (subtask is null) return Results.NotFound();
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Subtasks.Remove(subtask);
            await db.SaveChangesAsync();
            var rest = await db.Subtasks.Where(s => s.AssignmentId == id && s.UserId == subtask.UserId).OrderBy(s => s.Position).ToListAsync();
            for (var i = 0; i < rest.Count; i++) rest[i].Position = i;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        });

        group.MapPut("/assignments/{id:int}/subtasks/order", async (int id, SubtaskOrder input, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (await ScopeAsync(db, user, id) is not { } scope) return Results.NotFound();
            var steps = await db.Subtasks.Where(s => s.AssignmentId == id && s.UserId == scope.StepOwner).ToListAsync();
            var ids = input.Ids;
            if (ids is null || ids.Length == 0 || ids.Length != steps.Count || ids.Distinct().Count() != ids.Length
                || !ids.All(stepId => steps.Any(s => s.Id == stepId)))
                return Results.BadRequest(new { error = "Укажите все шаги задания по одному разу." });
            for (var i = 0; i < ids.Length; i++) steps.Single(s => s.Id == ids[i]).Position = i;
            await db.SaveChangesAsync();
            return Results.Ok(steps.OrderBy(s => s.Position).ToList());
        });
    }

    /// <param name="StepOwner">Чьи шаги видит аккаунт в этом задании: <c>null</c> у личного задания, его <c>Id</c> у задания группы.</param>
    private sealed record StepScope(string? StepOwner);

    /// <summary>Доступно ли аккаунту задание для шагов: личное — владельцу, групповое — подтверждённому участнику этой группы.</summary>
    private static async Task<StepScope?> ScopeAsync(TrackerDb db, ClaimsPrincipal user, int assignmentId)
    {
        var me = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var task = await db.Assignments.AsNoTracking().Where(a => a.Id == assignmentId)
            .Select(a => new { a.OwnerId, a.GroupId }).SingleOrDefaultAsync();
        if (task is null) return null;
        if (task.GroupId is { } groupId) return (await GroupAccess.LoadAsync(db, user)).IsActiveIn(groupId) ? new StepScope(me) : null;
        return task.OwnerId == me ? new StepScope(null) : null;
    }

    /// <summary>Шаг находится только через своё задание и среди своих шагов: чужое задание и чужой шаг дают одинаковое «не найдено».</summary>
    private static async Task<Subtask?> Find(TrackerDb db, ClaimsPrincipal user, int assignmentId, int subtaskId)
    {
        if (await ScopeAsync(db, user, assignmentId) is not { } scope) return null;
        return await db.Subtasks.SingleOrDefaultAsync(s => s.Id == subtaskId && s.AssignmentId == assignmentId && s.UserId == scope.StepOwner);
    }
}
