using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Windows.Audio;
using Battuta.Windows.Diy.ViewModels;

namespace Battuta.Engine.Diy;

/// <summary>
/// Lets the DIY editor preview the exact built-in sample selected by the
/// Core key mapper.
/// </summary>
public sealed class DiyBuiltInAudioLocator(BuiltInAudioResourceCatalog resources)
    : IDiyBuiltInAudioLocator
{
    private readonly BuiltInAudioResourceCatalog resources =
        resources ?? throw new ArgumentNullException(nameof(resources));

    public string? FindAudio(string profileId, PhysicalKeyId key, KeySoundPhase phase)
    {
        if (!SwitchProfileCatalog.TryGet(profileId, out var profile))
        {
            return null;
        }

        var requested = KeySoundMapper.SampleFor(key, phase, profile);
        if (requested is null)
        {
            return null;
        }

        try
        {
            return resources.ResolveKeyboardResource(profile, phase, requested.Value);
        }
        catch (FileNotFoundException)
        {
            var fallback = phase == KeySoundPhase.Release
                ? profile.HasRowSpecificReleaseSamples
                    ? KeySoundSample.GenericR2
                    : KeySoundSample.Generic
                : KeySoundSample.GenericR2;
            try
            {
                return resources.ResolveKeyboardResource(profile, phase, fallback);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
    }
}
