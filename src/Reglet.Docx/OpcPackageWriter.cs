using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Reglet;

/// <summary>
/// internal OPC package writer. Manages ZIP entries, content types, relationships and
/// stable part names using only System.IO.Compression and System.Xml.
/// </summary>
internal sealed class OpcPackageWriter : IDisposable
{
    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        CloseOutput = false,
    };

    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ZipArchive _archive;
    private readonly List<(string Extension, string ContentType)> _defaults = [];
    private readonly List<(string PartName, string ContentType)> _overrides = [];
    private readonly Dictionary<string, List<OpcRelationship>> _relationships = new(StringComparer.Ordinal);
    private readonly HashSet<string> _writtenParts = new(StringComparer.Ordinal);

    private readonly InvalidXmlCharacterBehavior _xmlBehavior;

    /// <param name="output">The package stream.</param>
    /// <param name="xmlBehavior">What every XML part does with a character XML 1.0 does not allow.</param>
    public OpcPackageWriter(Stream output, InvalidXmlCharacterBehavior xmlBehavior = InvalidXmlCharacterBehavior.Replace)
    {
        ArgumentNullException.ThrowIfNull(output);
        _xmlBehavior = xmlBehavior;

        _archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        AddDefault("rels", OpcNamespaces.RelationshipsContentType);
        AddDefault("xml", "application/xml");
    }

    /// <summary>Registers a content type for a file extension.</summary>
    public void AddDefault(string extension, string contentType)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        if (!_defaults.Any(d => string.Equals(d.Extension, extension, StringComparison.OrdinalIgnoreCase)))
        {
            _defaults.Add((extension, contentType));
        }
    }

    /// <summary>Registers a content type for a specific part.</summary>
    public void AddOverride(string partName, string contentType)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        var normalized = partName.StartsWith('/') ? partName : "/" + partName;
        if (!_overrides.Any(o => string.Equals(o.PartName, normalized, StringComparison.Ordinal)))
        {
            _overrides.Add((normalized, contentType));
        }
    }

    /// <summary>Registers a relationship for a source part (empty = package root).</summary>
    /// <exception cref="InvalidOperationException">The relationship ID is already assigned.</exception>
    public void AddRelationship(string sourcePart, OpcRelationship relationship)
    {
        ArgumentNullException.ThrowIfNull(sourcePart);
        ArgumentNullException.ThrowIfNull(relationship);

        if (!_relationships.TryGetValue(sourcePart, out var list))
        {
            list = [];
            _relationships[sourcePart] = list;
        }

        if (list.Any(r => string.Equals(r.Id, relationship.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Duplicate relationship id '{relationship.Id}' for part '{sourcePart}'.");
        }

        list.Add(relationship);
    }

    /// <summary>The package parts the registered internal relationships point to.</summary>
    public IEnumerable<string> InternalTargets()
        => _relationships.SelectMany(entry => entry.Value
            .Where(relationship => relationship.TargetMode != "External")
            .Select(relationship => ResolveTarget(entry.Key, relationship.Target)));

    /// <summary>The part name a relationship target of <paramref name="sourcePart"/> names ("media/a.png" → "word/media/a.png").</summary>
    public static string ResolveTarget(string sourcePart, string target)
    {
        var unescaped = Uri.UnescapeDataString(target);
        var start = unescaped.StartsWith('/') ? string.Empty : sourcePart[..(sourcePart.LastIndexOf('/') + 1)];
        var segments = new List<string>();
        foreach (var segment in (start + unescaped).Split('/'))
        {
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            }
            else if (segment is not ("." or ""))
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>Ensures that a (possibly empty) .rels part is created for the source part.</summary>
    public void EnsureRelationshipsPart(string sourcePart)
    {
        ArgumentNullException.ThrowIfNull(sourcePart);

        if (!_relationships.ContainsKey(sourcePart))
        {
            _relationships[sourcePart] = [];
        }
    }

    /// <summary>Whether a part has already been written by the builder.</summary>
    public bool HasWritten(string partName)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        return _writtenParts.Contains(partName);
    }

    /// <summary>
    /// Whether the writer reserves a part name itself (the content-types part and every
    /// relationships part of a registered source). Preserved source parts must not collide with
    /// these.
    /// </summary>
    public bool IsReserved(string partName)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        return string.Equals(partName, "[Content_Types].xml", StringComparison.Ordinal)
            || _relationships.Keys.Any(source => string.Equals(RelsPartName(source), partName, StringComparison.Ordinal));
    }

    /// <summary>Writes a binary part (e.g. an image) into the package.</summary>
    public void AddBinaryPart(string partName, ReadOnlyMemory<byte> content)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        PackagePath.Validate(partName);

        _writtenParts.Add(partName);
        var entry = _archive.CreateEntry(partName, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTimestamp;
        using var stream = entry.Open();
        stream.Write(content.Span);
    }

    /// <summary>Writes a part whose content is produced via an XmlWriter.</summary>
    public void WriteXmlPart(string partName, Action<XmlWriter> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        ArgumentNullException.ThrowIfNull(write);
        PackagePath.Validate(partName);

        _writtenParts.Add(partName);
        var entry = _archive.CreateEntry(partName, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTimestamp;
        using var stream = entry.Open();
        using var writer = new CleaningXmlWriter(XmlWriter.Create(stream, XmlSettings), _xmlBehavior);
        try
        {
            write(writer);
        }
        catch (ArgumentException exception) when (exception.GetType() == typeof(ArgumentException))
        {
            // XmlWriter's refusal of a character that a name or field code of the model carries.
            throw new DocumentValidationException($"DOCX: the document cannot be written: {exception.Message}");
        }
    }

    /// <summary>Writes relationships and content types and closes the package.</summary>
    public void Dispose()
    {
        WriteRelationshipParts();
        WriteContentTypesPart();
        _archive.Dispose();
    }

    internal static string RelsPartName(string sourcePart)
    {
        if (sourcePart.Length == 0)
        {
            return "_rels/.rels";
        }

        var slash = sourcePart.LastIndexOf('/');
        return slash < 0
            ? $"_rels/{sourcePart}.rels"
            : $"{sourcePart[..(slash + 1)]}_rels/{sourcePart[(slash + 1)..]}.rels";
    }

    private void WriteRelationshipParts()
    {
        foreach (var (sourcePart, relationships) in _relationships)
        {
            var relsName = RelsPartName(sourcePart);
            WriteXmlPart(relsName, writer =>
            {
                writer.WriteStartElement("Relationships", OpcNamespaces.Relationships);
                foreach (var relationship in relationships)
                {
                    writer.WriteStartElement("Relationship");
                    writer.WriteAttributeString("Id", relationship.Id);
                    writer.WriteAttributeString("Type", relationship.Type);
                    // A hyperlink target is user text: invalid XML characters must not break the package.
                    writer.WriteAttributeString("Target", XmlText.Clean(relationship.Target, InvalidXmlCharacterBehavior.Replace));
                    if (relationship.TargetMode is not null)
                    {
                        writer.WriteAttributeString("TargetMode", relationship.TargetMode);
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            });
        }
    }

    private void WriteContentTypesPart()
    {
        WriteXmlPart("[Content_Types].xml", writer =>
        {
            writer.WriteStartElement("Types", OpcNamespaces.ContentTypes);

            foreach (var (extension, contentType) in _defaults)
            {
                writer.WriteStartElement("Default");
                writer.WriteAttributeString("Extension", extension);
                writer.WriteAttributeString("ContentType", contentType);
                writer.WriteEndElement();
            }

            foreach (var (partName, contentType) in _overrides)
            {
                writer.WriteStartElement("Override");
                writer.WriteAttributeString("PartName", partName);
                writer.WriteAttributeString("ContentType", contentType);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });
    }
}
