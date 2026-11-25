using System.IO;

namespace Battuta.Windows.Diy.Audio;

public sealed class DiyAudioSplitService : IDisposable
{
    public const int OutputSampleRate = DiyAudioDecoding.OutputSampleRate;

    private readonly AudioSplitConfiguration _configuration;
    private readonly IDiyAudioDecodePipeline _decodePipeline;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DiyAudioSplitService(
        AudioSplitConfiguration? configuration = null,
        IDiyAudioDecodePipeline? decodePipeline = null)
    {
        _configuration = configuration ?? new AudioSplitConfiguration();
        _decodePipeline = decodePipeline ?? new WavDiyAudioDecodePipeline();
        ValidateConfiguration(_configuration);
    }

    public async Task<AudioSplitAnalysis> AnalyzeAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fullPath = Path.GetFullPath(sourcePath);
            var sourceByteCount = new FileInfo(fullPath).Length;
            var samples = await DecodeAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return await Task.Run(
                () => AnalyzeSamples(fullPath, sourceByteCount, samples, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudioSplitExportResult> ExportSplitAsync(
        string sourcePath,
        double splitTimeSeconds,
        double? releaseEndTimeSeconds,
        string pressDestinationPath,
        string releaseDestinationPath,
        bool overwriteExisting = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = Path.GetFullPath(sourcePath);
            var pressDestination = Path.GetFullPath(pressDestinationPath);
            var releaseDestination = Path.GetFullPath(releaseDestinationPath);
            ValidateDestinations(
                source,
                pressDestination,
                releaseDestination,
                overwriteExisting);

            var samples = await DecodeAsync(source, cancellationToken).ConfigureAwait(false);
            var duration = (double)samples.Length / OutputSampleRate;
            if (!double.IsFinite(splitTimeSeconds) ||
                splitTimeSeconds < _configuration.MinimumSegmentDurationSeconds ||
                splitTimeSeconds > duration - _configuration.MinimumSegmentDurationSeconds)
            {
                throw new DiyAudioException("按下与回弹切点超出有效范围。");
            }

            var resolvedReleaseEnd = releaseEndTimeSeconds ?? duration;
            if (!double.IsFinite(resolvedReleaseEnd) ||
                resolvedReleaseEnd <= splitTimeSeconds + _configuration.MinimumSegmentDurationSeconds ||
                resolvedReleaseEnd > duration)
            {
                throw new DiyAudioException("回弹结束时间无效。");
            }

            var splitFrame = FrameAt(splitTimeSeconds, samples.Length);
            var releaseEndFrame = FrameAt(resolvedReleaseEnd, samples.Length);
            var pressSamples = samples[..splitFrame];
            var releaseSamples = samples[splitFrame..releaseEndFrame];
            ApplyLinearFade(pressSamples, fadeInSeconds: 0, fadeOutSeconds: 0.004);
            ApplyLinearFade(releaseSamples, fadeInSeconds: 0.002, fadeOutSeconds: 0.004);
            cancellationToken.ThrowIfCancellationRequested();

            EnsureOutputParent(pressDestination);
            EnsureOutputParent(releaseDestination);
            var temporaryPress = TemporaryOutputPath(pressDestination);
            var temporaryRelease = TemporaryOutputPath(releaseDestination);
            try
            {
                await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Pcm16WaveFile.Write(temporaryPress, pressSamples);
                        cancellationToken.ThrowIfCancellationRequested();
                        Pcm16WaveFile.Write(temporaryRelease, releaseSamples);
                    },
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                InstallPair(
                    temporaryPress,
                    pressDestination,
                    temporaryRelease,
                    releaseDestination,
                    overwriteExisting);
            }
            finally
            {
                TryDelete(temporaryPress);
                TryDelete(temporaryRelease);
            }

            return new AudioSplitExportResult(
                pressDestination,
                releaseDestination,
                splitTimeSeconds,
                resolvedReleaseEnd,
                pressSamples.Length,
                releaseSamples.Length,
                OutputSampleRate);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<float[]> DecodeAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(
            () => _decodePipeline.DecodeMono48Khz(
                path,
                _configuration.MaximumSourceBytes,
                _configuration.MaximumDecodedBytes,
                _configuration.MinimumDurationSeconds,
                _configuration.MaximumDurationSeconds,
                _configuration.MinimumSampleRate,
                _configuration.MaximumSampleRate,
                _configuration.MaximumChannelCount,
                cancellationToken),
            cancellationToken);

    private AudioSplitAnalysis AnalyzeSamples(
        string sourcePath,
        long sourceByteCount,
        float[] samples,
        CancellationToken cancellationToken)
    {
        var duration = (double)samples.Length / OutputSampleRate;
        var frames = MakeAnalysisFrames(samples, cancellationToken);
        var detection = DetectSplit(frames, duration, cancellationToken);
        var waveform = MakeWaveform(samples, cancellationToken);
        var envelope = DownsampleEnvelope(frames);

        var splitTime = ClampSplitTime(
            frames[detection.Release.OnsetIndex].TimeSeconds,
            duration);
        var releaseEndTime = detection.ReleaseEndIndex is { } releaseEndIndex
            ? frames[releaseEndIndex].TimeSeconds
            : (double?)null;
        var previewEnd = releaseEndTime ?? duration;
        var pressTransient = frames[detection.PressPeakIndex].TimeSeconds;
        var valley = frames[detection.Release.ValleyIndex].TimeSeconds;
        var releaseTransient = frames[detection.Release.PeakIndex].TimeSeconds;

        var warnings = new HashSet<AudioSplitWarning>();
        if (detection.Confidence < 0.55f)
        {
            warnings.Add(AudioSplitWarning.LowConfidence);
        }

        if (detection.UsedFallback)
        {
            warnings.Add(AudioSplitWarning.FallbackValleyUsed);
        }

        if (detection.PossibleAdditionalKeystroke)
        {
            warnings.Add(AudioSplitWarning.PossibleAdditionalKeystroke);
        }

        if (waveform.Any(point => Math.Max(Math.Abs(point.Minimum), Math.Abs(point.Maximum)) >= 0.999f))
        {
            warnings.Add(AudioSplitWarning.SourceMayBeClipped);
        }

        return new AudioSplitAnalysis(
            sourcePath,
            sourceByteCount,
            duration,
            OutputSampleRate,
            samples.Length,
            new AudioSplitSuggestion(
                splitTime,
                pressTransient,
                valley,
                releaseTransient,
                releaseEndTime,
                detection.Confidence,
                detection.UsedFallback),
            SegmentPreview(samples, 0, splitTime, pressTransient),
            SegmentPreview(samples, splitTime, previewEnd, releaseTransient),
            AudioSplitAnalysis.Freeze(waveform),
            AudioSplitAnalysis.Freeze(envelope),
            warnings);
    }

    private List<AnalysisFrame> MakeAnalysisFrames(
        float[] samples,
        CancellationToken cancellationToken)
    {
        var windowSize = Math.Max(
            16,
            RoundToInt(_configuration.AnalysisWindowDurationSeconds * OutputSampleRate));
        var hopSize = Math.Max(
            1,
            RoundToInt(_configuration.AnalysisHopDurationSeconds * OutputSampleRate));
        var result = new List<AnalysisFrame>((samples.Length + hopSize - 1) / hopSize);
        var frameNumber = 0;
        for (var start = 0; start < samples.Length; start += hopSize)
        {
            if (frameNumber % 128 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var count = Math.Min(windowSize, samples.Length - start);
            double squareSum = 0;
            var peak = 0f;
            for (var index = start; index < start + count; index++)
            {
                var value = samples[index];
                squareSum += value * value;
                peak = Math.Max(peak, Math.Abs(value));
            }

            var rms = (float)Math.Sqrt(squareSum / count);
            var centerFrame = start + count / 2d;
            result.Add(new AnalysisFrame(centerFrame / OutputSampleRate, rms, peak));
            frameNumber++;
        }

        if (result.Count < 3)
        {
            throw new DiyAudioException("音频太短，无法分析。");
        }

        return result;
    }

    private Detection DetectSplit(
        IReadOnlyList<AnalysisFrame> frames,
        double duration,
        CancellationToken cancellationToken)
    {
        var rmsLevels = frames.Select(frame => frame.RootMeanSquareDbfs).ToArray();
        var globalPeak = rmsLevels.Max();
        var noiseFloor = Percentile(rmsLevels, 0.20f);
        var activeThreshold = Math.Max(Math.Max(noiseFloor + 10, globalPeak - 28), -60);
        var firstActive = 0;
        var foundActive = false;
        for (var index = 0; index < rmsLevels.Length; index++)
        {
            var end = Math.Min(frames.Count, index + FrameCountFor(0.008));
            if (Maximum(rmsLevels, index, Math.Max(index + 1, end)) >= activeThreshold)
            {
                firstActive = index;
                foundActive = true;
                break;
            }
        }

        if (!foundActive)
        {
            firstActive = MaximumIndex(rmsLevels, 0, rmsLevels.Length);
        }

        var pressSearchEnd = Math.Min(frames.Count, firstActive + FrameCountFor(0.055));
        var pressPeakIndex = MaximumIndex(
            rmsLevels,
            firstActive,
            Math.Max(firstActive + 1, pressSearchEnd));
        var releaseSearchStart = Math.Min(
            frames.Count - 2,
            Math.Max(
                firstActive + FrameCountFor(_configuration.MinimumReleaseDelaySeconds),
                pressPeakIndex + FrameCountFor(0.030)));

        var candidates = new List<ReleaseCandidate>();
        var localRadius = FrameCountFor(0.004);
        var valleyLookback = FrameCountFor(0.032);
        for (var peakIndex = releaseSearchStart; peakIndex < frames.Count - 1; peakIndex++)
        {
            if (peakIndex % 128 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var localStart = Math.Max(releaseSearchStart, peakIndex - localRadius);
            var localEnd = Math.Min(frames.Count, peakIndex + localRadius + 1);
            if (rmsLevels[peakIndex] < Maximum(rmsLevels, localStart, localEnd))
            {
                continue;
            }

            var valleyStart = Math.Max(pressPeakIndex + 1, peakIndex - valleyLookback);
            var valleyEnd = Math.Max(valleyStart + 1, peakIndex - FrameCountFor(0.004));
            if (valleyEnd > peakIndex)
            {
                continue;
            }

            var valleyIndex = MinimumIndex(rmsLevels, valleyStart, valleyEnd);
            var rise = rmsLevels[peakIndex] - rmsLevels[valleyIndex];
            var prominence = rmsLevels[peakIndex] - noiseFloor;
            if (rise < 6 || prominence < 8 || rmsLevels[peakIndex] < globalPeak - 30)
            {
                continue;
            }

            var crossing = rmsLevels[valleyIndex] + Math.Min(8, Math.Max(3, rise * 0.30f));
            var onsetIndex = valleyIndex;
            for (var index = valleyIndex; index <= peakIndex; index++)
            {
                if (rmsLevels[index] >= crossing)
                {
                    onsetIndex = index;
                    break;
                }
            }

            var quietDuration = frames[onsetIndex].TimeSeconds - frames[valleyIndex].TimeSeconds;
            var score = rise + 0.40f * prominence + (float)(Math.Min(quietDuration, 0.025) * 120);
            candidates.Add(new ReleaseCandidate(
                onsetIndex,
                valleyIndex,
                peakIndex,
                score,
                rise,
                prominence));
        }

        ReleaseCandidate selected;
        bool usedFallback;
        if (candidates.Count > 0)
        {
            var bestScore = candidates.Max(candidate => candidate.Score);
            selected = candidates
                .Where(candidate => candidate.Score >= bestScore * 0.50f)
                .MinBy(candidate => candidate.OnsetIndex)!;
            usedFallback = false;
        }
        else
        {
            var fallbackStart = Math.Max(releaseSearchStart, FrameCountFor(duration * 0.30));
            var fallbackEnd = Math.Min(
                frames.Count - 1,
                Math.Max(fallbackStart + 1, FrameCountFor(duration * 0.78)));
            var valleyIndex = MinimumIndex(rmsLevels, fallbackStart, fallbackEnd);
            var peakStart = Math.Min(frames.Count - 1, valleyIndex + 1);
            var peakIndex = MaximumIndex(rmsLevels, peakStart, frames.Count);
            selected = new ReleaseCandidate(
                valleyIndex,
                valleyIndex,
                peakIndex,
                0,
                Math.Max(0, rmsLevels[peakIndex] - rmsLevels[valleyIndex]),
                Math.Max(0, rmsLevels[peakIndex] - noiseFloor));
            usedFallback = true;
        }

        var later = candidates
            .Where(candidate =>
                candidate.OnsetIndex > selected.PeakIndex + FrameCountFor(_configuration.MinimumReleaseDelaySeconds) &&
                candidate.Score >= selected.Score * 0.72f)
            .MinBy(candidate => candidate.OnsetIndex);
        int? releaseEndIndex = later is null
            ? null
            : MinimumIndex(
                rmsLevels,
                selected.PeakIndex,
                Math.Max(selected.PeakIndex + 1, later.OnsetIndex));

        var riseConfidence = UnitInterval((selected.RiseDb - 6) / 18);
        var prominenceConfidence = UnitInterval((selected.ProminenceDb - 8) / 24);
        var separation = frames[selected.PeakIndex].TimeSeconds - frames[pressPeakIndex].TimeSeconds;
        var separationConfidence = UnitInterval((float)((separation - 0.030) / 0.090));
        var confidence = usedFallback
            ? 0.10f
            : 0.45f * riseConfidence + 0.35f * prominenceConfidence + 0.20f * separationConfidence;

        return new Detection(
            pressPeakIndex,
            selected,
            releaseEndIndex,
            confidence,
            usedFallback,
            later is not null);
    }

    private List<AudioWaveformPoint> MakeWaveform(
        float[] samples,
        CancellationToken cancellationToken)
    {
        var pointCount = Math.Min(_configuration.WaveformPointCount, samples.Length);
        var result = new List<AudioWaveformPoint>(pointCount);
        for (var point = 0; point < pointCount; point++)
        {
            if (point % 128 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var start = point * samples.Length / pointCount;
            var end = Math.Max(start + 1, (point + 1) * samples.Length / pointCount);
            var minimum = float.MaxValue;
            var maximum = float.MinValue;
            double squareSum = 0;
            for (var index = start; index < end; index++)
            {
                var value = samples[index];
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                squareSum += value * value;
            }

            result.Add(new AudioWaveformPoint(
                ((start + end) / 2d) / OutputSampleRate,
                minimum,
                maximum,
                (float)Math.Sqrt(squareSum / (end - start))));
        }

        return result;
    }

    private List<AudioEnergyEnvelopePoint> DownsampleEnvelope(IReadOnlyList<AnalysisFrame> frames)
    {
        var pointCount = Math.Min(_configuration.EnvelopePointCount, frames.Count);
        var result = new List<AudioEnergyEnvelopePoint>(pointCount);
        for (var point = 0; point < pointCount; point++)
        {
            var start = point * frames.Count / pointCount;
            var end = Math.Max(start + 1, (point + 1) * frames.Count / pointCount);
            var rms = 0f;
            var peak = 0f;
            for (var index = start; index < end; index++)
            {
                rms = Math.Max(rms, frames[index].RootMeanSquare);
                peak = Math.Max(peak, frames[index].Peak);
            }

            result.Add(new AudioEnergyEnvelopePoint(
                frames[start + ((end - start) / 2)].TimeSeconds,
                rms,
                peak,
                Decibels(rms),
                Decibels(peak)));
        }

        return result;
    }

    private static AudioSplitSegmentPreview SegmentPreview(
        float[] samples,
        double startTime,
        double endTime,
        double transientTime)
    {
        var start = FrameAt(startTime, samples.Length);
        var end = Math.Min(
            samples.Length,
            Math.Max(start + 1, FrameAt(endTime, samples.Length)));
        double squareSum = 0;
        var peak = 0f;
        for (var index = start; index < end; index++)
        {
            var value = samples[index];
            squareSum += value * value;
            peak = Math.Max(peak, Math.Abs(value));
        }

        var rms = (float)Math.Sqrt(squareSum / (end - start));
        return new AudioSplitSegmentPreview(
            startTime,
            endTime,
            endTime - startTime,
            Math.Max(0, Math.Min(endTime - startTime, transientTime - startTime)),
            peak,
            rms,
            Decibels(peak),
            Decibels(rms));
    }

    private static void ValidateConfiguration(AudioSplitConfiguration configuration)
    {
        if (!double.IsFinite(configuration.MinimumDurationSeconds) ||
            !double.IsFinite(configuration.MaximumDurationSeconds) ||
            !double.IsFinite(configuration.MinimumSegmentDurationSeconds) ||
            !double.IsFinite(configuration.AnalysisWindowDurationSeconds) ||
            !double.IsFinite(configuration.AnalysisHopDurationSeconds) ||
            !double.IsFinite(configuration.MinimumReleaseDelaySeconds) ||
            configuration.MinimumDurationSeconds <= 0 ||
            configuration.MaximumDurationSeconds <= configuration.MinimumDurationSeconds ||
            configuration.MinimumSegmentDurationSeconds <= 0 ||
            configuration.MinimumSegmentDurationSeconds * 2 >= configuration.MinimumDurationSeconds ||
            configuration.AnalysisWindowDurationSeconds <= 0 ||
            configuration.AnalysisHopDurationSeconds <= 0 ||
            configuration.MinimumReleaseDelaySeconds <= configuration.AnalysisHopDurationSeconds ||
            configuration.MaximumSourceBytes <= 0 ||
            configuration.MaximumDecodedBytes <= 0 ||
            configuration.MinimumSampleRate <= 0 ||
            configuration.MinimumSampleRate > OutputSampleRate ||
            configuration.MaximumSampleRate < OutputSampleRate ||
            configuration.MaximumChannelCount <= 0 ||
            configuration.WaveformPointCount <= 0 ||
            configuration.EnvelopePointCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "音频拆分参数必须为有限正数，且最短片段不能超过最短音频的一半。");
        }
    }

    private static void ValidateDestinations(
        string source,
        string press,
        string release,
        bool overwriteExisting)
    {
        if (string.Equals(press, release, StringComparison.OrdinalIgnoreCase))
        {
            throw new DiyAudioException("按下与回弹音频必须导出到不同文件。");
        }

        if (string.Equals(press, source, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(release, source, StringComparison.OrdinalIgnoreCase))
        {
            throw new DiyAudioException("导出位置不能覆盖原始音频。");
        }

        if (!string.Equals(Path.GetExtension(press), ".wav", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(release), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new DiyAudioException("目标文件扩展名必须是 .wav。");
        }

        foreach (var destination in new[] { press, release })
        {
            if (!File.Exists(destination))
            {
                continue;
            }

            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DiyAudioException("拒绝覆盖重解析点目标文件。");
            }

            if (!overwriteExisting)
            {
                throw new DiyAudioException($"目标文件已存在：{Path.GetFileName(destination)}");
            }
        }
    }

    private static void InstallPair(
        string temporaryPress,
        string pressDestination,
        string temporaryRelease,
        string releaseDestination,
        bool overwriteExisting)
    {
        var backups = new List<(string Destination, string Backup)>();
        var installed = new List<string>();
        try
        {
            foreach (var destination in new[] { pressDestination, releaseDestination })
            {
                if (!File.Exists(destination))
                {
                    continue;
                }

                if (!overwriteExisting)
                {
                    throw new DiyAudioException($"目标文件已存在：{Path.GetFileName(destination)}");
                }

                var backup = Path.Combine(
                    Path.GetDirectoryName(destination)!,
                    $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.split-backup");
                File.Move(destination, backup);
                backups.Add((destination, backup));
            }

            File.Move(temporaryPress, pressDestination);
            installed.Add(pressDestination);
            File.Move(temporaryRelease, releaseDestination);
            installed.Add(releaseDestination);
            foreach (var entry in backups)
            {
                TryDelete(entry.Backup);
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var destination in installed.AsEnumerable().Reverse())
            {
                try
                {
                    File.Delete(destination);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            foreach (var entry in backups.AsEnumerable().Reverse())
            {
                try
                {
                    File.Move(entry.Backup, entry.Destination);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                throw new DiyAudioException(
                    "音频导出失败，且无法完整恢复原文件。",
                    new AggregateException(new[] { error }.Concat(rollbackErrors)));
            }

            throw new DiyAudioException("音频导出失败。", error);
        }
    }

    private static void ApplyLinearFade(float[] samples, double fadeInSeconds, double fadeOutSeconds)
    {
        var fadeInFrames = Math.Min(samples.Length, RoundToInt(fadeInSeconds * OutputSampleRate));
        if (fadeInFrames > 1)
        {
            for (var index = 0; index < fadeInFrames; index++)
            {
                samples[index] *= (float)index / (fadeInFrames - 1);
            }
        }

        var fadeOutFrames = Math.Min(samples.Length, RoundToInt(fadeOutSeconds * OutputSampleRate));
        if (fadeOutFrames > 1)
        {
            var start = samples.Length - fadeOutFrames;
            for (var index = 0; index < fadeOutFrames; index++)
            {
                samples[start + index] *= (float)(fadeOutFrames - 1 - index) / (fadeOutFrames - 1);
            }
        }
    }

    private double ClampSplitTime(double value, double duration) =>
        Math.Max(
            _configuration.MinimumSegmentDurationSeconds,
            Math.Min(duration - _configuration.MinimumSegmentDurationSeconds, value));

    private int FrameCountFor(double durationSeconds) =>
        Math.Max(1, RoundToInt(durationSeconds / _configuration.AnalysisHopDurationSeconds));

    private static int FrameAt(double timeSeconds, int frameCount) =>
        Math.Min(frameCount, Math.Max(0, RoundToInt(timeSeconds * OutputSampleRate)));

    private static int RoundToInt(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static float Decibels(float amplitude) =>
        20 * MathF.Log10(Math.Max(amplitude, 1e-7f));

    private static int MaximumIndex(float[] values, int start, int end)
    {
        var result = start;
        for (var index = start + 1; index < end; index++)
        {
            if (values[index] > values[result])
            {
                result = index;
            }
        }

        return result;
    }

    private static int MinimumIndex(float[] values, int start, int end)
    {
        var result = start;
        for (var index = start + 1; index < end; index++)
        {
            if (values[index] < values[result])
            {
                result = index;
            }
        }

        return result;
    }

    private static float Maximum(float[] values, int start, int end) =>
        values[MaximumIndex(values, start, end)];

    private static float Percentile(float[] values, float fraction)
    {
        var sorted = values.Order().ToArray();
        var index = Math.Min(
            sorted.Length - 1,
            Math.Max(0, RoundToInt((sorted.Length - 1) * fraction)));
        return sorted[index];
    }

    private static float UnitInterval(float value) => Math.Clamp(value, 0, 1);

    private static void EnsureOutputParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
        {
            throw new DiyAudioException("导出路径缺少父目录。");
        }

        Directory.CreateDirectory(parent);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DiyAudioException("拒绝向重解析点目录导出音频。");
        }
    }

    private static string TemporaryOutputPath(string destination) =>
        Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.tmp.wav");

    private static void TryDelete(string path)
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

    private sealed record AnalysisFrame(double TimeSeconds, float RootMeanSquare, float Peak)
    {
        public float RootMeanSquareDbfs => Decibels(RootMeanSquare);
    }

    private sealed record ReleaseCandidate(
        int OnsetIndex,
        int ValleyIndex,
        int PeakIndex,
        float Score,
        float RiseDb,
        float ProminenceDb);

    private sealed record Detection(
        int PressPeakIndex,
        ReleaseCandidate Release,
        int? ReleaseEndIndex,
        float Confidence,
        bool UsedFallback,
        bool PossibleAdditionalKeystroke);

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
