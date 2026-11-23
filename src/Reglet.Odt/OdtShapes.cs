using System.Globalization;
using System.Xml;

namespace Reglet;

/// <summary>
/// Shapes and text boxes in ODT (ADR-0022): the graphic styles (one per distinct look, named from
/// the look itself so content.xml and styles.xml agree), the dash definitions and the geometry
/// attributes of the draw elements.
/// </summary>
internal static class OdtShapes
{
    private const string DashName = "PDDash";
    private const string DotName = "PDDot";

    /// <summary>All shapes of the document (body and bands, also in lists, tables and other text boxes).</summary>
    public static IEnumerable<(DrawingShape Shape, bool Centered)> Enumerate(PortableDocument document)
    {
        foreach (var section in document.Sections)
        {
            var groups = new[] { section.Header, section.Footer, section.FirstHeader, section.FirstFooter, section.EvenHeader, section.EvenFooter }
                .Where(band => band is not null).Select(band => band!.Blocks).Prepend(section.Blocks);
            foreach (var blocks in groups)
            {
                foreach (var shape in Enumerate(blocks))
                {
                    yield return shape;
                }
            }
        }
    }

    private static IEnumerable<(DrawingShape Shape, bool Centered)> Enumerate(IReadOnlyList<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case DrawingShape shape:
                    yield return (shape, false);
                    break;
                case Diagram diagram:
                    foreach (var placed in OdtDiagrams.Shapes(diagram))
                    {
                        // The text of a diagram node is centred vertically (LibreOffice ignores the space above it).
                        yield return (placed.Shape, placed.Shape.Kind == ShapeKind.TextBox);
                    }

                    break;
                case ListBlock list:
                    foreach (var inner in list.Items.SelectMany(item => Enumerate(item.Blocks)))
                    {
                        yield return inner;
                    }

                    break;
                case TableBlock table:
                    foreach (var inner in table.Rows.SelectMany(row => row.Cells).SelectMany(cell => Enumerate(cell.Blocks)))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }

    /// <summary>The graphic style name of a shape; it encodes everything the style contains.</summary>
    public static string StyleName(DrawingShape shape, bool centered = false)
    {
        var fill = shape.Fill is { } color ? Hex(color) : "N";
        var outline = shape.Outline is { } line ? Hex(line) + ((int)Math.Round(shape.OutlineWidth.Points * 10)).ToString(CultureInfo.InvariantCulture) : "N";
        var dash = shape.Dash switch { ShapeDash.Dash => "D", ShapeDash.Dot => "T", _ => "S" };
        var kind = shape.Kind == ShapeKind.TextBox ? "B" : "G";
        return $"PDShape_{kind}_{fill}_{outline}_{dash}_{(int)shape.Wrap}" + (centered ? "_M" : string.Empty);
    }

    private static string Hex(Color color) => color.ToString()[1..];

    /// <summary>Writes one graphic style per distinct look into the automatic styles.</summary>
    public static void WriteStyles(XmlWriter writer, PortableDocument document)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (shape, centered) in Enumerate(document))
        {
            if (!written.Add(StyleName(shape, centered)))
            {
                continue;
            }

            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, StyleName(shape, centered));
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "graphic");
            writer.WriteStartElement("style", "graphic-properties", OdtNamespaces.Style);
            writer.WriteAttributeString("draw", "fill", OdtNamespaces.Draw, shape.Fill is null ? "none" : "solid");
            if (shape.Fill is { } fill)
            {
                writer.WriteAttributeString("draw", "fill-color", OdtNamespaces.Draw, fill.ToString());
            }

            if (shape.Outline is { } outline)
            {
                writer.WriteAttributeString("draw", "stroke", OdtNamespaces.Draw, shape.Dash == ShapeDash.Solid ? "solid" : "dash");
                if (shape.Dash != ShapeDash.Solid)
                {
                    writer.WriteAttributeString("draw", "stroke-dash", OdtNamespaces.Draw, shape.Dash == ShapeDash.Dash ? DashName : DotName);
                }

                writer.WriteAttributeString("svg", "stroke-color", OdtNamespaces.Svg, outline.ToString());
                writer.WriteAttributeString("svg", "stroke-width", OdtNamespaces.Svg, Cm(shape.OutlineWidth.Points));
            }
            else
            {
                writer.WriteAttributeString("draw", "stroke", OdtNamespaces.Draw, "none");
            }

            writer.WriteAttributeString("draw", "shadow", OdtNamespaces.Draw, "hidden");
            if (shape.Kind == ShapeKind.TextBox)
            {
                writer.WriteAttributeString("draw", "auto-grow-height", OdtNamespaces.Draw, "false");
                writer.WriteAttributeString("fo", "padding", OdtNamespaces.Fo, "0.14cm");
                writer.WriteAttributeString("fo", "border", OdtNamespaces.Fo, "none");
                if (centered)
                {
                    writer.WriteAttributeString("draw", "textarea-vertical-align", OdtNamespaces.Draw, "middle");
                }
            }

            if (shape.Wrap != ImageWrap.None)
            {
                // As for images (OF-B06): ODF has no top-and-bottom wrap, so no wrap does that.
                writer.WriteAttributeString("style", "wrap", OdtNamespaces.Style, shape.Wrap switch
                {
                    ImageWrap.Square => "parallel",
                    ImageWrap.Through or ImageWrap.Behind or ImageWrap.InFront => "run-through",
                    _ => "none",
                });
                if (shape.Wrap is ImageWrap.Through or ImageWrap.Behind or ImageWrap.InFront)
                {
                    writer.WriteAttributeString("style", "run-through", OdtNamespaces.Style, shape.Wrap == ImageWrap.InFront ? "foreground" : "background");
                }

                OdtContentWriter.WriteFramePosition(writer, FloatPlacement.Of(shape));
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    /// <summary>The dash patterns the outline styles refer to (relative lengths), in <c>office:styles</c>.</summary>
    public static void WriteDashStyles(XmlWriter writer)
    {
        foreach (var (name, length, distance) in new[] { (DashName, "400%", "300%"), (DotName, "100%", "200%") })
        {
            writer.WriteStartElement("draw", "stroke-dash", OdtNamespaces.Draw);
            writer.WriteAttributeString("draw", "name", OdtNamespaces.Draw, name);
            writer.WriteAttributeString("draw", "style", OdtNamespaces.Draw, "rect");
            writer.WriteAttributeString("draw", "dots1", OdtNamespaces.Draw, "1");
            writer.WriteAttributeString("draw", "dots1-length", OdtNamespaces.Draw, length);
            writer.WriteAttributeString("draw", "distance", OdtNamespaces.Draw, distance);
            writer.WriteEndElement();
        }
    }

    /// <summary>
    /// The position attributes of a shape: the offset of an anchored one, and for a rotated one the
    /// transform that turns it clockwise around its center. Lines carry rotated end points instead.
    /// </summary>
    public static void WritePlacement(XmlWriter writer, DrawingShape shape, Length? at_x = null, Length? at_y = null)
    {
        // at_x/at_y place a child of a diagram group (ADR-0030) like an anchored shape.
        var anchored = shape.Wrap != ImageWrap.None || at_x is not null;
        double x = at_x?.Points ?? (anchored ? shape.OffsetX?.Points ?? 0 : 0);
        double y = at_y?.Points ?? (anchored ? shape.OffsetY?.Points ?? 0 : 0);
        if (shape.Kind == ShapeKind.Line)
        {
            double cx = x + shape.Width.Points / 2, cy = y + shape.Height.Points / 2;
            var (x1, y1) = Rotate(x, y, cx, cy, shape.Rotation);
            var (x2, y2) = Rotate(x + shape.Width.Points, y + shape.Height.Points, cx, cy, shape.Rotation);
            writer.WriteAttributeString("svg", "x1", OdtNamespaces.Svg, Cm(x1));
            writer.WriteAttributeString("svg", "y1", OdtNamespaces.Svg, Cm(y1));
            writer.WriteAttributeString("svg", "x2", OdtNamespaces.Svg, Cm(x2));
            writer.WriteAttributeString("svg", "y2", OdtNamespaces.Svg, Cm(y2));
            return;
        }

        writer.WriteAttributeString("svg", "width", OdtNamespaces.Svg, Cm(shape.Width.Points));
        writer.WriteAttributeString("svg", "height", OdtNamespaces.Svg, Cm(shape.Height.Points));
        if (shape.Rotation == 0)
        {
            if (anchored)
            {
                writer.WriteAttributeString("svg", "x", OdtNamespaces.Svg, Cm(x));
                writer.WriteAttributeString("svg", "y", OdtNamespaces.Svg, Cm(y));
            }

            return;
        }

        // draw:transform "rotate(a) translate(t)" turns the shape about its own top left corner and
        // then moves it by t (measured with LibreOffice). To turn about the center c the new center
        // t + R(c) has to be the wanted one: t = position + c - R(c).
        var angle = -shape.Rotation * Math.PI / 180.0;
        double halfWidth = shape.Width.Points / 2, halfHeight = shape.Height.Points / 2;
        var (rx, ry) = Rotate(halfWidth, halfHeight, 0, 0, shape.Rotation);
        writer.WriteAttributeString(
            "draw", "transform", OdtNamespaces.Draw,
            "rotate (" + angle.ToString("0.#####", CultureInfo.InvariantCulture) + ") translate ("
            + Cm(x + halfWidth - rx) + " " + Cm(y + halfHeight - ry) + ")");
    }

    /// <summary>Rotates a point clockwise (page coordinates, y downwards) around a center.</summary>
    internal static (double X, double Y) Rotate(double x, double y, double cx, double cy, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians), dx = x - cx, dy = y - cy;
        return (cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
    }

    /// <summary>The <c>svg:d</c> data and the view box of a freeform, in tenths of a point.</summary>
    public static (string ViewBox, string Data) Path(DrawingShape shape)
    {
        string N(Length value) => ((long)Math.Round(value.Points * 10)).ToString(CultureInfo.InvariantCulture);
        var data = new System.Text.StringBuilder();
        foreach (var step in shape.Path!)
        {
            data.Append(step.Command switch
            {
                ShapePathCommand.MoveTo => $"M {N(step.X)} {N(step.Y)} ",
                ShapePathCommand.LineTo => $"L {N(step.X)} {N(step.Y)} ",
                ShapePathCommand.CubicTo => $"C {N(step.X1)} {N(step.Y1)} {N(step.X2)} {N(step.Y2)} {N(step.X)} {N(step.Y)} ",
                _ => "Z ",
            });
        }

        return ($"0 0 {N(shape.Width)} {N(shape.Height)}", data.ToString().TrimEnd());
    }

    private static string Cm(double points)
        => (points / (72.0 / 2.54)).ToString("0.###", CultureInfo.InvariantCulture) + "cm";
}
