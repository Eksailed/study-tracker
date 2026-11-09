using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using StudyTracker.Notifications;

namespace StudyTracker;

public enum AssignmentStatus { Todo, InProgress, Done }

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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Ключ повтора при импорте из Obsidian; у заданий, созданных вручную, пуст.</summary>
    [JsonIgnore]
    public string? ImportKey { get; set; }
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
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<TelegramConnection> TelegramConnections => Set<TelegramConnection>();
    public DbSet<TelegramLinkCode> TelegramLinkCodes => Set<TelegramLinkCode>();
    public DbSet<TelegramUpdateState> TelegramUpdateStates => Set<TelegramUpdateState>();
    public DbSet<ReminderDelivery> ReminderDeliveries => Set<ReminderDelivery>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        NotificationModel.Configure(model);
        model.Entity<ApplicationUser>().HasIndex(u => u.NormalizedEmail).IsUnique();
        model.Entity<ApiToken>().HasIndex(t => t.Hash).IsUnique();
        model.Entity<ApiToken>().HasIndex(t => t.OwnerId);
        model.Entity<ApiToken>().HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.OwnerId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Subject>().HasIndex(s => new { s.OwnerId, s.NameKey }).IsUnique();
        model.Entity<Subject>().HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Restrict);
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
    DateOnly DueDate, AssignmentStatus Status);
