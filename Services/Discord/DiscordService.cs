using Discord;
using Discord.Net;
using Discord.WebSocket;
using Malyuvach.Configuration;
using Malyuvach.Services.Image;
using Malyuvach.Services.LLM;
using Malyuvach.Services.STT;
using Microsoft.Extensions.Options;
using System.Net;
using System.Threading.Channels;

namespace Malyuvach.Services.Discord;

public class DiscordService : IDiscordService
{
    private readonly ILogger<DiscordService> _logger;
    private readonly DiscordSettings _settings;
    private readonly ILLMService _llmService;
    private readonly IImageService _imageService;
    private readonly ISTTService _sttService;
    private readonly HttpClient _httpClient;

    private readonly DiscordSocketClient _client;
    private readonly Random _random = new();

    private readonly Channel<WorkItem> _workQueue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(200)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private CancellationTokenSource? _processingCts;
    private Task? _processingTask;

    private static readonly TimeSpan[] SendRetryDelays =
    [
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    ];

    public DiscordService(
        ILogger<DiscordService> logger,
        IOptions<DiscordSettings> settings,
        ILLMService llmService,
        IImageService imageService,
        ISTTService sttService,
        HttpClient httpClient)
    {
        _logger = logger;
        _settings = settings.Value;
        _llmService = llmService;
        _imageService = imageService;
        _sttService = sttService;
        _httpClient = httpClient;

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

        // Process messages off the gateway thread to avoid heartbeat timeouts.
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessQueueAsync(_processingCts.Token), _processingCts.Token);

        _logger.LogInformation("Discord bot started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
            return;

        try
        {
            if (_processingCts != null)
            {
                _processingCts.Cancel();
                if (_processingTask != null)
                {
                    try
                    {
                        await _processingTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // expected on shutdown
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error stopping Discord processing task");
                    }
                }

                _processingCts.Dispose();
                _processingCts = null;
                _processingTask = null;
            }

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

    private Task OnMessageReceivedAsync(SocketMessage rawMessage)
    {
        if (!_settings.Enabled)
            return Task.CompletedTask;

        if (rawMessage is not SocketUserMessage message)
            return Task.CompletedTask;

        if (message.Author.IsBot)
            return Task.CompletedTask;

        // Ignore system / non-user channels
        if (message.Channel is not IMessageChannel channel)
            return Task.CompletedTask;

        var isPrivate = message.Channel is IDMChannel;

        // Trigger rules: private always; in guild channels only if mentioned or replied-to-bot.
        // IMPORTANT: do not block the gateway thread (no LLM/image/STT here).
        var currentUser = _client.CurrentUser;
        if (currentUser == null)
            return Task.CompletedTask;

        var isBotMentioned = message.MentionedUsers.Any(u => u.Id == currentUser.Id);

        var referencedMessageId = message.Reference?.MessageId.IsSpecified == true
            ? message.Reference!.MessageId.Value
            : (ulong?)null;

        // Fast pre-filter: if it's neither DM nor mention nor reply (reference present), ignore.
        if (!isPrivate && !isBotMentioned && referencedMessageId == null)
            return Task.CompletedTask;

        var attachments = message.Attachments
            .Select(a => new AttachmentInfo(a.Url, a.Filename, a.ContentType, a.Size))
            .ToList();

        var work = new WorkItem(
            ChannelId: channel.Id,
            MessageId: message.Id,
            AuthorId: message.Author.Id,
            AuthorUsername: message.Author.Username,
            IsPrivate: isPrivate,
            IsBotMentioned: isBotMentioned,
            ReferencedMessageId: referencedMessageId,
            Content: message.Content,
            Attachments: attachments);

        if (!_workQueue.Writer.TryWrite(work))
        {
            _logger.LogWarning("Discord work queue full; dropped message {MessageId} in channel {ChannelId}",
                message.Id, channel.Id);
        }

        return Task.CompletedTask;
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var work in _workQueue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await ProcessWorkItemAsync(work, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while processing Discord work item");
            }
        }
    }

    private async Task ProcessWorkItemAsync(WorkItem work, CancellationToken cancellationToken)
    {
        var currentUser = _client.CurrentUser;
        if (currentUser == null)
        {
            _logger.LogWarning("Discord current user is null; cannot process messages");
            return;
        }

        var channel = await ResolveMessageChannelAsync(work, cancellationToken);
        if (channel == null)
        {
            _logger.LogWarning("Discord channel {ChannelId} not found; cannot process message {MessageId} (IsPrivate={IsPrivate})",
                work.ChannelId, work.MessageId, work.IsPrivate);
            return;
        }

        // Resolve reply-to-bot if needed. We only enqueue messages that are DM/mentioned/or have a reference.
        var isReplyToBot = false;
        if (!work.IsPrivate && !work.IsBotMentioned && work.ReferencedMessageId.HasValue)
        {
            try
            {
                var referenced = await channel.GetMessageAsync(work.ReferencedMessageId.Value);
                if (referenced?.Author?.Id == currentUser.Id)
                    isReplyToBot = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve referenced message for reply detection");
            }

            if (!isReplyToBot)
            {
                _logger.LogInformation("Ignoring Discord message {MessageId} in channel {ChannelId} as it is not a DM, mention, or reply to bot",
                    work.MessageId, work.ChannelId);
                return;
            }
        }

        var content = string.Empty;

        // If message contains audio attachments, try to treat them as voice messages (Discord voice messages are attachments).
        var transcribed = await TryTranscribeAudioAttachmentAsync(channel, work.Attachments, cancellationToken);
        if (!string.IsNullOrWhiteSpace(transcribed))
        {
            // Mimic Telegram: echo transcription as a reply (without pinging anyone)
            await TrySendAsync(
                () => channel.SendMessageAsync(
                    transcribed,
                    messageReference: new MessageReference(work.MessageId),
                    allowedMentions: AllowedMentions.None),
                "Send transcription reply",
                cancellationToken);

            content = transcribed;
        }
        else if (!string.IsNullOrWhiteSpace(work.Content))
        {
            content = work.Content;
        }
        else
        {
            // No relevant content to process
            _logger.LogInformation("Discord message {MessageId} in channel {ChannelId} has no content or transcribable audio; ignoring",
                work.MessageId, work.ChannelId);
            return;
        }

        // Remove bot mention tokens from message
        content = content
            .Replace($"<@{currentUser.Id}>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace($"<@!{currentUser.Id}>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (string.IsNullOrWhiteSpace(content))
            return;

        var contextId = $"discord:{work.ChannelId}";

        try
        {
            if (work.IsPrivate)
            {
                _logger.LogInformation("INPUT: private {Username}({UserId}): {Message}",
                    work.AuthorUsername,
                    work.AuthorId,
                    content);
            }
            else if (channel is SocketGuildChannel guildChannel)
            {
                _logger.LogInformation("INPUT: group {Username}({UserId})@{Guild}/{Channel}({ChannelId}): {Message}",
                    work.AuthorUsername,
                    work.AuthorId,
                    guildChannel.Guild.Name,
                    guildChannel.Name,
                    guildChannel.Id,
                    content);
            }
            else
            {
                _logger.LogInformation("INPUT: group {Username}({UserId})@{ChannelId}: {Message}",
                    work.AuthorUsername,
                    work.AuthorId,
                    work.ChannelId,
                    content);
            }

            await TrySendAsync(() => channel.TriggerTypingAsync(), "Trigger typing", cancellationToken);

            var answer = await _llmService.GetAnswerAsync(content, contextId, cancellationToken);
            if (answer == null)
            {
                await TrySendAsync(
                    () => channel.SendMessageAsync(
                        "Sorry, I couldn't process your request. Please try again.",
                        allowedMentions: AllowedMentions.None),
                    "Send error message",
                    cancellationToken);
                return;
            }

            if (!string.IsNullOrEmpty(answer.text) &&
                (string.IsNullOrEmpty(answer.prompt) || answer.prompt.Length < 10))
            {
                var text = TrimDiscordText(answer.text);
                _logger.LogInformation("OUTPUT: {Where} -> {Message}", work.IsPrivate ? "private" : "group", text);
                await TrySendAsync(
                    () => channel.SendMessageAsync(
                        text,
                        messageReference: new MessageReference(work.MessageId),
                        allowedMentions: AllowedMentions.None),
                    "Send text reply",
                    cancellationToken);
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
                    _logger.LogInformation("OUTPUT: {Where} -> {Message}", work.IsPrivate ? "private" : "group", fallback);
                    await TrySendAsync(
                        () => channel.SendMessageAsync(
                            fallback,
                            messageReference: new MessageReference(work.MessageId),
                            allowedMentions: AllowedMentions.None),
                        "Send image-fallback reply",
                        cancellationToken);
                    return;
                }

                var caption = TrimDiscordText(answer.text ?? answer.prompt);
                _logger.LogInformation("OUTPUT: {Where} -> [IMAGE] {Caption}", work.IsPrivate ? "private" : "group", caption);

                await using var imageStream = new MemoryStream(imageData);
                await TrySendAsync(
                    () => channel.SendFileAsync(
                        imageStream,
                        "malyuvach.png",
                        caption,
                        messageReference: new MessageReference(work.MessageId),
                        allowedMentions: AllowedMentions.None),
                    "Send image reply",
                    cancellationToken);

                if (_settings.BotShowRoomChannelId != 0)
                {
                    try
                    {
                        var showroom = _client.GetChannel(_settings.BotShowRoomChannelId) as IMessageChannel;
                        if (showroom != null)
                        {
                            await using var showroomStream = new MemoryStream(imageData);
                            await TrySendAsync(
                                () => showroom.SendFileAsync(
                                    showroomStream,
                                    "malyuvach.png",
                                    TrimDiscordText(answer.prompt),
                                    allowedMentions: AllowedMentions.None),
                                "Send image to showroom",
                                cancellationToken);
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
            await TrySendAsync(
                () => channel.SendMessageAsync(
                    "Ой якесь лишенько трапилось...",
                    messageReference: new MessageReference(work.MessageId),
                    allowedMentions: AllowedMentions.None),
                "Send fallback error reply",
                cancellationToken);
        }
    }

    private async Task<IMessageChannel?> ResolveMessageChannelAsync(WorkItem work, CancellationToken cancellationToken)
    {
        // 1) Socket cache (works for guild channels reliably)
        if (_client.GetChannel(work.ChannelId) is IMessageChannel cached)
            return cached;

        // 2) REST fallback (works when socket cache doesn't have it, e.g. some DM channels)
        try
        {
            var restChannel = await _client.Rest.GetChannelAsync(work.ChannelId);
            if (restChannel is IMessageChannel restMessageChannel)
                return restMessageChannel;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve channel {ChannelId} via REST", work.ChannelId);
        }

        // 3) DM fallback: open/create DM with the author
        if (work.IsPrivate)
        {
            try
            {
                var user = await _client.Rest.GetUserAsync(work.AuthorId);
                var dm = await user.CreateDMChannelAsync();
                return dm;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to create DM channel for user {UserId}", work.AuthorId);
            }
        }

        return null;
    }

    private async Task<string?> TryTranscribeAudioAttachmentAsync(IMessageChannel channel, IReadOnlyList<AttachmentInfo> attachments, CancellationToken cancellationToken)
    {
        try
        {
            var attachment = attachments
                .FirstOrDefault(a =>
                    (!string.IsNullOrWhiteSpace(a.ContentType) && a.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) ||
                    a.Filename.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                    a.Filename.EndsWith(".opus", StringComparison.OrdinalIgnoreCase));

            if (attachment == null)
                return null;

            // Current STT pipeline expects Telegram-style OGG/Opus.
            // Discord voice messages commonly come as audio/ogg; we support that shape.
            if (!(attachment.Filename.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                  attachment.Filename.EndsWith(".opus", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Audio attachment ignored (unsupported format): {Filename} ({ContentType})",
                    attachment.Filename, attachment.ContentType);
                return null;
            }

            _logger.LogInformation("INPUT: audio attachment {Filename} ({Size} bytes)", attachment.Filename, attachment.Size);

            await TrySendAsync(() => channel.TriggerTypingAsync(), "Trigger typing (audio)", cancellationToken);

            using var response = await _httpClient.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var audioStream = new MemoryStream();
            await remoteStream.CopyToAsync(audioStream, cancellationToken);
            audioStream.Position = 0;

            var text = await _sttService.TranscribeAsync(audioStream, cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            _logger.LogInformation("Audio transcribed successfully: {Text}", text);
            return text.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Discord audio attachment");
            return null;
        }
    }

    private async Task<bool> TrySendAsync(Func<Task> operation, string operationName, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < SendRetryDelays.Length; attempt++)
        {
            try
            {
                await operation();
                return true;
            }
            catch (RateLimitedException ex) when (attempt < SendRetryDelays.Length - 1)
            {
                var delay = SendRetryDelays[attempt] + Jitter();
                _logger.LogWarning(ex, "Discord rate limited during {Operation}. Retrying in {DelayMs}ms",
                    operationName, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpException ex) when (attempt < SendRetryDelays.Length - 1 && IsTransient(ex))
            {
                var delay = SendRetryDelays[attempt] + Jitter();
                _logger.LogWarning(ex,
                    "Discord HTTP error during {Operation} (HTTP {Status}, DiscordCode {DiscordCode}). Retrying in {DelayMs}ms",
                    operationName,
                    ex.HttpCode,
                    ex.DiscordCode,
                    (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (TaskCanceledException ex) when (attempt < SendRetryDelays.Length - 1)
            {
                var delay = SendRetryDelays[attempt] + Jitter();
                _logger.LogWarning(ex, "Discord send timeout/cancel during {Operation}. Retrying in {DelayMs}ms",
                    operationName, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                if (ex is HttpException httpEx)
                {
                    // Non-transient cases: missing permissions, bad channel, etc.
                    _logger.LogError(httpEx,
                        "Discord send failed during {Operation} (HTTP {Status}, DiscordCode {DiscordCode}). No retry.",
                        operationName,
                        httpEx.HttpCode,
                        httpEx.DiscordCode);
                }
                else
                {
                    _logger.LogError(ex, "Discord send failed during {Operation}. No retry.", operationName);
                }

                return false;
            }
        }

        _logger.LogError("Discord send failed during {Operation} after {Attempts} attempts", operationName, SendRetryDelays.Length);
        return false;
    }

    private sealed record AttachmentInfo(string Url, string Filename, string? ContentType, int Size);

    private sealed record WorkItem(
        ulong ChannelId,
        ulong MessageId,
        ulong AuthorId,
        string AuthorUsername,
        bool IsPrivate,
        bool IsBotMentioned,
        ulong? ReferencedMessageId,
        string? Content,
        IReadOnlyList<AttachmentInfo> Attachments);

    private static bool IsTransient(HttpException ex)
    {
        return ex.HttpCode == HttpStatusCode.RequestTimeout ||
               ex.HttpCode == HttpStatusCode.TooManyRequests ||
               ex.HttpCode == HttpStatusCode.BadGateway ||
               ex.HttpCode == HttpStatusCode.ServiceUnavailable ||
               ex.HttpCode == HttpStatusCode.GatewayTimeout ||
               ex.HttpCode == HttpStatusCode.InternalServerError;
    }

    private TimeSpan Jitter()
    {
        // small random jitter to avoid thundering herd on retries
        return TimeSpan.FromMilliseconds(_random.Next(0, 250));
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
