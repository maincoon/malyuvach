namespace Malyuvach.Configuration;

public class TelegramSettings
{
    public string BotShowRoomChannel { get; set; } = string.Empty;
    public List<string> BotNames { get; set; } = new List<string>();
    public string BotKey { get; set; } = string.Empty;
    public bool SkipUpdates { get; set; }
}