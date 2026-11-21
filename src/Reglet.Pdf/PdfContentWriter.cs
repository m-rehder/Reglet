using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// Encapsulates typed PDF content operators (text, lines, rectangles, colors,
/// transformations). The content is collected in a buffer.
/// </summary>
internal sealed class PdfContentWriter
{
    private readonly MemoryStream _buffer = new();

    public long Length => _buffer.Length;

    public PdfContentWriter BeginText() => Append("BT\n");

    public PdfContentWriter EndText() => Append("ET\n");

    public PdfContentWriter SetFont(string resourceName, double size)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        return Append("/" + resourceName + " " + Number(size) + " Tf\n");
    }

    public PdfContentWriter SetTextLeading(double leading)
        => Append(Number(leading) + " TL\n");

    /// <summary>Sets the text render mode (e.g. 2 = fill + stroke for synthetic bold).</summary>
    public PdfContentWriter SetTextRenderMode(int mode)
        => Append(mode.ToString(CultureInfo.InvariantCulture) + " Tr\n");

    public PdfContentWriter MoveText(double x, double y)
        => Append(Number(x) + " " + Number(y) + " Td\n");

    public PdfContentWriter NextLine(double x, double y)
        => Append(Number(x) + " " + Number(y) + " TD\n");

    public PdfContentWriter SetFillColor(Color color)
        => Append(Number(color.Red / 255.0) + " " + Number(color.Green / 255.0) + " " + Number(color.Blue / 255.0) + " rg\n");

    public PdfContentWriter SetStrokeColor(Color color)
        => Append(Number(color.Red / 255.0) + " " + Number(color.Green / 255.0) + " " + Number(color.Blue / 255.0) + " RG\n");

    public PdfContentWriter SetLineWidth(double width)
        => Append(Number(width) + " w\n");

    /// <summary>Sets the graphics state (e.g. transparency of a watermark) via <c>gs</c>.</summary>
    public PdfContentWriter SetExtGState(string resourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        return Append("/" + resourceName + " gs\n");
    }

    /// <summary>Sets the additional character spacing (<c>Tc</c>) in points.</summary>
    public PdfContentWriter SetCharacterSpacing(double spacing)
        => Append(Number(spacing) + " Tc\n");

    /// <summary>Sets the horizontal scaling (<c>Tz</c>) in percent (100 = normal).</summary>
    public PdfContentWriter SetHorizontalScaling(double percent)
        => Append(Number(percent) + " Tz\n");

    /// <summary>Sets a dash pattern (<c>d</c>) for dotted/dashed lines.</summary>
    public PdfContentWriter SetDash(IReadOnlyList<double> pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var builder = new StringBuilder("[");
        foreach (var value in pattern)
        {
            builder.Append(' ').Append(Number(value));
        }

        builder.Append(" ] 0 d\n");
        return Append(builder.ToString());
    }

    /// <summary>Fills a rectangle (e.g. character background) in the specified color.</summary>
    public PdfContentWriter FillRectangle(double x, double y, double width, double height, Color color)
    {
        SaveState();
        SetFillColor(color);
        Rectangle(x, y, width, height);
        Fill();
        return RestoreState();
    }

    public PdfContentWriter MoveTo(double x, double y)
        => Append(Number(x) + " " + Number(y) + " m\n");

    public PdfContentWriter LineTo(double x, double y)
        => Append(Number(x) + " " + Number(y) + " l\n");

    public PdfContentWriter Rectangle(double x, double y, double width, double height)
        => Append(Number(x) + " " + Number(y) + " " + Number(width) + " " + Number(height) + " re\n");

    /// <summary>Appends a cubic Bézier segment (<c>c</c>).</summary>
    public PdfContentWriter CurveTo(double x1, double y1, double x2, double y2, double x, double y)
        => Append(
            Number(x1) + " " + Number(y1) + " " + Number(x2) + " " + Number(y2) + " "
            + Number(x) + " " + Number(y) + " c\n");

    /// <summary>
    /// appends the path of a rectangle with rounded corners (OF-B04). The radius is clamped to half
    /// of the shorter side, so the radius never inverts the outline.
    /// </summary>
    public PdfContentWriter RoundedRectangle(double x, double y, double width, double height, double radius)
        => RoundedRectangle(x, y, width, height, radius, radius, radius, radius);

    /// <summary>
    /// appends the path of a rectangle whose corners round with <paramref name="topLeft"/>,
    /// <paramref name="topRight"/>, <paramref name="bottomRight"/> and <paramref name="bottomLeft"/>
    /// (a radius of 0 keeps that corner square). Every radius is clamped to half of the shorter side.
    /// </summary>
    public PdfContentWriter RoundedRectangle(
        double x,
        double y,
        double width,
        double height,
        double topLeft,
        double topRight,
        double bottomRight,
        double bottomLeft)
    {
        var max = Math.Min(width, height) / 2;
        var tl = Math.Clamp(topLeft, 0, max);
        var tr = Math.Clamp(topRight, 0, max);
        var br = Math.Clamp(bottomRight, 0, max);
        var bl = Math.Clamp(bottomLeft, 0, max);
        var right = x + width;
        var top = y + height;

        // Four Bézier quarters: the control points lie at the circle constant (kappa) of the radius,
        // which approximates a quarter circle to better than 0.02 % of the radius.
        const double Kappa = 0.5522847498307933;

        MoveTo(x + bl, y);
        LineTo(right - br, y);
        if (br > 0)
        {
            CurveTo(right - br + br * Kappa, y, right, y + br - br * Kappa, right, y + br);
        }

        LineTo(right, top - tr);
        if (tr > 0)
        {
            CurveTo(right, top - tr + tr * Kappa, right - tr + tr * Kappa, top, right - tr, top);
        }

        LineTo(x + tl, top);
        if (tl > 0)
        {
            CurveTo(x + tl - tl * Kappa, top, x, top - tl + tl * Kappa, x, top - tl);
        }

        LineTo(x, y + bl);
        if (bl > 0)
        {
            CurveTo(x, y + bl - bl * Kappa, x + bl - bl * Kappa, y, x + bl, y);
        }

        return ClosePath();
    }

    /// <summary>Closes the current sub-path (<c>h</c>).</summary>
    public PdfContentWriter ClosePath() => Append("h\n");

    /// <summary>Sets the clipping path to the most recently added rectangle (<c>W n</c>).</summary>
    public PdfContentWriter Clip() => Append("W n\n");

    /// <summary>Draws an outlined rectangle (table border) in the stroke color and width.</summary>
    public PdfContentWriter StrokeRectangle(double x, double y, double width, double height, Color color, double lineWidth)
    {
        // Own state block (q/Q): stroke color and width do not affect other
        // character operations (consistency with Underline).
        SaveState();
        SetStrokeColor(color);
        SetLineWidth(lineWidth);
        Rectangle(x, y, width, height);
        Stroke();
        return RestoreState();
    }

    public PdfContentWriter Stroke() => Append("S\n");

    /// <summary>
    /// starts marked content with alternative text for accessibility (without a structure
    /// tree), e.g. <c>/Span &lt;&lt; /Alt … &gt;&gt; BDC</c>. The text is written as a UTF-16BE hex string
    /// written so that arbitrary Unicode characters survive without escaping errors.
    /// </summary>
    public PdfContentWriter BeginMarkedContentWithAlt(string tag, string alt)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        return Append("/" + tag + " << /Alt " + AltString(alt) + " >> BDC\n");
    }

    /// <summary>Starts marked content with an <c>/ActualText</c> replacement.</summary>
    public PdfContentWriter BeginMarkedContentWithActualText(string actualText)
    {
        ArgumentNullException.ThrowIfNull(actualText);
        return Append("/Span << /ActualText " + AltString(actualText) + " >> BDC\n");
    }

    /// <summary>Ends marked content (EMC).</summary>
    public PdfContentWriter EndMarkedContent() => Append("EMC\n");

    /// <summary>
    /// starts a tagged marked-content section for a structure element (phase Q):
    /// <c>/P &lt;&lt; /MCID n &gt;&gt; BDC</c>.
    /// </summary>
    public PdfContentWriter BeginStructure(string role, int mcid)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        return Append("/" + role + " <</MCID " + mcid.ToString(CultureInfo.InvariantCulture) + ">> BDC\n");
    }

    /// <summary>Starts an artifact section (e.g. header/footer): <c>/Artifact BMC</c>.</summary>
    public PdfContentWriter BeginArtifact() => Append("/Artifact BMC\n");

    /// <summary>Encodes text as a UTF-16BE hex string <c>&lt;FEFF…&gt;</c>.</summary>
    private static string AltString(string alt)
    {
        var bytes = Encoding.BigEndianUnicode.GetBytes(alt);
        var builder = new StringBuilder("<FEFF");
        foreach (var value in bytes)
        {
            builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        builder.Append('>');
        return builder.ToString();
    }

    /// <summary>
    /// draws an underline or strikethrough line between two x positions.
    /// double lines are drawn as two strokes, dotted/dashed via a dash pattern.
    /// </summary>
    public PdfContentWriter Rule(
        Color color,
        double x1,
        double x2,
        double y,
        double thickness,
        TextUnderlineStyle style)
        => StrokeLine(x1, y, x2, y, color, thickness, style);

    /// <summary>Draws an arbitrary line with an optional dash pattern.</summary>
    public PdfContentWriter StrokeLine(
        double x1,
        double y1,
        double x2,
        double y2,
        Color color,
        double width,
        TextUnderlineStyle style)
    {
        // Own state block (q/Q): stroke color and width are not meant to affect later
        // character operations to pass through.
        SaveState();
        SetStrokeColor(color);
        SetLineWidth(width);
        if (style == TextUnderlineStyle.Dotted)
        {
            SetDash(new[] { width, width * 1.5 });
        }
        else if (style == TextUnderlineStyle.Dashed)
        {
            SetDash(new[] { width * 3, width * 2 });
        }

        MoveTo(x1, y1);
        LineTo(x2, y2);
        Stroke();

        if (style == TextUnderlineStyle.Double)
        {
            MoveTo(x1, y1 + width * 2.5);
            LineTo(x2, y2 + width * 2.5);
            Stroke();
        }

        return RestoreState();
    }

    public PdfContentWriter Fill() => Append("f\n");

    /// <summary>Fills and strokes the current path.</summary>
    public PdfContentWriter FillAndStroke() => Append("B\n");

    /// <summary>Ends the path without painting it.</summary>
    public PdfContentWriter EndPath() => Append("n\n");

    public PdfContentWriter SaveState() => Append("q\n");

    public PdfContentWriter RestoreState() => Append("Q\n");

    /// <summary>Places an image XObject with the <c>Do</c> operator.</summary>
    public PdfContentWriter DrawImage(string resourceName, double width, double height, double x, double y)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        SaveState();
        ConcatMatrix(width, 0, 0, height, x, y);
        Append("/" + resourceName + " Do\n");
        RestoreState();
        return this;
    }

    public PdfContentWriter ConcatMatrix(double a, double b, double c, double d, double e, double f)
        => Append(Number(a) + " " + Number(b) + " " + Number(c) + " " + Number(d) + " " + Number(e) + " " + Number(f) + " cm\n");

    /// <summary>Appends the bytes another content writer produced (a cached header or footer).</summary>
    public PdfContentWriter AppendBytes(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _buffer.Write(content);
        return this;
    }

    /// <summary>Appends ready-made operators (for example a converted SVG).</summary>
    public PdfContentWriter AppendOperators(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Append(content);
        return this;
    }

    /// <summary>Writes a text string (WinAnsi bytes) and the <c>Tj</c> operator.</summary>
    public PdfContentWriter ShowText(ReadOnlySpan<byte> bytes)
    {
        _buffer.WriteByte((byte)'(');
        foreach (var value in bytes)
        {
            if (value is (byte)'(' or (byte)')' or (byte)'\\')
            {
                _buffer.WriteByte((byte)'\\');
                _buffer.WriteByte(value);
            }
            else if (value < 32 || value > 126)
            {
                _buffer.WriteByte((byte)'\\');
                var octal = Convert.ToString(value, 8).PadLeft(3, '0');
                foreach (var character in octal)
                {
                    _buffer.WriteByte((byte)character);
                }
            }
            else
            {
                _buffer.WriteByte(value);
            }
        }

        _buffer.WriteByte((byte)')');
        return Append(" Tj\n");
    }

    public byte[] ToArray() => _buffer.ToArray();

    /// <summary>Writes a hex string (e.g. Identity-H glyphs) and the <c>Tj</c> operator.</summary>
    public PdfContentWriter ShowHexText(ReadOnlySpan<byte> bytes)
    {
        _buffer.WriteByte((byte)'<');
        foreach (var value in bytes)
        {
            WriteHexByte(value);
        }

        _buffer.WriteByte((byte)'>');
        return Append(" Tj\n");
    }

    /// <summary>
    /// writes a shaped glyph sequence as a <c>TJ</c> array (phase P). Kerning is output
    /// number is output between the glyphs (positive = pen to the left).
    /// </summary>
    public PdfContentWriter ShowShapedGlyphs(IReadOnlyList<ShapedGlyph> glyphs)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        _buffer.WriteByte((byte)'[');
        foreach (var glyph in glyphs)
        {
            _buffer.WriteByte((byte)' ');
            _buffer.WriteByte((byte)'<');
            WriteHexByte((byte)(glyph.GlyphId >> 8));
            WriteHexByte((byte)(glyph.GlyphId & 0xFF));
            _buffer.WriteByte((byte)'>');
            if (glyph.Kern != 0)
            {
                Append(" " + Number(-glyph.Kern));
            }
        }

        _buffer.WriteByte((byte)' ');
        _buffer.WriteByte((byte)']');
        return Append(" TJ\n");
    }

    private void WriteHexByte(byte value)
    {
        var hex = value.ToString("X2", CultureInfo.InvariantCulture);
        foreach (var character in hex)
        {
            _buffer.WriteByte((byte)character);
        }
    }

    private PdfContentWriter Append(string ascii)
    {
        foreach (var character in ascii)
        {
            _buffer.WriteByte((byte)character);
        }

        return this;
    }

    private static string Number(double value)
    {
        var text = value.ToString("0.###", CultureInfo.InvariantCulture);
        return text is "-0" or "" ? "0" : text;
    }
}
