namespace StudyTracker.Notifications;

public sealed class TelegramOptions
{
    public string BotToken { get; init; } = "";
    public string? PublicUrl { get; init; }
    public string? ApiBase { get; init; }
    public bool ManualMode { get; init; }
    public int SendTimeoutSeconds { get; init; } = 10;
    public int PollTimeoutSeconds { get; init; } = 40;
    public bool Configured => System.Text.RegularExpressions.Regex.IsMatch(BotToken, @"^\d+:[A-Za-z0-9_-]+$");
}
