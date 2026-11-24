namespace Reglet.Mcp;

/// <summary>
/// The directories the server may read and write (ADR-0045): the <c>--root</c> arguments, or the working directory.
/// Every path a tool gets is resolved against the first root, made absolute, and must lie inside a root after
/// following symbolic links, so that neither <c>..</c> nor a link leads out.
/// </summary>
public sealed class PathPolicy
{
    private readonly IReadOnlyList<string> _roots;

    /// <summary>Creates the policy for the given roots (at least one).</summary>
    /// <exception cref="ArgumentException">There is no root or a root does not exist.</exception>
    public PathPolicy(IEnumerable<string> roots)
    {
        _roots = roots.Select(root => Normalize(Real(Path.GetFullPath(root)))).Distinct(StringComparer.Ordinal).ToList();
        if (_roots.Count == 0)
        {
            throw new ArgumentException("The server needs at least one root directory.", nameof(roots));
        }

        foreach (var root in _roots.Where(root => !Directory.Exists(root)))
        {
            throw new ArgumentException($"The root directory '{root}' does not exist.", nameof(roots));
        }
    }

    /// <summary>The allowed directories, for the server's description.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>A file to read: it must exist inside a root.</summary>
    public string Existing(string path)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
        {
            throw new ModelContextProtocol.McpException($"The file '{path}' does not exist.");
        }

        return full;
    }

    /// <summary>A file to write: its folder must exist inside a root; an existing file is replaced only when allowed.</summary>
    public string Writable(string path, bool overwrite)
    {
        var full = Resolve(path);
        if (Directory.Exists(full))
        {
            throw new ModelContextProtocol.McpException($"'{path}' is a directory.");
        }

        if (File.Exists(full) && !overwrite)
        {
            throw new ModelContextProtocol.McpException($"'{path}' exists; pass overwrite: true to replace it.");
        }

        var folder = Path.GetDirectoryName(full);
        if (folder is null || !Directory.Exists(folder))
        {
            throw new ModelContextProtocol.McpException($"The folder of '{path}' does not exist.");
        }

        return full;
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ModelContextProtocol.McpException("A path is required.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(path, _roots[0]);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ModelContextProtocol.McpException($"'{path}' is not a valid path.");
        }

        // The file and every folder above it are followed through links before the check, a link whose target does not
        // exist yet included (writing to it would create the target).
        var real = Real(full);
        if (!_roots.Any(root => IsInside(Normalize(real), root)))
        {
            throw new ModelContextProtocol.McpException($"'{path}' is outside the directories this server may use ({string.Join(", ", _roots)}).");
        }

        return real;
    }

    /// <summary>The final target of a path whose parts may be symbolic links (missing parts stay as they are).</summary>
    private static string Real(string path)
    {
        var parent = Path.GetDirectoryName(path);
        var resolvedParent = parent is null ? null : Real(parent);
        var current = resolvedParent is null ? path : Path.Combine(resolvedParent, Path.GetFileName(path));
        try
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                // The final target, also when it does not exist (then the chain ends at the missing path).
                var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(current) ?? current);
                return Path.GetFullPath(target);
            }
        }
        catch (IOException)
        {
            // A link that cannot be resolved is checked as written.
        }

        return current;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path);

    private static bool IsInside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
