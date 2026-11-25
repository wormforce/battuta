using Battuta.Core.Audio;
using Battuta.Windows.Diy.Packages;

namespace Battuta.Windows.Audio;

public sealed record SoundPackActivationResult(
    string RequestedSelectionId,
    string? ActiveSelectionId,
    bool LoadedRequestedSelection,
    bool WasSuperseded,
    string? Error);

/// <summary>
/// Serializes the logical sound-pack selection lifecycle while allowing obsolete disk loads to
/// finish harmlessly. It mirrors the macOS AppModel contract: a broken custom pack falls back to
/// its built-in base profile, a missing package falls back to Holy Panda, and the error remains
/// visible until a later selection loads successfully.
/// </summary>
public sealed class SoundPackRuntimeController : IAsyncDisposable
{
    private const string CustomPrefix = "custom:";
    private readonly KeyboardAudioEngine audioEngine;
    private readonly Func<Guid, CancellationToken, Task<DiySoundPackDocument>> loadCustomPack;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly object selectionLock = new();
    private readonly object activationDrainLock = new();
    private CancellationTokenSource? selectionCancellation;
    private TaskCompletionSource? activationsDrained;
    private int activeActivationCount;
    private long selectionGeneration;
    private string? error;
    private int disposed;

    public SoundPackRuntimeController(
        KeyboardAudioEngine audioEngine,
        Func<Guid, CancellationToken, Task<DiySoundPackDocument>> loadCustomPack)
    {
        this.audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        this.loadCustomPack = loadCustomPack ?? throw new ArgumentNullException(nameof(loadCustomPack));
    }

    public event EventHandler? StateChanged;

    public string? Error => Volatile.Read(ref error);

    public string? ActiveSelectionId => audioEngine.LoadedKeyboardSelectionId;

    public async Task<SoundPackActivationResult> ActivateAsync(
        string selectionId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionId);
        BeginActivation();

        var normalizedSelectionId = selectionId.Trim().ToLowerInvariant();
        var generation = Interlocked.Increment(ref selectionGeneration);
        CancellationTokenSource linkedCancellation;
        lock (selectionLock)
        {
            selectionCancellation?.Cancel();
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetimeCancellation.Token);
            selectionCancellation = linkedCancellation;
        }

        try
        {
            SoundPackActivationResult result;
            if (SwitchProfileCatalog.TryGet(normalizedSelectionId, out var builtIn))
            {
                result = await ActivateBuiltInAsync(
                    normalizedSelectionId,
                    builtIn,
                    generation,
                    linkedCancellation.Token).ConfigureAwait(false);
            }
            else if (TryParseCustomId(normalizedSelectionId, out var customId))
            {
                result = await ActivateCustomAsync(
                    normalizedSelectionId,
                    customId,
                    generation,
                    linkedCancellation.Token).ConfigureAwait(false);
            }
            else
            {
                result = await FallBackAsync(
                    normalizedSelectionId,
                    SwitchProfileCatalog.Default,
                    "无法识别所选音色，已回退到 Holy Panda。",
                    generation,
                    linkedCancellation.Token).ConfigureAwait(false);
            }

            if (!result.WasSuperseded)
            {
                PublishError(result.Error, generation);
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            return result;
        }
        catch (OperationCanceledException) when (
            generation != Volatile.Read(ref selectionGeneration))
        {
            return Superseded(normalizedSelectionId);
        }
        finally
        {
            lock (selectionLock)
            {
                if (ReferenceEquals(selectionCancellation, linkedCancellation))
                {
                    selectionCancellation = null;
                }
            }

            linkedCancellation.Dispose();
            EndActivation();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        lock (selectionLock)
        {
            selectionCancellation?.Cancel();
        }

        Task drain;
        lock (activationDrainLock)
        {
            drain = activationsDrained?.Task ?? Task.CompletedTask;
        }

        await drain.ConfigureAwait(false);
        lifetimeCancellation.Dispose();
    }

    public static bool TryParseCustomId(string? selectionId, out Guid id)
    {
        if (selectionId is not null
            && selectionId.StartsWith(CustomPrefix, StringComparison.Ordinal)
            && Guid.TryParse(selectionId[CustomPrefix.Length..], out id))
        {
            return true;
        }

        id = default;
        return false;
    }

    private async Task<SoundPackActivationResult> ActivateBuiltInAsync(
        string selectionId,
        SwitchProfileDefinition profile,
        long generation,
        CancellationToken cancellationToken)
    {
        var loaded = await audioEngine
            .LoadKeyboardProfileAsync(profile.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!IsCurrent(generation))
        {
            return Superseded(selectionId);
        }

        if (loaded)
        {
            return new SoundPackActivationResult(
                selectionId,
                audioEngine.LoadedKeyboardSelectionId,
                LoadedRequestedSelection: true,
                WasSuperseded: false,
                Error: null);
        }

        var reason = audioEngine.KeyboardResourceError?.Message ?? "内置音频资源不完整。";
        if (profile.Id != SwitchProfileCatalog.Default.Id)
        {
            var fallbackLoaded = await audioEngine.LoadKeyboardProfileAsync(
                SwitchProfileCatalog.Default.Id,
                cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(generation))
            {
                return Superseded(selectionId);
            }

            if (fallbackLoaded)
            {
                return new SoundPackActivationResult(
                    selectionId,
                    audioEngine.LoadedKeyboardSelectionId,
                    LoadedRequestedSelection: false,
                    WasSuperseded: false,
                    Error: $"{profile.DisplayName} 加载失败，已回退到 Holy Panda：{reason}");
            }
        }

        return new SoundPackActivationResult(
            selectionId,
            audioEngine.LoadedKeyboardSelectionId,
            LoadedRequestedSelection: false,
            WasSuperseded: false,
            Error: $"{profile.DisplayName} 加载失败：{reason}");
    }

    private async Task<SoundPackActivationResult> ActivateCustomAsync(
        string selectionId,
        Guid customId,
        long generation,
        CancellationToken cancellationToken)
    {
        DiySoundPackDocument document;
        try
        {
            document = await loadCustomPack(customId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception loadError)
        {
            return await FallBackAsync(
                selectionId,
                SwitchProfileCatalog.Default,
                $"无法加载 DIY 音色，已回退到 Holy Panda：{loadError.Message}",
                generation,
                cancellationToken).ConfigureAwait(false);
        }

        if (!IsCurrent(generation))
        {
            return Superseded(selectionId);
        }

        var loaded = await audioEngine.LoadCustomSoundPackAsync(
            document.Manifest,
            document.AssetPath,
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(generation))
        {
            return Superseded(selectionId);
        }

        if (loaded)
        {
            return new SoundPackActivationResult(
                selectionId,
                audioEngine.LoadedKeyboardSelectionId,
                LoadedRequestedSelection: true,
                WasSuperseded: false,
                Error: null);
        }

        var fallback = SwitchProfileCatalog.TryGet(document.Manifest.BaseProfileId, out var baseProfile)
            ? baseProfile
            : SwitchProfileCatalog.Default;
        var reason = audioEngine.KeyboardResourceError?.Message ?? "自定义音频资源不完整。";
        return await FallBackAsync(
            selectionId,
            fallback,
            $"DIY 音色加载失败，已回退到 {fallback.DisplayName}：{reason}",
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SoundPackActivationResult> FallBackAsync(
        string requestedSelectionId,
        SwitchProfileDefinition fallback,
        string message,
        long generation,
        CancellationToken cancellationToken)
    {
        _ = await audioEngine
            .LoadKeyboardProfileAsync(fallback.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!IsCurrent(generation))
        {
            return Superseded(requestedSelectionId);
        }

        return new SoundPackActivationResult(
            requestedSelectionId,
            audioEngine.LoadedKeyboardSelectionId,
            LoadedRequestedSelection: false,
            WasSuperseded: false,
            Error: message);
    }

    private bool IsCurrent(long generation) =>
        generation == Volatile.Read(ref selectionGeneration);

    private void PublishError(string? next, long generation)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        _ = Interlocked.Exchange(ref error, next);
    }

    private SoundPackActivationResult Superseded(string requestedSelectionId) => new(
        requestedSelectionId,
        audioEngine.LoadedKeyboardSelectionId,
        LoadedRequestedSelection: false,
        WasSuperseded: true,
        Error: Error);

    private void BeginActivation()
    {
        lock (activationDrainLock)
        {
            if (activeActivationCount == 0)
            {
                activationsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            activeActivationCount++;
        }
    }

    private void EndActivation()
    {
        TaskCompletionSource? completed = null;
        lock (activationDrainLock)
        {
            activeActivationCount--;
            if (activeActivationCount == 0)
            {
                completed = activationsDrained;
                activationsDrained = null;
            }
        }

        completed?.TrySetResult();
    }
}
