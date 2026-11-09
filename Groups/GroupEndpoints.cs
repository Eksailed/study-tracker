using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

/// <summary>Стирает следы студента в группе: его отметки и его шаги по заданиям группы.</summary>
public static class GroupCleanup
{
    public static async Task EraseMemberDataAsync(TrackerDb db, int groupId, string userId)
    {
        await db.GroupProgress.Where(p => p.UserId == userId
            && db.Assignments.Any(a => a.Id == p.AssignmentId && a.GroupId == groupId)).ExecuteDeleteAsync();
        await db.Subtasks.Where(s => s.UserId == userId
            && db.Assignments.Any(a => a.Id == s.AssignmentId && a.GroupId == groupId)).ExecuteDeleteAsync();
    }
}

public static class GroupEndpoints
{
    /// <summary>Группа есть и текущий пользователь её ведёт (староста или владелец); иначе <c>null</c> — отвечаем <c>404</c>.</summary>
    private static async Task<Access?> ManagerOfAsync(TrackerDb db, ClaimsPrincipal user, int groupId)
    {
        var access = await GroupAccess.LoadAsync(db, user);
        if (!access.CanManage(groupId) || !await db.Groups.AnyAsync(g => g.Id == groupId)) return null;
        return access;
    }

    public static object GroupState(GroupMembership m) =>
        new { id = m.GroupId, name = m.GroupName, role = m.Role.ToString(), status = m.Status.ToString() };

    public static void MapGroups(this RouteGroupBuilder group)
    {
        group.MapGet("/groups", async (TrackerDb db) =>
            Results.Ok(await db.Groups.AsNoTracking().OrderBy(g => g.NameKey).Select(g => new { g.Id, g.Name }).ToListAsync()));

        group.MapPost("/groups/{gid:int}/join", async (int gid, TrackerDb db, ClaimsPrincipal user) =>
        {
            var access = await GroupAccess.LoadAsync(db, user);
            var target = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gid);
            if (target is null) return Results.NotFound();
            if (access.Membership is not null)
                return Results.Conflict(new { error = "Вы уже состоите в группе или подали заявку." });
            db.GroupMembers.Add(new GroupMember { GroupId = gid, UserId = access.UserId, Role = GroupRole.Student, Status = MemberStatus.Pending });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Вы уже состоите в группе или подали заявку." }); }
            return Results.Created("/api/auth/me", GroupState(new GroupMembership(gid, target.Name, GroupRole.Student, MemberStatus.Pending)));
        });

        group.MapDelete("/group/membership", async (TrackerDb db, ClaimsPrincipal user) =>
        {
            var access = await GroupAccess.LoadAsync(db, user);
            if (access.Membership is not { } membership) return Results.NotFound();
            if (membership.Role == GroupRole.Head) return Results.Conflict(new { error = "Старосту снимает владелец." });
            await using var transaction = await db.Database.BeginTransactionAsync();
            await GroupCleanup.EraseMemberDataAsync(db, membership.GroupId, access.UserId);
            await db.GroupMembers.Where(m => m.UserId == access.UserId).ExecuteDeleteAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        });

        group.MapGet("/groups/{gid:int}/members", async (int gid, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (await ManagerOfAsync(db, user, gid) is null) return Results.NotFound();
            var rows = await (from m in db.GroupMembers.AsNoTracking()
                              join u in db.Users on m.UserId equals u.Id
                              where m.GroupId == gid
                              orderby u.Email
                              select new { m.UserId, u.Email, m.Role, m.Status }).ToListAsync();
            return Results.Ok(rows.Select(r => new { r.UserId, r.Email, role = r.Role.ToString(), status = r.Status.ToString() }));
        });

        group.MapPost("/groups/{gid:int}/members/{userId}/approve", async (int gid, string userId, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (await ManagerOfAsync(db, user, gid) is null) return Results.NotFound();
            var member = await db.GroupMembers.SingleOrDefaultAsync(m => m.GroupId == gid && m.UserId == userId);
            if (member is null) return Results.NotFound();
            if (member.Status == MemberStatus.Active) return Results.Conflict(new { error = "Заявка уже подтверждена." });
            member.Status = MemberStatus.Active;
            await db.SaveChangesAsync();
            return Results.Ok(new { member.UserId, role = member.Role.ToString(), status = member.Status.ToString() });
        });

        group.MapDelete("/groups/{gid:int}/members/{userId}", async (int gid, string userId, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (await ManagerOfAsync(db, user, gid) is not { } access) return Results.NotFound();
            var member = await db.GroupMembers.SingleOrDefaultAsync(m => m.GroupId == gid && m.UserId == userId);
            if (member is null) return Results.NotFound();
            if (member.Role == GroupRole.Head && !access.IsOwner)
                return Results.Conflict(new { error = "Старосту снимает владелец." });
            await using var transaction = await db.Database.BeginTransactionAsync();
            await GroupCleanup.EraseMemberDataAsync(db, gid, userId);
            db.GroupMembers.Remove(member);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        });
    }
}
