using Battuta.Engine.Input;
using Battuta.Windows.Stats.Models;
using Xunit;

namespace Battuta.Engine.Tests.Input;

public sealed class ForegroundApplicationCacheTests
{
    [Fact]
    public void UnknownSnapshotHasStableContract()
    {
        var snapshot = ForegroundApplicationSnapshot.Unknown;

        Assert.Equal(0, snapshot.ProcessId);
        Assert.Equal("process:unknown", snapshot.ProcessKey);
        Assert.Equal("未知应用", snapshot.DisplayName);
        Assert.Equal("unknown", snapshot.ProcessName);
    }

    [Fact]
    public void InitialCurrentIsUnknown()
    {
        var cache = new ForegroundApplicationCache();

        Assert.Equal(ForegroundApplicationSnapshot.Unknown, cache.Current);
        Assert.Equal(0, cache.Generation);
    }

    [Fact]
    public void TryPublishStoresSnapshotAndGeneration()
    {
        var cache = new ForegroundApplicationCache();
        var snapshot = new ForegroundApplicationSnapshot(
            ProcessId: 4242,
            ProcessKey: "app:org.example.editor",
            DisplayName: "org.example.Editor",
            ProcessName: "org.example.Editor");

        Assert.True(cache.TryPublish(7, snapshot));

        Assert.Equal(snapshot, cache.Current);
        Assert.Equal(7, cache.Generation);
    }

    [Fact]
    public void TryPublishRejectsStaleGeneration()
    {
        var cache = new ForegroundApplicationCache();
        var older = new ForegroundApplicationSnapshot(
            10, "app:foot", "foot", "foot");
        var newer = new ForegroundApplicationSnapshot(
            20, "app:kitty", "kitty", "kitty");

        Assert.True(cache.TryPublish(5, newer));
        Assert.False(cache.TryPublish(4, older));

        // A slow lookup from an older notification cannot overwrite a newer
        // result (the generation-aware contract).
        Assert.Equal(newer, cache.Current);
        Assert.Equal(5, cache.Generation);
    }

    [Fact]
    public void TryPublishAcceptsEqualGeneration()
    {
        var cache = new ForegroundApplicationCache();
        var first = new ForegroundApplicationSnapshot(10, "app:foot", "foot", "foot");
        var second = new ForegroundApplicationSnapshot(10, "app:foot", "foot", "foot");

        Assert.True(cache.TryPublish(3, first));
        Assert.True(cache.TryPublish(3, second));

        Assert.Equal(3, cache.Generation);
    }

    [Fact]
    public void TryPublishNullThrows()
    {
        var cache = new ForegroundApplicationCache();

        Assert.Throws<ArgumentNullException>(
            () => cache.TryPublish(1, null!));
    }

    [Fact]
    public void ToTypingApplicationIdentityMapsFields()
    {
        var snapshot = new ForegroundApplicationSnapshot(
            ProcessId: 7,
            ProcessKey: "app:testapp",
            DisplayName: "TestApp",
            ProcessName: "testapp");

        var identity = snapshot.ToTypingApplicationIdentity();

        Assert.Equal(new TypingApplicationIdentity(
            "app:testapp",
            "TestApp",
            "testapp"), identity);
        Assert.Null(identity.ApplicationUserModelId);
    }

    [Fact]
    public void UnknownSnapshotMapsToPredictableIdentity()
    {
        // The snapshot's "process:unknown" key intentionally differs from the
        // sink-side TypingApplicationIdentity.Unknown ("unknown"): the sink
        // normalizes null applications, not published unknowns.
        var identity = ForegroundApplicationSnapshot.Unknown.ToTypingApplicationIdentity();

        Assert.Equal(new TypingApplicationIdentity(
            "process:unknown",
            "未知应用",
            "unknown"), identity);
    }
}
