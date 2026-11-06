namespace StudyTracker.Notifications;

public sealed class TelegramOptions
{
    public string BotToken { get; init; } = "";
    public string? PublicUrl { get; init; }
    public bool Configured => !string.IsNullOrWhiteSpace(BotToken);
}
