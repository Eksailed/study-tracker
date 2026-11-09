namespace StudyTracker.Tests.Support;

/// <summary>Прямые вставки в базу для тестов групп: аккаунт уже зарегистрирован через <c>ApiClient.SignIn</c>.</summary>
public static class GroupSupport
{
    public static void MakeOwner(TrackerFactory factory, string email) => factory.Scope(services =>
    {
        var db = services.GetRequiredService<TrackerDb>();
        db.Users.Single(u => u.Email == email).IsOwner = true;
        db.SaveChanges();
        return 0;
    });

    public static int CreateGroup(TrackerFactory factory, string name) => factory.Scope(services =>
    {
        var db = services.GetRequiredService<TrackerDb>();
        var group = new Group { Name = name, NameKey = name.ToUpperInvariant() };
        db.Groups.Add(group);
        db.SaveChanges();
        return group.Id;
    });

    public static void Join(TrackerFactory factory, int groupId, string email,
        GroupRole role = GroupRole.Student, MemberStatus status = MemberStatus.Active) => factory.Scope(services =>
    {
        var db = services.GetRequiredService<TrackerDb>();
        var userId = db.Users.Single(u => u.Email == email).Id;
        db.GroupMembers.Add(new GroupMember
        { GroupId = groupId, UserId = userId, Role = role, Status = status, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return 0;
    });
}
