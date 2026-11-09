using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using StudyTracker.Calendar;
using StudyTracker.Notifications;

namespace StudyTracker;

public enum AssignmentStatus { Todo, InProgress, Done }

public enum AssignmentPriority { Normal, Important, Urgent }

public class Subject
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string NameKey { get; set; }
    public required string Color { get; set; }
    public required string OwnerId { get; set; }
}

public class Assignment
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public int SubjectId { get; set; }
    public required string OwnerId { get; set; }
    public Subject? Subject { get; set; }
    public DateOnly DueDate { get; set; }
    public AssignmentStatus Status { get; set; }
    public AssignmentPriority Priority { get; set; } = AssignmentPriority.Normal;
    public List<Subtask> Subtasks { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Ключ повтора при импорте из Obsidian; у заданий, созданных вручную, пуст.</summary>
    [JsonIgnore]
    public string? ImportKey { get; set; }
}

/// <summary>Шаг задания. Порядок хранится в <c>Position</c> (0, 1, 2…) и пересчитывается без дыр.</summary>
public class Subtask
{
    public int Id { get; set; }
    [JsonIgnore]
    public int AssignmentId { get; set; }
    public required string Title { get; set; }
    public bool Done { get; set; }
    public int Position { get; set; }
}

public class ApplicationUser : IdentityUser;

/// <summary>Токен доступа к <c>/api/v1</c>. Сам токен не хранится: только SHA-256 и первые 8 символов для списка.</summary>
public class ApiToken
{
    public int Id { get; set; }
    public required string OwnerId { get; set; }
    public required string Name { get; set; }
    public required string Hash { get; set; }
    public required string Prefix { get; set; }
    public int Scopes { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long? LastUsedAt { get; set; }
    public long? RevokedAt { get; set; }
}

public class TrackerDb(DbContextOptions<TrackerDb> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Subtask> Subtasks => Set<Subtask>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<TelegramConnection> TelegramConnections => Set<TelegramConnection>();
    public DbSet<TelegramLinkCode> TelegramLinkCodes => Set<TelegramLinkCode>();
    public DbSet<TelegramUpdateState> TelegramUpdateStates => Set<TelegramUpdateState>();
    public DbSet<ReminderDelivery> ReminderDeliveries => Set<ReminderDelivery>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<CalendarLink> CalendarLinks => Set<CalendarLink>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        NotificationModel.Configure(model);
        model.Entity<ApplicationUser>().HasIndex(u => u.NormalizedEmail).IsUnique();
        model.Entity<ApiToken>().HasIndex(t => t.Hash).IsUnique();
        model.Entity<ApiToken>().HasIndex(t => t.OwnerId);
        model.Entity<ApiToken>().HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.OwnerId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CalendarLink>().Property(l => l.KeyHash).HasMaxLength(64);
        model.Entity<CalendarLink>().HasIndex(l => l.OwnerId).IsUnique();
        model.Entity<CalendarLink>().HasIndex(l => l.KeyHash).IsUnique();
        model.Entity<CalendarLink>().HasOne<ApplicationUser>().WithMany().HasForeignKey(l => l.OwnerId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Subject>().HasIndex(s => new { s.OwnerId, s.NameKey }).IsUnique();
        model.Entity<Subject>().HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Subtask>().Property(s => s.Title).HasMaxLength(140);
        model.Entity<Subtask>().HasIndex(s => s.AssignmentId);
        model.Entity<Assignment>().HasMany(a => a.Subtasks).WithOne().HasForeignKey(s => s.AssignmentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Assignment>().Property(a => a.ImportKey).HasMaxLength(300);
        model.Entity<Assignment>().HasIndex(a => new { a.OwnerId, a.ImportKey }).IsUnique().HasFilter("\"ImportKey\" IS NOT NULL");
        model.Entity<Assignment>().HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Assignment>().HasOne(a => a.Subject).WithMany()
            .HasForeignKey(a => new { a.SubjectId, a.OwnerId })
            .HasPrincipalKey(s => new { s.Id, s.OwnerId }).OnDelete(DeleteBehavior.Restrict);
    }
}

public record SubjectInput(string? Name, string? Color);
public record AssignmentInput(string? Title, string? Description, int SubjectId,
    DateOnly DueDate, AssignmentStatus Status, AssignmentPriority? Priority = null);

public record SubtaskInput(string? Title, bool? Done);
public record SubtaskOrder(int[]? Ids);
