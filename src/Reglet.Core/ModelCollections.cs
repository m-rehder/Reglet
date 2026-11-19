namespace Reglet;

internal static class ModelCollections
{
    internal static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);

        var items = source.ToArray();
        if (items.Any(item => item is null))
        {
            throw new ArgumentException("The collection must not contain null elements.", parameterName);
        }

        return Array.AsReadOnly(items);
    }
}
