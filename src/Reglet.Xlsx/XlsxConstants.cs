namespace Reglet;

/// <summary>Namespaces, relationship and content types of SpreadsheetML (ECMA-376 part 1, 18).</summary>
internal static class XlsxConstants
{
    public const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string Drawing = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    public const string DrawingMain = "http://schemas.openxmlformats.org/drawingml/2006/main";

    public const string OfficeDocumentType = Relationships + "/officeDocument";
    public const string WorksheetType = Relationships + "/worksheet";
    public const string StylesType = Relationships + "/styles";
    public const string SharedStringsType = Relationships + "/sharedStrings";
    public const string DrawingType = Relationships + "/drawing";
    public const string ImageType = Relationships + "/image";
    public const string ThemeType = Relationships + "/theme";

    public const string WorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    public const string WorksheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
    public const string StylesContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";
    public const string SharedStringsContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml";
    public const string DrawingContentType = "application/vnd.openxmlformats-officedocument.drawing+xml";

    /// <summary>Excel's default font, used where a cell font leaves name or size open.</summary>
    public const string DefaultFontName = "Calibri";
    public const double DefaultFontSize = 11;

    /// <summary>The widest digit of the default font (Calibri 11) in pixels at 96 dpi, Excel's unit of column width.</summary>
    public const int MaxDigitWidth = 7;

    /// <summary>
    /// The stored column width for a width as Excel shows it (ECMA-376 part 1, 18.3.1.13): the characters plus 5 pixels
    /// of cell padding, in digit widths, truncated to 1/256.
    /// </summary>
    public static double StoredWidth(double displayed)
        => displayed <= 0 ? 0 : Math.Min(255, Math.Truncate(((displayed * MaxDigitWidth) + 5) / MaxDigitWidth * 256) / 256);

    /// <summary>The width Excel shows for a stored width: whole pixels less the padding, in characters to 1/100.</summary>
    public static double DisplayedWidth(double stored)
    {
        var pixels = Math.Truncate(((256 * stored) + Math.Truncate(128.0 / MaxDigitWidth)) / 256 * MaxDigitWidth);
        return Math.Max(0, Math.Truncate(((pixels - 5) / MaxDigitWidth * 100) + 0.5) / 100);
    }

    /// <summary>EMU per pixel at 96 dpi.</summary>
    public const long EmuPerPixel = 9525;

    /// <summary>Built-in number formats by id (ECMA-376 18.8.30) for reading; the writer writes every code as custom.</summary>
    public static readonly IReadOnlyDictionary<int, string> BuiltInFormats = new Dictionary<int, string>
    {
        [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00", [9] = "0%", [10] = "0.00%", [11] = "0.00E+00",
        [12] = "# ?/?", [13] = "# ??/??", [14] = "m/d/yyyy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy",
        [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = "m/d/yyyy h:mm",
        [37] = "#,##0 ;(#,##0)", [38] = "#,##0 ;[Red](#,##0)", [39] = "#,##0.00;(#,##0.00)", [40] = "#,##0.00;[Red](#,##0.00)",
        [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mmss.0", [48] = "##0.0E+0", [49] = "@",
    };
}
