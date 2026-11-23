using System.IO.Compression;

namespace Reglet;

/// <summary>An embedded image in the ODT package.</summary>
internal sealed record OdtImageResource(string PartName, string MediaType)
{
    public string StyleName { get; set; } = "PDImage_" + Path.GetFileName(PartName).Replace('.', '_');
}

/// <summary>Registers images under <c>Pictures/</c> and declares them in the manifest.</summary>
internal static class OdtImageResources
{
    public static IReadOnlyDictionary<ImageBlock, OdtImageResource> Register(
        OdtPackageWriter package,
        PortableDocument document,
        ResourceLimits limits,
        IReadOnlySet<Block> sourceWrittenBlocks,
        CancellationToken cancellationToken)
    {
        var resources = new Dictionary<ImageBlock, OdtImageResource>();
        // AF12: a preserved drawing references its source picture part by name. Generated images
        // must not reuse such a name, or the drawing would show another image.
        var reserved = SourceParts(document);
        var index = 0;

        foreach (var image in EnumerateImages(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceWrittenBlocks.Contains(image) || resources.ContainsKey(image))
            {
                continue;
            }

            ImageGuard.Check(image, limits);
            var (extension, mediaType) = Media(image.MediaType);
            string partName;
            do
            {
                index++;
                partName = $"Pictures/image{index}.{extension}";
            }
            while (reserved.Contains(partName) || package.HasPart(partName));

            package.AddBinaryPart(partName, mediaType, image.RawData);
            resources[image] = new OdtImageResource(partName, mediaType);
        }

        return resources;
    }

    /// <summary>The entry names of the source package (a generated picture must avoid them).</summary>
    private static HashSet<string> SourceParts(PortableDocument document)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (document.SourcePackage is not { IsEmpty: false } bytes)
        {
            return names;
        }

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                names.Add(entry.FullName);
            }
        }
        catch (InvalidDataException)
        {
            // A damaged source package cannot be preserved anyway; generated names may collide.
        }

        return names;
    }

    /// <summary>File extension and media type of an image (SVG is supported natively by ODT).</summary>
    internal static (string Extension, string MediaType) Media(ImageMediaType type) => type switch
    {
        ImageMediaType.Svg => ("svg", OdtNamespaces.SvgContentType),
        ImageMediaType.Png => ("png", OdtNamespaces.PngContentType),
        _ => ("jpeg", OdtNamespaces.JpegContentType),
    };

    internal static IEnumerable<ImageBlock> EnumerateImages(PortableDocument document)
    {
        foreach (var section in document.Sections)
        {
            var bands = new[] { section.Header, section.Footer, section.FirstHeader, section.FirstFooter, section.EvenHeader, section.EvenFooter }
                .OfType<HeaderFooter>().SelectMany(band => band.Blocks);
            foreach (var image in EnumerateImages(section.Blocks.Concat(bands)))
            {
                yield return image;
            }
        }
    }

    internal static IEnumerable<ImageBlock> EnumerateImages(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ImageBlock image:
                    yield return image;
                    break;
                case Paragraph paragraph:
                    foreach (var image in InlineImage.In(paragraph.Inlines))
                    {
                        yield return image;
                    }

                    break;
                case Heading heading:
                    foreach (var image in InlineImage.In(heading.Inlines))
                    {
                        yield return image;
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        foreach (var image in EnumerateImages(item.Blocks))
                        {
                            yield return image;
                        }
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var image in EnumerateImages(cell.Blocks))
                            {
                                yield return image;
                            }
                        }
                    }

                    break;
            }
        }
    }
}
