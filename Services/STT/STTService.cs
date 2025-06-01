using System.Diagnostics;
using Malyuvach.Configuration;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace Malyuvach.Services.STT;

/// <summary>
/// Speech-to-Text service implementation using Whisper.Net
/// </summary>
public class STTService : ISTTService, IDisposable
{
    private readonly ILogger<STTService> _logger;
    private readonly STTSettings _settings;
    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _whisperProcessor;
    private readonly SemaphoreSlim _semaphore;
    private bool _disposed = false;

    public STTService(
        ILogger<STTService> logger,
        IOptions<STTSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
        _semaphore = new SemaphoreSlim(1, 1); // Only one transcription at a time

        InitializeWhisper();
    }

    /// <summary>
    /// Initialize Whisper model and processor
    /// </summary>
    private void InitializeWhisper()
    {
        try
        {
            _logger.LogInformation("Initializing Whisper model from {ModelPath}", _settings.ModelPath);

            if (!File.Exists(_settings.ModelPath))
            {
                _logger.LogError("Whisper model file not found at {ModelPath}", _settings.ModelPath);
                throw new FileNotFoundException($"Whisper model file not found at {_settings.ModelPath}");
            }

            // Create Whisper factory
            _whisperFactory = WhisperFactory.FromPath(_settings.ModelPath, new WhisperFactoryOptions
            {
                UseGpu = _settings.UseGPU
            });


            // Create processor with settings
            var processorBuilder = _whisperFactory.CreateBuilder();

            // Configure language
            if (!string.IsNullOrEmpty(_settings.Language) && _settings.Language.ToLower() != "auto")
            {
                processorBuilder = processorBuilder.WithLanguage(_settings.Language);
            }
            else
            {
                // Enable auto language detection
                processorBuilder.WithLanguageDetection();
            }

            // Configure other settings
            if (_settings.Threads > 0)
            {
                processorBuilder = processorBuilder.WithThreads(_settings.Threads);
            }

            if (_settings.Translate)
            {
                processorBuilder = processorBuilder.WithTranslate();
            }

            _whisperProcessor = processorBuilder.Build();

            _logger.LogInformation(
                "Whisper model initialized successfully. Language: {Language}, Threads: {Threads}, Gpu: {UseGpu}",
                _settings.Language, _settings.Threads, _settings.UseGPU);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Whisper model");
            throw;
        }
    }

    /// <summary>
    /// Transcribes audio stream to text using Whisper model.
    /// Expects OGG/Opus format from Telegram voice messages.
    /// </summary>
    public async Task<string> TranscribeAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(STTService));
        }

        if (_whisperProcessor == null)
        {
            _logger.LogError("Whisper processor is not initialized");
            return string.Empty;
        }

        // Ensure only one transcription runs at a time
        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogDebug("Audio OGG size: {Size} bytes", audioStream.Length);

            // Convert OGG/Opus to WAV format for Whisper.net
            using var wavStream = await AudioConverter.ConvertOggOpusToWav(audioStream, cancellationToken);

            // Process audio with Whisper
            var transcriptionResult = new List<string>();
            await foreach (var segment in _whisperProcessor.ProcessAsync(wavStream, cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    transcriptionResult.Add(segment.Text.Trim());
                }
            }
            var fullTranscription = string.Join(" ", transcriptionResult).Trim();

            // Log transcription result
            stopwatch.Stop();
            _logger.LogInformation(
                "Audio transcription completed in {ElapsedMs}ms. Result length: {Length} characters",
                stopwatch.ElapsedMilliseconds, fullTranscription.Length);

            if (string.IsNullOrWhiteSpace(fullTranscription))
            {
                _logger.LogWarning("Transcription result is empty");
                return string.Empty;
            }

            _logger.LogDebug("Transcription result: {Text}", fullTranscription);
            return fullTranscription;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during audio transcription");
            return string.Empty;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _whisperProcessor?.Dispose();
        _whisperFactory?.Dispose();
        _semaphore?.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
