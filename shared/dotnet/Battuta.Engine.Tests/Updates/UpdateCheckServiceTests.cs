using Battuta.Windows.Updates;

namespace Battuta.Windows.Tests.Platform.Updates;

public sealed class UpdateCheckServiceTests
{
    [Fact]
    public async Task ModifiedReleaseIsCachedAndCompared()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero));
        var release = GitHubReleaseSummary.Create(
            "v1.1.0",
            new Uri("https://github.com/wormforce/battuta/releases/tag/v1.1.0"),
            time.GetUtcNow());
        var client = new QueueReleaseClient(
            new ReleaseFetchResult.Modified(
                release,
                new GitHubRateLimit(59, null),
                "\"etag-one\""));
        var cache = new MemoryUpdateCacheStore();
        using var service = new UpdateCheckService(
            client,
            cache,
            SemanticVersion.Parse("1.0.0"),
            time);

        var outcome = await service.CheckAsync(UpdateCheckTrigger.Manual);

        Assert.Null(outcome.Failure);
        Assert.Equal(UpdateComparison.UpdateAvailable, outcome.Report?.Comparison);
        Assert.Equal("\"etag-one\"", cache.Value.ETag);
        Assert.Equal("v1.1.0", cache.Value.LatestTagName);
    }

    [Fact]
    public async Task RepeatedManualCheckIsThrottledWithoutNetworkCall()
    {
        var now = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var cache = new MemoryUpdateCacheStore
        {
            Value = new UpdateCacheSnapshot { LastAttemptAt = now },
        };
        var client = new QueueReleaseClient();
        using var service = new UpdateCheckService(
            client,
            cache,
            SemanticVersion.Parse("1.0.0"),
            time);

        var outcome = await service.CheckAsync(UpdateCheckTrigger.Manual);

        Assert.Equal(UpdateCheckFailureKind.RequestedTooSoon, outcome.Failure?.Kind);
        Assert.Equal(0, client.CallCount);
        Assert.Equal(now.AddSeconds(65), outcome.Failure?.RetryAt);
    }

    [Fact]
    public async Task InvalidLegacyCachedReleaseForcesFullRefreshInsteadOfSendingETag()
    {
        var now = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var canonicalRelease = GitHubReleaseSummary.Create(
            "v1.2.0",
            new Uri("https://github.com/wormforce/battuta/releases/tag/v1.2.0"),
            now);
        var client = new QueueReleaseClient(
            new ReleaseFetchResult.Modified(
                canonicalRelease,
                new GitHubRateLimit(59, null),
                "\"canonical-etag\""));
        var cache = new MemoryUpdateCacheStore
        {
            Value = new UpdateCacheSnapshot
            {
                ETag = "\"legacy-etag\"",
                LatestTagName = "v1.1.1",
                LatestReleaseUri = new Uri(
                    "https://github.com/7b7b7b/battuta/releases/tag/v1.1.1"),
                LatestPublishedAt = now.AddDays(-3),
                LastSuccessfulCheckAt = now.AddDays(-3),
            },
        };
        using var service = new UpdateCheckService(
            client,
            cache,
            SemanticVersion.Parse("1.2.0"),
            time);

        var outcome = await service.CheckAsync(UpdateCheckTrigger.Manual);

        Assert.Null(outcome.Failure);
        Assert.Equal(UpdateComparison.UpToDate, outcome.Report?.Comparison);
        Assert.Null(client.LastETag);
        Assert.Equal("\"canonical-etag\"", cache.Value.ETag);
        Assert.Equal(canonicalRelease.ReleaseUri, cache.Value.LatestReleaseUri);
    }

    [Fact]
    public async Task ValidCachedReleaseKeepsConditionalRequestBehavior()
    {
        var now = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var releaseUri = new Uri(
            "https://github.com/wormforce/battuta/releases/tag/v1.2.0");
        var client = new QueueReleaseClient(
            new ReleaseFetchResult.NotModified(
                new GitHubRateLimit(59, null),
                "\"canonical-etag\""));
        var cache = new MemoryUpdateCacheStore
        {
            Value = new UpdateCacheSnapshot
            {
                ETag = "\"canonical-etag\"",
                LatestTagName = "v1.2.0",
                LatestReleaseUri = releaseUri,
                LatestPublishedAt = now.AddDays(-2),
                LastSuccessfulCheckAt = now.AddDays(-2),
            },
        };
        using var service = new UpdateCheckService(
            client,
            cache,
            SemanticVersion.Parse("1.2.0"),
            time);

        var outcome = await service.CheckAsync(UpdateCheckTrigger.Manual);

        Assert.Null(outcome.Failure);
        Assert.Equal(UpdateComparison.UpToDate, outcome.Report?.Comparison);
        Assert.Equal("\"canonical-etag\"", client.LastETag);
        Assert.Equal(releaseUri, outcome.Report?.Release.ReleaseUri);
    }

    private sealed class QueueReleaseClient(params ReleaseFetchResult[] responses) : IReleaseClient
    {
        private readonly Queue<ReleaseFetchResult> _responses = new(responses);

        public int CallCount { get; private set; }

        public string? LastETag { get; private set; }

        public Task<ReleaseFetchResult> FetchLatestAsync(
            string? etag,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastETag = etag;
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake release response was configured.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class MemoryUpdateCacheStore : IUpdateCacheStore
    {
        public UpdateCacheSnapshot Value { get; set; } = new();

        public Task<UpdateCacheSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Value);

        public Task SaveAsync(
            UpdateCacheSnapshot cache,
            CancellationToken cancellationToken = default)
        {
            Value = cache;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
