namespace Reglet;

/// <summary>
/// writes the font objects of the default fonts (Type1, WinAnsiEncoding) once and
/// registers them as resources.
/// </summary>
internal sealed class PdfStandardFontManager
{
    private readonly PdfFileWriter _file;
    private readonly PdfResourceManager _resources;

    public PdfStandardFontManager(PdfFileWriter file, PdfResourceManager resources)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(resources);
        _file = file;
        _resources = resources;
    }

    /// <summary>Returns the resource name (e.g. <c>F1</c>) for a default font.</summary>
    public string GetFont(string baseFont)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseFont);

        var key = "standardfont:" + baseFont;
        var objectNumber = _file.Registry.GetOrAdd(key, () =>
        {
            var number = _file.ReserveObject();
            _file.WriteObject(number, tokens => tokens.WriteDictionary(font =>
            {
                font.WriteName("Type");
                font.WriteName("Font");
                font.WriteName("Subtype");
                font.WriteName("Type1");
                font.WriteName("BaseFont");
                font.WriteName(baseFont);
                font.WriteName("Encoding");
                font.WriteName("WinAnsiEncoding");
            }));
            return number;
        });

        return _resources.AddFont(key, objectNumber);
    }
}
