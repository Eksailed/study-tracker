using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

/// <summary>Предметы и задания группы: право ведения, вид задания для студента, личная отметка.</summary>
public static class GroupAssignments
{
    public const string ApiTokenRefusal = "В токене API группы не поддерживаются.";
    public const string MoveRefusal = "Нельзя перенести задание между личным списком и группой.";
    public const string BadStatus = "Выберите допустимый статус.";

    /// <summary>
    /// Предметы и задания группы ведёт её староста. Владелец без членства их не ведёт: он должен назначить себя старостой,
    /// поэтому здесь не <see cref="Access.CanManage"/>.
    /// </summary>
    public static bool Leads(Access access, int groupId) =>
        access.Membership is { Role: GroupRole.Head, Status: MemberStatus.Active } m && m.GroupId == groupId;

    /// <summary>Группа, записи которой видны аккаунту: только если он в ней подтверждён.</summary>
    public static int? VisibleGroup(Access access) =>
        access.Membership is { Status: MemberStatus.Active } m ? m.GroupId : null;

    private static IQueryable<Assignment> Visible(TrackerDb db, Access access)
    {
        var me = access.UserId;
        var group = VisibleGroup(access);
        return db.Assignments.AsNoTracking()
            .Include(a => a.Subtasks.Where(s => s.UserId == null || s.UserId == me).OrderBy(s => s.Position))
            .Where(a => a.OwnerId == me || (group != null && a.GroupId == group));
    }

    /// <summary>У группового задания подставляет статус из отметки этого студента (иначе <c>Todo</c>). Записи не отслеживаются, база не меняется.</summary>
    private static async Task<List<Assignment>> WithOwnStatusAsync(TrackerDb db, Access access, List<Assignment> items)
    {
        var grouped = items.Where(a => a.GroupId != null).ToList();
        if (grouped.Count == 0) return items;
        var ids = grouped.Select(a => a.Id).ToList();
        var marks = await db.GroupProgress.AsNoTracking()
            .Where(p => p.UserId == access.UserId && ids.Contains(p.AssignmentId))
            .ToDictionaryAsync(p => p.AssignmentId, p => p.Status);
        foreach (var item in grouped) item.Status = marks.GetValueOrDefault(item.Id, AssignmentStatus.Todo);
        return items;
    }

    /// <summary>Личные задания и задания своей группы в том виде, как их видит этот аккаунт.</summary>
    public static async Task<List<Assignment>> ListAsync(TrackerDb db, Access access) =>
        await WithOwnStatusAsync(db, access,
            await Visible(db, access).OrderBy(a => a.DueDate).ThenBy(a => a.Id).ToListAsync());

    public static async Task<Assignment?> ViewAsync(TrackerDb db, Access access, int id) =>
        (await WithOwnStatusAsync(db, access, await Visible(db, access).Where(a => a.Id == id).ToListAsync())).SingleOrDefault();

    /// <summary>Ставит личную отметку; повторная меняет прежнюю. При гонке двух первых отметок проигравший обновляет строку победителя.</summary>
    public static async Task SetProgressAsync(TrackerDb db, int assignmentId, string userId, AssignmentStatus status)
    {
        Task<int> Update() => db.GroupProgress.Where(p => p.AssignmentId == assignmentId && p.UserId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, status));
        if (await Update() > 0) return;
        db.GroupProgress.Add(new GroupProgress { AssignmentId = assignmentId, UserId = userId, Status = status });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            await Update();
        }
    }
}
