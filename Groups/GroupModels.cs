namespace StudyTracker;

public enum GroupRole { Student, Head }

public enum MemberStatus { Pending, Active }

public class Group
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string NameKey { get; set; }
    /// <summary>SHA-256 секрета ссылки для гостей; <c>null</c> — ссылки нет.</summary>
    public string? GuestKeyHash { get; set; }
    public DateTime? GuestKeyCreatedAt { get; set; }
}

/// <summary>Членство или заявка. Уникальный <c>UserId</c>: человек состоит в одной группе.</summary>
public class GroupMember
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public required string UserId { get; set; }
    public GroupRole Role { get; set; }
    public MemberStatus Status { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Личная отметка студента по заданию группы. Нет строки — значит <c>Todo</c>.</summary>
public class GroupProgress
{
    public int Id { get; set; }
    public int AssignmentId { get; set; }
    public required string UserId { get; set; }
    public AssignmentStatus Status { get; set; }
}
