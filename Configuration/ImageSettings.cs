namespace Malyuvach.Configuration;

public class ImageSettings
{
    public string ComfyUIBaseUrl { get; set; } = "http://127.0.0.1:8188";
    public int ImageIterationSteps { get; set; } = 6;
    public int ImageDefaultXDimension { get; set; } = 1200;
    public int ImageDefaultYDimension { get; set; } = 800;

    public WorkflowSettings Workflow { get; set; } = new();
}

public class WorkflowSettings
{
    public string? ComfyUIWorkflowPath { get; set; }
    public List<string>? PositivePromptFieldId { get; set; } = new();
    public List<string>? NegativePromptFieldId { get; set; } = new();
    public List<string>? ImageWidthFieldId { get; set; } = new();
    public List<string>? ImageHeightFieldId { get; set; } = new();
    public List<string>? NoiseSeedFieldId { get; set; } = new();
    public List<string>? StepsFieldId { get; set; } = new();
    public string? OutputNodeId { get; set; }
}