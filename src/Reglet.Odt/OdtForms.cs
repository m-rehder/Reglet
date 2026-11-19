using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// <see cref="FormField"/> as ODF form controls (ADR-0024): the controls live in one <c>form:form</c> in
/// <c>office:forms</c>, a <c>draw:control</c> in a paragraph places each one. Text, text area, check box,
/// list box (drop-down) and radio button map directly; the buttons of a radio group share the control name.
/// </summary>
internal static class OdtForms
{
    private const string FormNamespace = OdtNamespaces.Form;
    private static readonly XNamespace Form = OdtNamespaces.Form;
    private static readonly XNamespace Draw = OdtNamespaces.Draw;
    private static readonly XNamespace Svg = OdtNamespaces.Svg;
    private static readonly XNamespace Text = OdtNamespaces.Text;

    /// <summary>The form fields of the body in document order (also in tables, lists and text boxes), with their control ids.</summary>
    internal static Dictionary<FormField, string> Collect(PortableDocument document)
    {
        var ids = new Dictionary<FormField, string>(ReferenceEqualityComparer.Instance);
        foreach (var section in document.Sections)
        {
            Collect(section.Blocks, ids);
        }

        return ids;
    }

    private static void Collect(IReadOnlyList<Block> blocks, Dictionary<FormField, string> ids)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case FormField field:
                    ids[field] = "control" + (ids.Count + 1).ToString(CultureInfo.InvariantCulture);
                    break;
                case TableBlock table:
                    foreach (var cell in table.Rows.SelectMany(row => row.Cells))
                    {
                        Collect(cell.Blocks, ids);
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Collect(item.Blocks, ids);
                    }

                    break;
                case DrawingShape { Content: { } content }:
                    Collect(content, ids);
                    break;
                case Diagram diagram:
                    Collect(OdtDiagrams.Shapes(diagram).SelectMany(placed => placed.Shape.Content ?? []).ToList(), ids);
                    break;
            }
        }
    }

    /// <summary>Writes <c>office:forms</c> with every control; nothing when there is none.</summary>
    internal static void WriteForms(XmlWriter writer, IReadOnlyDictionary<FormField, string> ids, InvalidXmlCharacterBehavior xmlBehavior)
    {
        if (ids.Count == 0)
        {
            return;
        }

        writer.WriteStartElement("office", "forms", OdtNamespaces.Office);
        writer.WriteAttributeString("form", "automatic-focus", FormNamespace, "false");
        writer.WriteAttributeString("form", "apply-design-mode", FormNamespace, "false");
        writer.WriteStartElement("form", "form", FormNamespace);
        writer.WriteAttributeString("form", "name", FormNamespace, "Form");
        foreach (var (field, id) in ids)
        {
            WriteControl(writer, field, id, xmlBehavior);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteControl(XmlWriter writer, FormField field, string id, InvalidXmlCharacterBehavior xmlBehavior)
    {
        string Clean(string text) => XmlText.Clean(text, xmlBehavior);
        var element = field.Kind switch
        {
            FormFieldKind.Text when field.Multiline => "textarea",
            FormFieldKind.Text => "text",
            FormFieldKind.CheckBox => "checkbox",
            FormFieldKind.DropDown => "listbox",
            _ => "radio",
        };
        writer.WriteStartElement("form", element, FormNamespace);
        writer.WriteAttributeString("form", "name", FormNamespace, Clean(field.Name));
        // No form:control-implementation: LibreOffice drops every control that carries it (checked 2026-11-17).
        writer.WriteAttributeString("xml", "id", null, id);
        writer.WriteAttributeString("form", "id", FormNamespace, id);
        if (!string.IsNullOrEmpty(field.ToolTip))
        {
            writer.WriteAttributeString("form", "title", FormNamespace, Clean(field.ToolTip));
        }

        if (field.ReadOnly)
        {
            writer.WriteAttributeString("form", "readonly", FormNamespace, "true");
        }

        switch (field.Kind)
        {
            case FormFieldKind.Text:
                if (field.MaxLength is { } max)
                {
                    writer.WriteAttributeString("form", "max-length", FormNamespace, max.ToString(CultureInfo.InvariantCulture));
                }

                if (field.Multiline)
                {
                    foreach (var line in Clean(field.Value ?? string.Empty).Split('\n'))
                    {
                        writer.WriteElementString("text", "p", OdtNamespaces.Text, line.TrimEnd('\r'));
                    }
                }
                else if (!string.IsNullOrEmpty(field.Value))
                {
                    writer.WriteAttributeString("form", "current-value", FormNamespace, Clean(field.Value));
                    writer.WriteAttributeString("form", "value", FormNamespace, Clean(field.Value));
                }

                break;
            case FormFieldKind.CheckBox:
                writer.WriteAttributeString("form", "current-state", FormNamespace, field.Checked ? "checked" : "unchecked");
                writer.WriteAttributeString("form", "value", FormNamespace, "on");
                break;
            case FormFieldKind.DropDown:
                writer.WriteAttributeString("form", "dropdown", FormNamespace, "true");
                foreach (var option in field.Options!)
                {
                    writer.WriteStartElement("form", "option", FormNamespace);
                    writer.WriteAttributeString("form", "value", FormNamespace, Clean(option));
                    writer.WriteAttributeString("form", "label", FormNamespace, Clean(option));
                    if (option == field.Value)
                    {
                        writer.WriteAttributeString("form", "current-selected", FormNamespace, "true");
                    }

                    writer.WriteEndElement();
                }

                break;
            default:
                writer.WriteAttributeString("form", "value", FormNamespace, Clean(field.ExportValue ?? string.Empty));
                writer.WriteAttributeString("form", "current-selected", FormNamespace, field.Checked ? "true" : "false");
                break;
        }

        writer.WriteEndElement();
    }

    /// <summary>The paragraph that places the control.</summary>
    internal static void WritePlacement(XmlWriter writer, FormField field, string id, string? paragraphStyle)
    {
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        if (paragraphStyle is not null)
        {
            writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, paragraphStyle);
        }

        writer.WriteStartElement("draw", "control", OdtNamespaces.Draw);
        writer.WriteAttributeString("text", "anchor-type", OdtNamespaces.Text, "as-char");
        writer.WriteAttributeString("svg", "width", OdtNamespaces.Svg, Centimeters(field.Width.Points));
        writer.WriteAttributeString("svg", "height", OdtNamespaces.Svg, Centimeters(field.Height.Points));
        writer.WriteAttributeString("draw", "control", OdtNamespaces.Draw, id);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string Centimeters(double points)
        => (points * 2.54 / 72).ToString("0.###", CultureInfo.InvariantCulture) + "cm";

    /// <summary>The controls of <c>office:forms</c> by their id (<c>form:id</c> or <c>xml:id</c>).</summary>
    internal static Dictionary<string, XElement> ReadControls(XElement content)
    {
        var controls = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var control in content.Descendants(OfficeForms).Elements(Form + "form").Elements())
        {
            var id = (string?)control.Attribute(Form + "id") ?? (string?)control.Attribute(XNamespace.Xml + "id");
            if (id is not null)
            {
                controls[id] = control;
            }
        }

        return controls;
    }

    private static readonly XName OfficeForms = XName.Get("forms", OdtNamespaces.Office);

    /// <summary>
    /// Maps the control a <c>draw:control</c> refers to; null for a control kind the model does not have
    /// (button, password, formatted field ...) or data that cannot form a field.
    /// </summary>
    internal static FormField? ToField(XElement placement, IReadOnlyDictionary<string, XElement> controls)
    {
        if (((string?)placement.Attribute(Draw + "control")) is not { } id || !controls.TryGetValue(id, out var control))
        {
            return null;
        }

        var name = (string?)control.Attribute(Form + "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = id;
        }

        name = name.Replace('.', '_');
        var width = Parse((string?)placement.Attribute(Svg + "width"));
        var height = Parse((string?)placement.Attribute(Svg + "height"));
        var readOnly = (string?)control.Attribute(Form + "readonly") == "true";
        var toolTip = (string?)control.Attribute(Form + "title");
        int? maxLength = int.TryParse((string?)control.Attribute(Form + "max-length"), NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max > 0 ? max : null;
        try
        {
            switch (control.Name.LocalName)
            {
                case "text":
                {
                    var value = (string?)control.Attribute(Form + "current-value") ?? (string?)control.Attribute(Form + "value");
                    return new FormField(FormFieldKind.Text, name, width, height)
                    {
                        Value = string.IsNullOrEmpty(value) ? null : value,

                        // LibreOffice saves a multi-line text as form:text with line breaks in the value.
                        Multiline = value?.Contains('\n') == true,
                        MaxLength = maxLength,
                        ReadOnly = readOnly,
                        ToolTip = toolTip,
                    };
                }

                case "textarea":
                {
                    var value = string.Join('\n', control.Elements(Text + "p").Select(paragraph => paragraph.Value));
                    return new FormField(FormFieldKind.Text, name, width, height)
                    {
                        Multiline = true,
                        Value = value.Length == 0 ? null : value,
                        MaxLength = maxLength,
                        ReadOnly = readOnly,
                        ToolTip = toolTip,
                    };
                }

                case "checkbox":
                    return new FormField(FormFieldKind.CheckBox, name, width, height)
                    {
                        Checked = (string?)control.Attribute(Form + "current-state") == "checked",
                        ReadOnly = readOnly,
                        ToolTip = toolTip,
                    };
                case "radio":
                    return new FormField(FormFieldKind.RadioButton, name, width, height)
                    {
                        ExportValue = (string?)control.Attribute(Form + "value") ?? "on",
                        Checked = (string?)control.Attribute(Form + "current-selected") == "true",
                        ReadOnly = readOnly,
                        ToolTip = toolTip,
                    };
                case "listbox":
                case "combobox":
                {
                    var options = control.Elements(Form + "option")
                        .Select(option => (string?)option.Attribute(Form + "label") ?? (string?)option.Attribute(Form + "value") ?? option.Value)
                        .Where(option => option.Length > 0).ToList();
                    var selected = control.Elements(Form + "option").FirstOrDefault(option => (string?)option.Attribute(Form + "current-selected") == "true");
                    var value = selected is null
                        ? (string?)control.Attribute(Form + "current-value")
                        : (string?)selected.Attribute(Form + "label") ?? (string?)selected.Attribute(Form + "value");
                    return options.Count == 0
                        ? null
                        : new FormField(FormFieldKind.DropDown, name, width, height, options)
                        {
                            Value = value is not null && options.Contains(value) ? value : null,
                            ReadOnly = readOnly,
                            ToolTip = toolTip,
                        };
                }
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    // Lengths of draw:control: centimeters, millimeters, inches or points; anything else keeps the default size.
    private static Length? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var units = new (string Suffix, double Factor)[] { ("cm", 72 / 2.54), ("mm", 72 / 25.4), ("in", 72), ("pt", 1) };
        foreach (var (suffix, factor) in units)
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(text[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                return Length.FromPoints(value * factor);
            }
        }

        return null;
    }
}
