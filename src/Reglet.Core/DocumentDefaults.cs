namespace Reglet;

/// <summary>Dokumentweite Standardwerte (Standardstil).</summary>
public sealed record DocumentDefaults
{
    private string _fontFamily = "Calibri";
    private Length _fontSize = Length.FromPoints(11);
    private ParagraphStyle _paragraphStyle = ParagraphStyle.Default;

    /// <summary>Documented initial values.</summary>
    public static DocumentDefaults Default { get; } = new();

    /// <summary>Default font family.</summary>
    public string FontFamily
    {
        get => _fontFamily;
        init => _fontFamily = !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Font family must not be empty.", nameof(FontFamily));
    }

    /// <summary>Default font size.</summary>
    public Length FontSize
    {
        get => _fontSize;
        init => _fontSize = value.Points > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(FontSize), "Font size must be positive.");
    }

    /// <summary>Default text color.</summary>
    public Color TextColor { get; init; } = Color.Black;

    /// <summary>
    /// Default paragraph style. Readers fill it from <c>w:docDefaults/w:pPrDefault</c> or the ODT
    /// default style; DOCX and ODT write it and the PDF layout applies it to a document built from
    /// the model.
    /// </summary>
    public ParagraphStyle ParagraphStyle
    {
        get => _paragraphStyle;
        init => _paragraphStyle = value ?? throw new ArgumentNullException(nameof(ParagraphStyle));
    }
}
