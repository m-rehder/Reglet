using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// An application-defined XML document that travels with the document (DOCX <c>customXml/itemN.xml</c>,
/// for example the data that content controls are bound to). Only DOCX stores it; ODT and PDF ignore it.
/// </summary>
public sealed record CustomXmlPart
{
    private readonly string _xml = string.Empty;
    private readonly string? _itemId;

    /// <summary>The XML text of the part; must be a well-formed document.</summary>
    public required string Xml
    {
        get => _xml;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(Xml));
            try
            {
                _ = XDocument.Parse(value);
            }
            catch (System.Xml.XmlException exception)
            {
                throw new ArgumentException("The custom XML part is not well-formed: " + exception.Message, nameof(Xml));
            }

            _xml = value;
        }
    }

    /// <summary>The store item id (a GUID such as <c>{1B3C...}</c>); null derives a stable one from the content.</summary>
    public string? ItemId
    {
        get => _itemId;
        init
        {
            if (value is not null && !Guid.TryParse(value, out _))
            {
                throw new ArgumentException("The item id must be a GUID.", nameof(ItemId));
            }

            _itemId = value;
        }
    }
}
