using System.Globalization;

namespace Reglet;

/// <summary>
/// Writes the form fields of a document as AcroForm widgets (ADR-0024): one widget annotation with
/// an appearance stream per field, a parent field for each group of radio buttons.
/// </summary>
internal sealed class PdfFormWriter
{
    private const double Padding = 2;

    private readonly PdfFileWriter _file;
    private readonly PdfFontCatalog _fonts;
    private readonly int _resourcesObject;
    private readonly UnsupportedFeatureHandler _handler;
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RadioGroup> _groups = new(StringComparer.Ordinal);

    internal PdfFormWriter(PdfFileWriter file, PdfFontCatalog fonts, int resourcesObject, UnsupportedFeatureHandler handler, bool pdfA)
    {
        PdfA = pdfA;
        _file = file;
        _fonts = fonts;
        _resourcesObject = resourcesObject;
        _handler = handler;
    }

    /// <summary>The top-level fields for <c>/Fields</c> of the AcroForm (radio groups are added by <see cref="Complete"/>).</summary>
    internal List<int> Fields { get; } = [];

    /// <summary>Whether the file is PDF/A: the default appearance then names an embedded font.</summary>
    internal bool PdfA { get; }

    /// <summary>
    /// The font the default appearance (<c>DA</c>) and the default resources (<c>DR</c>) of the AcroForm
    /// name: the embedded font of the document in PDF/A, otherwise Helvetica (<c>Helv</c>), which a viewer
    /// that edits a value always has.
    /// </summary>
    internal string DefaultFontResource => PdfA ? _fonts.GetFont(null, bold: false, italic: false).ResourceName : "Helv";

    /// <summary>Whether any widget was written.</summary>
    internal bool HasFields => Fields.Count > 0 || _groups.Count > 0;

    /// <summary>
    /// Writes the widget of a field placed at the rectangle (bottom left corner x/y) and returns its
    /// annotation object, or null when the field is skipped (and reported).
    /// </summary>
    internal int? WriteWidget(FormField field, double x, double y, double width, double height, int pageObject)
    {
        if (field.Kind == FormFieldKind.RadioButton)
        {
            if (string.IsNullOrEmpty(field.ExportValue))
            {
                _handler.Report($"PDF: the radio button '{field.Name}' has no export value and is skipped.");
                return null;
            }

            if (_names.Contains(field.Name) && !_groups.ContainsKey(field.Name))
            {
                return Duplicate(field);
            }
        }
        else if (!_names.Add(field.Name))
        {
            return Duplicate(field);
        }

        var widget = _file.ReserveObject();
        RadioGroup? group = null;
        if (field.Kind == FormFieldKind.RadioButton)
        {
            if (!_groups.TryGetValue(field.Name, out group))
            {
                _names.Add(field.Name);
                group = new RadioGroup(_file.ReserveObject());
                _groups[field.Name] = group;
            }

            if (group.Values.Contains(field.ExportValue!))
            {
                _handler.Report($"PDF: the radio button value '{field.ExportValue}' of '{field.Name}' is used twice; the second button is skipped.");
                return null;
            }

            group.Values.Add(field.ExportValue!);
            group.Kids.Add(widget);
            if (field.Checked && group.Selected is null)
            {
                group.Selected = field.ExportValue;
            }
        }
        else
        {
            Fields.Add(widget);
        }

        var onName = field.Kind == FormFieldKind.RadioButton ? PdfAnnotationName(field.ExportValue!) : "Yes";
        var appearances = WriteAppearances(field, width, height);
        var fontSize = field.FontSize?.Points ?? 10;
        var selected = field.Kind == FormFieldKind.RadioButton ? field.Checked && group!.Selected == field.ExportValue : field.Checked;
        _file.WriteObject(widget, tokens => tokens.WriteDictionary(dictionary =>
        {
            dictionary.WriteName("Type");
            dictionary.WriteName("Annot");
            dictionary.WriteName("Subtype");
            dictionary.WriteName("Widget");
            dictionary.WriteName("Rect");
            dictionary.WriteArray(rect =>
            {
                rect.WriteNumber(x);
                rect.WriteNumber(y);
                rect.WriteNumber(x + width);
                rect.WriteNumber(y + height);
            });
            dictionary.WriteName("P");
            dictionary.WriteReference(pageObject);

            // Print flag: the widget belongs to the printed output (required for PDF/A).
            dictionary.WriteName("F");
            dictionary.WriteInteger(4);
            if (group is not null)
            {
                dictionary.WriteName("Parent");
                dictionary.WriteReference(group.Object);
            }
            else
            {
                WriteFieldEntries(dictionary, field, fontSize, onName);
            }

            if (field.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton)
            {
                dictionary.WriteName("AS");
                dictionary.WriteName(selected ? onName : "Off");
            }

            dictionary.WriteName("MK");
            dictionary.WriteDictionary(mk =>
            {
                mk.WriteName("BC");
                mk.WriteArray(color => { color.WriteInteger(0); color.WriteInteger(0); color.WriteInteger(0); });
                mk.WriteName("BG");
                mk.WriteArray(color => { color.WriteInteger(1); color.WriteInteger(1); color.WriteInteger(1); });
            });
            dictionary.WriteName("AP");
            dictionary.WriteDictionary(ap =>
            {
                ap.WriteName("N");
                if (appearances.Single is { } single)
                {
                    ap.WriteReference(single);
                }
                else
                {
                    ap.WriteDictionary(states =>
                    {
                        states.WriteName(onName);
                        states.WriteReference(appearances.On);
                        states.WriteName("Off");
                        states.WriteReference(appearances.Off);
                    });
                }
            });
        }));
        return widget;
    }

    /// <summary>Writes the parent fields of the radio groups; call once after the last page.</summary>
    internal void Complete()
    {
        foreach (var (name, group) in _groups)
        {
            _file.WriteObject(group.Object, tokens => tokens.WriteDictionary(dictionary =>
            {
                dictionary.WriteName("FT");
                dictionary.WriteName("Btn");
                dictionary.WriteName("Ff");

                // Radio (bit 16) and NoToggleToOff (bit 15).
                dictionary.WriteInteger(32768 + 16384);
                dictionary.WriteName("T");
                dictionary.WriteLiteralString(name);
                dictionary.WriteName("V");
                dictionary.WriteName(group.Selected is { } value ? PdfAnnotationName(value) : "Off");
                dictionary.WriteName("Kids");
                dictionary.WriteArray(kids =>
                {
                    foreach (var kid in group.Kids)
                    {
                        kids.WriteReference(kid);
                    }
                });
            }));
            Fields.Add(group.Object);
        }

        _groups.Clear();
    }

    private int? Duplicate(FormField field)
    {
        _handler.Report($"PDF: the field name '{field.Name}' is used twice; the second field is skipped.");
        return null;
    }

    private void WriteFieldEntries(PdfTokenWriter dictionary, FormField field, double fontSize, string onName)
    {
        dictionary.WriteName("FT");
        dictionary.WriteName(field.Kind switch
        {
            FormFieldKind.Text => "Tx",
            FormFieldKind.DropDown => "Ch",
            _ => "Btn",
        });
        dictionary.WriteName("T");
        dictionary.WriteLiteralString(field.Name);
        if (!string.IsNullOrEmpty(field.ToolTip))
        {
            dictionary.WriteName("TU");
            dictionary.WriteLiteralString(field.ToolTip);
        }

        // Read-only (bit 1), required (bit 2); multiline (bit 13), combo (bit 18).
        var flags = (field.ReadOnly ? 1 : 0) | (field.Required ? 2 : 0)
            | (field.Kind == FormFieldKind.Text && field.Multiline ? 1 << 12 : 0)
            | (field.Kind == FormFieldKind.DropDown ? 1 << 17 : 0);
        if (flags != 0)
        {
            dictionary.WriteName("Ff");
            dictionary.WriteInteger(flags);
        }

        switch (field.Kind)
        {
            case FormFieldKind.Text:
                if (!string.IsNullOrEmpty(field.Value))
                {
                    dictionary.WriteName("V");
                    dictionary.WriteLiteralString(field.Value);
                }

                if (field.MaxLength is { } max)
                {
                    dictionary.WriteName("MaxLen");
                    dictionary.WriteInteger(max);
                }

                break;
            case FormFieldKind.DropDown:
                dictionary.WriteName("Opt");
                dictionary.WriteArray(options =>
                {
                    foreach (var option in field.Options!)
                    {
                        options.WriteLiteralString(option);
                    }
                });
                if (field.Value is not null)
                {
                    dictionary.WriteName("V");
                    dictionary.WriteLiteralString(field.Value);
                }

                break;
            default:
                dictionary.WriteName("V");
                dictionary.WriteName(field.Checked ? onName : "Off");
                break;
        }

        if (field.Kind is FormFieldKind.Text or FormFieldKind.DropDown)
        {
            dictionary.WriteName("DA");
            dictionary.WriteLiteralString(string.Create(CultureInfo.InvariantCulture, $"0 g /{DefaultFontResource} {fontSize:0.##} Tf"));
        }
    }

    private (int? Single, int On, int Off) WriteAppearances(FormField field, double width, double height)
    {
        switch (field.Kind)
        {
            case FormFieldKind.CheckBox:
            case FormFieldKind.RadioButton:
                return (null, WriteForm(Button(field, width, height, on: true), width, height), WriteForm(Button(field, width, height, on: false), width, height));
            default:
                return (WriteForm(TextAppearance(field, width, height), width, height), 0, 0);
        }
    }

    private static byte[] Button(FormField field, double width, double height, bool on)
    {
        var content = new PdfContentWriter();
        content.FillRectangle(0, 0, width, height, Color.White);
        var black = Color.Black;
        if (field.Kind == FormFieldKind.RadioButton)
        {
            var (cx, cy, r) = (width / 2, height / 2, Math.Min(width, height) / 2 - 0.5);
            Circle(content, cx, cy, r);
            content.SaveState().SetStrokeColor(black).SetLineWidth(1).Stroke().RestoreState();
            if (on)
            {
                content.SaveState().SetFillColor(black);
                Circle(content, cx, cy, r * 0.5);
                content.Fill().RestoreState();
            }
        }
        else
        {
            content.StrokeRectangle(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1), black, 1);
            if (on)
            {
                content.SaveState().SetStrokeColor(black).SetLineWidth(Math.Max(1, Math.Min(width, height) / 8));
                content.MoveTo(width * 0.22, height * 0.5).LineTo(width * 0.42, height * 0.25).LineTo(width * 0.78, height * 0.75).Stroke().RestoreState();
            }
        }

        return content.ToArray();
    }

    private static void Circle(PdfContentWriter content, double cx, double cy, double r)
    {
        const double Kappa = 0.5522847498;
        var k = r * Kappa;
        content.MoveTo(cx + r, cy)
            .CurveTo(cx + r, cy + k, cx + k, cy + r, cx, cy + r)
            .CurveTo(cx - k, cy + r, cx - r, cy + k, cx - r, cy)
            .CurveTo(cx - r, cy - k, cx - k, cy - r, cx, cy - r)
            .CurveTo(cx + k, cy - r, cx + r, cy - k, cx + r, cy)
            .ClosePath();
    }

    private byte[] TextAppearance(FormField field, double width, double height)
    {
        var content = new PdfContentWriter();
        content.FillRectangle(0, 0, width, height, Color.White);
        content.StrokeRectangle(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1), Color.Black, 1);
        var fontSize = field.FontSize?.Points ?? 10;
        var font = _fonts.GetFont(null, bold: false, italic: false);
        var text = field.Value ?? string.Empty;
        if (text.Length > 0 && !font.CanEncode(text))
        {
            _handler.Report($"PDF: the value of the field '{field.Name}' has characters the font cannot show; the field is drawn empty.");
            text = string.Empty;
        }

        content.SaveState().Rectangle(1, 1, Math.Max(0, width - 2), Math.Max(0, height - 2)).Clip().EndPath();
        var line = font.Metrics.LineHeightEm * fontSize;

        // One line sits in the middle of the box, several lines start at the top.
        var top = field.Kind == FormFieldKind.Text && field.Multiline
            ? height - Padding - font.Metrics.AscentEm * fontSize
            : (height - fontSize) / 2 + fontSize * 0.2;
        var y = top;
        foreach (var part in text.Split('\n'))
        {
            PdfPageRenderer.WriteLineTo(content, font, part.TrimEnd('\r'), fontSize, Color.Black, Padding + 1, y, 0, 100, null);
            y -= line;
        }

        content.RestoreState();
        if (field.Kind == FormFieldKind.DropDown)
        {
            // The arrow of a combo box.
            var side = Math.Min(height - 6, 8);
            var ax = width - side - 4;
            var ay = (height - side / 2) / 2;
            content.SaveState().SetFillColor(Color.Black).MoveTo(ax, ay + side / 2).LineTo(ax + side, ay + side / 2).LineTo(ax + side / 2, ay).ClosePath().Fill().RestoreState();
        }

        return content.ToArray();
    }

    private int WriteForm(byte[] content, double width, double height)
    {
        var form = _file.ReserveObject();
        _file.WriteStreamObject(
            form,
            content,
            string.Create(CultureInfo.InvariantCulture, $"/Type /XObject /Subtype /Form /BBox [0 0 {width:0.###} {height:0.###}] /Resources {_resourcesObject} 0 R"));
        return form;
    }

    private static string PdfAnnotationName(string name) => PdfPageRenderer.PdfAnnotationName(name);

    private sealed class RadioGroup(int obj)
    {
        internal int Object { get; } = obj;

        internal List<int> Kids { get; } = [];

        internal List<string> Values { get; } = [];

        internal string? Selected { get; set; }
    }
}
