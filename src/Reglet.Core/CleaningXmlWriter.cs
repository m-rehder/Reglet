using System.Xml;

namespace Reglet;

/// <summary>
/// An <see cref="XmlWriter"/> that cleans every text and attribute value for XML 1.0 (<see cref="XmlText.Clean"/>),
/// so a character that is not allowed in XML follows <see cref="InvalidXmlCharacterBehavior"/> in names, field codes
/// and languages as well, instead of failing the whole document where a writer forgot to clean.
/// </summary>
internal sealed class CleaningXmlWriter(XmlWriter inner, InvalidXmlCharacterBehavior behavior) : XmlWriter
{
    private string Clean(string? text) => text is null ? string.Empty : XmlText.Clean(text, behavior);

    public override WriteState WriteState => inner.WriteState;

    public override XmlWriterSettings? Settings => inner.Settings;

    public override void Flush() => inner.Flush();

    public override string? LookupPrefix(string ns) => inner.LookupPrefix(ns);

    public override void WriteBase64(byte[] buffer, int index, int count) => inner.WriteBase64(buffer, index, count);

    public override void WriteCData(string? text) => inner.WriteCData(Clean(text));

    public override void WriteCharEntity(char ch) => inner.WriteString(Clean(ch.ToString()));

    public override void WriteChars(char[] buffer, int index, int count) => inner.WriteString(Clean(new string(buffer, index, count)));

    public override void WriteComment(string? text) => inner.WriteComment(Clean(text));

    public override void WriteDocType(string name, string? pubid, string? sysid, string? subset) => inner.WriteDocType(name, pubid, sysid, subset);

    public override void WriteEndAttribute() => inner.WriteEndAttribute();

    public override void WriteEndDocument() => inner.WriteEndDocument();

    public override void WriteEndElement() => inner.WriteEndElement();

    public override void WriteEntityRef(string name) => inner.WriteEntityRef(name);

    public override void WriteFullEndElement() => inner.WriteFullEndElement();

    public override void WriteProcessingInstruction(string name, string? text) => inner.WriteProcessingInstruction(name, Clean(text));

    public override void WriteRaw(char[] buffer, int index, int count) => inner.WriteRaw(buffer, index, count);

    public override void WriteRaw(string data) => inner.WriteRaw(data);

    public override void WriteStartAttribute(string? prefix, string localName, string? ns) => inner.WriteStartAttribute(prefix, localName, ns);

    public override void WriteStartDocument() => inner.WriteStartDocument();

    public override void WriteStartDocument(bool standalone) => inner.WriteStartDocument(standalone);

    public override void WriteStartElement(string? prefix, string localName, string? ns) => inner.WriteStartElement(prefix, localName, ns);

    public override void WriteString(string? text) => inner.WriteString(Clean(text));

    public override void WriteSurrogateCharEntity(char lowChar, char highChar) => inner.WriteString(Clean(new string([highChar, lowChar])));

    public override void WriteWhitespace(string? ws) => inner.WriteWhitespace(ws);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
