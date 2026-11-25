using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Core.SoundPacks;

namespace Battuta.Windows.Audio;

public readonly record struct KeyboardSampleKey(KeySoundPhase Phase, KeySoundSample Sample);

public readonly record struct PointerSampleKey(PointerSoundPhase Phase, PointerSoundSample Sample);

public sealed class PreparedKeyboardSample
{
    private readonly object variantLock = new();
    private PlaybackVariantCycle variants;

    public PreparedKeyboardSample(PreparedPcmSample pcm)
    {
        Pcm = pcm ?? throw new ArgumentNullException(nameof(pcm));
    }

    public PreparedPcmSample Pcm { get; }

    public PlaybackVariant NextVariant(bool variationEnabled)
    {
        lock (variantLock)
        {
            return variants.Next(variationEnabled);
        }
    }
}

public sealed class KeyboardSoundBank
{
    private readonly IReadOnlyDictionary<KeyboardSampleKey, PreparedKeyboardSample> samples;
    private readonly IReadOnlyDictionary<SoundPackAssetId, PreparedKeyboardSample> customSamples;
    private readonly SoundPackResolver? customResolver;

    internal KeyboardSoundBank(
        SwitchProfileDefinition profile,
        IReadOnlyDictionary<KeyboardSampleKey, PreparedKeyboardSample> samples,
        string? selectionId = null,
        SoundPackResolver? customResolver = null,
        IReadOnlyDictionary<SoundPackAssetId, PreparedKeyboardSample>? customSamples = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        this.samples = samples ?? throw new ArgumentNullException(nameof(samples));
        SelectionId = selectionId ?? profile.Id.Value;
        this.customResolver = customResolver;
        this.customSamples = customSamples
            ?? new Dictionary<SoundPackAssetId, PreparedKeyboardSample>();
    }

    public SwitchProfileDefinition Profile { get; }

    public string SelectionId { get; }

    public int SampleCount => samples.Count + customSamples.Count;

    public bool TryGet(
        KeySoundPhase phase,
        KeySoundSample sample,
        out PreparedKeyboardSample prepared) =>
        samples.TryGetValue(new KeyboardSampleKey(phase, sample), out prepared!);

    public KeyboardSoundBank WithCustomSamples(
        SoundPackManifest manifest,
        IReadOnlyDictionary<SoundPackAssetId, PreparedKeyboardSample> preparedCustomSamples)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(preparedCustomSamples);
        return new KeyboardSoundBank(
            Profile,
            samples,
            selectionId: $"custom:{manifest.Id:D}".ToLowerInvariant(),
            customResolver: new SoundPackResolver(manifest),
            customSamples: preparedCustomSamples);
    }

    public bool TryResolve(
        PhysicalKeyId key,
        KeySoundPhase phase,
        out PreparedKeyboardSample prepared,
        out bool explicitlySilent)
    {
        explicitlySilent = false;
        if (customResolver is not null)
        {
            var resolution = customResolver.Resolve(key, phase);
            if (resolution.Kind == SoundPackResolutionKind.Silent)
            {
                explicitlySilent = true;
                prepared = null!;
                return false;
            }

            if (resolution.Kind == SoundPackResolutionKind.Asset
                && resolution.AssetId is { } assetId
                && customSamples.TryGetValue(assetId, out prepared!))
            {
                return true;
            }
        }

        var requested = KeySoundMapper.SampleFor(key, phase, Profile);
        if (requested is null)
        {
            prepared = null!;
            return false;
        }

        var fallback = phase == KeySoundPhase.Release
            ? Profile.HasRowSpecificReleaseSamples
                ? KeySoundSample.GenericR2
                : KeySoundSample.Generic
            : KeySoundSample.GenericR2;
        return TryGet(phase, requested.Value, out prepared)
            || TryGet(phase, fallback, out prepared);
    }
}

public sealed class PointerSoundBank
{
    private readonly IReadOnlyDictionary<PointerSampleKey, PreparedPcmSample> samples;

    internal PointerSoundBank(
        PointerSoundProfileDefinition profile,
        IReadOnlyDictionary<PointerSampleKey, PreparedPcmSample> samples)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        this.samples = samples ?? throw new ArgumentNullException(nameof(samples));
    }

    public PointerSoundProfileDefinition Profile { get; }

    public int SampleCount => samples.Count;

    public bool TryGet(
        PointerSoundPhase phase,
        PointerSoundSample sample,
        out PreparedPcmSample prepared) =>
        samples.TryGetValue(new PointerSampleKey(phase, sample), out prepared!);
}

/// <summary>Resolves the linked 237-file resource tree and prepares complete banks atomically.</summary>
public sealed class BuiltInAudioResourceCatalog
{
    private static readonly string[] SupportedExtensions = [".wav", ".mp3"];
    private readonly IAudioSampleLoader loader;

    public BuiltInAudioResourceCatalog(string? audioRoot = null, IAudioSampleLoader? loader = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        AudioRoot = Path.GetFullPath(
            audioRoot ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds"));
        this.loader = loader;
    }

    public string AudioRoot { get; }

    public Task<KeyboardSoundBank> LoadKeyboardBankAsync(
        SwitchProfileDefinition profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Task.Run(() => LoadKeyboardBank(profile, cancellationToken), cancellationToken);
    }

    public Task<PointerSoundBank> LoadPointerBankAsync(
        PointerSoundProfileDefinition profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Task.Run(() => LoadPointerBank(profile, cancellationToken), cancellationToken);
    }

    public Task<PreparedKeyboardSample> LoadCustomKeyboardSampleAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(
            () => new PreparedKeyboardSample(
                loader.Decode(path, trimKeyboardLeadingSilence: true, cancellationToken)),
            cancellationToken);
    }

    public string ResolveKeyboardResource(
        SwitchProfileDefinition profile,
        KeySoundPhase phase,
        KeySoundSample sample) =>
        ResolveExistingResource(
            Path.Combine(AudioRoot, profile.Id.Value, phase.DirectoryName()),
            sample.ResourceName());

    public string ResolvePointerResource(
        PointerSoundProfileDefinition profile,
        PointerSoundPhase phase,
        PointerSoundSample sample) =>
        ResolveExistingResource(
            Path.Combine(AudioRoot, "pointer", profile.Id.Value, PointerPhaseDirectoryName(phase)),
            PointerResourceName(sample));

    private KeyboardSoundBank LoadKeyboardBank(
        SwitchProfileDefinition profile,
        CancellationToken cancellationToken)
    {
        var prepared = new Dictionary<KeyboardSampleKey, PreparedKeyboardSample>();
        foreach (var phase in Enum.GetValues<KeySoundPhase>())
        {
            foreach (var sample in BuiltInSamplePlan.RequiredSamples(profile, phase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveKeyboardResource(profile, phase, sample);
                var pcm = loader.Decode(path, trimKeyboardLeadingSilence: true, cancellationToken);
                prepared.Add(new KeyboardSampleKey(phase, sample), new PreparedKeyboardSample(pcm));
            }
        }

        return new KeyboardSoundBank(profile, prepared);
    }

    private PointerSoundBank LoadPointerBank(
        PointerSoundProfileDefinition profile,
        CancellationToken cancellationToken)
    {
        var prepared = new Dictionary<PointerSampleKey, PreparedPcmSample>();
        foreach (var phase in Enum.GetValues<PointerSoundPhase>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The current five bundled pointer profiles intentionally contain PRIMARY only.
            var path = ResolvePointerResource(profile, phase, PointerSoundSample.Primary);
            var pcm = loader.Decode(path, trimKeyboardLeadingSilence: false, cancellationToken);
            prepared.Add(new PointerSampleKey(phase, PointerSoundSample.Primary), pcm);
        }

        return new PointerSoundBank(profile, prepared);
    }

    private static string ResolveExistingResource(string directory, string resourceName)
    {
        foreach (var extension in SupportedExtensions)
        {
            var candidate = Path.Combine(directory, resourceName + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Required audio resource '{resourceName}' was not found in '{directory}'.");
    }

    private static string PointerPhaseDirectoryName(PointerSoundPhase phase) => phase switch
    {
        PointerSoundPhase.Press => "press",
        PointerSoundPhase.Release => "release",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static string PointerResourceName(PointerSoundSample sample) => sample switch
    {
        PointerSoundSample.Primary => "PRIMARY",
        PointerSoundSample.Secondary => "SECONDARY",
        PointerSoundSample.Middle => "MIDDLE",
        _ => throw new ArgumentOutOfRangeException(nameof(sample)),
    };
}
