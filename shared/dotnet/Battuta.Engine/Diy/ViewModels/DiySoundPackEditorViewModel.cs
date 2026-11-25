using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Core.SoundPacks;
using Battuta.Windows.Diy.Audio;
using Battuta.Windows.Diy.Packages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Battuta.Windows.Diy.ViewModels;

public sealed class DiySoundPackEditorViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DiySoundPackLibrary _library;
    private readonly DiyAudioImportService _audioImporter;
    private readonly DiyAudioSplitService _audioSplitter;
    private readonly DiySoundPackArchiveService _archiveService;
    private readonly IDiyAudioPreviewService _previewService;
    private readonly IDiyBuiltInAudioLocator _builtInAudioLocator;
    private readonly Func<string?, Task> _onLibraryChanged;
    private readonly string _initialSelectionId;
    private readonly string _temporaryRoot;
    private readonly bool _ownsImporter;
    private readonly bool _ownsSplitter;
    private readonly Dictionary<SoundPackAssetId, string> _assetPaths = [];
    private readonly Dictionary<SoundPackAssetId, PreparedDiyAudio> _preparedAudio = [];
    private Guid? _persistedPackId;
    private Guid? _selectedPackId;
    private SoundPackManifest? _manifest;
    private PhysicalKeyId _selectedKey = PhysicalKeys.KeyA;
    private DiyEditorMappingMode _mappingMode = DiyEditorMappingMode.Recommended;
    private DiyEditorSlot _recommendedSlot = DiyEditorSlot.ForRow(KeyboardRowId.R2);
    private DiySplitDraft? _splitDraft;
    private bool _isDirty;
    private bool _isWorking;
    private string? _statusMessage;
    private DiyEditorError? _error;
    private bool _disposed;

    public DiySoundPackEditorViewModel(
        DiySoundPackLibrary library,
        string initialSelectionId,
        DiyAudioImportService? audioImporter = null,
        DiyAudioSplitService? audioSplitter = null,
        DiySoundPackArchiveService? archiveService = null,
        IDiyAudioPreviewService? previewService = null,
        IDiyBuiltInAudioLocator? builtInAudioLocator = null,
        string? temporaryCacheParent = null,
        Func<string?, Task>? onLibraryChanged = null,
        IDiyAudioDecodePipeline? decodePipeline = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        ArgumentException.ThrowIfNullOrWhiteSpace(initialSelectionId);
        _initialSelectionId = initialSelectionId;
        var cacheParent = Path.GetFullPath(temporaryCacheParent ?? Path.GetTempPath());
        _temporaryRoot = Path.Combine(cacheParent, $"BattutaEditor-{Guid.NewGuid():N}");
        _audioImporter = audioImporter ?? new DiyAudioImportService(
            Path.Combine(_temporaryRoot, "NormalizedAudio"),
            decodePipeline: decodePipeline);
        _ownsImporter = audioImporter is null;
        _audioSplitter = audioSplitter ?? new DiyAudioSplitService(
            decodePipeline: decodePipeline);
        _ownsSplitter = audioSplitter is null;
        _archiveService = archiveService ?? new DiySoundPackArchiveService();
        _previewService = previewService ?? new NullDiyAudioPreviewService();
        _builtInAudioLocator = builtInAudioLocator ?? new NullDiyBuiltInAudioLocator();
        _onLibraryChanged = onLibraryChanged ?? (_ => Task.CompletedTask);

        LoadCommand = new AsyncRelayCommand(LoadInitialStateAsync);
        NewBlankCommand = new AsyncRelayCommand(CreateBlankAsync);
        CreateBasedOnCurrentCommand = new AsyncRelayCommand(CreateBasedOnInitialSelectionAsync);
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(enableAfterSaving: false));
        SaveAndEnableCommand = new AsyncRelayCommand(() => SaveAsync(enableAfterSaving: true));
        DeleteCommand = new AsyncRelayCommand(DeleteSelectedPackAsync);
        PrepareForClosingCommand = new AsyncRelayCommand(async () =>
        {
            _ = await PrepareForClosingAsync();
        });
    }

    public ObservableCollection<SoundPackDescriptor> CustomPacks { get; } = [];
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "WPF binds this instance property.")]
    public KeyboardLayoutDefinition Layout => KeyboardLayoutCatalog.CompactAnsi;

    public Guid? SelectedPackId
    {
        get => _selectedPackId;
        private set => SetProperty(ref _selectedPackId, value);
    }

    public SoundPackManifest? Manifest
    {
        get => _manifest;
        private set
        {
            if (SetProperty(ref _manifest, value))
            {
                OnPropertyChanged(nameof(HasDraft));
                OnPropertyChanged(nameof(AssetChoices));
            }
        }
    }

    public PhysicalKeyId SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (SetProperty(ref _selectedKey, value))
            {
                OnPropertyChanged(nameof(ActiveSlot));
            }
        }
    }

    /// <summary>
    /// The mapping slot the inspector currently edits, resolved from the
    /// mapping mode: generic, the recommended row/special slot, or a
    /// per-key override for <see cref="SelectedKey"/>.
    /// </summary>
    public DiyEditorSlot ActiveSlot => MappingMode switch
    {
        DiyEditorMappingMode.Generic => DiyEditorSlot.Generic,
        DiyEditorMappingMode.Recommended => RecommendedSlot,
        DiyEditorMappingMode.PerKey => DiyEditorSlot.ForKey(SelectedKey),
        _ => DiyEditorSlot.Generic,
    };

    public DiyEditorMappingMode MappingMode
    {
        get => _mappingMode;
        set
        {
            if (SetProperty(ref _mappingMode, value))
            {
                OnPropertyChanged(nameof(ActiveSlot));
            }
        }
    }

    public DiyEditorSlot RecommendedSlot
    {
        get => _recommendedSlot;
        set
        {
            if (SetProperty(ref _recommendedSlot, value ?? throw new ArgumentNullException(nameof(value))))
            {
                OnPropertyChanged(nameof(ActiveSlot));
            }
        }
    }

    public DiySplitDraft? SplitDraft
    {
        get => _splitDraft;
        private set
        {
            if (SetProperty(ref _splitDraft, value))
            {
                OnPropertyChanged(nameof(HasTemporaryAudioResources));
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(CanExport));
            }
        }
    }

    public bool IsWorking
    {
        get => _isWorking;
        private set => SetProperty(ref _isWorking, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public DiyEditorError? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public bool CanExport => _persistedPackId.HasValue && !IsDirty;
    public bool CanDelete => _persistedPackId.HasValue;
    public bool HasDraft => Manifest is not null;
    public bool HasTemporaryAudioResources =>
        _preparedAudio.Count > 0 || SplitDraft is not null || Directory.Exists(_temporaryRoot);
    public string TemporaryCacheRoot => _temporaryRoot;

    public IReadOnlyList<SoundPackAudioAsset> AssetChoices => Manifest?.Assets.Values
        .OrderBy(asset => asset.OriginalFilename ?? asset.Id.Value, StringComparer.CurrentCultureIgnoreCase)
        .ToArray() ?? [];

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand NewBlankCommand { get; }
    public IAsyncRelayCommand CreateBasedOnCurrentCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand SaveAndEnableCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand PrepareForClosingCommand { get; }

    public Task LoadInitialStateAsync() => PerformWorkAsync(
        "正在载入 DIY 音色…",
        async cancellationToken =>
        {
            if (Manifest is not null)
            {
                return;
            }

            await ReloadLibraryCoreAsync(cancellationToken);
            if (TryCustomPackId(_initialSelectionId, out var selectedId) &&
                CustomPacks.Any(pack => pack.CustomPackId == selectedId))
            {
                await LoadPackCoreAsync(selectedId, cancellationToken);
            }
            else if (CustomPacks.FirstOrDefault()?.CustomPackId is { } firstId)
            {
                await LoadPackCoreAsync(firstId, cancellationToken);
            }
            else
            {
                await CreateDraftBasedOnInitialSelectionCoreAsync(cancellationToken);
            }
        });

    public Task ReloadLibraryAsync() => PerformWorkAsync(
        "正在刷新音色库…",
        async cancellationToken =>
        {
            await ReloadLibraryCoreAsync(cancellationToken);
            StatusMessage = null;
        });

    public Task SelectPackAsync(Guid id) => PerformWorkAsync(
        "正在打开音色包…",
        async cancellationToken =>
        {
            await LoadPackCoreAsync(id, cancellationToken);
            StatusMessage = null;
        });

    public Task CreateBlankAsync() => PerformWorkAsync(
        "正在新建空白音色…",
        async cancellationToken =>
        {
            await CleanupTemporaryAudioAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            Manifest = new SoundPackManifest
            {
                Name = "未命名音色",
                Family = "DIY",
                Tone = "自定义音色",
                CreatedAt = now,
                ModifiedAt = now,
            };
            _assetPaths.Clear();
            _persistedPackId = null;
            SelectedPackId = null;
            MappingMode = DiyEditorMappingMode.Generic;
            IsDirty = true;
            StatusMessage = "已创建空白草稿";
            NotifyPackState();
        });

    public Task CreateBasedOnInitialSelectionAsync() => PerformWorkAsync(
        "正在复制当前音色…",
        CreateDraftBasedOnInitialSelectionCoreAsync);

    public void SetName(string value)
    {
        if (Manifest is not null && !string.Equals(Manifest.Name, value, StringComparison.Ordinal))
        {
            MutateManifest(manifest => manifest with { Name = value });
        }
    }

    public void SetAuthor(string value)
    {
        var resolved = string.IsNullOrWhiteSpace(value) ? null : value;
        if (Manifest is not null && !string.Equals(Manifest.Author, resolved, StringComparison.Ordinal))
        {
            MutateManifest(manifest => manifest with { Author = resolved });
        }
    }

    public void SetNotes(string value)
    {
        var resolved = string.IsNullOrWhiteSpace(value) ? null : value;
        if (Manifest is not null && !string.Equals(Manifest.Notes, resolved, StringComparison.Ordinal))
        {
            MutateManifest(manifest => manifest with { Notes = resolved });
        }
    }

    public void ClearError() => Error = null;

    public void ReportError(Exception error, string title = "操作失败")
    {
        ArgumentNullException.ThrowIfNull(error);
        Present(title, error.Message);
    }

    public async Task SaveAsync(
        bool enableAfterSaving,
        CancellationToken cancellationToken = default)
    {
        if (IsWorking || Manifest is null)
        {
            return;
        }

        var trimmedName = Manifest.Name.Trim();
        if (trimmedName.Length == 0)
        {
            Present("无法保存", "请先输入音色包名称。");
            return;
        }

        await PerformWorkAsync(
            "正在保存音色包…",
            async operationToken =>
            {
                using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    operationToken);
                {
                    var linkedToken = linkedSource.Token;
                    var draft = PruneUnusedAssets(Manifest with { Name = trimmedName });
                    var files = _assetPaths
                        .Where(entry => draft.Assets.ContainsKey(entry.Key.Value))
                        .ToDictionary();
                    var descriptor = await _library.SaveAsync(
                        draft,
                        files,
                        cancellationToken: linkedToken);
                    var document = await _library.LoadAsync(draft.Id, linkedToken);
                    await CleanupTemporaryAudioAsync(linkedToken);
                    InstallDocument(document);
                    IsDirty = false;
                    await ReloadLibraryCoreAsync(linkedToken);
                    StatusMessage = enableAfterSaving
                        ? $"已保存并启用 {descriptor.Name}"
                        : $"已保存 {descriptor.Name}";
                    await _onLibraryChanged(enableAfterSaving ? descriptor.SelectionId : null);
                }
            },
            cancellationToken);
    }

    public Task DeleteSelectedPackAsync() => PerformWorkAsync(
        "正在移到 Battuta 可恢复区…",
        async cancellationToken =>
        {
            if (_persistedPackId is not { } id)
            {
                return;
            }

            _ = await _library.RemoveAsync(id, cancellationToken);
            await ReloadLibraryCoreAsync(cancellationToken);
            await _onLibraryChanged(null);
            if (CustomPacks.FirstOrDefault()?.CustomPackId is { } nextId)
            {
                await LoadPackCoreAsync(nextId, cancellationToken);
            }
            else
            {
                await CleanupTemporaryAudioAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                Manifest = new SoundPackManifest
                {
                    Name = "未命名音色",
                    Family = "DIY",
                    Tone = "自定义音色",
                    CreatedAt = now,
                    ModifiedAt = now,
                };
                _assetPaths.Clear();
                _persistedPackId = null;
                SelectedPackId = null;
                MappingMode = DiyEditorMappingMode.Generic;
                IsDirty = true;
            }

            StatusMessage = "音色包已移入 Battuta 的可恢复废纸篓";
            NotifyPackState();
        });

    public Task ImportPackAsync(string sourcePackagePath) => PerformWorkAsync(
        "正在导入音色包…",
        async cancellationToken =>
        {
            var descriptor = await _archiveService.ImportAsync(
                sourcePackagePath,
                _library,
                SoundPackImportCollisionPolicy.Duplicate,
                cancellationToken);
            if (descriptor.CustomPackId is not { } id)
            {
                throw new SoundPackException(
                    SoundPackErrorKind.InvalidManifest,
                    "Imported descriptor is not a custom sound pack.");
            }

            await ReloadLibraryCoreAsync(cancellationToken);
            var document = await _library.LoadAsync(id, cancellationToken);
            await CleanupTemporaryAudioAsync(cancellationToken);
            InstallDocument(document);
            IsDirty = false;
            StatusMessage = $"已导入 {descriptor.Name}";
            await _onLibraryChanged(null);
        });

    public Task ExportSelectedPackAsync(
        string destinationPath,
        bool overwriteExisting = false) => PerformWorkAsync(
        "正在导出音色包…",
        async cancellationToken =>
        {
            if (IsDirty || _persistedPackId is not { } id)
            {
                return;
            }

            var exported = await _archiveService.ExportAsync(
                id,
                _library,
                destinationPath,
                overwriteExisting,
                cancellationToken);
            StatusMessage = $"已导出到 {Path.GetFileName(exported)}";
        });

    public Task ImportAudioAsync(string sourcePath, DiyEditorAudioTarget target) => PerformWorkAsync(
        "正在转换音频…",
        async cancellationToken =>
        {
            if (Manifest is null)
            {
                return;
            }

            var prepared = await _audioImporter.PrepareImportAsync(sourcePath, cancellationToken);
            InstallPrepared(prepared, target);
            StatusMessage = $"已导入 {Path.GetFileName(sourcePath)}";
        });

    public Task AnalyzeFullKeystrokeAsync(string sourcePath, DiyEditorSlot target) => PerformWorkAsync(
        "正在分析按下与回弹…",
        async cancellationToken =>
        {
            if (Manifest is null)
            {
                return;
            }

            var localPath = MakeLocalSplitSourceCopy(sourcePath);
            try
            {
                var analysis = await _audioSplitter.AnalyzeAsync(localPath, cancellationToken);
                SplitDraft = new DiySplitDraft(Guid.NewGuid(), target, analysis);
                StatusMessage = null;
            }
            catch
            {
                DeleteOwnedSplitDirectory(Path.GetDirectoryName(localPath)!);
                throw;
            }
        });

    public async Task<bool> ConfirmSplitAsync(
        DiySplitDraft draft,
        double splitTimeSeconds,
        double releaseEndTimeSeconds)
    {
        var succeeded = false;
        await PerformWorkAsync(
            "正在生成两段音频…",
            async cancellationToken =>
            {
                if (Manifest is null || SplitDraft?.Id != draft.Id)
                {
                    return;
                }

                var directory = MakeTemporaryDirectory("SplitExport");
                var pressPath = Path.Combine(directory, "press.wav");
                var releasePath = Path.Combine(directory, "release.wav");
                try
                {
                    _ = await _audioSplitter.ExportSplitAsync(
                        draft.Analysis.SourcePath,
                        splitTimeSeconds,
                        releaseEndTimeSeconds,
                        pressPath,
                        releasePath,
                        cancellationToken: cancellationToken);
                    var press = await _audioImporter.PrepareImportAsync(pressPath, cancellationToken);
                    _preparedAudio[ToAssetId(press)] = press;
                    var release = await _audioImporter.PrepareImportAsync(releasePath, cancellationToken);
                    _preparedAudio[ToAssetId(release)] = release;
                    InstallPrepared(press, new DiyEditorAudioTarget(draft.Target, KeySoundPhase.Press));
                    InstallPrepared(release, new DiyEditorAudioTarget(draft.Target, KeySoundPhase.Release));
                    SplitDraft = null;
                    DiscardSplitSource(draft);
                    StatusMessage = "已拆分并设置按下/回弹音";
                    succeeded = true;
                }
                finally
                {
                    DeleteOwnedSplitDirectory(directory);
                }
            });
        return succeeded;
    }

    public Task PreviewSplitAsync(
        DiySplitDraft draft,
        double splitTimeSeconds,
        double releaseEndTimeSeconds,
        KeySoundPhase phase) => PerformWorkAsync(
        "正在准备试听…",
        async cancellationToken =>
        {
            var directory = MakeTemporaryDirectory("SplitPreview");
            var pressPath = Path.Combine(directory, "press.wav");
            var releasePath = Path.Combine(directory, "release.wav");
            try
            {
                _ = await _audioSplitter.ExportSplitAsync(
                    draft.Analysis.SourcePath,
                    splitTimeSeconds,
                    releaseEndTimeSeconds,
                    pressPath,
                    releasePath,
                    cancellationToken: cancellationToken);
                await _previewService.PreviewAsync(
                    phase == KeySoundPhase.Press ? pressPath : releasePath,
                    cancellationToken);
                StatusMessage = null;
            }
            finally
            {
                DeleteOwnedSplitDirectory(directory);
            }
        });

    public void CancelSplit()
    {
        if (SplitDraft is { } draft)
        {
            DiscardSplitSource(draft);
        }

        SplitDraft = null;
    }

    public SoundPackAssetId? AssignmentAsset(DiyEditorSlot slot, KeySoundPhase phase)
    {
        if (Manifest is null)
        {
            return null;
        }

        var assignments = Manifest.AssignmentsFor(phase);
        return slot.Kind switch
        {
            DiyEditorSlotKind.Generic => assignments.Generic,
            DiyEditorSlotKind.Row when slot.Row is { } row => assignments.AssetFor(row),
            DiyEditorSlotKind.Special when slot.Special is { } special => assignments.AssetFor(special),
            DiyEditorSlotKind.Key when slot.Key is { } key &&
                assignments.OverrideFor(key) is { Kind: SoundPackKeyOverrideKind.Asset, AssetId: { } assetId } =>
                assetId,
            _ => null,
        };
    }

    public DiyKeyOverrideChoice OverrideChoice(PhysicalKeyId key, KeySoundPhase phase)
    {
        var value = Manifest?.AssignmentsFor(phase).OverrideFor(key);
        return value?.Kind switch
        {
            SoundPackKeyOverrideKind.Silent => DiyKeyOverrideChoice.Silent,
            SoundPackKeyOverrideKind.Asset => DiyKeyOverrideChoice.Asset,
            _ => DiyKeyOverrideChoice.Inherit,
        };
    }

    public void SetOverrideChoice(
        DiyKeyOverrideChoice choice,
        PhysicalKeyId key,
        KeySoundPhase phase)
    {
        MutateAssignments(phase, assignments =>
        {
            var value = choice switch
            {
                DiyKeyOverrideChoice.Inherit => null,
                DiyKeyOverrideChoice.Silent => SoundPackKeyOverride.Silent,
                DiyKeyOverrideChoice.Asset when assignments.OverrideFor(key)?.Kind ==
                    SoundPackKeyOverrideKind.Asset => assignments.OverrideFor(key),
                DiyKeyOverrideChoice.Asset => SoundPackKeyOverride.Inherit,
                _ => throw new ArgumentOutOfRangeException(nameof(choice)),
            };
            if (!assignments.TrySetOverride(key, value))
            {
                throw new InvalidOperationException($"Key {key.Value} cannot be persisted in schema v1.");
            }

            return assignments;
        });
    }

    public void SetExistingAsset(
        SoundPackAssetId? assetId,
        DiyEditorSlot slot,
        KeySoundPhase phase) =>
        MutateAssignments(phase, assignments => SetAsset(assignments, slot, assetId));

    public string AssetLabel(SoundPackAssetId? assetId)
    {
        if (assetId is not { } id || Manifest is null || !Manifest.Assets.TryGetValue(id.Value, out var asset))
        {
            return "继承上一级";
        }

        return asset.OriginalFilename ?? id.Value[..Math.Min(10, id.Value.Length)];
    }

    public async Task PreviewAsync(
        DiyEditorSlot slot,
        KeySoundPhase phase,
        CancellationToken cancellationToken = default)
    {
        if (RepresentativeKey(slot) is { } key)
        {
            await PreviewAsync(key, phase, cancellationToken);
        }
    }

    public async Task PreviewAsync(
        PhysicalKeyId key,
        KeySoundPhase phase,
        CancellationToken cancellationToken = default)
    {
        if (Manifest is null)
        {
            return;
        }

        var resolution = new SoundPackResolver(Manifest).Resolve(key, phase);
        if (resolution.Kind == SoundPackResolutionKind.Asset && resolution.AssetId is { } assetId &&
            _assetPaths.TryGetValue(assetId, out var customPath))
        {
            await _previewService.PreviewAsync(customPath, cancellationToken);
            return;
        }

        if (resolution.Kind == SoundPackResolutionKind.Silent)
        {
            return;
        }

        var baseProfile = Manifest.BaseProfileId ?? SwitchProfiles.HolyPanda.Value;
        var builtInPath = _builtInAudioLocator.FindAudio(baseProfile, key, phase);
        if (builtInPath is not null)
        {
            await _previewService.PreviewAsync(builtInPath, cancellationToken);
        }
    }

    public async Task<bool> PrepareForClosingAsync(CancellationToken cancellationToken = default)
    {
        if (IsWorking)
        {
            return false;
        }

        if (!HasTemporaryAudioResources)
        {
            return true;
        }

        IsWorking = true;
        StatusMessage = "正在清理临时音频…";
        try
        {
            await CleanupTemporaryAudioAsync(cancellationToken);
            StatusMessage = null;
            return true;
        }
        catch (Exception error)
        {
            Present("无法清理临时音频", error.Message);
            return false;
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!IsWorking)
        {
            _ = await PrepareForClosingAsync();
            if (_ownsImporter)
            {
                _audioImporter.Dispose();
            }
            if (_ownsSplitter)
            {
                _audioSplitter.Dispose();
            }
        }
    }

    private async Task PerformWorkAsync(
        string status,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        if (IsWorking || _disposed)
        {
            return;
        }

        IsWorking = true;
        StatusMessage = status;
        Error = null;
        try
        {
            await operation(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
        }
        catch (Exception error)
        {
            StatusMessage = null;
            Present("操作失败", LocalizedErrorMessage(error));
        }
        finally
        {
            IsWorking = false;
        }
    }

    private async Task ReloadLibraryCoreAsync(CancellationToken cancellationToken)
    {
        var descriptors = await _library.DescriptorsAsync(cancellationToken);
        CustomPacks.Clear();
        foreach (var descriptor in descriptors.Where(descriptor => !descriptor.IsReadOnly))
        {
            CustomPacks.Add(descriptor);
        }
    }

    private async Task LoadPackCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var document = await _library.LoadAsync(id, cancellationToken);
        await CleanupTemporaryAudioAsync(cancellationToken);
        InstallDocument(document);
        IsDirty = false;
    }

    private async Task CreateDraftBasedOnInitialSelectionCoreAsync(CancellationToken cancellationToken)
    {
        if (TryCustomPackId(_initialSelectionId, out var sourceId))
        {
            try
            {
                var source = await _library.LoadAsync(sourceId, cancellationToken);
                var now = DateTimeOffset.UtcNow;
                var copy = DeepClone(source.Manifest) with
                {
                    Id = Guid.NewGuid(),
                    Name = $"{source.Manifest.Name} 副本",
                    CreatedAt = now,
                    ModifiedAt = now,
                };
                await CleanupTemporaryAudioAsync(cancellationToken);
                Manifest = copy;
                _assetPaths.Clear();
                foreach (var asset in source.Manifest.Assets.Values)
                {
                    _assetPaths[asset.Id] = source.AssetPath(asset.Id);
                }
                _persistedPackId = null;
                SelectedPackId = null;
                IsDirty = true;
                StatusMessage = "已基于当前音色创建草稿";
                NotifyPackState();
                return;
            }
            catch (SoundPackException error)
            {
                Present("无法复制当前音色", error.Message);
            }
        }

        await CleanupTemporaryAudioAsync(cancellationToken);
        var baseProfile = SwitchProfileCatalog.TryGet(_initialSelectionId, out var profile)
            ? profile
            : SwitchProfileCatalog.Default;
        var timestamp = DateTimeOffset.UtcNow;
        Manifest = new SoundPackManifest
        {
            Name = $"{baseProfile.DisplayName} DIY",
            Family = baseProfile.Family,
            Tone = baseProfile.Tone,
            BaseProfileId = baseProfile.Id.Value,
            CreatedAt = timestamp,
            ModifiedAt = timestamp,
        };
        _assetPaths.Clear();
        _persistedPackId = null;
        SelectedPackId = null;
        MappingMode = DiyEditorMappingMode.Recommended;
        IsDirty = true;
        StatusMessage = $"未设置的位置会继承 {baseProfile.DisplayName}";
        NotifyPackState();
    }

    private void InstallDocument(DiySoundPackDocument document)
    {
        Manifest = DeepClone(document.Manifest);
        _assetPaths.Clear();
        foreach (var asset in document.Manifest.Assets.Values)
        {
            _assetPaths[asset.Id] = document.AssetPath(asset.Id);
        }
        _persistedPackId = document.Manifest.Id;
        SelectedPackId = document.Manifest.Id;
        NotifyPackState();
    }

    private void InstallPrepared(PreparedDiyAudio prepared, DiyEditorAudioTarget target)
    {
        if (Manifest is null)
        {
            return;
        }

        var assetId = ToAssetId(prepared);
        var asset = new SoundPackAudioAsset
        {
            Id = assetId,
            RelativePath = $"assets/{prepared.AssetId}.wav",
            Sha256 = prepared.AssetId,
            OriginalFilename = prepared.OriginalFileName,
            DurationSeconds = prepared.AudioInfo.DurationSeconds,
            SampleRate = prepared.AudioInfo.SampleRate,
            ChannelCount = prepared.AudioInfo.ChannelCount,
            ByteCount = prepared.AudioInfo.ByteCount,
        };
        var assets = new Dictionary<string, SoundPackAudioAsset>(Manifest.Assets, StringComparer.Ordinal)
        {
            [assetId.Value] = asset,
        };
        var assignments = CloneAssignments(Manifest.AssignmentsFor(target.Phase));
        assignments = SetAsset(assignments, target.Slot, assetId);
        Manifest = target.Phase == KeySoundPhase.Press
            ? Manifest with
            {
                Assets = assets,
                Press = assignments,
                ModifiedAt = DateTimeOffset.UtcNow,
            }
            : Manifest with
            {
                Assets = assets,
                Release = assignments,
                ModifiedAt = DateTimeOffset.UtcNow,
            };
        _assetPaths[assetId] = prepared.NormalizedFilePath;
        _preparedAudio[assetId] = prepared;
        IsDirty = true;
        NotifyPackState();
    }

    private void MutateManifest(Func<SoundPackManifest, SoundPackManifest> mutation)
    {
        if (Manifest is null)
        {
            return;
        }

        Manifest = mutation(Manifest) with { ModifiedAt = DateTimeOffset.UtcNow };
        IsDirty = true;
    }

    private void MutateAssignments(
        KeySoundPhase phase,
        Func<SoundPackPhaseAssignments, SoundPackPhaseAssignments> mutation)
    {
        if (Manifest is null)
        {
            return;
        }

        var assignments = CloneAssignments(Manifest.AssignmentsFor(phase));
        assignments = mutation(assignments);
        Manifest = phase == KeySoundPhase.Press
            ? Manifest with { Press = assignments, ModifiedAt = DateTimeOffset.UtcNow }
            : Manifest with { Release = assignments, ModifiedAt = DateTimeOffset.UtcNow };
        IsDirty = true;
    }

    private static SoundPackPhaseAssignments SetAsset(
        SoundPackPhaseAssignments assignments,
        DiyEditorSlot slot,
        SoundPackAssetId? assetId)
    {
        switch (slot.Kind)
        {
            case DiyEditorSlotKind.Generic:
                return assignments with { Generic = assetId };
            case DiyEditorSlotKind.Row when slot.Row is { } row:
                SetDictionaryAsset(assignments.Rows, SoundPackV1WireNames.Row(row), assetId);
                return assignments;
            case DiyEditorSlotKind.Special when slot.Special is { } special:
                SetDictionaryAsset(assignments.Specials, SoundPackV1WireNames.Special(special), assetId);
                return assignments;
            case DiyEditorSlotKind.Key when slot.Key is { } key:
                if (!assignments.TrySetOverride(
                        key,
                        assetId is { } id ? SoundPackKeyOverride.Asset(id) : SoundPackKeyOverride.Inherit))
                {
                    throw new InvalidOperationException($"Key {key.Value} cannot be persisted in schema v1.");
                }
                return assignments;
            default:
                throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private static void SetDictionaryAsset(
        Dictionary<string, SoundPackAssetId> dictionary,
        string key,
        SoundPackAssetId? assetId)
    {
        if (assetId is { } id)
        {
            dictionary[key] = id;
        }
        else
        {
            dictionary.Remove(key);
        }
    }

    private static SoundPackManifest PruneUnusedAssets(SoundPackManifest manifest)
    {
        var referenced = manifest.ReferencedAssetIds();
        return manifest with
        {
            Assets = manifest.Assets
                .Where(entry => referenced.Contains(new SoundPackAssetId(entry.Key)))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
        };
    }

    private static SoundPackManifest DeepClone(SoundPackManifest manifest) => manifest with
    {
        Press = CloneAssignments(manifest.Press),
        Release = CloneAssignments(manifest.Release),
        Assets = manifest.Assets.ToDictionary(
            entry => entry.Key,
            entry => entry.Value with { },
            StringComparer.Ordinal),
        Attributions = manifest.Attributions.Select(attribution => attribution with { }).ToList(),
    };

    private static SoundPackPhaseAssignments CloneAssignments(SoundPackPhaseAssignments assignments) => new()
    {
        Generic = assignments.Generic,
        Rows = new Dictionary<string, SoundPackAssetId>(assignments.Rows, StringComparer.Ordinal),
        Specials = new Dictionary<string, SoundPackAssetId>(assignments.Specials, StringComparer.Ordinal),
        KeyOverrides = new Dictionary<string, SoundPackKeyOverride>(
            assignments.KeyOverrides,
            StringComparer.Ordinal),
    };

    private async Task CleanupTemporaryAudioAsync(CancellationToken cancellationToken)
    {
        SplitDraft = null;
        if (_ownsImporter)
        {
            await _audioImporter.RemoveAllPreparedAudioAsync(cancellationToken);
            _preparedAudio.Clear();
        }
        else
        {
            foreach (var entry in _preparedAudio.ToArray())
            {
                await _audioImporter.DiscardPreparedAudioAsync(entry.Value, cancellationToken);
                _preparedAudio.Remove(entry.Key);
            }
        }

        if (Directory.Exists(_temporaryRoot))
        {
            var attributes = File.GetAttributes(_temporaryRoot);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                !Path.GetFileName(_temporaryRoot).StartsWith("BattutaEditor-", StringComparison.Ordinal))
            {
                throw new IOException("拒绝清理不安全的编辑器临时目录。");
            }
            DiyTemporaryFileSafety.DeleteDirectoryTree(_temporaryRoot);
        }
        OnPropertyChanged(nameof(HasTemporaryAudioResources));
    }

    private string MakeLocalSplitSourceCopy(string sourcePath)
    {
        var source = new FileInfo(Path.GetFullPath(sourcePath));
        if (!source.Exists || source.Length <= 0 || source.Length > 64 * 1_024 * 1_024 ||
            (source.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new DiyAudioException("完整击键录音不是安全的普通音频文件。");
        }

        var directory = MakeTemporaryDirectory("SplitSource");
        var extension = source.Extension.Length == 0 ? ".audio" : source.Extension;
        var destination = Path.Combine(directory, $"source{extension}");
        try
        {
            DiySoundPackFileSafety.CopyRegularFile(
                source.FullName,
                destination,
                64 * 1_024 * 1_024);
            return destination;
        }
        catch
        {
            DeleteOwnedSplitDirectory(directory);
            throw;
        }
    }

    private string MakeTemporaryDirectory(string prefix)
    {
        Directory.CreateDirectory(_temporaryRoot);
        if ((File.GetAttributes(_temporaryRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("编辑器临时目录是重解析点。");
        }
        var directory = Path.Combine(_temporaryRoot, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void DiscardSplitSource(DiySplitDraft draft) =>
        DeleteOwnedSplitDirectory(Path.GetDirectoryName(draft.Analysis.SourcePath)!);

    private void DeleteOwnedSplitDirectory(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_temporaryRoot));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var name = Path.GetFileName(candidate);
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !(name.StartsWith("SplitSource-", StringComparison.Ordinal) ||
              name.StartsWith("SplitExport-", StringComparison.Ordinal) ||
              name.StartsWith("SplitPreview-", StringComparison.Ordinal)))
        {
            return;
        }
        if (Directory.Exists(candidate) &&
            (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0)
        {
            DiyTemporaryFileSafety.DeleteDirectoryTree(candidate);
        }
    }

    private static PhysicalKeyId? RepresentativeKey(DiyEditorSlot slot) => slot.Kind switch
    {
        DiyEditorSlotKind.Generic => PhysicalKeys.KeyA,
        DiyEditorSlotKind.Row when slot.Row is { } row => PhysicalKeyCatalog.All
            .FirstOrDefault(key => key.Row == row && key.SpecialKey is null)?.Id,
        DiyEditorSlotKind.Special when slot.Special is { } special => PhysicalKeyCatalog.All
            .FirstOrDefault(key => key.SpecialKey == special)?.Id,
        DiyEditorSlotKind.Key => slot.Key,
        _ => null,
    };

    private void Present(string title, string message) => Error = new DiyEditorError(title, message);

    private static string LocalizedErrorMessage(Exception error)
    {
        if (error is not SoundPackException soundPackError)
        {
            return error.Message;
        }

        var prefix = soundPackError.Kind switch
        {
            SoundPackErrorKind.InvalidManifest => "音色包清单无效",
            SoundPackErrorKind.UnsupportedSchema => "不支持此音色包格式版本",
            SoundPackErrorKind.UnsafePath => "音色包包含不安全路径",
            SoundPackErrorKind.UnsafeFile => "音色包包含不安全文件",
            SoundPackErrorKind.MissingAsset => "音色包缺少音频资源",
            SoundPackErrorKind.InvalidAudio => "音频无效",
            SoundPackErrorKind.SizeLimitExceeded => "音色包超过安全限制",
            SoundPackErrorKind.HashMismatch => "音频校验失败",
            SoundPackErrorKind.PackAlreadyExists => "音色包已存在",
            SoundPackErrorKind.PackNotFound => "找不到音色包",
            SoundPackErrorKind.FileOperation => "文件操作失败",
            _ => "音色包操作失败",
        };
        return $"{prefix}：{soundPackError.Message}";
    }

    private void NotifyPackState()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(AssetChoices));
        OnPropertyChanged(nameof(HasTemporaryAudioResources));
    }

    private static bool TryCustomPackId(string selectionId, out Guid id)
    {
        const string prefix = "custom:";
        if (selectionId.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParse(selectionId[prefix.Length..], out id))
        {
            return true;
        }

        id = default;
        return false;
    }

    private static SoundPackAssetId ToAssetId(PreparedDiyAudio prepared) => new(prepared.AssetId);

}
