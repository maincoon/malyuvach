using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Malyuvach.Models;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;
namespace Malyuvach;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private MalyuvachOptions _options = new MalyuvachOptions();
    private readonly IOptionsMonitor<MalyuvachOptions> _optionsMonitor;

    private readonly TelegramBotClient _botClient;
    private readonly Random _random = new Random();
    private readonly IOptions<JsonSerializerOptions> _jsonOptions;

    /// <summary>
    /// System prompt cache
    /// </summary>
    private string _systemPromptCache = "";

    /// <summary>
    /// Returns system prompt from file or returns cached value
    /// </summary>
    private string _systemPrompt
    {
        get
        {
            try
            {
                // read from file
                _systemPromptCache = System.IO.File.ReadAllText(_options.MainSystemPromptPath!);
            }
            catch (Exception exception)
            {
                // log error
                _logger.LogError(exception,
                    $"ERROR: system prompt file not found - {_options.MainSystemPromptPath}");
            }
            return _systemPromptCache ?? "";
        }
    }

    /// <summary>
    /// JSON validator system prompt cache
    /// </summary>
    private string _jsonValidatorSystemPromptCache = "";

    /// <summary>
    /// Returns JSON validator system prompt from file or returns cached value
    /// </summary>
    private string _jsonValidatorSystemPrompt
    {
        get
        {
            try
            {
                // read from file
                _jsonValidatorSystemPromptCache = System.IO.File.ReadAllText(_options.JSONValidatorSystemPromptPath!);
            }
            catch (Exception exception)
            {
                // log error
                _logger.LogError(exception,
                    $"ERROR: JSON validator system prompt file not found - {_options.JSONValidatorSystemPromptPath}");
            }
            return _jsonValidatorSystemPromptCache ?? "";
        }
    }

    public Worker(ILogger<Worker> logger, IOptionsMonitor<MalyuvachOptions> options, IOptions<JsonSerializerOptions> jsonOptions)
    {
        _optionsMonitor = options;
        _options = _optionsMonitor.CurrentValue;
        _optionsMonitor.OnChange((newOptions) =>
        {
            _options = newOptions;
            StartLogging();
        });
        _jsonOptions = jsonOptions;
        _logger = logger;
        _botClient = new TelegramBotClient(_options.BotKey);
    }

    protected void StartLogging()
    {
        // log some
        _logger.LogInformation($"START: LLM - {_options.ModelName}");
        _logger.LogInformation($"START: SYSTEM prom path - {_options.MainSystemPromptPath}");
        _logger.LogInformation($"START: JSON validator path - {_options.JSONValidatorSystemPromptPath}");
        _logger.LogInformation($"START: ComfyUI workflow - {_options.Workflow.ComfyUIWorkflowPath}");
        _logger.LogInformation($"START: ---------------------------------------------------");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // log some
        StartLogging();

        // rewind updates to not get offline messages
        if (_options.SkipUpdates)
        {
            var updates = await _botClient.GetUpdatesAsync(cancellationToken: stoppingToken);
            _logger.LogInformation($"START: skip updates {updates.Length}");
        }

        // start listening
        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: HandlePollingErrorAsync,
            receiverOptions: new() { AllowedUpdates = Array.Empty<UpdateType>() },
            cancellationToken: stoppingToken
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    /// <summary>
    /// Handle polling errors
    /// </summary>
    private Task HandlePollingErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        var ErrorMessage = exception switch
        {
            ApiRequestException apiRequestException =>
                $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString(),
        };

        _logger.LogError(exception, ErrorMessage);
        return Task.CompletedTask;
    }

    /// <summary>
    /// replace system prompt in case if it was changed
    /// </summary>
    private void UpdateSystemPrompt(OllamaSharp.Chat chat)
    {
        // check if there is enough messages
        if (chat.Messages.Count == 0)
        {
            return;
        }

        // check if system prompt is the same
        if (chat.Messages.ElementAt(0).Content == _systemPrompt)
        {
            return;
        }

        // replace system prompt
        var systemMessage = new OllamaSharp.Models.Chat.Message(ChatRole.System, _systemPrompt);
        var messagesCopy = chat.Messages.ToList();
        messagesCopy[0] = systemMessage;
        chat.Messages.Clear();
        chat.Messages.AddRange(messagesCopy);
    }

    private void SaveContext(OllamaSharp.Chat chat, string contextId)
    {
        try
        {
            // save context to file
            var contextPath = Path.Combine(_options.ContextsPath, contextId + ".json");
            // create directory if not exists
            if (!Directory.Exists(_options.ContextsPath))
            {
                Directory.CreateDirectory(_options.ContextsPath);
            }
            var contextJson = JsonSerializer.Serialize(chat.Messages, _jsonOptions.Value);
            System.IO.File.WriteAllText(contextPath, contextJson);
            // log some 
            _logger.LogDebug($"CTX SAVED: {contextId} {chat.Messages.Count} messages");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"ERROR: saving context {contextId}");
        }
    }

    private OllamaSharp.Chat LoadContext(string contextId)
    {
        // create client
        var ollamaClient = new OllamaApiClient(new Uri(_options.OllamaUIBaseUrl));
        ollamaClient.SelectedModel = _options.ModelName;
        var ollamaChat = new OllamaSharp.Chat(ollamaClient, _systemPrompt);
        ollamaChat.Options = new RequestOptions { NumCtx = _options.MaxContextSize };
        try
        {
            // check if context file exists
            var contextPath = Path.Combine(_options.ContextsPath, contextId + ".json");
            if (System.IO.File.Exists(contextPath))
            {
                // read context from file
                var contextJson = System.IO.File.ReadAllText(contextPath);
                var messages = JsonSerializer.Deserialize<List<OllamaSharp.Models.Chat.Message>>(contextJson, _jsonOptions.Value);
                ollamaChat.Messages.Clear();
                ollamaChat.Messages.AddRange(messages!);
                // log some
                _logger.LogDebug($"CTX LOADED: {contextId} {messages!.Count} messages");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"ERROR: loading context {contextId} - using default empty");
        }

        // update system prompt
        UpdateSystemPrompt(ollamaChat);

        return ollamaChat;
    }

    /// <summary>
    /// Try to fix chat by removing last messages
    /// </summary>
    private void FixContext(OllamaSharp.Chat chat, int messages)
    {
        if (messages <= 0)
        {
            return;
        }
        var messagesCopy = chat.Messages.ToList();
        if (messagesCopy.Count > messages)
        {
            chat.Messages.Clear();
            chat.Messages.AddRange(messagesCopy.GetRange(0, messagesCopy.Count - messages));
        }
    }

    /// <summary>
    /// Clear messages from chat if count is more than maxMessages except first one
    /// </summary>
    private void ClearContext(OllamaSharp.Chat chat)
    {
        if (chat.Messages.Count > _options.MaxContextMsgs)
        {
            var messagesCopy = chat.Messages.ToList();
            while (messagesCopy.Count > _options.MaxContextMsgs)
            {
                messagesCopy.RemoveAt(1);
            }
            chat.Messages.Clear();
            chat.Messages.AddRange(messagesCopy);
        }
    }

    /// <summary>
    /// Validate JSON and return ClientAnswer
    /// </summary>
    private async Task<ClientAnswer?> ValidateJSON(string message, CancellationToken cancel)
    {
        var answer = "";
        if (_options.UseJSONValidator)
        {
            // create client
            var ollama = new OllamaApiClient(new Uri(_options.OllamaUIBaseUrl));
            ollama.SelectedModel = _options.ModelName;

            // always new context
            var validator = new OllamaSharp.Chat(ollama, _jsonValidatorSystemPrompt);
            validator.Options = new RequestOptions { NumCtx = 8192 };

            // sending request and specifying context size
            var response = validator.SendAsync(message, cancel);

            // asking model for answer and collecting it to one string
            await foreach (var answerToken in response)
                answer += answerToken;
        }
        else
        {
            answer = message;
        }

        // strip special characters like new lines and tabs
        answer = answer
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Replace("```json", "")
            .Replace("```", "")
            .Trim();

        // log some
        _logger.LogDebug($"VALIDATOR: raw {answer}");

        // parsing answer but without throwing exceptions
        try
        {
            var result = JsonSerializer.Deserialize<ClientAnswer>(answer, _jsonOptions.Value);
            // trim values
            if (result != null)
            {
                result.text = result.text?.Trim();
                result.prompt = result.prompt?.Trim();
                result.orientation = result.orientation?.Trim();
            }
            // check if all fields is null
            if (string.IsNullOrEmpty(result?.text) && string.IsNullOrEmpty(result?.prompt))
            {
                _logger.LogError("ERROR - null answer");
                return null;
            }

            // log some
            if (_options.UseJSONValidator)
            {
                _logger.LogInformation($"VALIDATOR: {JsonSerializer.Serialize(result, _jsonOptions.Value)}");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"JSON Validator error");
            return null;
        }
    }

    private async Task<ClientAnswer?> GetClientAnswer(
        OllamaSharp.Chat context,
        string message,
        CancellationToken cancel
    )
    {
        // remember context
        var ctxSize = context.Messages.Count;


        // sending request and specifying context size
        var response = context.SendAsAsync(ChatRole.User, message, cancel);

        // asking model for answer and collecting it to one string
        var answer = "";
        await foreach (var answerToken in response)
            answer += answerToken;

        // log some
        answer = answer.Trim();
        _logger.LogInformation($"ANSWER ({_options.ModelName}): {answer}");

        // validating JSON
        var result = await ValidateJSON(answer, cancel);
        if (result == null)
        {
            // fix context according to previous size
            FixContext(context, context.Messages.Count - ctxSize);
            return null;
        }

        // replace message to get more consistent results
        context.Messages.Last().Content = JsonSerializer.Serialize(result, _jsonOptions.Value);

        // return result
        return result;
    }

    private long GetRandomSeed()
    {
        return _random.NextInt64();
    }

    private async Task<byte[]?> WaitForPromptImage(string promptId)
    {
        using var httpClient = new HttpClient();
        // waiting for prompt to be finished
        while (true)
        {
            // find prompt by id comfyUIBaseURL/history/{prompt_id}"
            using var response = await httpClient.GetAsync(
                $"{_options.ComfyUIBaseUrl}/history/{promptId}"
            );
            // read response
            var responseString = await response.Content.ReadAsStringAsync();
            // check response code
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError($"ERROR: {response.StatusCode} - {responseString}");
                return null;
            }

            // deserialize response
            var responseJson = JsonObject.Parse(responseString)!;

            // check if node with prompt_id is finished
            if (responseJson[promptId] != null && responseJson[promptId]!["outputs"] != null)
            {
                _logger.LogDebug($"RESPONSE: {responseString}");

                // get output result
                var outputFile = responseJson[promptId]!["outputs"]!
                    [_options.Workflow.OutputNodeId]!
                    ["images"]![0]!["filename"]!.ToString();

                _logger.LogDebug($"PROMPT {promptId} FINISHED - {outputFile}");

                // download image from comfyUIBaseURL/view?filename={outputFile}}&type=temp
                using var imageResponse = await httpClient.GetAsync(
                    $"{_options.ComfyUIBaseUrl}/view?filename={outputFile}&type=temp"
                );

                // read image
                var image = await imageResponse.Content.ReadAsByteArrayAsync();

                // log some
                _logger.LogInformation($"IMAGE: {promptId} {outputFile} {image.Length} bytes");
                return image!;
            }
            // wait some for retry
            await Task.Delay(300);
        }
    }

    /// <summary>
    /// Sets the value of a specified key in a JSON object.
    /// </summary>
    /// <typeparam name="T">The type of the value to set.</typeparam>
    /// <param name="objValue">The JSON object node where the value will be set.</param>
    /// <param name="key">The key whose value needs to be set. Nested keys can be specified using dot notation.</param>
    /// <param name="value">The value to set for the specified key.</param>
    /// <remarks>
    /// If the JSON object node is null or if any intermediate key in the dot notation is not found, the method will return without setting the value.
    /// </remarks>
    void SetJsonObjectValue<T>(JsonNode? objValue, string key, T value)
    {
        if (objValue == null)
        {
            return;
        }

        var keys = key.Split('.');

        for (var i = 0; i < keys.Length - 1; i++)
        {
            objValue = objValue?[keys[i]];
            if (objValue == null)
                return;
        }

        objValue[keys[^1]] = JsonSerializer.SerializeToNode(value, _jsonOptions.Value);
    }

    /// <summary>
    /// Same as SetJsonObjectValue, but for multiple keys.
    /// </summary>
    void SetJsonObjectValue<T>(JsonNode? objValue, IEnumerable<string> keys, T value)
    {
        if (objValue == null)
        {
            return;
        }
        foreach (var k in keys)
        {
            SetJsonObjectValue(objValue, k, value);
        }
    }

    private async Task<byte[]?> CreateImageAsync(
        string positivePrompt,
        string negativePrompt,
        string orientation,
        long seed,
        int steps
    )
    {
        // load prompt api as JToken
        var promptApiText = System.IO.File.ReadAllText(_options.Workflow.ComfyUIWorkflowPath);
        var promptApiJson = JsonNode.Parse(promptApiText)!;

        // set prompt text
        SetJsonObjectValue(promptApiJson, _options.Workflow.PositivePromptFieldId, positivePrompt);
        // set negative prompt text
        SetJsonObjectValue(promptApiJson, _options.Workflow.NegativePromptFieldId, negativePrompt ?? "");
        // set image type and dimensions
        switch (orientation)
        {
            case "landscape":
                SetJsonObjectValue(
                    promptApiJson,
                    _options.Workflow.ImageWidthFieldId,
                    _options.ImageDefaultXDimension
                );
                SetJsonObjectValue(
                    promptApiJson,
                    _options.Workflow.ImageHeightFieldId,
                    _options.ImageDefaultYDimension
                );
                break;
            case "portrait":
                SetJsonObjectValue(
                    promptApiJson,
                    _options.Workflow.ImageWidthFieldId,
                    _options.ImageDefaultYDimension
                );
                SetJsonObjectValue(
                    promptApiJson,
                    _options.Workflow.ImageHeightFieldId,
                    _options.ImageDefaultXDimension
                );
                break;
            default:
                SetJsonObjectValue(promptApiJson, _options.Workflow.ImageWidthFieldId, 1024);
                SetJsonObjectValue(promptApiJson, _options.Workflow.ImageHeightFieldId, 1024);
                break;
        }

        // create random seed
        SetJsonObjectValue(promptApiJson, _options.Workflow.NoiseSeedFieldId, seed);
        // set steps
        SetJsonObjectValue(promptApiJson, _options.Workflow.StepsFieldId, steps);

        // create final object
        var promptApiRoot = new { prompt = promptApiJson };
        var promptApiFinal = JsonSerializer.Serialize(promptApiRoot, _jsonOptions.Value);

        // make POST request to comfyUIBaseURL/prompt
        using var httpClient = new HttpClient();
        using var requestContent = new StringContent(
            promptApiFinal,
            Encoding.UTF8,
            "application/json"
        );
        using var response = await httpClient.PostAsync(
            $"{_options.ComfyUIBaseUrl}/prompt",
            requestContent
        );

        // read response
        var responseString = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            _logger.LogError($"ERROR: {response.StatusCode} - {responseString}");
            return null;
        }

        // deserialize response
        var responseJson = JsonObject.Parse(responseString);
        var promptId = responseJson!["prompt_id"]!.ToString();

        // log some
        _logger.LogDebug($"PROMPT: {promptId}, {positivePrompt}");

        // wait for prompt to be finished
        return await WaitForPromptImage(promptId);
    }

    private async Task HandleUpdateAsync(
        ITelegramBotClient botClient,
        Update update,
        CancellationToken cancel
    )
    {
        try
        {
            await HandleUpdateAsyncInternal(botClient, update, cancel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Internal error");
        }
    }

    private async Task HandleUpdateAsyncInternal(
        ITelegramBotClient botClient,
        Update update,
        CancellationToken cancel
    )
    {
        // from text message
        if (update.Message is not { } message || message.Text is null || message.From is null)
            return;

        // figure out is it chat or private message
        var chatType = message.Chat.Type switch
        {
            ChatType.Private => "private",
            ChatType.Group => "group",
            ChatType.Supergroup => "group",
            _ => "unknown",
        };

        var chatId = update.Message.Chat.Id;
        var messageId = update.Message.MessageId;
        var userId = update.Message.From!.Id;
        var finalMessage = message.Text;

        // out message
        _logger.LogInformation(chatType == "private" ?
            $"INPUT: {chatType} {message.From.Username}({(ulong)message.From.Id}): {message.Text}" :
            $"INPUT: {chatType} {message.From.Username}({(ulong)message.From.Id})@{message.Chat.Title}({(ulong)message.Chat.Id}): {message.Text}"
        );

        // compose context id for group chats
        var contextId =
            chatType == "group"
                ? (((ulong)chatId).ToString() + ((ulong)userId).ToString())
                : chatId.ToString();

        // for group chats parse first word as bot name using comma, colon, space as separators
        var guessBotName = message.Text.Split(
            new[] { ',', ':', ' ' },
            StringSplitOptions.RemoveEmptyEntries
        )[0];
        if (chatType == "group")
        {
            // check if it is reply
            if (message.ReplyToMessage != null)
            {
                // check if it is reply to bot
                if (message.ReplyToMessage.From!.Username != "Malyuvach_bot")
                {
                    _logger.LogDebug("IGNORED");
                    return;
                }
                // parse message
                finalMessage = message.Text;
            }
            else
            {
                if (!_options.BotNames.Contains(guessBotName))
                {
                    _logger.LogDebug("IGNORED");
                    return;
                }
                // parse message
                finalMessage =
                    message.Text.Split(' ').Length > 1
                        ? message.Text.Substring(message.Text.IndexOf(' ') + 1)
                        : "";
            }
        }

        try
        {
            // loading context
            var context = LoadContext(contextId);

            // log context and size (average token length is 4)
            _logger.LogDebug(
                $"CTX: {contextId} {context.Messages.Count}/{_options.MaxContextMsgs} msg "
                + $"{context.Messages.Sum(m => m.Content?.Split(' ').Length) * 4}/{_options.MaxContextSize} tok"
            );

            // get client answer with max retries
            ClientAnswer? clientAnswer = null;
            var retries = _options.MaxAnswerRetries;

            while (clientAnswer == null && retries-- > 0)
            {
                // send typing action
                await botClient.SendChatActionAsync(chatId, ChatAction.Typing, cancel);
                // get client answer
                clientAnswer = await GetClientAnswer(context, finalMessage, cancel);
                if (clientAnswer == null && retries > 0)
                {
                    _logger.LogWarning($"RETRY: {contextId} {_options.MaxAnswerRetries - retries}");
                }
            }

            // Save context to file
            ClearContext(context);
            SaveContext(context, contextId);

            if (clientAnswer == null)
            {
                // send message to chat
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    replyToMessageId: messageId,
                    text: $"Халепа! Давай ще раз?",
                    cancellationToken: cancel
                );
                return;
            }

            // check response text
            if (clientAnswer!.text != null &&
                (string.IsNullOrEmpty(clientAnswer.prompt) || clientAnswer?.prompt?.Length < 10))
            {
                // send text to chat
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: clientAnswer.text,
                    replyToMessageId: chatType == "group" ? messageId : null,
                    cancellationToken: cancel,
                    parseMode: ParseMode.Markdown
                );
                return;
            }

            // check response image
            if (!string.IsNullOrEmpty(clientAnswer?.prompt))
            {
                // send uploading image action
                await botClient.SendChatActionAsync(chatId, ChatAction.UploadPhoto, cancel);

                // creating image
                var promptSeed = GetRandomSeed();
                var imageBytes = await CreateImageAsync(
                    clientAnswer.prompt,
                    "", // no negative prompt for now
                    clientAnswer.orientation ?? "square",
                    promptSeed,
                    _options.ImageIterationSteps
                );

                // send image to chat
                if (imageBytes != null)
                {
                    using var stream = new MemoryStream(imageBytes);
                    var photo = new InputOnlineFile(stream, "Generated Image");

                    // trim caption to 1000 characters
                    var caption = clientAnswer.text ?? clientAnswer.prompt;
                    if (caption.Length > 1000)
                        caption = caption.Substring(0, 1000) + "...";

                    // send image
                    await botClient.SendPhotoAsync(
                        chatId,
                        photo,
                        caption,
                        replyToMessageId: chatType == "group" ? messageId : null,
                        replyMarkup: null,
                        cancellationToken: cancel,
                        parseMode: ParseMode.Markdown
                    );

                    // send image to showroom
                    if (!string.IsNullOrEmpty(_options.BotShowRoomChannel))
                    {
                        // trim caption to 1000 characters
                        caption = clientAnswer.prompt;
                        if (caption.Length > 1000)
                            caption = caption.Substring(0, 1000) + "...";
                        // forward to showroom
                        photo?.Content?.Seek(0, SeekOrigin.Begin);
                        await botClient.SendPhotoAsync(
                            _options.BotShowRoomChannel,
                            photo!,
                            caption,
                            cancellationToken: cancel
                        );
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Bot HandleUpdateAsync error, context {contextId} not saved");
            try
            {
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"Ой якесь лишенько трапилось...",
                    cancellationToken: cancel
                );
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, $"Bot HandleUpdateAsync FATAL error");
            }
        }
    }
}
