namespace Reglet;

/// <summary>
/// An image in the line of a paragraph (Word's <c>wp:inline</c>): it stands on the baseline like a large character and
/// follows the paragraph's alignment. The line is as high as the font's line gap, the image and the font's descent.
/// </summary>
/// <remarks>
/// The image's size, crop and alternative text come from <see cref="Image"/>; its wrap and float settings do not apply.
/// </remarks>
public sealed class InlineImage : Inline
{
    /// <summary>Creates an inline image from an image.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    public InlineImage(ImageBlock image)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    /// <summary>Creates an inline image from bytes and media type, with an optional size (null keeps the image's own).</summary>
    public InlineImage(ReadOnlyMemory<byte> data, ImageMediaType mediaType, Length? width = null, Length? height = null)
        : this(new ImageBlock(data, mediaType) { Width = width, Height = height })
    {
    }

    /// <summary>The image: bytes, size, crop and alternative text.</summary>
    public ImageBlock Image { get; }

    /// <summary>The images in a run of inlines, also inside hyperlinks.</summary>
    internal static IEnumerable<ImageBlock> In(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is InlineImage image)
            {
                yield return image.Image;
            }
            else if (inline is Hyperlink link)
            {
                foreach (var nested in In(link.Inlines))
                {
                    yield return nested;
                }
            }
        }
    }
}
