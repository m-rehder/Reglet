namespace Reglet;

/// <summary>Finds the repository root directory and the golden directory.</summary>
internal static class GoldenPaths
{
    public static string RepositoryRoot { get; } = FindRoot();

    public static string Directory { get; } = EnsureDirectory();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Reglet.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return System.IO.Directory.GetCurrentDirectory();
    }

    private static string EnsureDirectory()
    {
        var path = Path.Combine(RepositoryRoot, "tests", "Reglet.CompatibilityTests", "fixtures", "golden");
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
