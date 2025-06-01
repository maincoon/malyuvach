namespace Malyuvach.Services.STT;

/// <summary>
/// Interface for Speech-to-Text service that converts audio streams to text
/// </summary>
public interface ISTTService
{
    /// <summary>
    /// Transcribes audio from a stream to text using Whisper.Net
    /// </summary>
    /// <param name="audioStream">Audio stream to transcribe</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Transcribed text or empty string if transcription failed</returns>
    Task<string> TranscribeAsync(Stream audioStream, CancellationToken cancellationToken = default);
}
