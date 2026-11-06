namespace StudyTracker.Notifications;

public sealed class TelegramOptions
{
    public string BotToken { get; init; } = "";
    public string? PublicUrl { get; init; }
    public string? ApiBase { get; init; }
    public bool ManualMode { get; init; }
    public bool Configured => System.Text.RegularExpressions.Regex.IsMatch(BotToken, @"^\d+:[A-Za-z0-9_-]+$");
}
