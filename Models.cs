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
}

public class ApplicationUser : IdentityUser;

public class TrackerDb(DbContextOptions<TrackerDb> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<TelegramConnection> TelegramConnections => Set<TelegramConnection>();
    public DbSet<TelegramLinkCode> TelegramLinkCodes => Set<TelegramLinkCode>();
    public DbSet<TelegramUpdateState> TelegramUpdateStates => Set<TelegramUpdateState>();
    public DbSet<ReminderDelivery> ReminderDeliveries => Set<ReminderDelivery>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        NotificationModel.Configure(model);
        model.Entity<ApplicationUser>().HasIndex(u => u.NormalizedEmail).IsUnique();
        model.Entity<Subject>().HasIndex(s => new { s.OwnerId, s.NameKey }).IsUnique();
        model.Entity<Subject>().HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Assignment>().HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Assignment>().HasOne(a => a.Subject).WithMany()
            .HasForeignKey(a => new { a.SubjectId, a.OwnerId })
            .HasPrincipalKey(s => new { s.Id, s.OwnerId }).OnDelete(DeleteBehavior.Restrict);
    }
}

public record SubjectInput(string? Name, string? Color);
public record AssignmentInput(string? Title, string? Description, int SubjectId,
    DateOnly DueDate, AssignmentStatus Status);
