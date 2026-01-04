using Discord;
using Discord.WebSocket;
using Malyuvach.Configuration;
using Malyuvach.Services.Image;
using Malyuvach.Services.LLM;
using Microsoft.Extensions.Options;

namespace Malyuvach.Services.Discord;

public class DiscordService : IDiscordService
{
    private readonly ILogger<DiscordService> _logger;
    private readonly DiscordSettings _settings;
    private readonly ILLMService _llmService;
    private readonly IImageService _imageService;

    private readonly DiscordSocketClient _client;
    private readonly Random _random = new();

    public DiscordService(
        ILogger<DiscordService> logger,
        IOptions<DiscordSettings> settings,
        ILLMService llmService,
        IImageService imageService)
    {
        _logger = logger;
        _settings = settings.Value;
        _llmService = llmService;
        _imageService = imageService;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds |
                            GatewayIntents.GuildMessages |
                            GatewayIntents.DirectMessages |
                            GatewayIntents.MessageContent
        });

        _client.Log += OnDiscordLog;
        _client.MessageReceived += OnMessageReceivedAsync;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Discord is disabled (Malyuvach:Discord:Enabled=false)");
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.BotKey))
        {
            _logger.LogWarning("Discord is enabled but BotKey is empty");
            return;
        }

        await _client.LoginAsync(TokenType.Bot, _settings.BotKey);
        await _client.StartAsync();

        _logger.LogInformation("Discord bot started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
            return;

        try
        {
            await _client.StopAsync();
            await _client.LogoutAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping Discord client");
        }
    }

    private Task OnDiscordLog(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.Information
        };

        if (message.Exception != null)
            _logger.Log(level, message.Exception, "Discord: {Message}", message.Message);
        else
            _logger.Log(level, "Discord: {Message}", message.Message);

        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(SocketMessage rawMessage)
    {
        if (!_settings.Enabled)
            return;

        if (rawMessage is not SocketUserMessage message)
            return;

        if (message.Author.IsBot)
            return;

        // Ignore system / non-user channels
        if (message.Channel is not IMessageChannel channel)
            return;

        var isPrivate = message.Channel is IDMChannel;

        // Trigger rules: private always; in guild channels only if mentioned or replied-to-bot
        var currentUser = _client.CurrentUser;
        if (currentUser == null)
            return;

        var isBotMentioned = message.MentionedUsers.Any(u => u.Id == currentUser.Id);

        var isReplyToBot = false;
        if (message.Reference?.MessageId.IsSpecified == true)
        {
            try
            {
                var referenced = await channel.GetMessageAsync(message.Reference.MessageId.Value);
                if (referenced?.Author?.Id == currentUser.Id)
                    isReplyToBot = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve referenced message for reply detection");
            }
        }

        if (!isPrivate && !isBotMentioned && !isReplyToBot)
            return;

        var content = message.Content ?? string.Empty;

        // Remove bot mention tokens from message
        content = content
            .Replace($"<@{currentUser.Id}>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace($"<@!{currentUser.Id}>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (string.IsNullOrWhiteSpace(content))
            return;

        var contextId = $"discord:{message.Channel.Id}";

        try
        {
            if (isPrivate)
            {
                _logger.LogInformation("INPUT: private {Username}({UserId}): {Message}",
                    message.Author.Username,
                    message.Author.Id,
                    content);
            }
            else if (message.Channel is SocketGuildChannel guildChannel)
            {
                _logger.LogInformation("INPUT: group {Username}({UserId})@{Guild}/{Channel}({ChannelId}): {Message}",
                    message.Author.Username,
                    message.Author.Id,
                    guildChannel.Guild.Name,
                    guildChannel.Name,
                    guildChannel.Id,
                    content);
            }
            else
            {
                _logger.LogInformation("INPUT: group {Username}({UserId})@{ChannelId}: {Message}",
                    message.Author.Username,
                    message.Author.Id,
                    message.Channel.Id,
                    content);
            }

            await channel.TriggerTypingAsync();

            var answer = await _llmService.GetAnswerAsync(content, contextId, CancellationToken.None);
            if (answer == null)
            {
                await channel.SendMessageAsync("Sorry, I couldn't process your request. Please try again.");
                return;
            }

            if (!string.IsNullOrEmpty(answer.text) &&
                (string.IsNullOrEmpty(answer.prompt) || answer.prompt.Length < 10))
            {
                var text = TrimDiscordText(answer.text);
                _logger.LogInformation("OUTPUT: {Where} -> {Message}", isPrivate ? "private" : "group", text);
                await channel.SendMessageAsync(text, messageReference: new MessageReference(message.Id));
                return;
            }

            if (!string.IsNullOrEmpty(answer.prompt))
            {
                var seed = _random.NextInt64();
                var imageData = await _imageService.CreateImageAsync(
                    answer.prompt,
                    string.Empty,
                    answer.orientation ?? "landscape",
                    seed);

                if (imageData == null)
                {
                    var fallback = TrimDiscordText(answer.text ?? "Sorry, I couldn't generate an image. Please try again.");
                    _logger.LogInformation("OUTPUT: {Where} -> {Message}", isPrivate ? "private" : "group", fallback);
                    await channel.SendMessageAsync(fallback, messageReference: new MessageReference(message.Id));
                    return;
                }

                var caption = TrimDiscordText(answer.text ?? answer.prompt);
                _logger.LogInformation("OUTPUT: {Where} -> [IMAGE] {Caption}", isPrivate ? "private" : "group", caption);

                await using var imageStream = new MemoryStream(imageData);
                await channel.SendFileAsync(
                    imageStream,
                    "malyuvach.png",
                    caption,
                    messageReference: new MessageReference(message.Id));

                if (_settings.BotShowRoomChannelId != 0)
                {
                    try
                    {
                        var showroom = _client.GetChannel(_settings.BotShowRoomChannelId) as IMessageChannel;
                        if (showroom != null)
                        {
                            await using var showroomStream = new MemoryStream(imageData);
                            await showroom.SendFileAsync(showroomStream, "malyuvach.png", TrimDiscordText(answer.prompt));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send image to Discord showroom channel {ChannelId}", _settings.BotShowRoomChannelId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Discord message");
            try
            {
                await channel.SendMessageAsync("Ой якесь лишенько трапилось...", messageReference: new MessageReference(message.Id));
            }
            catch
            {
                // ignore secondary failures
            }
        }
    }

    private static string TrimDiscordText(string text)
    {
        // Discord max message length is 2000 chars.
        const int max = 1900;
        if (string.IsNullOrEmpty(text))
            return text;

        if (text.Length <= max)
            return text;

        return text.Substring(0, max) + "...";
    }
}
