using Malyuvach.Models;

namespace Malyuvach.Services.LLM;

public interface ILLMService
{
    Task<ClientAnswer?> GetAnswerAsync(string message, string contextId, CancellationToken cancellationToken);
    Task<ClientAnswer?> ValidateJSONAsync(string message, CancellationToken cancellationToken);
    void SaveContext(OllamaSharp.Chat chat, string contextId);
    OllamaSharp.Chat LoadContext(string contextId);
    void ClearContext(OllamaSharp.Chat chat);
    void UpdateSystemPrompt(OllamaSharp.Chat chat);
}