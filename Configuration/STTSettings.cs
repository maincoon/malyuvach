namespace Malyuvach.Configuration;

/// <summary>
/// Configuration settings for Speech-to-Text service using Whisper.Net
/// </summary>
public class STTSettings
{
    /// <summary>
    /// Path to the Whisper model file (.ggml format)
    /// </summary>
    public string ModelPath { get; set; } = "models/ggml-base.bin";

    /// <summary>
    /// Whether to use GPU acceleration if available
    /// </summary>
    public bool UseGPU { get; set; } = false;

    /// <summary>
    /// Language code for transcription (e.g., "en", "uk", "ru", "auto" for auto-detection)
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Number of threads to use for processing (0 = auto-detect)
    /// </summary>
    public int Threads { get; set; } = 0;

    /// <summary>
    /// Translate output to English if source language is not English
    /// </summary>
    public bool Translate { get; set; } = false;
}
