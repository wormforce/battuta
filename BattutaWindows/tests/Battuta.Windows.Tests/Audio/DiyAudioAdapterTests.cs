using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Windows.Audio;

namespace Battuta.Windows.Tests.Audio;

public sealed class DiyAudioAdapterTests
{
    [Theory]
    [InlineData(0, 0f)]
    [InlineData(0.5, 0.1f)]
    [InlineData(1.5, 0.3f)]
    [InlineData(2, 0.4f)]
    [InlineData(3, 0.4f)]
    public async Task PreviewDecodesOffHotPathThenUsesSharedMixer(double volume, float expected)
    {
        var root = Path.Combine(Path.GetTempPath(), $"battuta-preview-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "preview.wav");
        try
        {
            AudioTestFiles.WriteMonoPcm16Wave(path, [0.2f, 0.1f]);
            var mixer = new PolyphonicSampleProvider();
            var engine = new KeyboardAudioEngine(mixer);
            var preview = new DiyAudioPreviewService(engine, volumeProvider: () => volume);

            await preview.PreviewAsync(path);
            var output = new float[2];
            mixer.Read(output);

            Assert.Equal(expected, output[0], precision: 3);
            Assert.Equal(output[0], output[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuiltInLocatorUsesTheSameSpecialKeyMappingAsPlayback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"battuta-locator-{Guid.NewGuid():N}");
        try
        {
            var profile = SwitchProfileCatalog.Default;
            AudioTestFiles.CreateKeyboardProfile(root, profile, [0.1f]);
            var locator = new DiyBuiltInAudioLocator(new BuiltInAudioResourceCatalog(root));

            var path = locator.FindAudio(
                profile.Id.Value,
                PhysicalKeys.Space,
                KeySoundPhase.Press);

            Assert.NotNull(path);
            Assert.EndsWith(Path.Combine("press", "SPACE.wav"), path, StringComparison.OrdinalIgnoreCase);
            Assert.Null(locator.FindAudio("unknown-profile", PhysicalKeys.Space, KeySoundPhase.Press));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
