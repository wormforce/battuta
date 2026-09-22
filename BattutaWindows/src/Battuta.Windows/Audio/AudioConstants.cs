using Battuta.Core.Audio;
using Battuta.Core.Settings;

namespace Battuta.Windows.Audio;

/// <summary>Canonical format and safety limits used by the realtime audio path.</summary>
public static class AudioConstants
{
    public const int SampleRate = 48_000;
    public const int OutputChannelCount = 2;
    public const int VoiceCount = 16;
    public const int MaximumQueuedCommands = 512;
    public const int IdleOutputFrameCount = SampleRate;
    public const float MinimumPlaybackRate = 0.25f;
    public const float MaximumPlaybackRate = 4f;
    public static readonly float MaximumVoiceGain = (float)AppSettingsDefaults.MaximumKeyboardVolume
        * PlaybackVariantCycle.Variants.Max(static variant => variant.Gain);
}
