using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

public record GroupNameInput(string? Name);

/// <summary>Раздел владельца: группы, поиск пользователей, старосты. Не владелец получает <c>404</c> на любой адрес.</summary>
public static class OwnerEndpoints
{
    private const string DuplicateName = "Такая группа уже есть.";

    private static async Task<bool> IsOwnerAsync(TrackerDb db, ClaimsPrincipal user) => (await GroupAccess.LoadAsync(db, user)).IsOwner;

    private static object MemberEntry(GroupMember m, string? email) =>
        new { m.UserId, email, role = m.Role.ToString(), status = m.Status.ToString() };

    public static void MapOwner(this RouteGroupBuilder group)
    {
        var owner = group.MapGroup("/owner");

        owner.MapGet("/groups", async (TrackerDb db, ClaimsPrincipal user) =>
        {
            if (!await IsOwnerAsync(db, user)) return Results.NotFound();
            var groups = await db.Groups.AsNoTracking().OrderBy(g => g.NameKey).ToListAsync();
            var members = await (from m in db.GroupMembers.AsNoTracking()
                                 join u in db.Users on m.UserId equals u.Id
                                 select new { m.GroupId, m.UserId, u.Email, m.Role, m.Status }).ToListAsync();
            return Results.Ok(groups.Select(g => new
            {
                g.Id,
                g.Name,
                members = members.Count(m => m.GroupId == g.Id && m.Status == MemberStatus.Active),
                pending = members.Count(m => m.GroupId == g.Id && m.Status == MemberStatus.Pending),
                heads = members.Where(m => m.GroupId == g.Id && m.Status == MemberStatus.Active && m.Role == GroupRole.Head)
                    .OrderBy(m => m.Email).Select(m => new { m.UserId, m.Email }),
            }));
        });

        owner.MapPost("/groups", async (GroupNameInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (!await IsOwnerAsync(db, user)) return Results.NotFound();
            if (GroupNames.Normalize(input.Name) is not var (name, key))
                return Results.BadRequest(new { error = "Название группы — от 1 до 60 символов." });
            if (await db.Groups.AnyAsync(g => g.NameKey == key)) return Results.Conflict(new { error = DuplicateName });
            var created = new Group { Name = name, NameKey = key };
            db.Groups.Add(created);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { error = DuplicateName }); }
            return Results.Created($"/api/owner/groups/{created.Id}", new { created.Id, created.Name });
        });

        owner.MapPut("/groups/{gid:int}", async (int gid, GroupNameInput input, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (!await IsOwnerAsync(db, user)) return Results.NotFound();
            var target = await db.Groups.SingleOrDefaultAsync(g => g.Id == gid);
            if (target is null) return Results.NotFound();
            if (GroupNames.Normalize(input.Name) is not var (name, key))
                return Results.BadRequest(new { error = "Название группы — от 1 до 60 символов." });
            if (await db.Groups.AnyAsync(g => g.NameKey == key && g.Id != gid)) return Results.Conflict(new { error = DuplicateName });
            target.Name = name;
            target.NameKey = key;
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { error = DuplicateName }); }
            return Results.Ok(new { target.Id, target.Name });
        });

        owner.MapDelete("/groups/{gid:int}", async (int gid, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (!await IsOwnerAsync(db, user)) return Results.NotFound();
            if (!await db.Groups.AnyAsync(g => g.Id == gid)) return Results.NotFound();
            // Задание ссылается на предмет без каскада, поэтому порядок такой: задания, предметы, группа.
            // Отметки и шаги уходят каскадом вместе с заданиями, участники — вместе с группой.
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Assignments.Where(a => a.GroupId == gid).ExecuteDeleteAsync();
            await db.Subjects.Where(s => s.GroupId == gid).ExecuteDeleteAsync();
            await db.Groups.Where(g => g.Id == gid).ExecuteDeleteAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        });

        owner.MapGet("/users", async (string? q, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (!await IsOwnerAsync(db, user)) return Results.NotFound();
            var needle = (q ?? "").Trim().ToLowerInvariant();
            var users = await db.Users.AsNoTracking()
                .Where(u => u.Email != null && u.Email.ToLower().Contains(needle))
                .OrderBy(u => u.Email).Take(50)
                .Select(u => new { u.Id, u.Email, u.IsOwner }).ToListAsync();
            var ids = users.Select(u => u.Id).ToList();
            var memberships = await (from m in db.GroupMembers.AsNoTracking()
                                     join g in db.Groups on m.GroupId equals g.Id
                                     where ids.Contains(m.UserId)
                                     select new { m.UserId, GroupId = g.Id, g.Name, m.Role, m.Status }).ToListAsync();
            return Results.Ok(users.Select(u => new
            {
                u.Id,
                u.Email,
                u.IsOwner,
                group = memberships.Where(m => m.UserId == u.Id)
                    .Select(m => new { id = m.GroupId, name = m.Name, role = m.Role.ToString(), status = m.Status.ToString() })
                    .FirstOrDefault(),
            }));
        });

        owner.MapPut("/groups/{gid:int}/heads/{userId}", async (int gid, string userId, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (!await IsOwnerAsync(db, user)) return Results.NotFound();
            if (!await db.Groups.AnyAsync(g => g.Id == gid)) return Results.NotFound();
            var person = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId);
            if (person is null) return Results.NotFound();
            var member = await db.GroupMembers.SingleOrDefaultAsync(m => m.UserId == userId);
            if (member is not null && member.GroupId != gid) return Results.Conflict(new { error = "Состоит в другой группе." });
            if (member is null) db.GroupMembers.Add(member = new GroupMember { GroupId = gid, UserId = userId });
            member.Role = GroupRole.Head;
            member.Status = MemberStatus.Active;
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Состоит в другой группе." }); }
            return Results.Ok(MemberEntry(member, person.Email));
        });

        owner.MapDelete("/groups/{gid:int}/heads/{userId}", async (int gid, string userId, TrackerDb db, ClaimsPrincipal user) =>
        {
            if (!await IsOwnerAsync(db, user)) return Results.NotFound();
            var member = await db.GroupMembers.SingleOrDefaultAsync(m =>
                m.GroupId == gid && m.UserId == userId && m.Role == GroupRole.Head && m.Status == MemberStatus.Active);
            if (member is null) return Results.NotFound();
            member.Role = GroupRole.Student;
            await db.SaveChangesAsync();
            var email = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).SingleAsync();
            return Results.Ok(MemberEntry(member, email));
        });
    }
}
