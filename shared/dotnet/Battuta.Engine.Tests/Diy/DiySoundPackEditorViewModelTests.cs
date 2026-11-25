using Battuta.TestSupport;
using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Core.SoundPacks;
using Battuta.Windows.Diy.Packages;
using Battuta.Windows.Diy.ViewModels;

namespace Battuta.Engine.Tests.Diy;

public sealed class DiySoundPackEditorViewModelTests
{
    [Fact]
    public async Task EditorCreatesImportsSavesAndEnablesDraft()
    {
        using var root = new TemporaryDirectory();
        using var library = new DiySoundPackLibrary(
            root.Combine("library"),
            builtInDescriptors: SoundPackDescriptors.BundledDefaults);
        var callbacks = new List<string?>();
        await using var editor = new DiySoundPackEditorViewModel(
            library,
            SwitchProfiles.MxBrown.Value,
            temporaryCacheParent: root.Path,
            onLibraryChanged: selectionId =>
            {
                callbacks.Add(selectionId);
                return Task.CompletedTask;
            });

        await editor.LoadInitialStateAsync();

        Assert.NotNull(editor.Manifest);
        Assert.Equal(SwitchProfiles.MxBrown.Value, editor.Manifest.BaseProfileId);
        Assert.True(editor.IsDirty);
        editor.SetName("Windows DIY");

        var source = WaveFixture.WriteStereoSine(root.Combine("sample.wav"));
        await editor.ImportAudioAsync(
            source,
            new DiyEditorAudioTarget(DiyEditorSlot.Generic, KeySoundPhase.Press));
        await editor.ImportAudioAsync(
            source,
            new DiyEditorAudioTarget(DiyEditorSlot.Generic, KeySoundPhase.Release));

        Assert.NotNull(editor.AssignmentAsset(DiyEditorSlot.Generic, KeySoundPhase.Press));
        Assert.Single(editor.AssetChoices);
        await editor.SaveAsync(enableAfterSaving: true);

        Assert.False(editor.IsDirty);
        Assert.True(editor.CanExport);
        Assert.NotNull(editor.SelectedPackId);
        Assert.Single(editor.CustomPacks);
        Assert.Equal(editor.CustomPacks[0].SelectionId, callbacks.Single());
        Assert.False(editor.HasTemporaryAudioResources);

        editor.SetOverrideChoice(DiyKeyOverrideChoice.Silent, PhysicalKeys.KeyA, KeySoundPhase.Press);
        Assert.Equal(
            DiyKeyOverrideChoice.Silent,
            editor.OverrideChoice(PhysicalKeys.KeyA, KeySoundPhase.Press));
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task EditorAnalyzesAndConfirmsCompleteKeystroke()
    {
        using var root = new TemporaryDirectory();
        using var library = new DiySoundPackLibrary(
            root.Combine("library"),
            builtInDescriptors: SoundPackDescriptors.BundledDefaults);
        await using var editor = new DiySoundPackEditorViewModel(
            library,
            SwitchProfiles.HolyPanda.Value,
            temporaryCacheParent: root.Path);
        await editor.LoadInitialStateAsync();
        var complete = WaveFixture.WriteCompleteKeystroke(root.Combine("complete.wav"));

        await editor.AnalyzeFullKeystrokeAsync(complete, DiyEditorSlot.ForRow(KeyboardRowId.R2));

        var draft = Assert.IsType<DiySplitDraft>(editor.SplitDraft);
        var succeeded = await editor.ConfirmSplitAsync(
            draft,
            draft.Analysis.DurationSeconds / 2,
            draft.Analysis.DurationSeconds);

        Assert.True(succeeded);
        Assert.Null(editor.SplitDraft);
        Assert.NotNull(editor.AssignmentAsset(
            DiyEditorSlot.ForRow(KeyboardRowId.R2),
            KeySoundPhase.Press));
        Assert.NotNull(editor.AssignmentAsset(
            DiyEditorSlot.ForRow(KeyboardRowId.R2),
            KeySoundPhase.Release));
        Assert.True(editor.IsDirty);
        Assert.True(await editor.PrepareForClosingAsync());
        Assert.False(editor.HasTemporaryAudioResources);
    }
}
