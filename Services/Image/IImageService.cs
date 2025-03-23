namespace Malyuvach.Services.Image;

public interface IImageService
{
    Task<byte[]?> CreateImageAsync(
        string positivePrompt,
        string negativePrompt,
        string orientation,
        long seed,
        int steps);

    Task<byte[]?> WaitForPromptImageAsync(string promptId);
}