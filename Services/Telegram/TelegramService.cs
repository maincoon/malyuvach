using Malyuvach.Configuration;
using Malyuvach.Services.Image;
using Malyuvach.Services.LLM;
using Malyuvach.Services.STT;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Malyuvach.Services.Telegram;

public class TelegramService : ITelegramService
{
    private readonly ILogger<TelegramService> _logger;
    private readonly TelegramSettings _settings;
    private readonly ILLMService _llmService;
    private readonly IImageService _imageService;
    private readonly ISTTService _sttService;
    private readonly TelegramBotClient _botClient;
    private readonly Random _random = new();

    public TelegramService(
        ILogger<TelegramService> logger,
        IOptions<TelegramSettings> settings,
        ILLMService llmService,
        IImageService imageService,
        ISTTService sttService)
    {
        _logger = logger;
        _settings = settings.Value;
        _llmService = llmService;
        _imageService = imageService;
        _sttService = sttService;
        _botClient = new TelegramBotClient(_settings.BotKey);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_settings.SkipUpdates)
        {
            // Drain and acknowledge all pending updates so bot doesn't process historical messages
            int totalSkipped = 0;
            while (true)
            {
                var updates = await _botClient.GetUpdatesAsync(limit: 100, timeout: 0, cancellationToken: cancellationToken);
                if (updates.Length == 0)
                    break;

                totalSkipped += updates.Length;
                var lastUpdateId = updates[^1].Id;
                // Acknowledge updates by requesting the next (non-existent) one with offset = lastUpdateId + 1
                await _botClient.GetUpdatesAsync(offset: lastUpdateId + 1, limit: 1, timeout: 0, cancellationToken: cancellationToken);
                _logger.LogInformation("Skipped batch of {Batch} updates (last id {LastId})", updates.Length, lastUpdateId);
            }
            _logger.LogInformation("SkipUpdates enabled: skipped total {Total} pending updates", totalSkipped);
        }

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: HandlePollingErrorAsync,
            receiverOptions: new() { AllowedUpdates = Array.Empty<UpdateType>() },
            cancellationToken: cancellationToken
        );

        _logger.LogInformation("Telegram bot started");
    }

    public async Task HandleUpdateAsync(
        ITelegramBotClient botClient,
        Update update,
        CancellationToken cancellationToken)
    {
        try
        {
            var chatId = update.Message?.Chat.Id;

            if (chatId == null)
                return;

            var contextId = chatId.ToString()!;
            var messageId = update.Message?.MessageId!;
            var chatType = update.Message?.Chat.Type switch
            {
                ChatType.Private => "private",
                ChatType.Group => "group",
                ChatType.Supergroup => "group",
                _ => "unknown"
            };

            string messageText;

            // Handle audio/voice messages
            if (update.Message?.Voice != null || update.Message?.Audio != null)
            {
                messageText = await ProcessAudioMessageAsync(
                    botClient, update.Message, chatId.Value,
                    chatType, messageId.Value, cancellationToken);

                if (string.IsNullOrEmpty(messageText))
                    return;
            }
            else if (update.Message?.Text is not { } text)
            {
                return;
            }
            else
            {
                messageText = text;
            }

            await ProcessTextMessageAsync(botClient, update, messageText, chatId.Value, messageId.Value, chatType, contextId, cancellationToken);
        }
        catch (Exception exception)
        {
            await HandlePollingErrorAsync(botClient, exception, cancellationToken);

            try
            {
                var errorMsg = "Ой якесь лишенько трапилось...";
                _logger.LogInformation("OUTPUT: error -> {Message}", errorMsg);
                await botClient.SendTextMessageAsync(
                    chatId: update.Message!.Chat.Id,
                    text: errorMsg,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FATAL: Could not send error message to chat");
            }
        }
    }

    private async Task<string> ProcessAudioMessageAsync(
        ITelegramBotClient botClient,
        Message message,
        long chatId,
        string chatType,
        int messageId,
        CancellationToken cancellationToken)
    {

        if (chatType == "private")
        {
            _logger.LogInformation("INPUT: {ChatType} {Username}({UserId}) voice",
                chatType,
                message.From?.Username,
                message.From?.Id);
        }
        else
        {
            _logger.LogInformation("INPUT: {ChatType} {Username}({UserId})@{ChatTitle}({ChatId}) voice",
                chatType,
                message.From?.Username,
                message.From?.Id,
                message.Chat.Title,
                message.Chat.Id);
        }

        string fileId = message.Voice?.FileId ?? message.Audio?.FileId!;

        if (string.IsNullOrEmpty(fileId))
            return string.Empty;

        // Send typing action
        await botClient.SendChatActionAsync(
            chatId: chatId,
            chatAction: ChatAction.Typing,
            cancellationToken: cancellationToken);

        try
        {
            // Download the audio file
            var file = await botClient.GetFileAsync(fileId, cancellationToken);

            _logger.LogInformation("Processing audio message. File ID: {FileId}, Size: {Size} bytes",
                fileId, file.FileSize);

            // Download audio to memory stream and transcribe directly
            using var audioStream = new MemoryStream();
            await botClient.DownloadFileAsync(file.FilePath!, audioStream, cancellationToken);
            audioStream.Position = 0; // Reset stream position for reading

            // Transcribe audio using STT service
            var transcribedText = await _sttService.TranscribeAsync(audioStream, cancellationToken);

            if (string.IsNullOrWhiteSpace(transcribedText))
            {
                // If no text was extracted or transcription failed
                var errorMessage = "Sorry, I couldn't understand your audio message. Please try sending it as text or in a different audio format.";
                _logger.LogWarning("Audio transcription failed or returned empty result");

                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: errorMessage,
                    replyToMessageId: messageId,
                    cancellationToken: cancellationToken);
                return string.Empty;
            }

            // Send transcribed text as a silent reply to the original message
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: transcribedText,
                replyToMessageId: messageId,
                cancellationToken: cancellationToken,
                disableNotification: true);

            _logger.LogInformation("Audio transcribed successfully: {Text}", transcribedText);
            return transcribedText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio message");

            var errorMessage = "Sorry, there was an error processing your audio message.";
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: errorMessage,
                replyToMessageId: messageId,
                cancellationToken: cancellationToken);
            return string.Empty;
        }
    }

    private async Task ProcessTextMessageAsync(
        ITelegramBotClient botClient,
        Update update,
        string messageText,
        long chatId,
        int messageId,
        string chatType,
        string contextId,
        CancellationToken cancellationToken)
    {
        // Log incoming message
        if (chatType == "private")
        {
            _logger.LogInformation("INPUT: {ChatType} {Username}({UserId}): {Message}",
                chatType,
                update.Message?.From?.Username,
                update.Message?.From?.Id,
                messageText);
        }
        else
        {
            _logger.LogInformation("INPUT: {ChatType} {Username}({UserId})@{ChatTitle}({ChatId}): {Message}",
                chatType,
                update.Message?.From?.Username,
                update.Message?.From?.Id,
                update.Message?.Chat.Title,
                update.Message?.Chat.Id,
                messageText);
        }

        // Check if message is a reply to bot or contains bot mention or is in private chat
        var isBotMentioned = _settings.BotNames.Any(name =>
            messageText.Contains(name, StringComparison.OrdinalIgnoreCase));

        var isReplyToBot = update.Message?.ReplyToMessage?.From?.Username == "Malyuvach_bot";

        if (!isBotMentioned && !isReplyToBot && update.Message?.Chat.Type != ChatType.Private)
        {
            _logger.LogDebug("IGNORED");
            return;
        }

        // Remove bot name from message
        foreach (var name in _settings.BotNames)
        {
            messageText = messageText.Replace(name, "", StringComparison.OrdinalIgnoreCase);
        }
        messageText = messageText.Trim();

        if (string.IsNullOrEmpty(messageText))
            return;

        // Send typing action
        await botClient.SendChatActionAsync(
            chatId: chatId,
            chatAction: ChatAction.Typing,
            cancellationToken: cancellationToken);

        // Process message
        var answer = await _llmService.GetAnswerAsync(messageText, contextId, cancellationToken);
        if (answer == null)
        {
            var errorMsg = "Sorry, I couldn't process your request. Please try again.";
            _logger.LogInformation("OUTPUT: {ChatType} -> {Message}", chatType, errorMsg);
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: errorMsg,
                replyToMessageId: messageId,
                cancellationToken: cancellationToken);
            return;
        }

        // If there's only text (no prompt or very short prompt), send just text
        if (!string.IsNullOrEmpty(answer.text) &&
            (string.IsNullOrEmpty(answer.prompt) || answer.prompt.Length < 10))
        {
            _logger.LogInformation("OUTPUT: {ChatType} -> {Message}", chatType, answer.text);
            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: answer.text,
                replyToMessageId: messageId,
                cancellationToken: cancellationToken);
            return;
        }

        // Generate and send image if prompt is available
        if (!string.IsNullOrEmpty(answer.prompt))
        {
            await botClient.SendChatActionAsync(
                chatId: chatId,
                chatAction: ChatAction.UploadPhoto,
                cancellationToken: cancellationToken);

            var seed = _random.NextInt64();
            var imageData = await _imageService.CreateImageAsync(
                answer.prompt,
                string.Empty,
                answer.orientation ?? "landscape",
                seed);

            if (imageData != null)
            {
                using var imageStream = new MemoryStream(imageData);
                var photo = InputFile.FromStream(imageStream);

                // Trim caption to 1000 characters (Telegram limit)
                var caption = answer.text ?? answer.prompt;
                if (caption.Length > 1000)
                {
                    caption = caption.Substring(0, 1000) + "...";
                }

                // Log image with text
                _logger.LogInformation("OUTPUT: {ChatType} -> [IMAGE] {Caption}", chatType, caption);

                // Send to chat
                await botClient.SendPhotoAsync(
                    chatId: chatId,
                    photo: photo,
                    caption: caption,
                    replyToMessageId: messageId,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);

                // Send to showroom if configured
                if (!string.IsNullOrEmpty(_settings.BotShowRoomChannel))
                {
                    // limit caption to 1000 characters (Telegram limit)
                    caption = answer.prompt;
                    if (caption.Length > 1000)
                    {
                        caption = caption.Substring(0, 1000) + "...";
                    }
                    _logger.LogInformation("OUTPUT: showroom -> [IMAGE] {Prompt}", answer.prompt);
                    imageStream.Seek(0, SeekOrigin.Begin);
                    await botClient.SendPhotoAsync(
                        chatId: _settings.BotShowRoomChannel,
                        photo: InputFile.FromStream(imageStream),
                        caption: caption,
                        cancellationToken: cancellationToken);
                }
            }
            else
            {
                var errorMsg = answer.text ?? "Sorry, I couldn't generate an image. Please try again.";
                _logger.LogInformation("OUTPUT: {ChatType} -> {Message}", chatType, errorMsg);
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: errorMsg,
                    replyToMessageId: messageId,
                    cancellationToken: cancellationToken);
            }
        }
    }

    public Task HandlePollingErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException =>
                $"API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogError(exception, "Polling Error: {ErrorMessage}", errorMessage);
        return Task.Delay(1000, cancellationToken); // Prevent tight loop in case of continuous errors
    }
}