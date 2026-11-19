namespace Reglet;

/// <summary>
/// reports unsupported features according to <see cref="UnsupportedContentBehavior"/>:
/// either collected as a warning or raised as an error. Silent data loss is
/// thereby excluded.
/// </summary>
internal sealed class UnsupportedFeatureHandler
{
    private readonly List<WriteWarning> _warnings;

    public UnsupportedFeatureHandler(UnsupportedContentBehavior behavior, List<WriteWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        Behavior = behavior;
        _warnings = warnings;
    }

    public UnsupportedContentBehavior Behavior { get; }

    public void Report(string message)
    {
        if (Behavior == UnsupportedContentBehavior.Fail)
        {
            throw new UnsupportedFeatureException(message);
        }

        _warnings.Add(new WriteWarning(message));
    }
}
