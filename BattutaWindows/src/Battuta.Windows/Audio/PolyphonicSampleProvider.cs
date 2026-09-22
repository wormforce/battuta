using System.Collections.Concurrent;
using NAudio.Wave;

namespace Battuta.Windows.Audio;

/// <summary>
/// Allocation-free render provider backed by a fixed sixteen-voice pool.
/// Producers enqueue immutable start commands; only the WASAPI render thread mutates voices.
/// </summary>
public sealed class PolyphonicSampleProvider : ISampleProvider
{
    private readonly ConcurrentQueue<StartVoiceCommand> commands = new();
    private readonly VoiceState[] voices;
    private readonly TaskCompletionSource renderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int queuedCommandCount;
    private int voiceCursor;
    private int resetRequested;
    private int schedulingSuspended;
    private int generation;
    private int activeVoiceCount;
    private int idleOutputFrameCount;
    private int outputEndedForIdle;
    private long scheduledVoiceEpoch;
    private long droppedCommandCount;
    private long voiceStealCount;

    public PolyphonicSampleProvider(int voiceCount = AudioConstants.VoiceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(voiceCount);

        voices = new VoiceState[voiceCount];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            AudioConstants.SampleRate,
            AudioConstants.OutputChannelCount);
    }

    public event EventHandler? PlaybackRequested;

    public WaveFormat WaveFormat { get; }

    public int VoiceCount => voices.Length;

    public int ActiveVoiceCount => Volatile.Read(ref activeVoiceCount);

    public int QueuedCommandCount => Math.Max(0, Volatile.Read(ref queuedCommandCount));

    public bool HasPendingPlayback => ActiveVoiceCount != 0 || QueuedCommandCount != 0;

    public bool OutputEndedForIdle => Volatile.Read(ref outputEndedForIdle) != 0;

    public long DroppedCommandCount => Interlocked.Read(ref droppedCommandCount);

    public long VoiceStealCount => Interlocked.Read(ref voiceStealCount);

    public bool IsSchedulingSuspended => Volatile.Read(ref schedulingSuspended) != 0;

    public bool TrySchedule(
        PreparedPcmSample sample,
        float gain,
        float rate,
        bool allowsStealing = true)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (Volatile.Read(ref schedulingSuspended) != 0)
        {
            return false;
        }

        if (!float.IsFinite(gain) || !float.IsFinite(rate))
        {
            return false;
        }

        gain = Math.Clamp(gain, 0f, AudioConstants.MaximumVoiceGain);
        rate = Math.Clamp(
            rate,
            AudioConstants.MinimumPlaybackRate,
            AudioConstants.MaximumPlaybackRate);

        var nextCount = Interlocked.Increment(ref queuedCommandCount);
        if (nextCount > AudioConstants.MaximumQueuedCommands)
        {
            Interlocked.Decrement(ref queuedCommandCount);
            Interlocked.Increment(ref droppedCommandCount);
            return false;
        }

        commands.Enqueue(new StartVoiceCommand(
            sample,
            gain,
            rate,
            allowsStealing,
            Volatile.Read(ref generation)));
        PlaybackRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Invalidates queued clicks and asks the next render callback to clear active voices. This is
    /// safe to call while an output session is being torn down and prevents stale clicks on resume.
    /// </summary>
    public void ResetForOutputRestart()
    {
        Interlocked.Increment(ref generation);
        Volatile.Write(ref resetRequested, 1);
    }

    public void SuspendForOutputRestart()
    {
        Volatile.Write(ref schedulingSuspended, 1);
        ResetForOutputRestart();
    }

    public void ResumeAfterOutputRestart()
    {
        ResetForOutputRestart();
        Volatile.Write(ref schedulingSuspended, 0);
    }

    public Task WaitForFirstRenderAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return timeout == Timeout.InfiniteTimeSpan
            ? renderStarted.Task.WaitAsync(cancellationToken)
            : renderStarted.Task.WaitAsync(timeout, cancellationToken);
    }

    public int Read(Span<float> buffer)
    {
        buffer.Clear();
        renderStarted.TrySetResult();

        if (Interlocked.Exchange(ref resetRequested, 0) != 0)
        {
            Array.Clear(voices);
            voiceCursor = 0;
            scheduledVoiceEpoch = 0;
            idleOutputFrameCount = 0;
            Volatile.Write(ref activeVoiceCount, 0);
            Volatile.Write(ref outputEndedForIdle, 0);
        }

        DrainCommands();

        var outputFrames = buffer.Length / AudioConstants.OutputChannelCount;
        var renderedVoice = false;
        for (var voiceIndex = 0; voiceIndex < voices.Length; voiceIndex++)
        {
            ref var voice = ref voices[voiceIndex];
            if (!voice.Active || voice.Sample is null)
            {
                continue;
            }

            renderedVoice = true;
            var samples = voice.Sample.SampleSpan;
            var position = voice.Position;
            var outputFrame = 0;
            while (outputFrame < outputFrames && position < samples.Length)
            {
                var value = CubicInterpolate(samples, position) * voice.Gain;
                var outputIndex = outputFrame * AudioConstants.OutputChannelCount;
                buffer[outputIndex] += value;
                buffer[outputIndex + 1] += value;
                position += voice.Rate;
                outputFrame++;
            }

            voice.Position = position;
            if (position >= samples.Length)
            {
                voice = default;
            }
        }

        var active = 0;
        for (var voiceIndex = 0; voiceIndex < voices.Length; voiceIndex++)
        {
            if (voices[voiceIndex].Active)
            {
                active++;
            }
        }

        Volatile.Write(ref activeVoiceCount, active);

        if (!renderedVoice && active == 0 && QueuedCommandCount == 0)
        {
            idleOutputFrameCount = (int)Math.Min(
                AudioConstants.IdleOutputFrameCount,
                (long)idleOutputFrameCount + outputFrames);
            if (idleOutputFrameCount >= AudioConstants.IdleOutputFrameCount)
            {
                // NAudio treats zero as end-of-stream. The output service keeps the initialized
                // WASAPI session and restarts it on the next accepted playback command.
                Volatile.Write(ref outputEndedForIdle, 1);
                return 0;
            }
        }
        else
        {
            idleOutputFrameCount = 0;
        }

        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = Math.Clamp(buffer[index], -1f, 1f);
        }

        Volatile.Write(ref outputEndedForIdle, 0);
        // Short idle gaps keep the one WASAPI stream warm; sustained silence ends the render loop.
        return buffer.Length;
    }

    private void DrainCommands()
    {
        var currentGeneration = Volatile.Read(ref generation);
        var drained = 0;
        while (drained < AudioConstants.MaximumQueuedCommands && commands.TryDequeue(out var command))
        {
            Interlocked.Decrement(ref queuedCommandCount);
            drained++;
            if (command.Generation != currentGeneration)
            {
                continue;
            }

            var selectedVoiceIndex = FindAvailableVoice(command.AllowsStealing);
            if (selectedVoiceIndex < 0)
            {
                Interlocked.Increment(ref droppedCommandCount);
                continue;
            }

            ref var voice = ref voices[selectedVoiceIndex];
            if (voice.Active)
            {
                Interlocked.Increment(ref voiceStealCount);
            }

            voice = new VoiceState(
                command.Sample,
                command.Gain,
                command.Rate,
                ++scheduledVoiceEpoch);
            voiceCursor = (selectedVoiceIndex + 1) % voices.Length;
        }
    }

    private int FindAvailableVoice(bool allowsStealing)
    {
        for (var offset = 0; offset < voices.Length; offset++)
        {
            var index = (voiceCursor + offset) % voices.Length;
            if (!voices[index].Active)
            {
                return index;
            }
        }

        if (!allowsStealing)
        {
            return -1;
        }

        var oldestIndex = 0;
        var oldestEpoch = voices[0].StartedEpoch;
        for (var index = 1; index < voices.Length; index++)
        {
            if (voices[index].StartedEpoch < oldestEpoch)
            {
                oldestIndex = index;
                oldestEpoch = voices[index].StartedEpoch;
            }
        }

        return oldestIndex;
    }

    private static float CubicInterpolate(ReadOnlySpan<float> samples, double position)
    {
        var index1 = (int)position;
        if (index1 >= samples.Length - 1)
        {
            return samples[^1];
        }

        var index0 = Math.Max(0, index1 - 1);
        var index2 = index1 + 1;
        var index3 = Math.Min(samples.Length - 1, index1 + 2);
        var fraction = (float)(position - index1);

        var p0 = samples[index0];
        var p1 = samples[index1];
        var p2 = samples[index2];
        var p3 = samples[index3];
        var fractionSquared = fraction * fraction;
        var fractionCubed = fractionSquared * fraction;

        return 0.5f * ((2f * p1)
            + ((-p0 + p2) * fraction)
            + ((2f * p0 - 5f * p1 + 4f * p2 - p3) * fractionSquared)
            + ((-p0 + 3f * p1 - 3f * p2 + p3) * fractionCubed));
    }

    private readonly record struct StartVoiceCommand(
        PreparedPcmSample Sample,
        float Gain,
        float Rate,
        bool AllowsStealing,
        int Generation);

    private struct VoiceState
    {
        public VoiceState(PreparedPcmSample sample, float gain, float rate, long startedEpoch)
        {
            Sample = sample;
            Gain = gain;
            Rate = rate;
            Position = 0;
            StartedEpoch = startedEpoch;
            Active = true;
        }

        public PreparedPcmSample? Sample;
        public double Position;
        public float Gain;
        public float Rate;
        public long StartedEpoch;
        public bool Active;
    }
}
