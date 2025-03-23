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
    public string ComfyUIWorkflowPath { get; set; } = "workflow/workflow_api-flux-schnell.json";
    public List<string> PositivePromptFieldId { get; set; } = new() { "6.inputs.text" };
    public List<string> NegativePromptFieldId { get; set; } = new() { "7.inputs.text" };
    public List<string> ImageWidthFieldId { get; set; } = new() { "5.inputs.width" };
    public List<string> ImageHeightFieldId { get; set; } = new() { "5.inputs.height" };
    public List<string> NoiseSeedFieldId { get; set; } = new() { "25.inputs.noise_seed" };
    public List<string> StepsFieldId { get; set; } = new() { "17.inputs.steps" };
    public string OutputNodeId { get; set; } = "26";
}