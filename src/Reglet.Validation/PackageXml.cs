using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>Loads a package part as XML with line numbers, within the reader limits; a malformed part is reported.</summary>
internal static class PackageXml
{
    public static XDocument? Load(ZipArchiveEntry entry, IssueList issues)
    {
        try
        {
            using var stream = ReaderGuard.OpenBounded(entry);
            using var reader = XmlReader.Create(stream, ReaderGuard.SafeXml);
            return XDocument.Load(ReaderGuard.LimitXmlDepth(reader), LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            issues.Error(entry.FullName, exception.LineNumber > 0 ? exception.LineNumber : null, "The XML is not well-formed: " + exception.Message);
            return null;
        }
        catch (InvalidDataException exception)
        {
            issues.Error(entry.FullName, null, "The part cannot be decompressed: " + exception.Message);
            return null;
        }
    }
}
