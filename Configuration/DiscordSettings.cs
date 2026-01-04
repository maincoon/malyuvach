namespace Malyuvach.Configuration;

public class DiscordSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Discord bot token.
    /// </summary>
    public string BotKey { get; set; } = string.Empty;

    /// <summary>
    /// If set, repost generated images to this channel.
    /// </summary>
    public ulong BotShowRoomChannelId { get; set; }
}
