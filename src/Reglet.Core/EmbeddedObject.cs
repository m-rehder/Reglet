namespace Reglet;

/// <summary>What the bytes of an <see cref="EmbeddedObject"/> are.</summary>
public enum EmbeddedObjectKind
{
    /// <summary>An OLE compound file (DOCX <c>oleObjectN.bin</c>, ODT <c>draw:object-ole</c>).</summary>
    OleStorage,

    /// <summary>An Open XML package such as an embedded workbook (DOCX <c>PackageN.xlsx</c>); DOCX only.</summary>
    Package,
}

/// <summary>
/// An object another program owns, embedded in the document (ADR-0029): kept as opaque bytes with a preview
/// picture. The library never opens, runs or updates it. DOCX and ODT store it; PDF draws the preview.
/// </summary>
public sealed class EmbeddedObject : Block
{
    private readonly byte[] _data;

    /// <summary>A transparent 1x1 PNG, drawn by the writers when an object has no preview (so the file stays valid).</summary>
    internal static readonly byte[] PlaceholderPreviewPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNgAAIAAAUAAen63NgAAAAASUVORK5CYII=");

    /// <summary>Creates an embedded object.</summary>
    /// <param name="data">The bytes of the object; must not be empty. The value is copied.</param>
    /// <param name="kind">What the bytes are.</param>
    /// <param name="width">The size on the page; must be positive.</param>
    /// <param name="height">The size on the page; must be positive.</param>
    /// <exception cref="ArgumentException">The data is empty, the kind is unknown or a size is not positive.</exception>
    public EmbeddedObject(ReadOnlyMemory<byte> data, EmbeddedObjectKind kind, Length width, Length height)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("The data of an embedded object must not be empty.", nameof(data));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown embedded object kind.");
        }

        if (width.Points <= 0 || height.Points <= 0)
        {
            throw new ArgumentException("The size of an embedded object must be positive.");
        }

        _data = data.ToArray();
        Kind = kind;
        Width = width;
        Height = height;
    }

    /// <summary>What the bytes are.</summary>
    public EmbeddedObjectKind Kind { get; }

    /// <summary>The bytes of the object as a defensive copy.</summary>
    public ReadOnlyMemory<byte> Data => _data.ToArray();

    internal ReadOnlyMemory<byte> RawData => _data;

    /// <summary>The size on the page.</summary>
    public Length Width { get; }

    /// <summary>The size on the page.</summary>
    public Length Height { get; }

    /// <summary>The OLE program id (<c>Excel.Sheet.12</c>); may be empty.</summary>
    public string ProgId { get; init; } = string.Empty;

    /// <summary>
    /// The file extension of a <see cref="EmbeddedObjectKind.Package"/> part without the dot (<c>xlsx</c>);
    /// letters and digits, at most 8. <c>bin</c> by default; an <see cref="EmbeddedObjectKind.OleStorage"/> is always <c>bin</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The extension is not 1 to 8 letters or digits.</exception>
    public string FileExtension
    {
        get => _fileExtension;
        init
        {
            if (value is not { Length: >= 1 and <= 8 } || !value.All(char.IsAsciiLetterOrDigit))
            {
                throw new ArgumentException("The file extension must be 1 to 8 letters or digits.", nameof(FileExtension));
            }

            _fileExtension = value.ToLowerInvariant();
        }
    }

    private readonly string _fileExtension = "bin";

    /// <summary>The preview picture (PNG or JPEG), or null.</summary>
    /// <exception cref="ArgumentException">The picture is an SVG.</exception>
    public ImageBlock? Preview
    {
        get => _preview;
        init
        {
            if (value is { MediaType: ImageMediaType.Svg })
            {
                throw new ArgumentException("The preview of an embedded object must be a PNG or JPEG.", nameof(Preview));
            }

            _preview = value;
        }
    }

    private readonly ImageBlock? _preview;
}
