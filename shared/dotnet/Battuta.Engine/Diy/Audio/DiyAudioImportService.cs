using System.IO;

namespace Battuta.Windows.Diy.Audio;

public sealed class DiyAudioImportService : IDisposable
{
    private readonly string _workingDirectory;
    private readonly DiyAudioImportLimits _limits;
    private readonly IDiyAudioDecodePipeline _decodePipeline;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DiyAudioImportService(
        string workingDirectory,
        DiyAudioImportLimits? limits = null,
        IDiyAudioDecodePipeline? decodePipeline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _limits = limits ?? DiyAudioImportLimits.SoundPack;
        _decodePipeline = decodePipeline ?? new WavDiyAudioDecodePipeline();
    }

    public string WorkingDirectory => _workingDirectory;

    public async Task<PreparedDiyAudio> PrepareImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWorkingDirectory();
            var temporaryPath = Path.Combine(
                _workingDirectory,
                $".import-{Guid.NewGuid():N}.wav");
            try
            {
                var samples = await Task.Run(
                    () => _decodePipeline.DecodeMono48Khz(
                        sourcePath,
                        _limits.MaximumSourceBytes,
                        _limits.MaximumDecodedBytes,
                        _limits.MinimumDurationSeconds,
                        _limits.MaximumDurationSeconds,
                        _limits.MinimumSampleRate,
                        _limits.MaximumSampleRate,
                        _limits.MaximumChannelCount,
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                Pcm16WaveFile.Write(temporaryPath, samples);
                var hash = Pcm16WaveFile.Sha256(temporaryPath);
                var finalPath = Path.Combine(_workingDirectory, $"{hash}.wav");
                if (File.Exists(finalPath))
                {
                    if (!string.Equals(
                            Pcm16WaveFile.Sha256(finalPath),
                            hash,
                            StringComparison.Ordinal))
                    {
                        throw new DiyAudioException("规范化音频缓存的哈希校验失败。");
                    }

                    File.Delete(temporaryPath);
                }
                else
                {
                    File.Move(temporaryPath, finalPath);
                }

                var info = Pcm16WaveFile.ValidateNormalized(finalPath, _limits);
                return new PreparedDiyAudio(
                    hash,
                    finalPath,
                    Path.GetFileName(sourcePath),
                    info);
            }
            catch
            {
                TryDeleteFile(temporaryPath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public NormalizedDiyAudioInfo ValidateNormalizedAudio(string path) =>
        Pcm16WaveFile.ValidateNormalized(path, _limits);

    public async Task DiscardPreparedAudioAsync(
        PreparedDiyAudio prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidate = Path.GetFullPath(prepared.NormalizedFilePath);
            EnsureDescendant(candidate);
            if (File.Exists(candidate))
            {
                var attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new DiyAudioException("拒绝删除重解析点音频缓存。");
                }

                File.Delete(candidate);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAllPreparedAudioAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_workingDirectory))
            {
                return;
            }

            var attributes = File.GetAttributes(_workingDirectory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new DiyAudioException("拒绝清理重解析点音频缓存目录。");
            }

            DiyTemporaryFileSafety.DeleteDirectoryTree(_workingDirectory);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureWorkingDirectory()
    {
        Directory.CreateDirectory(_workingDirectory);
        var attributes = File.GetAttributes(_workingDirectory);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new DiyAudioException("音频缓存目录不安全。");
        }
    }

    private void EnsureDescendant(string candidate)
    {
        var prefix = Path.TrimEndingDirectorySeparator(_workingDirectory)
            + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new DiyAudioException("音频缓存路径越出当前编辑器会话目录。");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
