using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Reglet;

/// <summary>A single difference between two content models, with its path.</summary>
internal readonly record struct ModelDifference(string Path, string Expected, string Actual)
{
    public override string ToString() => $"{Path}: expected {Expected}, actual {Actual}";
}

/// <summary>
/// AF01: canonical, deep comparison of two <see cref="PortableDocument"/> models. It walks the
/// whole public model surface (every block, inline, style and metadata type — including the
/// properties the ad-hoc round-trip description never looked at) and reports differences per
/// path. Reflection lives in the test project on purpose; the shipped library stays
/// reflection-free.
/// </summary>
internal static class DocumentComparer
{
    /// <summary>Returns the differences between two models; empty means semantically equal.</summary>
    public static IReadOnlyList<ModelDifference> Compare(PortableDocument expected, PortableDocument actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var left = ToDictionary(expected);
        var right = ToDictionary(actual);
        var paths = left.Keys
            .Union(right.Keys, StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal);

        var differences = new List<ModelDifference>();
        foreach (var path in paths)
        {
            left.TryGetValue(path, out var expectedValue);
            right.TryGetValue(path, out var actualValue);
            if (!string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
            {
                differences.Add(new ModelDifference(path, expectedValue ?? "<missing>", actualValue ?? "<missing>"));
            }
        }

        return differences;
    }

    /// <summary>Canonical, culture-independent dump of a model (one <c>path = value</c> per line).</summary>
    public static string Describe(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return string.Join('\n', Flatten(document).Select(entry => entry.Path + " = " + entry.Value));
    }

    private static Dictionary<string, string> ToDictionary(PortableDocument document)
    {
        // DOCX and ODT do not carry the numbering name; the reader derives it from the numbering
        // id or the list order. Names are therefore compared by their position in the definition
        // list, not literally.
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var position = 0; position < document.Numbering.Count; position++)
        {
            index[document.Numbering[position].Name] = position;
        }

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, value) in Flatten(document))
        {
            if (path.EndsWith(".NumberingName", StringComparison.Ordinal)
                && index.TryGetValue(value.Trim('\''), out var position))
            {
                entries[path] = Quote("#" + position.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            var name = value.Trim('\'');
            entries[path] = path.StartsWith("Document.Numbering[", StringComparison.Ordinal)
                && path.EndsWith("].Name", StringComparison.Ordinal)
                && index.TryGetValue(name, out var ownPosition)
                    ? Quote("#" + ownPosition.ToString(CultureInfo.InvariantCulture))
                    : value;
        }

        return entries;
    }

    private static List<(string Path, string Value)> Flatten(PortableDocument document)
    {
        var output = new List<(string, string)>();
        Walk(document, typeof(PortableDocument), "Document", output);
        return output;
    }

    private static void Walk(object? value, Type declaredType, string path, List<(string, string)> output)
    {
        if (value is null)
        {
            output.Add((path, "null"));
            return;
        }

        var runtime = value.GetType();
        switch (value)
        {
            case string text:
                output.Add((path, Quote(text)));
                return;
            case bool boolean:
                output.Add((path, boolean ? "true" : "false"));
                return;
            case Enum enumeration:
                output.Add((path, enumeration.ToString()));
                return;
            case Length length:
                output.Add((path, Number(length.Points)));
                return;
            case Color color:
                output.Add((path, color.ToString()));
                return;
            case DateTimeOffset dateTimeOffset:
                output.Add((path, dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
                return;
            case DateTime dateTime:
                output.Add((path, dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
                return;
            case TimeSpan timeSpan:
                output.Add((path, timeSpan.ToString("c", CultureInfo.InvariantCulture)));
                return;
            case byte[] bytes:
                output.Add((path, Hash(bytes)));
                return;
            case ReadOnlyMemory<byte> readOnlyMemory:
                output.Add((path, Hash(readOnlyMemory.Span)));
                return;
            case Memory<byte> memory:
                output.Add((path, Hash(memory.Span)));
                return;
            case Guid guid:
                output.Add((path, guid.ToString("D")));
                return;
        }

        if ((value is IConvertible && runtime.IsPrimitive) || value is decimal)
        {
            output.Add((path, Number(value)));
            return;
        }

        if (value is IEnumerable enumerable)
        {
            var elementType = ElementType(declaredType);
            var index = 0;
            foreach (var item in enumerable)
            {
                var child = path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                if (elementType is { IsAbstract: true })
                {
                    child += "(" + (item?.GetType().Name ?? "null") + ")";
                }

                Walk(item, elementType ?? typeof(object), child, output);
                index++;
            }

            return;
        }

        if (runtime.Namespace is { } ns && ns.StartsWith("Reglet", StringComparison.Ordinal))
        {
            foreach (var property in runtime
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod is not null)
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                var child = path + "." + property.Name;
                object? propertyValue;
                try
                {
                    propertyValue = property.GetValue(value);
                }
                catch (Exception exception)
                {
                    output.Add((child, "<error:" + exception.GetType().Name + ">"));
                    continue;
                }

                Walk(propertyValue, property.PropertyType, child, output);
            }

            return;
        }

        output.Add((path, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"));
    }

    /// <summary>Element type of a list/array property, used to annotate polymorphic block/inline nodes.</summary>
    private static Type? ElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        foreach (var candidate in type.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return candidate.GetGenericArguments()[0];
            }
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return type.GetGenericArguments()[0];
        }

        return null;
    }

    private static string Quote(string value)
        => "'" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + "'";

    private static string Number(object value) => value switch
    {
        double number => Number(number),
        float number => Number(number),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "null",
        _ => value.ToString() ?? "null",
    };

    private static string Number(double value)
        => value == 0 ? "0" : value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Hash(ReadOnlySpan<byte> data)
        => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data)) + "(" + data.Length.ToString(CultureInfo.InvariantCulture) + ")";
}
