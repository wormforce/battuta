using Battuta.Windows.Audio;
using Battuta.TestSupport.IO;

namespace Battuta.Engine.Tests.Audio;

public sealed class LeadingSilenceTrimmerTests
{
    [Fact]
    public void DelayedOnsetIsAlignedWithTinyPreroll()
    {
        var source = new float[4_096];
        source[960] = 0.5f;

        var trimmed = LeadingSilenceTrimmer.Trim(source);

        Assert.True(trimmed.Length < source.Length);
        var firstAudible = Array.FindIndex(
            trimmed,
            value => MathF.Abs(value) >= LeadingSilenceTrimmer.SilenceThreshold);
        Assert.InRange(firstAudible, 0, 16);
        Assert.Equal(0.5f, trimmed[firstAudible]);
    }

    [Fact]
    public void AlreadyAlignedSampleIsReturnedWithoutCopyOrShift()
    {
        var source = new float[4_096];
        source[4] = 0.5f;

        var trimmed = LeadingSilenceTrimmer.Trim(source);

        Assert.Same(source, trimmed);
        Assert.Equal(0.5f, trimmed[4]);
    }

    [Fact]
    public void EntirelySilentSampleIsNotDiscarded()
    {
        var source = new float[128];

        var trimmed = LeadingSilenceTrimmer.Trim(source);

        Assert.Same(source, trimmed);
        Assert.Equal(source.Length, trimmed.Length);
    }

    [Fact]
    public void OnsetBeyondScanWindowIsNotTrimmed()
    {
        var source = new float[20_000];
        source[13_000] = 0.5f;

        var trimmed = LeadingSilenceTrimmer.Trim(source);

        Assert.Same(source, trimmed);
    }
}
