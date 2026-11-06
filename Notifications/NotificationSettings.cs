using Microsoft.EntityFrameworkCore;

namespace StudyTracker.Notifications;

public enum ReminderKind { Before, Due, Test }
public enum DeliveryState { Pending, Sending, Sent, Failed, Unknown, Cancelled, Skipped }

public class NotificationSettings
{
    public required string OwnerId { get; set; }
    public bool Enabled { get; set; }
    public string TimeZone { get; set; } = "Europe/Moscow";
    public bool BeforeEnabled { get; set; } = true;
    public int BeforeMinute { get; set; } = 19 * 60;
    public bool DueEnabled { get; set; } = true;
    public int DueMinute { get; set; } = 9 * 60;
    public long BindingVersion { get; set; }
    public long CodeWindowStartedAt { get; set; }
    public int CodeIssueCount { get; set; }
    public long? LastTestAt { get; set; }
}

public class TelegramConnection
{
    public required string OwnerId { get; set; }
    public long ChatId { get; set; }
    public long BotId { get; set; }
    public string? Problem { get; set; }
}

public class TelegramLinkCode
{
    public required string OwnerId { get; set; }
    public required string Hash { get; set; }
    public long ExpiresAt { get; set; }
}

public class TelegramUpdateState
{
    public int Id { get; set; } = 1;
    public long BotId { get; set; }
    public long NextOffset { get; set; }
}

public class ReminderDelivery
{
    public long Id { get; set; }
    public required string OwnerId { get; set; }
    // История остаётся после удаления задания; текст задания здесь не хранится.
    public int? AssignmentId { get; set; }
    public DateOnly? DueDate { get; set; }
    public ReminderKind Kind { get; set; }
    public long ScheduledAt { get; set; }
    public long NextAttemptAt { get; set; }
    public long ExpiresAt { get; set; }
    public long BindingVersion { get; set; }
    public DeliveryState State { get; set; }
    public int Attempts { get; set; }
    public long? MessageId { get; set; }
    public long? SentAt { get; set; }
    public string? Problem { get; set; }
}

public static class NotificationModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<NotificationSettings>().HasKey(s => s.OwnerId);
        model.Entity<TelegramConnection>().HasKey(s => s.OwnerId);
        model.Entity<TelegramConnection>().HasIndex(s => s.ChatId).IsUnique();
        model.Entity<TelegramLinkCode>().HasKey(s => s.OwnerId);
        model.Entity<TelegramLinkCode>().HasIndex(s => s.Hash).IsUnique();
        model.Entity<NotificationSettings>().HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.OwnerId);
        model.Entity<TelegramConnection>().HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.OwnerId);
        model.Entity<TelegramLinkCode>().HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.OwnerId);
        model.Entity<ReminderDelivery>().HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.OwnerId);
        model.Entity<ReminderDelivery>().HasIndex(d => new { d.AssignmentId, d.DueDate, d.Kind }).IsUnique();
        model.Entity<ReminderDelivery>().HasIndex(d => new { d.State, d.NextAttemptAt });
    }
}
