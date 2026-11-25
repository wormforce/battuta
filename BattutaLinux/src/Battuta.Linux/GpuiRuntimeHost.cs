using System.Reflection;
using System.Text.Json;
using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Core.SoundPacks;
using Battuta.Windows.Audio;
using Battuta.Windows.Audio.Linux;
using Battuta.Engine.Diy;
using Battuta.Windows.Diy.ViewModels;
using Battuta.Windows.Settings;
using Battuta.Windows.Stats.Models;
using Battuta.Windows.Updates;
using Battuta.Linux.Diy;
using Battuta.Linux.Platform.Paths;
using Battuta.Linux.Platform.SingleInstance;
using Battuta.Linux.Runtime;
using Battuta.Linux.ViewModels;
using Battuta.Linux.ViewModels.Stats;

namespace Battuta.Linux;

// One private stdin/stdout connection to the GPUI process. Only settings,
// aggregate statistics and editor data cross this boundary.
internal sealed class GpuiRuntimeHost(LinuxBattutaRuntime runtime) : IAsyncDisposable
{
    private const string DesktopFile = "io.github.wormforce.Battuta.desktop";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Version = typeof(GpuiRuntimeHost).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    private readonly MainViewModel main = new(runtime);
    private readonly StatsViewModel stats = new(runtime);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private DiySoundPackEditorViewModel? editor;
    private UpdateCheckService? updates;
    private string? releaseUrl;
    private string? updateStatus;
    private Task? updateTask;

    public static async Task RunAsync()
    {
        var output = Console.Out;
        Console.SetOut(Console.Error);
        try
        {
            var paths = LinuxAppPaths.ForCurrentProcess();
            paths.EnsureCreated();
            await using var instance = new SingleInstanceService(
                Environment.GetEnvironmentVariable("BATTUTA_INSTANCE_SOCKET") ?? paths.SingleInstanceSocket);
            instance.ActivationReceived += (_, _) => Write(output, new { activate = true });
            if (!await instance.TryAcquireAsync("show"))
            {
                Write(output, new { secondary = true });
                return;
            }

            Write(output, new { primary = true });

            await using var runtime = new LinuxBattutaRuntime(new LinuxRuntimeOptions
            {
                Paths = paths,
                BrokerSocketPath = Environment.GetEnvironmentVariable("BATTUTA_INPUT_SOCKET"),
            });
            await using var host = new GpuiRuntimeHost(runtime);
            await host.main.StartAsync();
            await host.stats.RefreshCommand.ExecuteAsync(null);
            if (runtime.Settings.AutomaticUpdateCheckPreference == AutomaticUpdateCheckPreference.Enabled)
            {
                host.BeginUpdate(UpdateCheckTrigger.Automatic);
            }

            while (await Console.In.ReadLineAsync() is { } line)
            {
                try
                {
                    using var request = JsonDocument.Parse(line);
                    Write(output, await host.ExecuteAsync(request.RootElement));
                }
                catch (Exception error)
                {
                    Write(output, new { error = error.Message });
                }
            }
        }
        catch (Exception error)
        {
            Write(output, new { fatal = error.Message });
        }
    }

    // Console.Out is synchronized. Write each complete message in one call because
    // activation can arrive on the socket listener while a command is in progress.
    private static void Write(TextWriter output, object message) =>
        output.WriteLine(JsonSerializer.Serialize(message, Json));

    private async Task<object> ExecuteAsync(JsonElement request)
    {
        var action = request.GetProperty("action").GetString()!;
        var value = request.TryGetProperty("value", out var supplied) ? supplied : default;
        var includeProfiles = action == "init";
        var includeStats = action.StartsWith("stats", StringComparison.Ordinal);
        var includeEditor = action.StartsWith("diy", StringComparison.Ordinal);
        object? report = null;
        switch (action)
        {
            case "init":
            case "poll":
                break;
            case "retry":
                main.RetryBroker();
                break;
            case "authorize":
                _ = main.RequestAuthorizationAsync();
                break;
            case "preview":
                main.PreviewKeyboardSound();
                break;
            case "previewPointer":
                runtime.AudioEngine.PlayPointer(PointerButton.Primary, PointerSoundPhase.Press,
                    runtime.Settings.PointerVolume, runtime.Settings.UsesPitchVariation);
                break;
            case "setting":
                var name = request.GetProperty("name").GetString()!;
                await ApplySettingAsync(name, value);
                break;
            case "checkUpdate":
                BeginUpdate(UpdateCheckTrigger.Manual);
                break;
            case "statsRange":
                await stats.SetRangeAsync(Enum.Parse<TypingTimelineRange>(value.GetString()!));
                break;
            case "statsScope":
                stats.ToggleHeatmapScopeCommand.Execute(null);
                break;
            case "statsRefresh":
                await stats.RefreshCommand.ExecuteAsync(null);
                break;
            case "statsClear":
                await stats.ConfirmClearCommand.ExecuteAsync(null);
                break;
            case "statsReport":
                var (year, previous) = TypingStatsReportRanges.Annual(DateOnly.FromDateTime(DateTime.Now));
                var annual = await runtime.StatisticsStore.LoadReportAsync(year, previous);
                report = new
                {
                    current = annual.Metrics.CharacterCount,
                    previous = annual.ComparisonMetrics?.CharacterCount ?? 0,
                    annual.Metrics.ActiveDayCount,
                    annual.Metrics.LongestActiveDayStreak,
                    days = annual.Days.Select(day => new { date = day.DateKey, day.CharacterCount, weekday = (int)day.Date.DayOfWeek }),
                    apps = annual.Applications.Take(12).Select(app => new
                    {
                        app.Application.DisplayName, app.CharacterCount, app.ComparisonCharacterCount,
                    }),
                };
                break;
            default:
                if (!includeEditor)
                {
                    throw new ArgumentException($"Unknown action {action}");
                }
                await ExecuteEditorAsync(action, request, value);
                includeProfiles = action is "diySave" or "diyDelete" or "diyImportPack";
                break;
        }

        var broker = runtime.Broker?.Status.Phase;
        return new
        {
            settings = runtime.Settings with
            {
                IsLaunchAtLoginEnabled = IsAutostartEnabled(),
            },
            profiles = includeProfiles ? main.Profiles : null,
            pointerProfiles = includeProfiles ? main.PointerProfiles : null,
            connected = broker == BrokerConnectionPhase.Connected,
            brokerPhase = broker?.ToString(),
            brokerDetail = runtime.Broker?.Status.Detail,
            audioReady = runtime.Audio.IsStreamReady,
            authorization = main.AuthorizationStatusText,
            soundPackError = runtime.SoundPackError,
            version = Version,
            updateStatus,
            releaseUrl,
            today = new { count = stats.TodayCharacterCount, peak = stats.TodayPeakCps, app = stats.TodayTopApplication },
            stats = includeStats ? StatsSnapshot() : null,
            report,
            diy = includeEditor ? EditorSnapshot(editor!) : null,
        };
    }

    private async Task ApplySettingAsync(string name, JsonElement value)
    {
        if (name == "selectedProfileId")
        {
            await runtime.ActivateSoundPackAsync(value.GetString()!);
            return;
        }

        if (name == "isLaunchAtLoginEnabled")
        {
            var enabled = value.GetBoolean();
            SetAutostart(enabled);
            runtime.UpdateSettings(settings => settings with { IsLaunchAtLoginEnabled = enabled });
            return;
        }

        runtime.UpdateSettings(settings => name switch
        {
            "isEnabled" => settings with { IsEnabled = value.GetBoolean() },
            "isPointerSoundEnabled" => settings with { IsPointerSoundEnabled = value.GetBoolean() },
            "isTypingStatsEnabled" => settings with { IsTypingStatsEnabled = value.GetBoolean() },
            "playsReleaseSound" => settings with { PlaysReleaseSound = value.GetBoolean() },
            "playsKeyRepeatSound" => settings with { PlaysKeyRepeatSound = value.GetBoolean() },
            "playsPointerReleaseSound" => settings with { PlaysPointerReleaseSound = value.GetBoolean() },
            "usesPitchVariation" => settings with { UsesPitchVariation = value.GetBoolean() },
            "volume" => settings with { Volume = value.GetDouble() },
            "pointerVolume" => settings with { PointerVolume = value.GetDouble() },
            "selectedPointerProfileId" => settings with { SelectedPointerProfileId = value.GetString()! },
            "automaticUpdateCheckPreference" => settings with
            {
                AutomaticUpdateCheckPreference = value.GetBoolean()
                    ? AutomaticUpdateCheckPreference.Enabled : AutomaticUpdateCheckPreference.Disabled,
            },
            _ => throw new ArgumentException($"Unknown setting {name}"),
        });
        if (name == "automaticUpdateCheckPreference" && value.GetBoolean())
        {
            BeginUpdate(UpdateCheckTrigger.Automatic);
        }
    }

    private string AutostartPath => Path.Combine(runtime.Paths.ConfigRoot, "autostart", DesktopFile);

    private string DesktopEntryPath
    {
        get
        {
            var userEntry = Path.Combine(runtime.Paths.DataRoot, "applications", DesktopFile);
            return File.Exists(userEntry) ? userEntry : Path.Combine("/usr/share/applications", DesktopFile);
        }
    }

    private bool IsAutostartEnabled() =>
        File.Exists(AutostartPath) && !File.ReadLines(AutostartPath).Any(line =>
            line is "Hidden=true" or "X-GNOME-Autostart-enabled=false");

    private void SetAutostart(bool enable)
    {
        var target = AutostartPath;
        if (!enable)
        {
            File.Delete(target);
            return;
        }

        var source = DesktopEntryPath;
        if (!File.Exists(source))
        {
            throw new InvalidOperationException("Install the Battuta Linux package to enable launch at login.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var pending = target + ".tmp";
        try
        {
            File.WriteAllLines(pending, File.ReadLines(source).Where(line => line is not
                ("Hidden=true" or "X-GNOME-Autostart-enabled=true" or "X-GNOME-Autostart-enabled=false"))
                .Append("X-GNOME-Autostart-enabled=true"));
            File.Move(pending, target, overwrite: true);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private void BeginUpdate(UpdateCheckTrigger trigger)
    {
        if (updateTask is { IsCompleted: false })
        {
            return;
        }
        updates ??= new UpdateCheckService(new GitHubReleaseClient(http, "Battuta-Linux/1.0", MatchesLinuxReleaseAsset),
            new JsonUpdateCacheStore(Path.Combine(runtime.Paths.TemporaryDirectory, "update-cache.json")),
            SemanticVersion.Parse(Version));
        updateTask = CheckUpdateAsync(updates, trigger);
    }

    internal static bool MatchesLinuxReleaseAsset(string name) =>
        name.StartsWith("Battuta-Linux-", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith("-linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase);

    private async Task CheckUpdateAsync(UpdateCheckService service, UpdateCheckTrigger trigger)
    {
        updateStatus = "正在检查更新…";
        try
        {
            var outcome = await service.CheckAsync(trigger);
            if (outcome.Report is { } report)
            {
                releaseUrl = report.Release.ReleaseUri.ToString();
            }
            updateStatus = outcome.Failure?.Message ?? outcome.Report switch
            {
                { Comparison: UpdateComparison.UpdateAvailable } latest => $"发现新版本 {latest.Release.Version}",
                { Comparison: UpdateComparison.UpToDate } => "当前已是最新版本",
                { Comparison: UpdateComparison.InstalledVersionIsNewer } => "当前版本比已发布版本更新",
                _ => "尚未检查更新",
            };
        }
        catch (Exception error)
        {
            updateStatus = error.Message;
        }
    }

    private object StatsSnapshot() => new
    {
        stats.TodayActiveMinutes,
        stats.TodayPhysicalPresses,
        stats.DataSourceText,
        stats.StatusMessage,
        range = stats.SelectedRange.ToString(),
        stats.RangeTitle,
        stats.RangeBucketDescription,
        stats.IsAllTimeHeatmapScope,
        stats.Bars,
        stats.Apps,
        stats.History,
        keys = KeyboardSnapshot(),
    };

    private object KeyboardSnapshot() => stats.Heatmap.Cells.Select(cell => new
    {
        cell.Id, cell.Label, key = cell.KeyId?.Value,
        cell.XUnits, cell.YUnits, cell.WidthUnits, cell.HeightUnits,
        cell.Count, cell.Intensity, extended = false,
    }).Concat(stats.ExtendedHeatmap.Cells.Select(cell => new
    {
        cell.Id, cell.Label, key = cell.KeyId?.Value,
        cell.XUnits, cell.YUnits, cell.WidthUnits, cell.HeightUnits,
        cell.Count, cell.Intensity, extended = true,
    })).ToArray();

    private async Task ExecuteEditorAsync(string action, JsonElement request, JsonElement value)
    {
        if (editor is null)
        {
            editor = new DiySoundPackEditorViewModel(runtime.SoundPackLibrary, runtime.Settings.SelectedProfileId,
                previewService: new LinuxDiyAudioPreviewService(runtime.AudioEngine, volumeProvider: () => runtime.Settings.Volume),
                builtInAudioLocator: new DiyBuiltInAudioLocator(
                    new BuiltInAudioResourceCatalog(loader: new PreparedAudioProductLoader())),
                decodePipeline: new LinuxDiyAudioDecodePipeline(),
                temporaryCacheParent: runtime.Paths.TemporaryDirectory,
                onLibraryChanged: async selection =>
                {
                    if (selection is not null)
                    {
                        await runtime.ActivateSoundPackAsync(selection);
                    }
                    await main.RefreshProfilesAsync();
                });
            await editor.LoadInitialStateAsync();
        }

        var phase = request.TryGetProperty("release", out var release) && release.GetBoolean()
            ? KeySoundPhase.Release : KeySoundPhase.Press;
        editor.ClearError();
        switch (action)
        {
            case "diyOpen": break;
            case "diyNew": await editor.CreateBlankAsync(); break;
            case "diyClone": await editor.CreateBasedOnInitialSelectionAsync(); break;
            case "diySelect": await editor.SelectPackAsync(value.GetGuid()); break;
            case "diySave": await editor.SaveAsync(value.GetBoolean()); break;
            case "diyDelete": await editor.DeleteSelectedPackAsync(); break;
            case "diyImportPack": await editor.ImportPackAsync(value.GetString()!); break;
            case "diyExportPack": await editor.ExportSelectedPackAsync(value.GetString()!); break;
            case "diyName": editor.SetName(value.GetString()!); break;
            case "diyAuthor": editor.SetAuthor(value.GetString()!); break;
            case "diyNotes": editor.SetNotes(value.GetString()!); break;
            case "diyMode": editor.MappingMode = Enum.Parse<DiyEditorMappingMode>(value.GetString()!); break;
            case "diySlot": editor.RecommendedSlot = ParseSlot(value.GetString()!); break;
            case "diyKey":
                editor.SelectedKey = new PhysicalKeyId(value.GetString()!);
                await editor.PreviewAsync(editor.SelectedKey, phase);
                break;
            case "diyAsset":
                editor.SetExistingAsset(value.ValueKind == JsonValueKind.Null ? null
                    : new SoundPackAssetId(value.GetString()!), editor.ActiveSlot, phase);
                break;
            case "diySilent":
                editor.SetOverrideChoice(DiyKeyOverrideChoice.Silent, editor.SelectedKey, phase);
                break;
            case "diyPreview": await editor.PreviewAsync(editor.ActiveSlot, phase); break;
            case "diyImportAudio":
                await editor.ImportAudioAsync(value.GetString()!, new DiyEditorAudioTarget(editor.ActiveSlot, phase));
                break;
            case "diyAnalyze": await editor.AnalyzeFullKeystrokeAsync(value.GetString()!, editor.ActiveSlot); break;
            case "diyCancelSplit": editor.CancelSplit(); break;
            case "diyPreviewSplit":
            case "diyConfirmSplit":
                if (editor.SplitDraft is { } split)
                {
                    var start = value.GetProperty("start").GetDouble();
                    var end = value.GetProperty("end").GetDouble();
                    if (action == "diyConfirmSplit")
                        await editor.ConfirmSplitAsync(split, start, end);
                    else
                        await editor.PreviewSplitAsync(split, start, end, phase);
                }
                break;
            default: throw new ArgumentException($"Unknown editor action {action}");
        }
    }

    private static DiyEditorSlot ParseSlot(string slot) => Enum.TryParse<KeyboardRowId>(slot, out var row)
        ? DiyEditorSlot.ForRow(row) : DiyEditorSlot.ForSpecial(Enum.Parse<KeyboardSpecialKeyId>(slot));

    private object EditorSnapshot(DiySoundPackEditorViewModel vm) => new
    {
        packs = vm.CustomPacks.Select(pack => new { id = pack.CustomPackId, pack.Name }),
        vm.SelectedPackId,
        vm.HasDraft,
        vm.IsDirty,
        vm.CanDelete,
        vm.CanExport,
        vm.StatusMessage,
        error = vm.Error?.Message,
        vm.Manifest?.Name,
        vm.Manifest?.Author,
        vm.Manifest?.Notes,
        selectedKey = vm.SelectedKey.Value,
        mode = vm.MappingMode.ToString(),
        slot = vm.RecommendedSlot.Row?.ToString() ?? vm.RecommendedSlot.Special?.ToString(),
        slotName = vm.ActiveSlot.DisplayName,
        assets = vm.AssetChoices.Select(asset => new { id = asset.Id.Value, name = vm.AssetLabel(asset.Id) }),
        press = Assignment(vm, KeySoundPhase.Press),
        release = Assignment(vm, KeySoundPhase.Release),
        keys = KeyboardSnapshot(),
        split = vm.SplitDraft is { } split ? new
        {
            split.Id,
            split.Analysis.DurationSeconds,
            start = split.Analysis.Suggestion.SplitTimeSeconds,
            end = split.Analysis.Suggestion.SuggestedReleaseEndTimeSeconds ?? split.Analysis.DurationSeconds,
            confidence = split.Analysis.Suggestion.Confidence,
            warnings = split.Analysis.Warnings.Select(warning => warning.ToString()),
            split.Analysis.Waveform,
        } : null,
    };

    private static string Assignment(DiySoundPackEditorViewModel vm, KeySoundPhase phase) =>
        vm.MappingMode == DiyEditorMappingMode.PerKey
            && vm.OverrideChoice(vm.SelectedKey, phase) == DiyKeyOverrideChoice.Silent
            ? "静音" : vm.AssetLabel(vm.AssignmentAsset(vm.ActiveSlot, phase));

    public async ValueTask DisposeAsync()
    {
        if (editor is not null)
        {
            await editor.DisposeAsync();
        }
        stats.Dispose();
        if (updateTask is { } task)
        {
            http.CancelPendingRequests();
            await task;
        }
        updates?.Dispose();
        http.Dispose();
    }
}
