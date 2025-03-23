using Malyuvach.Configuration;
using Malyuvach.Models;
using Malyuvach.Services.Image;
using Malyuvach.Services.LLM;
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
    private readonly TelegramBotClient _botClient;
    private readonly Random _random = new();

    public TelegramService(
        ILogger<TelegramService> logger,
        IOptions<TelegramSettings> settings,
        ILLMService llmService,
        IImageService imageService)
    {
        _logger = logger;
        _settings = settings.Value;
        _llmService = llmService;
        _imageService = imageService;
        _botClient = new TelegramBotClient(_settings.BotKey);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_settings.SkipUpdates)
        {
            var updates = await _botClient.GetUpdatesAsync(cancellationToken: cancellationToken);
            _logger.LogInformation("Skipped {Count} updates", updates.Length);
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
            if (update.Message?.Text is not { } messageText)
                return;

            var chatId = update.Message.Chat.Id;
            var contextId = chatId.ToString();
            var messageId = update.Message.MessageId;
            var chatType = update.Message.Chat.Type switch
            {
                ChatType.Private => "private",
                ChatType.Group => "group",
                ChatType.Supergroup => "group",
                _ => "unknown"
            };

            // Log incoming message
            if (chatType == "private")
            {
                _logger.LogInformation("INPUT: {ChatType} {Username}({UserId}): {Message}",
                    chatType,
                    update.Message.From?.Username,
                    update.Message.From?.Id,
                    messageText);
            }
            else
            {
                _logger.LogInformation("INPUT: {ChatType} {Username}({UserId})@{ChatTitle}({ChatId}): {Message}",
                    chatType,
                    update.Message.From?.Username,
                    update.Message.From?.Id,
                    update.Message.Chat.Title,
                    update.Message.Chat.Id,
                    messageText);
            }

            // Check if message contains bot mention or is in private chat
            var isBotMentioned = _settings.BotNames.Any(name =>
                messageText.Contains(name, StringComparison.OrdinalIgnoreCase));

            if (!isBotMentioned && update.Message.Chat.Type != ChatType.Private)
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
                    parseMode: ParseMode.Markdown,
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
                    seed,
                    6);

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
                        _logger.LogInformation("OUTPUT: showroom -> [IMAGE] {Prompt}", answer.prompt);
                        imageStream.Seek(0, SeekOrigin.Begin);
                        await botClient.SendPhotoAsync(
                            chatId: _settings.BotShowRoomChannel,
                            photo: InputFile.FromStream(imageStream),
                            caption: answer.prompt,
                            parseMode: ParseMode.Markdown,
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
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken);
                }
            }
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

    public Task HandlePollingErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException =>
                $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogError(exception, "{ErrorMessage}", errorMessage);
        return Task.CompletedTask;
    }
}