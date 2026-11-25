using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Battuta.Windows.Diy.Audio;

/// <summary>
/// Pure managed PCM WAV decoder used as the cross-platform default
/// <see cref="IDiyAudioDecodePipeline"/>. It intentionally understands only
/// uncompressed RIFF/WAVE PCM and IEEE float sources; platforms that support
/// compressed formats (MP3, M4A, AIFF…) wrap or replace it.
/// </summary>
public sealed class WavDiyAudioDecodePipeline : IDiyAudioDecodePipeline
{
    /// <inheritdoc />
    public float[] DecodeMono48Khz(
        string sourcePath,
        long maximumSourceBytes,
        long maximumDecodedBytes,
        double minimumDurationSeconds,
        double maximumDurationSeconds,
        int minimumSampleRate,
        int maximumSampleRate,
        int maximumChannelCount,
        CancellationToken cancellationToken)
    {
        ValidateSourcePath(sourcePath, maximumSourceBytes);
        FileStream? sourceStream = null;
        try
        {
            sourceStream = new FileStream(
                Path.GetFullPath(sourcePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1_024,
                FileOptions.SequentialScan);
            if (sourceStream.Length <= 0 || sourceStream.Length > maximumSourceBytes)
            {
                throw new DiyAudioException("音频文件超过安全大小限制。");
            }

            return DecodeStream(
                sourceStream,
                maximumDecodedBytes,
                minimumDurationSeconds,
                maximumDurationSeconds,
                minimumSampleRate,
                maximumSampleRate,
                maximumChannelCount,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DiyAudioException)
        {
            throw;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or FormatException)
        {
            throw new DiyAudioException($"无法读取音频：{error.Message}", error);
        }
        finally
        {
            sourceStream?.Dispose();
        }
    }

    /// <summary>
    /// Decodes an already positioned RIFF/WAVE stream into mono 48 kHz
    /// samples. Used by platform pipelines that obtain WAV bytes from an
    /// external decoder (for example ffmpeg on Linux).
    /// </summary>
    public static float[] DecodeStream(
        Stream source,
        long maximumDecodedBytes,
        double minimumDurationSeconds,
        double maximumDurationSeconds,
        int minimumSampleRate,
        int maximumSampleRate,
        int maximumChannelCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (format, dataPosition, dataLength) = ReadFormat(source);
        if (format.SampleRate < minimumSampleRate ||
            format.SampleRate > maximumSampleRate ||
            format.Channels <= 0 ||
            format.Channels > maximumChannelCount)
        {
            throw new DiyAudioException("源音频采样率或声道数超出安全范围。");
        }

        if (dataLength <= 0 || dataLength > maximumDecodedBytes)
        {
            throw new DiyAudioException("源音频解码后超过 64 MiB 安全上限。");
        }

        var frames = (int)(dataLength / format.FrameBytes);
        var reportedDuration = (double)frames / format.SampleRate;
        if (!double.IsFinite(reportedDuration) ||
            reportedDuration < minimumDurationSeconds ||
            reportedDuration > maximumDurationSeconds)
        {
            throw new DiyAudioException(
                $"源音频时长必须介于 {minimumDurationSeconds:0.###}–{maximumDurationSeconds:0.###} 秒。");
        }

        var mono = ReadMonoSamples(
            source,
            dataPosition,
            dataLength,
            frames,
            format,
            maximumDecodedBytes,
            cancellationToken);

        var maximumFrames = checked((int)Math.Floor(maximumDurationSeconds * DiyAudioDecoding.OutputSampleRate + 1e-9));
        var minimumFrames = checked((int)Math.Ceiling(minimumDurationSeconds * DiyAudioDecoding.OutputSampleRate - 1e-9));
        var output = format.SampleRate == DiyAudioDecoding.OutputSampleRate
            ? mono
            : ResampleLinear(
                mono,
                format.SampleRate,
                DiyAudioDecoding.OutputSampleRate,
                maximumFrames,
                cancellationToken);
        if (output.Length > maximumFrames)
        {
            throw new DiyAudioException(
                $"源音频时长必须介于 {minimumDurationSeconds:0.###}–{maximumDurationSeconds:0.###} 秒。");
        }

        if (output.Length < minimumFrames)
        {
            throw new DiyAudioException(
                $"源音频时长必须介于 {minimumDurationSeconds:0.###}–{maximumDurationSeconds:0.###} 秒。");
        }

        if (!output.Any(sample => Math.Abs(sample) > 1e-7f))
        {
            throw new DiyAudioException("音频解码后没有可用采样。");
        }

        return output;
    }

    private static void ValidateSourcePath(string sourcePath, long maximumSourceBytes)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new DiyAudioException("未选择音频文件。");
        }

        var extension = Path.GetExtension(sourcePath);
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".wave", ".aif", ".aiff", ".mp3", ".m4a", ".aac", ".wma", ".caf", ".audio",
        };
        if (!allowedExtensions.Contains(extension))
        {
            throw new DiyAudioException("不支持此音频文件格式。");
        }

        var file = new FileInfo(Path.GetFullPath(sourcePath));
        if (!file.Exists)
        {
            throw new DiyAudioException("找不到音频文件。");
        }

        if ((file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new DiyAudioException("所选项目不是普通音频文件。");
        }

        if (file.Length <= 0)
        {
            throw new DiyAudioException("音频文件为空。");
        }

        if (file.Length > maximumSourceBytes)
        {
            throw new DiyAudioException("音频文件超过安全大小限制。");
        }
    }

    private readonly record struct WavFormat(
        int Channels,
        int SampleRate,
        int BitsPerSample,
        bool IsFloat,
        int FrameBytes);

    private static (WavFormat Format, long DataPosition, long DataLength) ReadFormat(Stream source)
    {
        if (!source.CanSeek)
        {
            throw new DiyAudioException("无法读取音频：输入流不支持定位。");
        }

        var header = ReadExactlyOrThrow(source, 12);
        if (header[0] != (byte)'R' || header[1] != (byte)'I' || header[2] != (byte)'F' || header[3] != (byte)'F' ||
            header[8] != (byte)'W' || header[9] != (byte)'A' || header[10] != (byte)'V' || header[11] != (byte)'E')
        {
            throw new DiyAudioException("无法读取音频：不是有效的 WAV 文件。");
        }

        WavFormat? format = null;
        long dataPosition = -1;
        long dataLength = -1;
        while (true)
        {
            var chunkHeader = ReadChunkHeaderOrStop(source);
            if (chunkHeader is null)
            {
                break;
            }

            var (chunkId, chunkSize) = chunkHeader.Value;
            var payloadPosition = source.Position;
            var remaining = source.Length - payloadPosition;
            var payloadLength = chunkSize == 0xFFFF_FFFF || chunkSize > remaining
                ? remaining
                : chunkSize;
            switch (chunkId)
            {
                case "fmt ":
                    format = ReadFormatChunk(source, payloadLength);
                    break;
                case "data":
                    dataPosition = payloadPosition;
                    dataLength = payloadLength;
                    break;
            }

            source.Seek(payloadPosition + payloadLength + (payloadLength & 1), SeekOrigin.Begin);
            if (source.Position >= source.Length)
            {
                break;
            }
        }

        if (format is not { } resolvedFormat)
        {
            throw new DiyAudioException("无法读取音频：WAV 缺少 fmt 块。");
        }

        if (dataPosition < 0)
        {
            throw new DiyAudioException("无法读取音频：WAV 缺少 data 块。");
        }

        return (resolvedFormat, dataPosition, dataLength);
    }

    private static (string Id, long Size)? ReadChunkHeaderOrStop(Stream source)
    {
        if (source.Length - source.Position < 8)
        {
            return null;
        }

        var header = ReadExactlyOrThrow(source, 8);
        var id = Encoding.ASCII.GetString(header, 0, 4);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        return (id, size);
    }

    private static WavFormat ReadFormatChunk(Stream source, long payloadLength)
    {
        if (payloadLength < 16)
        {
            throw new DiyAudioException("无法读取音频：WAV fmt 块损坏。");
        }

        var payload = ReadExactlyOrThrow(source, (int)Math.Min(payloadLength, 40));
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12, 2));
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(14, 2));
        if (tag == 0xFFFE && payloadLength >= 40)
        {
            // WAVE_FORMAT_EXTENSIBLE: the effective format is the first two
            // bytes of the SubFormat GUID.
            tag = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(24, 2));
        }

        var isFloat = tag switch
        {
            1 => false,
            3 => true,
            _ => throw new DiyAudioException("无法读取音频：仅支持未压缩的 PCM WAV。"),
        };
        var bytesPerSample = bitsPerSample switch
        {
            8 => 1,
            16 => 2,
            24 => 3,
            32 => 4,
            64 when isFloat => 8,
            _ => throw new DiyAudioException("无法读取音频：不支持的 WAV 位深。"),
        };
        var frameBytes = blockAlign > 0
            ? blockAlign
            : channels * bytesPerSample;
        if (frameBytes < channels || frameBytes > channels * bytesPerSample)
        {
            frameBytes = channels * bytesPerSample;
        }

        if (sampleRate <= 0)
        {
            throw new DiyAudioException("源音频采样率或声道数超出安全范围。");
        }

        return new WavFormat(channels, sampleRate, bitsPerSample, isFloat, frameBytes);
    }

    private static float[] ReadMonoSamples(
        Stream source,
        long dataPosition,
        long dataLength,
        int frames,
        WavFormat format,
        long maximumDecodedBytes,
        CancellationToken cancellationToken)
    {
        var maximumSamples = maximumDecodedBytes / sizeof(float);
        if ((long)frames * format.Channels > maximumSamples)
        {
            throw new DiyAudioException("源音频解码后超过 64 MiB 安全上限。");
        }

        var mono = new float[frames];
        var bytesPerSample = format.BitsPerSample / 8;
        var frameBuffer = new byte[format.FrameBytes];
        source.Seek(dataPosition, SeekOrigin.Begin);
        var channels = format.Channels;
        for (var frame = 0; frame < frames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.ReadAtLeast(frameBuffer, frameBuffer.Length, throwOnEndOfStream: false);
            if (read < frameBuffer.Length)
            {
                frames = frame == 0
                    ? 0
                    : frame;
                break;
            }

            double sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = channel * bytesPerSample;
                double sample = format.IsFloat
                    ? bytesPerSample switch
                    {
                        4 => BinaryPrimitives.ReadSingleLittleEndian(frameBuffer.AsSpan(offset, 4)),
                        8 => BinaryPrimitives.ReadDoubleLittleEndian(frameBuffer.AsSpan(offset, 8)),
                        _ => throw new DiyAudioException("无法读取音频：不支持的 WAV 位深。"),
                    }
                    : bytesPerSample switch
                    {
                        1 => (sbyte)(frameBuffer[offset] - 128) / 128d,
                        2 => BinaryPrimitives.ReadInt16LittleEndian(frameBuffer.AsSpan(offset, 2)) / 32_768d,
                        3 => (ReadInt24LittleEndian(frameBuffer.AsSpan(offset, 3))) / 8_388_608d,
                        4 => BinaryPrimitives.ReadInt32LittleEndian(frameBuffer.AsSpan(offset, 4)) / 2_147_483_648d,
                        _ => throw new DiyAudioException("无法读取音频：不支持的 WAV 位深。"),
                    };
                sum += sample;
            }

            var value = (float)(sum / channels);
            if (!float.IsFinite(value))
            {
                throw new DiyAudioException("源音频包含非有限采样值。");
            }

            mono[frame] = value;
        }

        if (frames != mono.Length)
        {
            Array.Resize(ref mono, frames);
        }

        return mono;
    }

    private static int ReadInt24LittleEndian(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return (value << 8) >> 8;
    }

    private static float[] ResampleLinear(
        float[] samples,
        int sourceRate,
        int targetRate,
        int maximumFrames,
        CancellationToken cancellationToken)
    {
        var outputFrames = (int)Math.Floor((double)samples.Length * targetRate / sourceRate + 1e-9);
        if (outputFrames > maximumFrames)
        {
            throw new DiyAudioException("源音频解码后超过安全时长上限。");
        }

        var output = new float[outputFrames];
        var step = (double)(sourceRate - 1) / Math.Max(1, samples.Length - 1) * outputFrames / targetRate
            * targetRate / sourceRate;
        // Position mapping: source index for output frame i is i * sourceRate / targetRate.
        _ = step;
        for (var index = 0; index < outputFrames; index++)
        {
            if ((index & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var position = (double)index * sourceRate / targetRate;
            var left = (int)position;
            var right = Math.Min(left + 1, samples.Length - 1);
            var fraction = position - left;
            output[index] = (float)(samples[left] + (samples[right] - samples[left]) * fraction);
        }

        return output;
    }

    private static byte[] ReadExactlyOrThrow(Stream source, int count)
    {
        var buffer = new byte[count];
        var read = source.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        if (read < count)
        {
            throw new DiyAudioException("无法读取音频：WAV 文件被截断。");
        }

        return buffer;
    }
}
