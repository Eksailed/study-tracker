using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

public record GroupMembership(int GroupId, string GroupName, GroupRole Role, MemberStatus Status);

/// <summary>Что может текущий пользователь. Считается по базе на каждом запросе, поэтому снятие роли действует сразу.</summary>
public record Access(string UserId, bool IsOwner, GroupMembership? Membership)
{
    /// <summary>Подтверждённый участник этой группы (заявка не считается).</summary>
    public bool IsActiveIn(int groupId) => Membership is { Status: MemberStatus.Active } m && m.GroupId == groupId;

    public bool CanSeeGroup(int groupId) => IsActiveIn(groupId);

    /// <summary>Владелец (любая группа) или подтверждённый староста этой группы.</summary>
    public bool CanManage(int groupId) => IsOwner || (IsActiveIn(groupId) && Membership!.Role == GroupRole.Head);
}

public static class GroupAccess
{
    public static async Task<Access> LoadAsync(TrackerDb db, ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("В запросе нет идентификатора пользователя.");
        var isOwner = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.IsOwner).FirstOrDefaultAsync();
        var membership = await db.GroupMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Select(m => new GroupMembership(m.GroupId, db.Groups.Single(g => g.Id == m.GroupId).Name, m.Role, m.Status))
            .FirstOrDefaultAsync();
        return new Access(userId, isOwner, membership);
    }
}
