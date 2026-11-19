namespace Reglet;

/// <summary>The kind of a <see cref="FormField"/> (ADR-0024).</summary>
public enum FormFieldKind
{
    /// <summary>A text entry (single line, or several lines with <see cref="FormField.Multiline"/>).</summary>
    Text,

    /// <summary>A box that is checked or not.</summary>
    CheckBox,

    /// <summary>A choice of one entry from <see cref="FormField.Options"/>.</summary>
    DropDown,

    /// <summary>One button of a group of radio buttons; the buttons of a group share the name.</summary>
    RadioButton,
}

/// <summary>
/// A fillable form field (ADR-0024), placed in the text flow like a shape and also allowed in table
/// cells. Only the PDF writer writes it so far (an AcroForm widget); DOCX and ODT report it.
/// </summary>
public sealed class FormField : Block
{
    private string? _value;
    private int? _maxLength;

    /// <summary>Creates a form field.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="name">
    /// The field name; unique in the document (radio buttons of one group share it). It must not be
    /// empty or contain a period.
    /// </param>
    /// <param name="width">The width; 150 pt for text and drop-down, 12 pt for a check box or radio button when null.</param>
    /// <param name="height">The height; 20 pt for text and drop-down, 12 pt for a check box or radio button when null.</param>
    /// <param name="options">The entries of a drop-down (at least one, no empty entry); null for another kind.</param>
    /// <exception cref="ArgumentException">
    /// The name is empty or has a period, options are missing for a drop-down or given for another kind, or
    /// an entry is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The kind is unknown or a size is not positive.</exception>
    public FormField(FormFieldKind kind, string name, Length? width = null, Length? height = null, IEnumerable<string>? options = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown form field kind.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('.'))
        {
            throw new ArgumentException("A field name must not contain a period.", nameof(name));
        }

        var small = kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton;
        Width = width ?? Length.FromPoints(small ? 12 : 150);
        Height = height ?? Length.FromPoints(small ? 12 : 20);
        if (Width.Points <= 0 || Height.Points <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A form field needs a positive size.");
        }

        if ((options is not null) != (kind == FormFieldKind.DropDown))
        {
            throw new ArgumentException("Only a drop-down has options, and a drop-down needs them.", nameof(options));
        }

        Kind = kind;
        Name = name;
        if (options is not null)
        {
            Options = ModelCollections.Snapshot(options, nameof(options));
            if (Options.Count == 0 || Options.Any(string.IsNullOrEmpty))
            {
                throw new ArgumentException("A drop-down needs at least one entry and no entry may be empty.", nameof(options));
            }
        }
    }

    /// <summary>The kind.</summary>
    public FormFieldKind Kind { get; }

    /// <summary>The field name.</summary>
    public string Name { get; }

    /// <summary>The width.</summary>
    public Length Width { get; }

    /// <summary>The height.</summary>
    public Length Height { get; }

    /// <summary>The entries of a drop-down; null for another kind.</summary>
    public IReadOnlyList<string>? Options { get; }

    /// <summary>The text of a text field or the selected entry of a drop-down (one of <see cref="Options"/>).</summary>
    public string? Value
    {
        get => _value;
        init
        {
            if (value is not null && Options is { } options && !options.Contains(value))
            {
                throw new ArgumentException("The value must be one of the options.", nameof(Value));
            }

            _value = value;
        }
    }

    /// <summary>Whether a check box or radio button is checked.</summary>
    public bool Checked { get; init; }

    /// <summary>
    /// The value the group takes when this radio button is chosen (required for a radio button; the
    /// values of one group differ). Ignored by the other kinds.
    /// </summary>
    public string? ExportValue { get; init; }

    /// <summary>Whether a text field takes several lines.</summary>
    public bool Multiline { get; init; }

    /// <summary>The most characters a text field takes; null for no limit.</summary>
    public int? MaxLength
    {
        get => _maxLength;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value ?? 1, 1, nameof(MaxLength));
            _maxLength = value;
        }
    }

    /// <summary>Whether the user may change the field.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Whether the field must be filled in before the form is submitted.</summary>
    public bool Required { get; init; }

    /// <summary>The font size of the text; 10 pt when null.</summary>
    public Length? FontSize { get; init; }

    /// <summary>The name a screen reader or tooltip shows (the PDF entry <c>TU</c>).</summary>
    public string? ToolTip { get; init; }
}
