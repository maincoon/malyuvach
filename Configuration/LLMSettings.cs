namespace Malyuvach.Configuration;

public class LLMSettings
{
    public string ModelName { get; set; } = "gemma2:latest";
    public string OllamaUIBaseUrl { get; set; } = "http://127.0.0.1:11434";
    public int MaxContextSize { get; set; } = 4096;
    public int MaxContextMsgs { get; set; } = 10;
    public int MaxAnswerRetries { get; set; } = 3;
    public float JSONValidatorTemperature { get; set; } = 0.5f;
    public float DialogTemperature { get; set; } = 5.0f;
    public bool UseJSONValidator { get; set; } = false;
    public string? JSONValidatorSystemPromptPath { get; set; }
    public string? MainSystemPromptPath { get; set; }
    public string ContextsPath { get; set; } = "contexts";
    public int MaxAnswerLength { get; set; } = 4096;
    public int? OllamaKeepAlive { get; set; } = 0;
}