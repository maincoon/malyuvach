using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Malyuvach.Configuration;
using Malyuvach.Utilities;
using Microsoft.Extensions.Options;

namespace Malyuvach.Services.Image;

public class ImageService : IImageService
{
    private readonly ILogger<ImageService> _logger;
    private readonly ImageSettings _settings;
    private readonly IOptions<JsonSerializerOptions> _jsonOptions;
    private readonly HttpClient _httpClient;

    public ImageService(
        ILogger<ImageService> logger,
        IOptions<ImageSettings> settings,
        IOptions<JsonSerializerOptions> jsonOptions,
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _settings = settings.Value;
        _jsonOptions = jsonOptions;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<byte[]?> CreateImageAsync(
        string positivePrompt,
        string negativePrompt,
        string orientation,
        long seed,
        int steps)
    {
        try
        {
            _logger.LogDebug("Starting image generation with params: Orientation={Orientation}, Steps={Steps}, Seed={Seed}",
                orientation, steps, seed);

            // Load workflow template
            var promptApiText = await File.ReadAllTextAsync(_settings.Workflow.ComfyUIWorkflowPath);
            var workflowJson = JsonNode.Parse(promptApiText)!;

            _logger.LogDebug("Loaded workflow template from {Path}", _settings.Workflow.ComfyUIWorkflowPath);

            // Set prompt texts
            JsonHelper.SetJsonObjectValues(workflowJson, _settings.Workflow.PositivePromptFieldId, positivePrompt, _jsonOptions.Value);
            JsonHelper.SetJsonObjectValues(workflowJson, _settings.Workflow.NegativePromptFieldId, negativePrompt, _jsonOptions.Value);

            // Set image dimensions based on orientation
            var width = _settings.ImageDefaultXDimension;
            var height = _settings.ImageDefaultYDimension;

            if (orientation.Equals("portrait", StringComparison.OrdinalIgnoreCase))
            {
                // Swap dimensions for portrait orientation
                (width, height) = (height, width);
            }

            _logger.LogDebug("Setting image dimensions: {Width}x{Height}", width, height);

            JsonHelper.SetJsonObjectValues(workflowJson, _settings.Workflow.ImageWidthFieldId, width, _jsonOptions.Value);
            JsonHelper.SetJsonObjectValues(workflowJson, _settings.Workflow.ImageHeightFieldId, height, _jsonOptions.Value);

            // Set other parameters
            JsonHelper.SetJsonObjectValues(workflowJson, _settings.Workflow.NoiseSeedFieldId, seed, _jsonOptions.Value);
            JsonHelper.SetJsonObjectValues(workflowJson, _settings.Workflow.StepsFieldId, steps, _jsonOptions.Value);

            // Create the final prompt object with the workflow
            var promptApiJson = new JsonObject
            {
                ["prompt"] = workflowJson
            };

            var promptJson = promptApiJson.ToJsonString();
            _logger.LogDebug("Prepared ComfyUI workflow: {Workflow}", promptJson);

            // Send prompt to ComfyUI
            using var promptContent = new StringContent(
                promptJson,
                System.Text.Encoding.UTF8,
                "application/json"
            );

            using var promptResponse = await _httpClient.PostAsync(
                $"{_settings.ComfyUIBaseUrl}/prompt",
                promptContent
            );

            var responseContent = await promptResponse.Content.ReadAsStringAsync();
            if (!promptResponse.IsSuccessStatusCode)
            {
                _logger.LogError("ComfyUI prompt error: {StatusCode} - {Error}",
                    promptResponse.StatusCode, responseContent);
                return null;
            }

            var promptResult = JsonNode.Parse(responseContent);
            if (promptResult?["prompt_id"] == null)
            {
                _logger.LogError("Invalid ComfyUI response - missing prompt_id: {Response}", responseContent);
                return null;
            }

            var promptId = promptResult["prompt_id"]!.GetValue<string>();
            _logger.LogInformation("Image generation started: {PromptId}", promptId);

            // Wait for and return the generated image
            return await WaitForPromptImageAsync(promptId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating image");
            return null;
        }
    }

    public async Task<byte[]?> WaitForPromptImageAsync(string promptId)
    {
        while (true)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"{_settings.ComfyUIBaseUrl}/history/{promptId}"
                );

                var responseContent = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.LogError("ComfyUI history error: {StatusCode} - {Error}",
                        response.StatusCode, responseContent);
                    return null;
                }

                _logger.LogTrace("ComfyUI history response: {Response}", responseContent);

                var responseJson = JsonNode.Parse(responseContent);
                if (responseJson?[promptId]?["outputs"]?[_settings.Workflow.OutputNodeId]?["images"] != null)
                {
                    var outputFile = responseJson[promptId]!["outputs"]![_settings.Workflow.OutputNodeId]!
                        ["images"]![0]!["filename"]!.GetValue<string>();

                    _logger.LogDebug("Prompt {PromptId} finished - {OutputFile}", promptId, outputFile);

                    using var imageResponse = await _httpClient.GetAsync(
                        $"{_settings.ComfyUIBaseUrl}/view?filename={outputFile}&type=temp"
                    );

                    var imageData = await imageResponse.Content.ReadAsByteArrayAsync();
                    _logger.LogInformation("Image downloaded: {PromptId} {OutputFile} ({Size} bytes)",
                        promptId, outputFile, imageData.Length);

                    return imageData;
                }

                await Task.Delay(300); // Wait before next check
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error waiting for prompt {PromptId}", promptId);
                return null;
            }
        }
    }
}