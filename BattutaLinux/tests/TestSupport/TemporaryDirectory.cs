namespace Battuta.TestSupport;

/// <summary>
/// Owns an isolated temporary directory with a convenient
/// <see cref="Combine"/> helper for fixture-relative paths.
/// </summary>
public sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"Battuta-Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] components) =>
        components.Aggregate(Path, System.IO.Path.Combine);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
