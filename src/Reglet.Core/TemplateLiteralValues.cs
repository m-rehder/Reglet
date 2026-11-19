using System.Globalization;

namespace Reglet;

/// <summary>Keeps repeated-row values opaque until template processing has finished.</summary>
internal sealed class TemplateLiteralValues
{
    private readonly string _prefix = "\uE000" + Guid.NewGuid().ToString("N") + ":";
    private readonly List<string> _values = [];

    public string Protect(string value)
    {
        var token = _prefix + _values.Count.ToString(CultureInfo.InvariantCulture) + "\uE001";
        _values.Add(value);
        return token;
    }

    public long RestoredLength(string text)
    {
        long length = text.Length;
        var start = text.IndexOf(_prefix, StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = text.IndexOf('\uE001', start + _prefix.Length);
            if (end < 0 || !int.TryParse(text.AsSpan(start + _prefix.Length, end - start - _prefix.Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= _values.Count)
            {
                break;
            }

            length += (long)_values[index].Length - (end + 1 - start);
            start = text.IndexOf(_prefix, end + 1, StringComparison.Ordinal);
        }

        return length;
    }

    public string Restore(string text)
    {
        var start = text.IndexOf(_prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var position = 0;
        while (start >= 0)
        {
            var end = text.IndexOf('\uE001', start + _prefix.Length);
            if (end < 0 || !int.TryParse(text.AsSpan(start + _prefix.Length, end - start - _prefix.Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= _values.Count)
            {
                break;
            }

            builder.Append(text, position, start - position).Append(_values[index]);
            position = end + 1;
            start = text.IndexOf(_prefix, position, StringComparison.Ordinal);
        }

        return builder.Append(text, position, text.Length - position).ToString();
    }
}
