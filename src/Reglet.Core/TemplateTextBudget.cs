using System.Xml.Linq;

namespace Reglet;

/// <summary>Limits the XML that binding a template may add in total.</summary>
internal sealed class TemplateTextBudget(long limit)
{
    private long _used;

    public void Charge(long characters)
    {
        _used += characters;
        if (_used > limit)
        {
            throw new DocumentValidationException($"The bound template exceeds the limit of {limit} characters.");
        }
    }

    /// <summary>The characters of an element with its descendants, names and attributes included.</summary>
    public static long SizeOf(XElement element)
        => element.DescendantsAndSelf().Sum(item => (long)item.Name.LocalName.Length
            + item.Attributes().Sum(attribute => (long)attribute.Value.Length)
            + item.Nodes().OfType<XText>().Sum(text => (long)text.Value.Length));
}
