using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Core.SoundPacks;
using Battuta.Windows.Audio;

namespace Battuta.Windows.Tests.Audio;

public sealed class KeyboardAudioEngineTests
{
    [Fact]
    public async Task KeyboardProfileLoadIsAtomicAndFailedReplacementKeepsPriorBank()
    {
        var root = Path.Combine(Path.GetTempPath(), $"battuta-bank-{Guid.NewGuid():N}");
        try
        {
            var holyPanda = SwitchProfileCatalog.Get(SwitchProfiles.HolyPanda);
            AudioTestFiles.CreateKeyboardProfile(root, holyPanda, [0.1f, 0.1f, 0.1f]);
            var mixer = new PolyphonicSampleProvider();
            var engine = new KeyboardAudioEngine(
                mixer,
                new BuiltInAudioResourceCatalog(root));

            Assert.True(await engine.LoadKeyboardProfileAsync(SwitchProfiles.HolyPanda));
            Assert.False(await engine.LoadKeyboardProfileAsync(SwitchProfiles.MxBrown));

            Assert.Equal(SwitchProfiles.HolyPanda, engine.LoadedKeyboardProfileId);
            Assert.NotNull(engine.KeyboardResourceError);
            Assert.True(engine.PlayKeyboard(
                PhysicalKeys.KeyA,
                KeySoundPhase.Press,
                volume: 0.5,
                variationEnabled: false));
            var output = new float[2];
            mixer.Read(output);
            Assert.Equal(0.05f, output[0], precision: 3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0.25, 0.05f)]
    [InlineData(1, 0.2f)]
    [InlineData(2, 0.2f)]
    public async Task PointerButtonsFallbackToPrimaryAndKeepIndependentVolume(double volume, float expected)
    {
        var root = Path.Combine(Path.GetTempPath(), $"battuta-pointer-bank-{Guid.NewGuid():N}");
        try
        {
            var classic = PointerSoundProfileCatalog.Default;
            AudioTestFiles.CreatePointerProfile(root, classic, [0.2f, 0.1f, 0f]);
            var mixer = new PolyphonicSampleProvider();
            var engine = new KeyboardAudioEngine(
                mixer,
                new BuiltInAudioResourceCatalog(root),
                new FixedPointerVariation(1.03f));
            Assert.True(await engine.LoadPointerProfileAsync(classic.Id));

            Assert.True(engine.PlayPointer(
                PointerButton.Secondary,
                PointerSoundPhase.Press,
                volume: volume,
                variationEnabled: true));
            var output = new float[2];
            mixer.Read(output);

            Assert.Equal(expected, output[0], precision: 3);
            Assert.Equal(output[0], output[1]);
            Assert.Equal(PointerSoundProfiles.Classic, engine.LoadedPointerProfileId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PointerUsesOnlyASpareVoiceWhileKeyboardMayStealTheOldestVoice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"battuta-shared-voice-{Guid.NewGuid():N}");
        try
        {
            var keyboardProfile = SwitchProfileCatalog.Default;
            var pointerProfile = PointerSoundProfileCatalog.Default;
            AudioTestFiles.CreateKeyboardProfile(
                root,
                keyboardProfile,
                Enumerable.Repeat(0.1f, 480).ToArray());
            AudioTestFiles.CreatePointerProfile(
                root,
                pointerProfile,
                Enumerable.Repeat(0.2f, 480).ToArray());
            var mixer = new PolyphonicSampleProvider(voiceCount: 1);
            var engine = new KeyboardAudioEngine(mixer, new BuiltInAudioResourceCatalog(root));
            Assert.True(await engine.LoadKeyboardProfileAsync(keyboardProfile.Id));
            Assert.True(await engine.LoadPointerProfileAsync(pointerProfile.Id));

            Assert.True(engine.PlayKeyboard(
                PhysicalKeys.KeyA,
                KeySoundPhase.Press,
                volume: 1,
                variationEnabled: false));
            mixer.Read(new float[2]);

            Assert.True(engine.PlayPointer(
                PointerButton.Primary,
                PointerSoundPhase.Press,
                volume: 1,
                variationEnabled: false));
            mixer.Read(new float[2]);
            Assert.Equal(1, mixer.DroppedCommandCount);
            Assert.Equal(0, mixer.VoiceStealCount);

            Assert.True(engine.PlayKeyboard(
                PhysicalKeys.KeyA,
                KeySoundPhase.Press,
                volume: 1,
                variationEnabled: false));
            mixer.Read(new float[2]);
            Assert.Equal(1, mixer.VoiceStealCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public async Task KeyboardVariationUsesPerSampleBalancedCycle(double volume)
    {
        var root = Path.Combine(Path.GetTempPath(), $"battuta-variant-bank-{Guid.NewGuid():N}");
        try
        {
            var profile = SwitchProfileCatalog.Default;
            AudioTestFiles.CreateKeyboardProfile(root, profile, [0.2f]);
            var mixer = new PolyphonicSampleProvider();
            var engine = new KeyboardAudioEngine(mixer, new BuiltInAudioResourceCatalog(root));
            Assert.True(await engine.LoadKeyboardProfileAsync(profile.Id));

            var observedFirstFrames = new List<float>();
            for (var index = 0; index < 4; index++)
            {
                Assert.True(engine.PlayKeyboard(
                    PhysicalKeys.KeyA,
                    KeySoundPhase.Press,
                    volume: volume,
                    variationEnabled: true));
                var output = new float[2];
                mixer.Read(output);
                observedFirstFrames.Add(output[0]);
                mixer.Read(new float[64]); // let a sub-1.0 rate variant finish before the next press
            }

            Assert.Equal(0.2f * (float)volume, observedFirstFrames[0], precision: 3);
            Assert.Equal(0.198f * (float)volume, observedFirstFrames[1], precision: 3); // recipe index 2
            Assert.Equal(0.195f * (float)volume, observedFirstFrames[2], precision: 3); // recipe index 1
            Assert.Equal(0.204f * (float)volume, observedFirstFrames[3], precision: 3); // recipe index 3
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2)]
    public async Task CustomPackIsPreloadedAndFallsBackToItsBuiltInBaseProfile(double volume)
    {
        var root = Path.Combine(Path.GetTempPath(), $"battuta-custom-bank-{Guid.NewGuid():N}");
        try
        {
            var baseProfile = SwitchProfileCatalog.Default;
            AudioTestFiles.CreateKeyboardProfile(root, baseProfile, Enumerable.Repeat(0.1f, 480).ToArray());
            var assetId = new SoundPackAssetId(new string('a', 64));
            var assetPath = Path.Combine(root, "custom", assetId.Value + ".wav");
            AudioTestFiles.WriteMonoPcm16Wave(assetPath, Enumerable.Repeat(0.6f, 480).ToArray());
            var packId = Guid.NewGuid();
            var manifest = new SoundPackManifest
            {
                Id = packId,
                Name = "Custom audio test",
                BaseProfileId = baseProfile.Id.Value,
                Press = new SoundPackPhaseAssignments { Generic = assetId },
                Assets = new Dictionary<string, SoundPackAudioAsset>(StringComparer.Ordinal)
                {
                    [assetId.Value] = new SoundPackAudioAsset
                    {
                        Id = assetId,
                        RelativePath = $"assets/{assetId.Value}.wav",
                        Sha256 = assetId.Value,
                        DurationSeconds = 0.01,
                        SampleRate = 48_000,
                        ChannelCount = 1,
                        ByteCount = new FileInfo(assetPath).Length,
                    },
                },
            };
            var mixer = new PolyphonicSampleProvider();
            var engine = new KeyboardAudioEngine(mixer, new BuiltInAudioResourceCatalog(root));

            Assert.True(await engine.LoadCustomSoundPackAsync(manifest, _ => assetPath));
            Assert.Equal($"custom:{packId:D}".ToLowerInvariant(), engine.LoadedKeyboardSelectionId);

            Assert.True(engine.PlayKeyboard(
                PhysicalKeys.KeyA,
                KeySoundPhase.Press,
                volume: volume,
                variationEnabled: false));
            var customOutput = new float[2];
            mixer.Read(customOutput);
            Assert.Equal(Math.Min(0.6f * (float)volume, 1f), customOutput[0], precision: 3);
            mixer.Read(new float[2_000]);

            Assert.True(engine.PlayKeyboard(
                PhysicalKeys.KeyA,
                KeySoundPhase.Release,
                volume: volume,
                variationEnabled: false));
            var fallbackOutput = new float[2];
            mixer.Read(fallbackOutput);
            Assert.Equal(0.1f * (float)volume, fallbackOutput[0], precision: 3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CustomExplicitSilenceBlocksBuiltInFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"battuta-silent-bank-{Guid.NewGuid():N}");
        try
        {
            var baseProfile = SwitchProfileCatalog.Default;
            AudioTestFiles.CreateKeyboardProfile(root, baseProfile, Enumerable.Repeat(0.1f, 480).ToArray());
            var press = new SoundPackPhaseAssignments();
            Assert.True(press.TrySetOverride(PhysicalKeys.KeyA, SoundPackKeyOverride.Silent));
            var manifest = new SoundPackManifest
            {
                Name = "Silent key test",
                BaseProfileId = baseProfile.Id.Value,
                Press = press,
            };
            var mixer = new PolyphonicSampleProvider();
            var engine = new KeyboardAudioEngine(mixer, new BuiltInAudioResourceCatalog(root));
            Assert.True(await engine.LoadCustomSoundPackAsync(
                manifest,
                _ => throw new InvalidOperationException("No asset should be requested.")));

            Assert.False(engine.PlayKeyboard(
                PhysicalKeys.KeyA,
                KeySoundPhase.Press,
                volume: 1,
                variationEnabled: false));
            var output = new float[2];
            mixer.Read(output);
            Assert.Equal([0f, 0f], output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedPointerVariation(float value) : IPointerPitchVariationSource
    {
        public float NextMultiplier() => value;
    }
}
