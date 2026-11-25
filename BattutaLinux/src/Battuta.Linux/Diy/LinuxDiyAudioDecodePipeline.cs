using System.Diagnostics;
using System.Globalization;
using System.IO;

using Battuta.Windows.Diy.Audio;

namespace Battuta.Linux.Diy;

/// <summary>
/// Linux DIY decode pipeline (plan §6.2.5): PCM WAV sources are decoded by
/// the managed cross-platform reader; compressed formats (MP3, M4A, AIFF,
/// FLAC, OGG…) are transcoded non-realtime by an ffmpeg subprocess. The
/// playback hot path never calls this pipeline or ffmpeg.
/// </summary>
public sealed class LinuxDiyAudioDecodePipeline : IDiyAudioDecodePipeline
{
    private static readonly HashSet<string> ManagedWavExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".wave",
    };

    // Same allowlist as the Windows NAudio pipeline.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".wave", ".aif", ".aiff", ".mp3", ".m4a", ".aac", ".wma", ".caf", ".audio",
    };

    private readonly WavDiyAudioDecodePipeline wavPipeline = new();
    private readonly Func<string?> ffmpegLocator;

    public LinuxDiyAudioDecodePipeline(Func<string?>? ffmpegLocator = null)
    {
        this.ffmpegLocator = ffmpegLocator ?? LocateFfmpegOnPath;
    }

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
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new DiyAudioException("未选择音频文件。");
        }

        var extension = Path.GetExtension(sourcePath);
        if (ManagedWavExtensions.Contains(extension))
        {
            // A corrupt WAV must report its own decode error, not the ffmpeg hint.
            return wavPipeline.DecodeMono48Khz(
                sourcePath,
                maximumSourceBytes,
                maximumDecodedBytes,
                minimumDurationSeconds,
                maximumDurationSeconds,
                minimumSampleRate,
                maximumSampleRate,
                maximumChannelCount,
                cancellationToken);
        }

        if (!AllowedExtensions.Contains(extension))
        {
            throw new DiyAudioException("不支持此音频文件格式。");
        }

        return TranscodeWithFfmpeg(
            sourcePath,
            maximumSourceBytes,
            maximumDecodedBytes,
            minimumDurationSeconds,
            maximumDurationSeconds,
            minimumSampleRate,
            maximumSampleRate,
            maximumChannelCount,
            cancellationToken);
    }

    /// <summary>Locates an executable ffmpeg in PATH; null when absent.</summary>
    public static string? LocateFfmpegOnPath()
    {
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            return null;
        }

        foreach (var directory in pathEnvironment.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "ffmpeg");
            if (File.Exists(candidate) &&
                !Directory.Exists(candidate) &&
                (File.GetUnixFileMode(candidate) &
                    (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private float[] TranscodeWithFfmpeg(
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
        var file = new FileInfo(Path.GetFullPath(sourcePath));
        if (!file.Exists)
        {
            throw new DiyAudioException("找不到音频文件。");
        }

        if (file.Length <= 0 || file.Length > maximumSourceBytes)
        {
            throw new DiyAudioException("音频文件超过安全大小限制。");
        }

        var ffmpegPath = ffmpegLocator() ??
            throw new DiyAudioException(
                "无法解码此音频格式：请先安装 ffmpeg（例如 sudo apt install ffmpeg）。" +
                "Battuta 的播放热路径不依赖 ffmpeg。");

        // 48 kHz mono PCM16 output: two bytes per frame, so the WAV bytes are
        // bounded by half the decoded float budget.
        var maximumWavBytes = Math.Max(1, maximumDecodedBytes / 2);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                // ArgumentList passes the source path verbatim (no shell, no quoting).
                FileName = ffmpegPath,
                ArgumentList =
                {
                    "-v", "error",
                    "-i", file.FullName,
                    "-vn",
                    "-ac", "1",
                    "-ar", DiyAudioDecoding.OutputSampleRate.ToString(CultureInfo.InvariantCulture),
                    "-acodec", "pcm_s16le",
                    "-f", "wav",
                    "pipe:1",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        try
        {
            _ = process.Start();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            throw new DiyAudioException($"无法启动 ffmpeg：{error.Message}", error);
        }

        using var cancellationRegistration = cancellationToken.Register(
            () =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited.
                }
            });

        // Drain stderr concurrently so a chatty ffmpeg cannot deadlock on a
        // full pipe while stdout is being copied.
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var output = new MemoryStream();
        try
        {
            CopyBounded(process.StandardOutput.BaseStream, output, maximumWavBytes, cancellationToken);
        }
        finally
        {
            try
            {
                if (!process.WaitForExit(15_000))
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(5_000);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stderr = SafeSnapshot(stderrTask);
        if (process.ExitCode != 0)
        {
            throw new DiyAudioException(
                $"ffmpeg 无法解码该音频：{FirstLine(stderr) ?? "未知错误"}");
        }

        if (output.Length <= 0)
        {
            throw new DiyAudioException("ffmpeg 未产生任何解码输出。");
        }

        try
        {
            output.Seek(0, SeekOrigin.Begin);
            return WavDiyAudioDecodePipeline.DecodeStream(
                output,
                maximumDecodedBytes,
                minimumDurationSeconds,
                maximumDurationSeconds,
                minimumSampleRate,
                maximumSampleRate,
                maximumChannelCount,
                cancellationToken);
        }
        catch (DiyAudioException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            throw new DiyAudioException($"ffmpeg 输出解析失败：{error.Message}", error);
        }
    }

    private static void CopyBounded(
        Stream source,
        MemoryStream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1_024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new DiyAudioException("源音频解码后超过 64 MiB 安全上限。");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static string SafeSnapshot(Task<string> stderrTask)
    {
        try
        {
            return stderrTask.Wait(TimeSpan.FromSeconds(5)) ? stderrTask.Result : string.Empty;
        }
        catch (Exception error) when (error is AggregateException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string? FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].TrimEnd('\r');
        return line.Length == 0 ? null : line;
    }
}
