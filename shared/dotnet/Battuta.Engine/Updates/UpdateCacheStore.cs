using System.Text.Json;
using System.IO;

namespace Battuta.Windows.Updates;

public sealed record UpdateCacheSnapshot
{
    public string? ETag { get; init; }
    public string? LatestTagName { get; init; }
    public Uri? LatestReleaseUri { get; init; }
    public DateTimeOffset? LatestPublishedAt { get; init; }
    public DateTimeOffset? LastSuccessfulCheckAt { get; init; }
    public DateTimeOffset? LastFailedCheckAt { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? RateLimitedUntil { get; init; }

    public GitHubReleaseSummary? TryGetRelease()
    {
        if (LatestTagName is null || LatestReleaseUri is null)
        {
            return null;
        }

        try
        {
            return GitHubReleaseSummary.Create(LatestTagName, LatestReleaseUri, LatestPublishedAt);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

public interface IUpdateCacheStore
{
    Task<UpdateCacheSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UpdateCacheSnapshot cache, CancellationToken cancellationToken = default);
}

public sealed class JsonUpdateCacheStore : IUpdateCacheStore, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public JsonUpdateCacheStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<UpdateCacheSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new UpdateCacheSnapshot();
            try
            {
                await using var stream = File.OpenRead(_path);
                return await JsonSerializer.DeserializeAsync<UpdateCacheSnapshot>(
                    stream,
                    _options,
                    cancellationToken).ConfigureAwait(false) ?? new UpdateCacheSnapshot();
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                return new UpdateCacheSnapshot();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        UpdateCacheSnapshot cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                8192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, cache, _options, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }

            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
