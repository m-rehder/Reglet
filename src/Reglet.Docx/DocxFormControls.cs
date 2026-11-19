using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// <see cref="FormField"/> as a content control (<c>w:sdt</c>, ADR-0024): text, drop-down list and check
/// box. Word has no radio button control, so a radio button is written as a check box whose tag says
/// <c>pd-radio:name=value</c>; reading restores it.
/// </summary>
internal static class DocxFormControls
{
    internal const string Word2010Namespace = "http://schemas.microsoft.com/office/word/2010/wordml";

    private const string RadioTag = "pd-radio:";
    private const string Wml = DocxConstants.WordprocessingML;
    private static readonly XNamespace W = Wml;
    private static readonly XNamespace W14 = Word2010Namespace;

    /// <summary>Writes the control as a paragraph that holds it; reports what Word cannot keep.</summary>
    internal static void Write(XmlWriter writer, FormField field, DocxWriteContext context)
    {
        var clean = (string text) => XmlText.Clean(text, context.XmlBehavior);
        if (field.Required || field.MaxLength is not null)
        {
            context.Handler.Report($"DOCX: 'Required' and 'MaxLength' of the form field '{field.Name}' are not written (a content control has neither).");
        }

        var radio = field.Kind == FormFieldKind.RadioButton;
        if (radio)
        {
            context.Handler.Report($"DOCX: the radio button '{field.Name}' is written as a check box; Word has no radio button control, the group is restored when reading.");
        }

        var checkBox = radio || field.Kind == FormFieldKind.CheckBox;
        var text = checkBox ? (field.Checked ? "☒" : "☐") : field.Value ?? string.Empty;
        var placeholder = !checkBox && text.Length == 0;
        if (placeholder)
        {
            text = field.ToolTip ?? field.Name;
        }

        writer.WriteStartElement("w", "p", Wml);
        writer.WriteStartElement("w", "sdt", Wml);
        writer.WriteStartElement("w", "sdtPr", Wml);
        if (!string.IsNullOrEmpty(field.ToolTip))
        {
            writer.WriteStartElement("w", "alias", Wml);
            writer.WriteAttributeString("w", "val", Wml, clean(field.ToolTip));
            writer.WriteEndElement();
        }

        writer.WriteStartElement("w", "tag", Wml);
        writer.WriteAttributeString("w", "val", Wml, clean(radio ? $"{RadioTag}{field.Name}={field.ExportValue}" : field.Name));
        writer.WriteEndElement();
        writer.WriteStartElement("w", "id", Wml);
        writer.WriteAttributeString("w", "val", Wml, context.NextSdtId().ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        if (field.ReadOnly)
        {
            writer.WriteStartElement("w", "lock", Wml);
            writer.WriteAttributeString("w", "val", Wml, "sdtContentLocked");
            writer.WriteEndElement();
        }

        if (placeholder)
        {
            writer.WriteElementString("w", "showingPlcHdr", Wml, null);
        }

        switch (field.Kind)
        {
            case FormFieldKind.Text:
                writer.WriteStartElement("w", "text", Wml);
                if (field.Multiline)
                {
                    writer.WriteAttributeString("w", "multiLine", Wml, "1");
                }

                writer.WriteEndElement();
                break;
            case FormFieldKind.DropDown:
                writer.WriteStartElement("w", "dropDownList", Wml);
                foreach (var option in field.Options!)
                {
                    writer.WriteStartElement("w", "listItem", Wml);
                    writer.WriteAttributeString("w", "displayText", Wml, clean(option));
                    writer.WriteAttributeString("w", "value", Wml, clean(option));
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                break;
            default:
                writer.WriteStartElement("w14", "checkbox", Word2010Namespace);
                WriteState(writer, "checked", field.Checked ? "1" : "0", null);
                WriteState(writer, "checkedState", "2612", "MS Gothic");
                WriteState(writer, "uncheckedState", "2610", "MS Gothic");
                writer.WriteEndElement();
                break;
        }

        writer.WriteEndElement(); // sdtPr
        writer.WriteStartElement("w", "sdtContent", Wml);
        writer.WriteStartElement("w", "r", Wml);
        if (checkBox || placeholder || field.FontSize is not null)
        {
            writer.WriteStartElement("w", "rPr", Wml);
            if (checkBox)
            {
                writer.WriteStartElement("w", "rFonts", Wml);
                foreach (var attribute in new[] { "ascii", "eastAsia", "hAnsi" })
                {
                    writer.WriteAttributeString("w", attribute, Wml, "MS Gothic");
                }

                writer.WriteEndElement();
            }

            if (placeholder)
            {
                writer.WriteStartElement("w", "color", Wml);
                writer.WriteAttributeString("w", "val", Wml, "808080");
                writer.WriteEndElement();
            }

            if (field.FontSize is { } size)
            {
                writer.WriteStartElement("w", "sz", Wml);
                writer.WriteAttributeString("w", "val", Wml, Math.Round(size.Points * 2).ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        // Several lines of a multi-line text are separate lines of the run.
        var lines = clean(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                writer.WriteElementString("w", "br", Wml, null);
            }

            writer.WriteStartElement("w", "t", Wml);
            writer.WriteAttributeString("xml", "space", null, "preserve");
            writer.WriteString(lines[i].TrimEnd('\r'));
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // r
        writer.WriteEndElement(); // sdtContent
        writer.WriteEndElement(); // sdt
        writer.WriteEndElement(); // p
    }

    private static void WriteState(XmlWriter writer, string name, string value, string? font)
    {
        writer.WriteStartElement("w14", name, Word2010Namespace);
        writer.WriteAttributeString("w14", "val", Word2010Namespace, value);
        if (font is not null)
        {
            writer.WriteAttributeString("w14", "font", Word2010Namespace, font);
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// Reads a content control that is a text, drop-down or check box control as a form field; null for
    /// any other control (its content is then read as text) or when its data cannot form a field.
    /// </summary>
    internal static FormField? TryRead(XElement sdt)
    {
        var properties = sdt.Element(W + "sdtPr");
        if (properties is null)
        {
            return null;
        }

        var content = sdt.Element(W + "sdtContent");
        var tag = properties.Element(W + "tag")?.Attribute(W + "val")?.Value;
        var id = properties.Element(W + "id")?.Attribute(W + "val")?.Value;
        var alias = properties.Element(W + "alias")?.Attribute(W + "val")?.Value;
        var showingPlaceholder = properties.Element(W + "showingPlcHdr") is not null;
        var shown = showingPlaceholder ? null : TextOf(content);
        var name = FieldName(tag, alias, id);
        var readOnly = properties.Element(W + "lock")?.Attribute(W + "val")?.Value is "sdtContentLocked" or "contentLocked";
        var size = content?.Descendants(W + "sz").Select(sz => (double?)ParseHalfPoints(sz.Attribute(W + "val")?.Value)).FirstOrDefault(v => v is > 0);
        var fontSize = size is { } points ? Length.FromPoints(points) : (Length?)null;
        try
        {
            if (properties.Element(W14 + "checkbox") is { } checkbox)
            {
                var value = checkbox.Element(W14 + "checked")?.Attribute(W14 + "val")?.Value;
                var isChecked = value is "1" or "true" or "on";
                if (tag?.StartsWith(RadioTag, StringComparison.Ordinal) == true && tag[RadioTag.Length..].LastIndexOf('=') is var cut and > 0)
                {
                    var rest = tag[RadioTag.Length..];
                    return new FormField(FormFieldKind.RadioButton, Safe(rest[..cut]) ?? name)
                    {
                        ExportValue = rest[(cut + 1)..],
                        Checked = isChecked,
                        ReadOnly = readOnly,
                        ToolTip = alias,
                    };
                }

                return new FormField(FormFieldKind.CheckBox, name) { Checked = isChecked, ReadOnly = readOnly, ToolTip = alias };
            }

            if (properties.Element(W + "text") is { } text)
            {
                return new FormField(FormFieldKind.Text, name)
                {
                    Value = string.IsNullOrEmpty(shown) ? null : shown,
                    Multiline = text.Attribute(W + "multiLine")?.Value is "1" or "true" or "on",
                    ReadOnly = readOnly,
                    ToolTip = alias,
                    FontSize = fontSize,
                };
            }

            var list = properties.Element(W + "dropDownList") ?? properties.Element(W + "comboBox");
            if (list is not null)
            {
                var options = list.Elements(W + "listItem")
                    .Select(item => item.Attribute(W + "displayText")?.Value ?? item.Attribute(W + "value")?.Value)
                    .Where(option => !string.IsNullOrEmpty(option)).Select(option => option!).ToList();
                if (options.Count == 0)
                {
                    return null;
                }

                return new FormField(FormFieldKind.DropDown, name, options: options)
                {
                    Value = shown is not null && options.Contains(shown) ? shown : null,
                    ReadOnly = readOnly,
                    ToolTip = alias,
                    FontSize = fontSize,
                };
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    private static string? Safe(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Replace('.', '_');

    // A control without a tag still needs a name: the alias, then the id of the control.
    private static string FieldName(string? tag, string? alias, string? id)
        => Safe(tag) ?? Safe(alias) ?? "field" + (id ?? "0");

    private static double ParseHalfPoints(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var half) ? half / 2 : 0;

    private static string? TextOf(XElement? content)
    {
        if (content is null)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var node in content.Descendants())
        {
            if (node.Name == W + "t")
            {
                builder.Append(node.Value);
            }
            else if (node.Name == W + "br" || node.Name == W + "p" && node.PreviousNode is not null)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }
}
