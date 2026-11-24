namespace Reglet;

/// <summary>A PNG or JPEG image whose top-left corner sits in a cell (Excel's one-cell anchor).</summary>
public sealed record WorksheetImage
{
    /// <summary>Creates an image anchored at a 1-based row and column.</summary>
    /// <exception cref="ImageException">The bytes are neither PNG nor JPEG.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The row or column is out of Excel's range.</exception>
    public WorksheetImage(ReadOnlyMemory<byte> data, int row, int column)
    {
        CellReference.Check(row, column);
        Info = ImageInfo.Read(data.Span);
        Data = data;
        Row = row;
        Column = column;
    }

    /// <summary>The image bytes.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>The format and pixel size read from the bytes; use it to keep the aspect ratio when scaling.</summary>
    public ImageInfo Info { get; }

    /// <summary>The 1-based row of the anchor cell.</summary>
    public int Row { get; }

    /// <summary>The 1-based column of the anchor cell.</summary>
    public int Column { get; }

    /// <summary>The shown width in pixels; null keeps the image's own width.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive.</exception>
    public int? WidthPixels
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(WidthPixels), "The width must be positive.");
    }

    /// <summary>The shown height in pixels; null keeps the image's own height.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The height is not positive.</exception>
    public int? HeightPixels
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(HeightPixels), "The height must be positive.");
    }

    /// <summary>Alternative text; null means none.</summary>
    public string? AlternativeText { get; init; }
}
