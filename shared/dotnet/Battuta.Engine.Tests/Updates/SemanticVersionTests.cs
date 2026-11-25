using Battuta.Windows.Updates;

namespace Battuta.Windows.Tests.Platform.Updates;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("1.2.3-alpha.1+build.9", "1.2.3-alpha.1+build.9")]
    public void ValidVersionRoundTrips(string source, string expected)
    {
        Assert.True(SemanticVersion.TryParse(source, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0+")]
    public void InvalidVersionIsRejected(string source)
    {
        Assert.False(SemanticVersion.TryParse(source, out _));
    }

    [Fact]
    public void ReleaseSortsAfterPrereleaseAndIgnoresBuildMetadataForEquality()
    {
        var prerelease = SemanticVersion.Parse("1.0.0-rc.1");
        var release = SemanticVersion.Parse("1.0.0+windows.23");
        var alternateBuild = SemanticVersion.Parse("1.0.0+mac.23");

        Assert.True(release > prerelease);
        Assert.Equal(release, alternateBuild);
    }
}
