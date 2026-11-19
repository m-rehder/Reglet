/// <summary>
/// minimal test runner without a test framework or PackageReference. Runs registered
/// check suites, reports each error and returns an exit code.
/// </summary>
internal sealed class TestRunner
{
    private readonly List<(string Name, Func<int> Run)> _checks = [];

    public TestRunner Add(string name, Func<int> run)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(run);
        _checks.Add((name, run));
        return this;
    }

    public int Run()
    {
        var failures = 0;

        foreach (var (name, run) in _checks)
        {
            Console.WriteLine($"=== {name} ===");
            try
            {
                if (run() != 0)
                {
                    failures++;
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"{name}: {exception}");
                failures++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"All {_checks.Count} check suites passed."
            : $"{failures} of {_checks.Count} check suites failed.");

        return failures == 0 ? 0 : 1;
    }
}
