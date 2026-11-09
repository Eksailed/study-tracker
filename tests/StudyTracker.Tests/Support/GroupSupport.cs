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

    public static string UserId(TrackerFactory factory, string email) => factory.Scope(services =>
        services.GetRequiredService<TrackerDb>().Users.Single(u => u.Email == email).Id);

    /// <summary>Предмет и задание группы напрямую в базе; возвращает их <c>Id</c>.</summary>
    public static (int SubjectId, int AssignmentId) AddGroupTask(TrackerFactory factory, int groupId,
        string subject = "Физика", string title = "Лабораторная") => factory.Scope(services =>
    {
        var db = services.GetRequiredService<TrackerDb>();
        var item = db.Subjects.SingleOrDefault(s => s.GroupId == groupId && s.NameKey == subject.ToUpperInvariant())
            ?? db.Subjects.Add(new Subject { Name = subject, NameKey = subject.ToUpperInvariant(), Color = "blue", GroupId = groupId }).Entity;
        db.SaveChanges();
        var task = db.Assignments.Add(new Assignment
        { Title = title, SubjectId = item.Id, GroupId = groupId, DueDate = new DateOnly(2026, 12, 12) }).Entity;
        db.SaveChanges();
        return (item.Id, task.Id);
    });

    /// <summary>Много заданий группы одним предметом сразу: заголовок и срок каждого задаются снаружи.</summary>
    public static void AddGroupTasks(TrackerFactory factory, int groupId, IEnumerable<(string Title, DateOnly Due)> tasks,
        string subject = "Физика") => factory.Scope(services =>
    {
        var db = services.GetRequiredService<TrackerDb>();
        var item = db.Subjects.SingleOrDefault(s => s.GroupId == groupId && s.NameKey == subject.ToUpperInvariant())
            ?? db.Subjects.Add(new Subject { Name = subject, NameKey = subject.ToUpperInvariant(), Color = "blue", GroupId = groupId }).Entity;
        db.SaveChanges();
        db.Assignments.AddRange(tasks.Select(t => new Assignment { Title = t.Title, SubjectId = item.Id, GroupId = groupId, DueDate = t.Due }));
        db.SaveChanges();
        return 0;
    });

    public static void AddProgress(TrackerFactory factory, int assignmentId, string email, AssignmentStatus status) => factory.Scope(services =>
    {
        var db = services.GetRequiredService<TrackerDb>();
        db.GroupProgress.Add(new GroupProgress
        { AssignmentId = assignmentId, UserId = db.Users.Single(u => u.Email == email).Id, Status = status });
        db.SaveChanges();
        return 0;
    });

    public static void AddStep(TrackerFactory factory, int assignmentId, string email, string title = "Шаг") => factory.Scope(services =>
    {
        var db = services.GetRequiredService<TrackerDb>();
        db.Subtasks.Add(new Subtask
        { AssignmentId = assignmentId, Title = title, UserId = db.Users.Single(u => u.Email == email).Id });
        db.SaveChanges();
        return 0;
    });
}
