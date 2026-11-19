using System.Security.Cryptography;
using System.Text;

namespace Reglet;

/// <summary>
/// PDF/A-specific components (phase R): OutputIntent with ICC profile, attachments
/// (<c>/EmbeddedFile</c>, R03) and a stable document ID for the trailer.
/// </summary>
internal static class PdfAWriter
{
    /// <summary>Writes the ICC profile and the OutputIntent and returns its object number.</summary>
    public static bool IsRgbOutputProfile(byte[] profile)
        => profile.Length >= 128
            && "acsp"u8.SequenceEqual(profile.AsSpan(36, 4))
            && ("prtr"u8.SequenceEqual(profile.AsSpan(12, 4)) || "mntr"u8.SequenceEqual(profile.AsSpan(12, 4)))
            && "RGB "u8.SequenceEqual(profile.AsSpan(16, 4));

    public static int WriteOutputIntent(PdfFileWriter file, PdfWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);

        var icc = options.IccProfile ?? PdfIccProfile.Srgb();
        var iccObject = file.ReserveObject();
        file.WriteStreamObject(iccObject, PdfStreamCompressor.Compress(icc), "/N 3 /Filter /FlateDecode");

        var outputIntent = file.ReserveObject();
        file.WriteObject(outputIntent, tokens => tokens.WriteDictionary(intent =>
        {
            intent.WriteName("Type");
            intent.WriteName("OutputIntent");
            intent.WriteName("S");
            intent.WriteName("GTS_PDFA1");
            intent.WriteName("OutputConditionIdentifier");
            intent.WriteLiteralString("sRGB IEC61966-2.1");
            intent.WriteName("Info");
            intent.WriteLiteralString("sRGB IEC61966-2.1");
            intent.WriteName("DestOutputProfile");
            intent.WriteReference(iccObject);
        }));

        return outputIntent;
    }

    /// <summary>
    /// writes the attachments as <c>/EmbeddedFile</c> and <c>/Filespec</c> and returns the
    /// Filespec object numbers as well as the name tree.
    /// </summary>
    public static (IReadOnlyList<int> Files, int NamesTree) WriteAttachments(
        PdfFileWriter file,
        IReadOnlyList<PdfAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(attachments);

        var files = new List<int>(attachments.Count);
        foreach (var attachment in attachments.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var embedded = file.ReserveObject();
            var data = attachment.Data.ToArray();
            file.WriteStreamObject(
                embedded,
                data,
                "/Type /EmbeddedFile /Subtype /" + EscapeName(attachment.MimeType)
                + " /Params << /Size " + data.Length + " >>");

            var filespec = file.ReserveObject();
            file.WriteObject(filespec, tokens => tokens.WriteDictionary(spec =>
            {
                spec.WriteName("Type");
                spec.WriteName("Filespec");
                spec.WriteName("F");
                spec.WriteLiteralString(attachment.Name);
                spec.WriteName("UF");
                spec.WriteLiteralString(attachment.Name);
                if (!string.IsNullOrEmpty(attachment.Description))
                {
                    spec.WriteName("Desc");
                    spec.WriteLiteralString(attachment.Description);
                }

                spec.WriteName("AFRelationship");
                spec.WriteName(string.IsNullOrEmpty(attachment.Relationship) ? "Data" : attachment.Relationship);
                spec.WriteName("EF");
                spec.WriteDictionary(ef =>
                {
                    ef.WriteName("F");
                    ef.WriteReference(embedded);
                    ef.WriteName("UF");
                    ef.WriteReference(embedded);
                });
            }));

            files.Add(filespec);
        }

        var namesTree = file.ReserveObject();
        var sorted = attachments.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        file.WriteObject(namesTree, tokens => tokens.WriteDictionary(tree =>
        {
            tree.WriteName("Names");
            tree.WriteArray(names =>
            {
                for (var i = 0; i < sorted.Length; i++)
                {
                    names.WriteLiteralString(sorted[i].Name);
                    names.WriteReference(files[i]);
                }
            });
        }));

        return (files, namesTree);
    }

    /// <summary>Stable document ID from the metadata (two 16-byte values for the trailer).</summary>
    public static (byte[] Original, byte[] Changing) DocumentId(DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var seed = string.Join(
            '\u001F',
            metadata.Title ?? string.Empty,
            metadata.Author ?? string.Empty,
            metadata.Subject ?? string.Empty,
            metadata.Language ?? string.Empty,
            metadata.Created?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

        var original = SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..16];
        var changing = SHA256.HashData(original)[..16];
        return (original, changing);
    }

    private static string EscapeName(string mimeType)
    {
        var builder = new StringBuilder(mimeType.Length);
        foreach (var value in Encoding.UTF8.GetBytes(mimeType))
        {
            if (IsRegularNameCharacter(value))
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append('#').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static bool IsRegularNameCharacter(byte value)
        => value is > 32 and < 127
            && value is not (byte)'#' and not (byte)'%' and not (byte)'(' and not (byte)')'
            and not (byte)'<' and not (byte)'>' and not (byte)'[' and not (byte)']'
            and not (byte)'{' and not (byte)'}' and not (byte)'/' and not (byte)'*';
}
