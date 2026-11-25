using System.IO;

namespace Battuta.TestSupport;

/// <summary>Locates the repository root from any test output directory.</summary>
public static class RepositoryTestFiles
{
    public static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "shared")) &&
                    Directory.Exists(Path.Combine(current.FullName, "BattutaWindows")) &&
                    Directory.Exists(Path.Combine(current.FullName, "BattutaLinux")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Battuta repository root.");
    }
}
