using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Core.Settings;
using Battuta.Core.SoundPacks;

namespace Battuta.Windows.Audio;

public interface IPointerPitchVariationSource
{
    float NextMultiplier();
}

public sealed class RandomPointerPitchVariationSource : IPointerPitchVariationSource
{
    public float NextMultiplier() => 0.97f + (Random.Shared.NextSingle() * 0.06f);
}

/// <summary>
/// Hot-path sound lookup and scheduling. Profile decoding happens asynchronously and banks are
/// committed only after every required resource has been prepared successfully.
/// </summary>
public sealed class KeyboardAudioEngine
{
    private readonly PolyphonicSampleProvider mixer;
    private readonly BuiltInAudioResourceCatalog resources;
    private readonly IPointerPitchVariationSource pointerPitchVariation;
    private KeyboardSoundBank? keyboardBank;
    private PointerSoundBank? pointerBank;
    private Exception? keyboardResourceError;
    private Exception? pointerResourceError;
    private long keyboardLoadGeneration;
    private long pointerLoadGeneration;

    public KeyboardAudioEngine(
        PolyphonicSampleProvider mixer,
        BuiltInAudioResourceCatalog? resources = null,
        IPointerPitchVariationSource? pointerPitchVariation = null)
    {
        this.mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        this.resources = resources ?? new BuiltInAudioResourceCatalog();
        this.pointerPitchVariation = pointerPitchVariation ?? new RandomPointerPitchVariationSource();
    }

    public SwitchProfileId? LoadedKeyboardProfileId => Volatile.Read(ref keyboardBank)?.Profile.Id;

    public string? LoadedKeyboardSelectionId => Volatile.Read(ref keyboardBank)?.SelectionId;

    public PointerSoundProfileId? LoadedPointerProfileId => Volatile.Read(ref pointerBank)?.Profile.Id;

    public Exception? KeyboardResourceError => Volatile.Read(ref keyboardResourceError);

    public Exception? PointerResourceError => Volatile.Read(ref pointerResourceError);

    public async Task<bool> LoadDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var keyboard = LoadKeyboardProfileAsync(SwitchProfileCatalog.Default.Id, cancellationToken);
        var pointer = LoadPointerProfileAsync(PointerSoundProfileCatalog.Default.Id, cancellationToken);
        var results = await Task.WhenAll(keyboard, pointer).ConfigureAwait(false);
        return results.All(static result => result);
    }

    public async Task<bool> LoadKeyboardProfileAsync(
        SwitchProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref keyboardLoadGeneration);
        try
        {
            var profile = SwitchProfileCatalog.Get(profileId);
            var next = await resources
                .LoadKeyboardBankAsync(profile, cancellationToken)
                .ConfigureAwait(false);
            if (generation != Volatile.Read(ref keyboardLoadGeneration))
            {
                return false;
            }

            Volatile.Write(ref keyboardBank, next);
            Volatile.Write(ref keyboardResourceError, null);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            if (generation == Volatile.Read(ref keyboardLoadGeneration))
            {
                Volatile.Write(ref keyboardResourceError, error);
            }

            return false;
        }
    }

    public async Task<bool> LoadPointerProfileAsync(
        PointerSoundProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref pointerLoadGeneration);
        try
        {
            var profile = PointerSoundProfileCatalog.Get(profileId);
            var next = await resources
                .LoadPointerBankAsync(profile, cancellationToken)
                .ConfigureAwait(false);
            if (generation != Volatile.Read(ref pointerLoadGeneration))
            {
                return false;
            }

            Volatile.Write(ref pointerBank, next);
            Volatile.Write(ref pointerResourceError, null);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            if (generation == Volatile.Read(ref pointerLoadGeneration))
            {
                Volatile.Write(ref pointerResourceError, error);
            }

            return false;
        }
    }

    public async Task<bool> LoadCustomSoundPackAsync(
        SoundPackManifest manifest,
        Func<SoundPackAssetId, string> assetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(assetPath);
        var generation = Interlocked.Increment(ref keyboardLoadGeneration);
        try
        {
            SoundPackValidator.Validate(manifest);
            var baseProfile = SwitchProfileCatalog.TryGet(manifest.BaseProfileId, out var selectedBase)
                ? selectedBase
                : SwitchProfileCatalog.Default;
            var next = await resources
                .LoadKeyboardBankAsync(baseProfile, cancellationToken)
                .ConfigureAwait(false);

            var custom = new Dictionary<SoundPackAssetId, PreparedKeyboardSample>();
            foreach (var assetId in manifest.ReferencedAssetIds().OrderBy(id => id.Value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prepared = await resources
                    .LoadCustomKeyboardSampleAsync(assetPath(assetId), cancellationToken)
                    .ConfigureAwait(false);
                custom.Add(assetId, prepared);
            }

            if (generation != Volatile.Read(ref keyboardLoadGeneration))
            {
                return false;
            }

            Volatile.Write(ref keyboardBank, next.WithCustomSamples(manifest, custom));
            Volatile.Write(ref keyboardResourceError, null);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            if (generation == Volatile.Read(ref keyboardLoadGeneration))
            {
                Volatile.Write(ref keyboardResourceError, error);
            }

            return false;
        }
    }

    public bool PlayKeyboard(
        PhysicalKeyId key,
        KeySoundPhase phase,
        double volume,
        bool variationEnabled)
    {
        var bank = Volatile.Read(ref keyboardBank);
        if (bank is null)
        {
            return false;
        }

        if (!bank.TryResolve(key, phase, out var sample, out _))
        {
            return false;
        }

        var variant = sample.NextVariant(variationEnabled);
        var gain = (float)AppSettingsMigration.ClampKeyboardVolume(volume) * variant.Gain;
        return mixer.TrySchedule(sample.Pcm, gain, variant.Rate, allowsStealing: true);
    }

    public bool PlayPointer(
        PointerButton button,
        PointerSoundPhase phase,
        double volume,
        bool variationEnabled)
    {
        var bank = Volatile.Read(ref pointerBank);
        if (bank is null)
        {
            return false;
        }

        if (!bank.TryGet(phase, button.Sample, out var sample)
            && !bank.TryGet(phase, PointerSoundSample.Primary, out sample))
        {
            return false;
        }

        var variation = variationEnabled ? pointerPitchVariation.NextMultiplier() : 1f;
        var rate = Math.Clamp(
            button.PlaybackRate * variation,
            AudioConstants.MinimumPlaybackRate,
            AudioConstants.MaximumPlaybackRate);
        return mixer.TrySchedule(
            sample,
            (float)AppSettingsMigration.ClampPointerVolume(volume),
            rate,
            allowsStealing: false);
    }

    public bool Preview(PreparedPcmSample sample, double volume, float rate = 1f) =>
        mixer.TrySchedule(
            sample,
            (float)AppSettingsMigration.ClampKeyboardVolume(volume),
            rate,
            allowsStealing: true);
}
