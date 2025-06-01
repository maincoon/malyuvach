using Concentus;
using Concentus.Oggfile;
using Concentus.Structs;
using System.IO;

namespace Malyuvach.Services.STT;

/// <summary>
/// Audio format converter for OGG/Opus to PCM WAV
/// </summary>
public static class AudioConverter
{
    /// <summary>
    /// Converts OGG/Opus stream to WAV PCM format suitable for Whisper.net
    /// </summary>
    /// <param name="oggStream">Input OGG/Opus stream</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>WAV formatted stream</returns>
    public static async Task<MemoryStream> ConvertOggOpusToWav(Stream oggStream, CancellationToken cancellationToken = default)
    {
        try
        {
            // Reset stream position
            if (oggStream.CanSeek)
                oggStream.Position = 0;

            Console.WriteLine($"[AudioConverter] Starting OGG/Opus conversion. Input size: {oggStream.Length} bytes");

            // Create Opus decoder for mono 48kHz (Telegram default)
            var decoder = OpusCodecFactory.CreateDecoder(48000, 1);
            var oggIn = new OpusOggReadStream(decoder, oggStream);

            Console.WriteLine("[AudioConverter] Created Opus decoder and OGG reader");

            // Prepare output stream with WAV header for 16kHz mono PCM
            const int sampleRate = 16000;
            const int channels = 1;
            const int bitsPerSample = 16;

            var wavStream = new MemoryStream();

            // Write WAV header (44 bytes)
            await WriteWavHeader(wavStream, sampleRate, channels, bitsPerSample);

            Console.WriteLine("[AudioConverter] Written WAV header");

            // Decode and resample audio
            var pcmBuffer = new short[960]; // Opus frame size for 20ms at 48kHz
            var resampleBuffer = new short[320]; // Target frame size for 20ms at 16kHz

            long totalSamples = 0;
            int packetCount = 0;

            while (oggIn.HasNextPacket)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var samplesDecoded = oggIn.DecodeNextPacket();
                if (samplesDecoded != null && samplesDecoded.Length > 0)
                {
                    packetCount++;
                    // Simple downsampling from 48kHz to 16kHz (3:1 ratio)
                    var outputSamples = Resample48to16(samplesDecoded, samplesDecoded.Length, resampleBuffer);

                    // Write PCM data to WAV stream
                    for (int i = 0; i < outputSamples; i++)
                    {
                        var bytes = BitConverter.GetBytes(resampleBuffer[i]);
                        await wavStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                    }

                    totalSamples += outputSamples;
                }
            }

            Console.WriteLine($"[AudioConverter] Processed {packetCount} packets, total samples: {totalSamples}");

            // Update WAV header with actual data size
            await UpdateWavHeader(wavStream, totalSamples, sampleRate, channels, bitsPerSample);

            // Reset stream position for reading
            wavStream.Position = 0;
            Console.WriteLine($"[AudioConverter] Conversion completed. Output WAV size: {wavStream.Length} bytes");
            return wavStream;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioConverter] Error: {ex.Message}");
            throw new InvalidOperationException($"Failed to convert OGG/Opus to WAV: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Simple 3:1 downsampling from 48kHz to 16kHz
    /// </summary>
    private static int Resample48to16(short[] input, int inputLength, short[] output)
    {
        var outputLength = inputLength / 3;
        for (int i = 0; i < outputLength; i++)
        {
            // Take every 3rd sample for simple downsampling
            output[i] = input[i * 3];
        }
        return outputLength;
    }

    /// <summary>
    /// Writes WAV file header
    /// </summary>
    private static async Task WriteWavHeader(Stream stream, int sampleRate, int channels, int bitsPerSample)
    {
        var writer = new BinaryWriter(stream);

        // RIFF header
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0); // File size - will be updated later
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // Format chunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Format chunk size
        writer.Write((short)1); // PCM format
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8); // Byte rate
        writer.Write((short)(channels * bitsPerSample / 8)); // Block align
        writer.Write((short)bitsPerSample);

        // Data chunk header
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(0); // Data size - will be updated later

        await stream.FlushAsync();
    }

    /// <summary>
    /// Updates WAV header with actual file and data sizes
    /// </summary>
    private static async Task UpdateWavHeader(Stream stream, long totalSamples, int sampleRate, int channels, int bitsPerSample)
    {
        var dataSize = (int)(totalSamples * channels * bitsPerSample / 8);
        var fileSize = dataSize + 36; // 44 byte header - 8 bytes for RIFF header

        // Update file size
        stream.Position = 4;
        var writer = new BinaryWriter(stream);
        writer.Write(fileSize);

        // Update data size
        stream.Position = 40;
        writer.Write(dataSize);

        await stream.FlushAsync();
    }
}
