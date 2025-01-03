public class MalyuvachOptions
{
    public const string Section = "Malyuvach";

    public string ModelName { get; set; } = "gemma2:latest";
    public string ComfyUIBaseUrl { get; set; } = "http://127.0.0.1:8188";
    public string OllamaUIBaseUrl { get; set; } = "http://127.0.0.1:11434";
    public int MaxContextSize { get; set; } = 4096;
    public int MaxContextMsgs { get; set; } = 10;
    public int MaxAnswerRetries { get; set; } = 3;

    public string BotShowRoomChannel { get; set; } = "@malyuvachshowroom";
    public List<string> BotNames { get; set; } = new List<string> { "@malyuvach_bot" };
    public string BotKey { get; set; } = string.Empty;

    public string ImagePromptApiPath { get; set; } = "workflow/workflow_api.json";
    public int ImageIterationSteps { get; set; } = 6;
    public int ImageDefaultXDimension { get; set; } = 1200;
    public int ImageDefaultYDimension { get; set; } = 800;

    public bool UseJSONValidator { get; set; } = false;
    public string? JSONValidatorSystemPromptPath { get; set; }
    public string? MainSystemPromptPath { get; set; }
    public string ContextsPath { get; set; } = "contexts";
    public bool SkipUpdates { get; set; }
}
