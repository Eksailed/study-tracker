using Microsoft.EntityFrameworkCore;

namespace StudyTracker;

public enum AssignmentStatus { Todo, InProgress, Done }

public class Subject
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string NameKey { get; set; }
    public required string Color { get; set; }
}

public class Assignment
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public int SubjectId { get; set; }
    public Subject? Subject { get; set; }
    public DateOnly DueDate { get; set; }
    public AssignmentStatus Status { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TrackerDb(DbContextOptions<TrackerDb> options) : DbContext(options)
{
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<Assignment> Assignments => Set<Assignment>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Subject>().HasIndex(s => s.NameKey).IsUnique();
        model.Entity<Assignment>().HasOne(a => a.Subject).WithMany()
            .HasForeignKey(a => a.SubjectId).OnDelete(DeleteBehavior.Restrict);
    }
}

public record SubjectInput(string? Name, string? Color);
public record AssignmentInput(string? Title, string? Description, int SubjectId,
    DateOnly DueDate, AssignmentStatus Status);
