public class MalyuvachOptions
{
    public const string Section = "Malyuvach";

    public class MalyuvachWokrflow
    {
        public string ComfyUIWorkflowPath { get; set; } = "workflow/workflow_api-flux-schnell.json";
        public List<string> PositivePromptFieldId { get; set; } = new() { "6.inputs.text" };
        public List<string> NegativePromptFieldId { get; set; } = new() { "7.inputs.text" };

        public List<string> ImageWidthFieldId { get; set; } = new() { "5.inputs.width" };
        public List<string> ImageHeightFieldId { get; set; } = new() { "5.inputs.height" };
        public List<string> NoiseSeedFieldId { get; set; } = new() { "25.inputs.noise_seed" };
        public List<string> StepsFieldId { get; set; } = new() { "17.inputs.steps" };
        public string OutputNodeId { get; set; } = "26";
    }

    public string ModelName { get; set; } = "gemma2:latest";
    public string ComfyUIBaseUrl { get; set; } = "http://127.0.0.1:8188";
    public string OllamaUIBaseUrl { get; set; } = "http://127.0.0.1:11434";
    public int MaxContextSize { get; set; } = 4096;
    public int MaxContextMsgs { get; set; } = 10;
    public int MaxAnswerRetries { get; set; } = 3;

    public string BotShowRoomChannel { get; set; } = string.Empty;
    public List<string> BotNames { get; set; } = new List<string> { };
    public string BotKey { get; set; } = string.Empty;

    public int ImageIterationSteps { get; set; } = 6;
    public int ImageDefaultXDimension { get; set; } = 1200;
    public int ImageDefaultYDimension { get; set; } = 800;

    public bool UseJSONValidator { get; set; } = false;
    public string? JSONValidatorSystemPromptPath { get; set; }
    public string? MainSystemPromptPath { get; set; }
    public string ContextsPath { get; set; } = "contexts";
    public bool SkipUpdates { get; set; }

    public MalyuvachWokrflow Workflow { get; set; } = new MalyuvachWokrflow();
}
