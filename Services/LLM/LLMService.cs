using System.Text.Json;
using Malyuvach.Configuration;
using Malyuvach.Models;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

namespace Malyuvach.Services.LLM;

public class LLMService : ILLMService
{
    private readonly ILogger<LLMService> _logger;
    private readonly LLMSettings _settings;
    private readonly IOptions<JsonSerializerOptions> _jsonOptions;
    private string _systemPromptCache = string.Empty;
    private string _jsonValidatorSystemPromptCache = string.Empty;

    public LLMService(
        ILogger<LLMService> logger,
        IOptions<LLMSettings> settings,
        IOptions<JsonSerializerOptions> jsonOptions)
    {
        _logger = logger;
        _settings = settings.Value;
        _jsonOptions = jsonOptions;
        _logger.LogInformation("LLM service initialized with model {Model}", _settings.ModelName);
    }

    private string GetSystemPrompt()
    {
        try
        {
            if (string.IsNullOrEmpty(_systemPromptCache))
            {
                _systemPromptCache = File.ReadAllText(_settings.MainSystemPromptPath!);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading system prompt from {Path}", _settings.MainSystemPromptPath);
        }
        return _systemPromptCache;
    }

    private string GetJsonValidatorSystemPrompt()
    {
        try
        {
            if (string.IsNullOrEmpty(_jsonValidatorSystemPromptCache))
            {
                _jsonValidatorSystemPromptCache = File.ReadAllText(_settings.JSONValidatorSystemPromptPath!);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading JSON validator system prompt from {Path}", _settings.JSONValidatorSystemPromptPath);
        }
        return _jsonValidatorSystemPromptCache;
    }

    public async Task<ClientAnswer?> GetAnswerAsync(string message, string contextId, CancellationToken cancellationToken)
    {
        var chat = LoadContext(contextId);
        var initialMessagesCount = chat.Messages.Count;
        var retries = _settings.MaxAnswerRetries;

        while (retries > 0)
        {
            try
            {
                var response = chat.SendAsAsync(ChatRole.User, message, cancellationToken);
                var answer = string.Empty;

                await foreach (var token in response)
                {
                    answer += token;
                }

                answer = answer.Trim();
                _logger.LogDebug("ANSWER ({Model}): {Answer}", _settings.ModelName, answer);

                var result = await ValidateJSONAsync(answer, cancellationToken);
                if (result == null)
                {
                    if (--retries > 0)
                    {
                        _logger.LogWarning("Retry {Attempt}/{Max} for context {ContextId}",
                            _settings.MaxAnswerRetries - retries + 1,
                            _settings.MaxAnswerRetries,
                            contextId);

                        FixContext(chat, chat.Messages.Count - initialMessagesCount);
                        continue;
                    }

                    FixContext(chat, chat.Messages.Count - initialMessagesCount);
                    return null;
                }

                // Replace message for consistency
                chat.Messages.Last().Content = JsonSerializer.Serialize(result, _jsonOptions.Value);

                // Clean up and save context
                ClearContext(chat);
                SaveContext(chat, contextId);

                _logger.LogDebug("Context managed: {ContextId} ({Count}/{Max} messages)",
                    contextId, chat.Messages.Count, _settings.MaxContextMsgs);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting answer for context {ContextId}", contextId);
                if (--retries <= 0)
                {
                    FixContext(chat, chat.Messages.Count - initialMessagesCount);
                    return null;
                }

                _logger.LogWarning("Retry {Attempt}/{Max} after error for context {ContextId}",
                    _settings.MaxAnswerRetries - retries + 1,
                    _settings.MaxAnswerRetries,
                    contextId);
            }
        }

        return null;
    }

    public async Task<ClientAnswer?> ValidateJSONAsync(string message, CancellationToken cancellationToken)
    {
        var answer = message;

        if (_settings.UseJSONValidator)
        {
            try
            {
                var ollama = new OllamaApiClient(new Uri(_settings.OllamaUIBaseUrl));
                ollama.SelectedModel = _settings.ModelName;

                var validator = new Chat(ollama, GetJsonValidatorSystemPrompt());
                validator.Options = new RequestOptions
                {
                    NumCtx = 8192,
                    Temperature = _settings.JSONValidatorTemperature
                };

                var response = validator.SendAsync(message, cancellationToken);
                answer = string.Empty;

                await foreach (var token in response)
                {
                    answer += token;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating JSON");
                return null;
            }
        }

        // Clean up the response
        answer = answer
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Replace("```json", "")
            .Replace("```", "")
            .Trim();

        _logger.LogDebug("Validator raw response t={Temperature}: {Answer}",
            _settings.JSONValidatorTemperature, answer);

        try
        {
            var result = JsonSerializer.Deserialize<ClientAnswer>(answer, _jsonOptions.Value);
            if (result != null)
            {
                result.text = result.text?.Trim();
                result.prompt = result.prompt?.Trim();
                result.orientation = result.orientation?.Trim();

                if (string.IsNullOrEmpty(result.text) && string.IsNullOrEmpty(result.prompt))
                {
                    _logger.LogError("Null or empty answer after validation");
                    return null;
                }

                if (_settings.UseJSONValidator)
                {
                    _logger.LogInformation("Validated result: {Result}",
                        JsonSerializer.Serialize(result, _jsonOptions.Value));
                }

                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JSON validation error");
        }

        return null;
    }

    public void SaveContext(Chat chat, string contextId)
    {
        try
        {
            var contextPath = Path.Combine(_settings.ContextsPath, $"{contextId}.json");
            Directory.CreateDirectory(_settings.ContextsPath);

            var contextJson = JsonSerializer.Serialize(chat.Messages, _jsonOptions.Value);
            File.WriteAllText(contextPath, contextJson);

            _logger.LogDebug("Context saved: {ContextId} ({Count} messages)", contextId, chat.Messages.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving context {ContextId}", contextId);
        }
    }

    public Chat LoadContext(string contextId)
    {
        var ollamaClient = new OllamaApiClient(new Uri(_settings.OllamaUIBaseUrl));
        ollamaClient.SelectedModel = _settings.ModelName;
        var chat = new Chat(ollamaClient, GetSystemPrompt());
        chat.Options = new RequestOptions
        {
            NumCtx = _settings.MaxContextSize,
            Temperature = _settings.DialogTemperature
        };

        try
        {
            var contextPath = Path.Combine(_settings.ContextsPath, $"{contextId}.json");
            if (File.Exists(contextPath))
            {
                var contextJson = File.ReadAllText(contextPath);
                var messages = JsonSerializer.Deserialize<List<Message>>(contextJson, _jsonOptions.Value);

                chat.Messages.Clear();
                chat.Messages.AddRange(messages!);

                _logger.LogDebug("Context loaded: {ContextId} ({Count} messages) t={Temperature}",
                    contextId, messages!.Count, _settings.DialogTemperature);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading context {ContextId}", contextId);
        }

        UpdateSystemPrompt(chat);
        return chat;
    }

    public void ClearContext(Chat chat)
    {
        if (chat.Messages.Count > _settings.MaxContextMsgs)
        {
            var messages = chat.Messages.ToList();
            while (messages.Count > _settings.MaxContextMsgs)
            {
                messages.RemoveAt(1); // Keep system prompt
            }
            chat.Messages.Clear();
            chat.Messages.AddRange(messages);
        }
    }

    public void UpdateSystemPrompt(Chat chat)
    {
        if (chat.Messages.Count == 0) return;

        var systemPrompt = GetSystemPrompt();
        if (chat.Messages.ElementAt(0).Content == systemPrompt) return;

        var systemMessage = new Message(ChatRole.System, systemPrompt);
        var messages = chat.Messages.ToList();
        messages[0] = systemMessage;
        chat.Messages.Clear();
        chat.Messages.AddRange(messages);
    }

    private void FixContext(Chat chat, int messagesToRemove)
    {
        if (messagesToRemove <= 0) return;

        var messages = chat.Messages.ToList();
        if (messages.Count > messagesToRemove)
        {
            chat.Messages.Clear();
            chat.Messages.AddRange(messages.GetRange(0, messages.Count - messagesToRemove));
        }
    }
}