namespace Reglet;

/// <summary>
/// embeds glyf-based TrueType fonts: FontFile2 + FontDescriptor (H13) as well as
/// Type0/CIDFontType2 with Identity-H, CIDToGIDMap and widths (H14). ToUnicode follows in H15.
/// CFF/OTTO and TTC are rejected by <see cref="TrueTypeFont"/>.
/// </summary>
internal sealed class PdfTrueTypeFontManager
{
    private readonly PdfFileWriter _file;
    private readonly PdfResourceManager _resources;

    // per family the already embedded styles (compared by instance).
    private readonly Dictionary<string, List<(FontFace Face, PdfEmbeddedFont Font)>> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PdfEmbeddedFont> _order = [];
    private bool _finished;

    private readonly bool _kerning;
    private readonly bool _ligatures;

    public PdfTrueTypeFontManager(PdfFileWriter file, PdfResourceManager resources, bool kerning = true, bool ligatures = true)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(resources);
        _file = file;
        _resources = resources;
        _kerning = kerning;
        _ligatures = ligatures;
    }

    /// <summary>
    /// Returns the embedded style of a family (deduplicated by family and style). The objects
    /// are only reserved here; <see cref="Finish"/> writes them with the glyphs actually used.
    /// </summary>
    public PdfEmbeddedFont GetFont(string family, FontFace face)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(face);

        if (!_fonts.TryGetValue(family, out var faces))
        {
            faces = [];
            _fonts[family] = faces;
        }

        foreach (var (existingFace, existingFont) in faces)
        {
            if (ReferenceEquals(existingFace, face))
            {
                return existingFont;
            }
        }

        if (_finished)
        {
            throw new InvalidOperationException("The embedded fonts have already been written.");
        }

        // The empty name stands for the default font family (no family value on the run). The
        // first style of a family keeps the previous key, further ones are numbered.
        var key = (family.Length == 0 ? "default" : family) + (faces.Count == 0 ? string.Empty : ":" + faces.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var font = face.Font;
        var layout = OpenTypeLayout.TryCreate(font);
        var type0Object = _file.ReserveObject();
        var embedded = new PdfEmbeddedFont
        {
            Face = face,
            Font = font,
            Layout = layout,
            Shaper = new TextShaper(font, layout, _kerning, _ligatures), // without layout tables it still applies the bidi order
            Type0Object = type0Object,
            CidFontObject = _file.ReserveObject(),
            DescriptorObject = _file.ReserveObject(),
            FontFileObject = _file.ReserveObject(),
            ToUnicodeObject = _file.ReserveObject(),
            ResourceName = _resources.AddFont("embedded:" + key, type0Object),
        };

        faces.Add((face, embedded));
        _order.Add(embedded);
        return embedded;
    }

    /// <summary>
    /// Writes all embedded fonts: reduced to the drawn glyphs where the license allows it
    /// (the complete file otherwise or if the reduction is not possible).
    /// </summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        foreach (var embedded in _order)
        {
            var font = embedded.Font;
            var subset = embedded.Face.AllowsSubsetting
                ? TrueTypeSubsetter.TrySubset(font, embedded.UsedGlyphs)
                : null;

            // ISO 32000-1, 9.6.4: the name of a subset starts with a six-letter tag.
            var name = subset is null ? font.PostScriptName : SubsetTag(embedded.UsedGlyphs) + "+" + font.PostScriptName;
            WriteFontFile(embedded.FontFileObject, subset ?? embedded.Face.Data);
            WriteDescriptor(embedded.DescriptorObject, font, name, embedded.FontFileObject);
            WriteCidFont(embedded.CidFontObject, font, name, embedded.DescriptorObject, subset is null ? null : embedded.UsedGlyphs);
            WriteToUnicode(embedded.ToUnicodeObject, ToUnicodeMappings(embedded));
            WriteType0(embedded.Type0Object, name, embedded.CidFontObject, embedded.ToUnicodeObject);
        }
    }

    /// <summary>The recorded glyph texts; glyphs without a recorded text use the font's cmap.</summary>
    private static IReadOnlyList<(ushort GlyphId, string Text)> ToUnicodeMappings(PdfEmbeddedFont embedded)
    {
        var result = new Dictionary<ushort, string>(embedded.GlyphText);
        var missing = embedded.UsedGlyphs.Where(glyph => glyph != 0 && !result.ContainsKey(glyph)).ToHashSet();
        if (missing.Count > 0)
        {
            foreach (var (codePoint, glyph) in embedded.Font.EnumerateMappings())
            {
                if (missing.Remove(glyph) && codePoint is < 0xD800 or > 0xDFFF)
                {
                    result[glyph] = char.ConvertFromUtf32(codePoint);
                }

                if (missing.Count == 0)
                {
                    break;
                }
            }
        }

        return result.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).ToList();
    }

    /// <summary>A deterministic six-letter tag derived from the glyph set.</summary>
    private static string SubsetTag(IReadOnlySet<ushort> glyphs)
    {
        var bytes = new byte[glyphs.Count * 2];
        var index = 0;
        foreach (var glyph in glyphs.Order())
        {
            bytes[index++] = (byte)(glyph >> 8);
            bytes[index++] = (byte)glyph;
        }

        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return string.Create(6, hash, static (span, digest) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = (char)('A' + (digest[i] % 26));
            }
        });
    }

    private void WriteFontFile(int objectNumber, byte[] fontData)
    {
        var compressed = PdfStreamCompressor.Compress(fontData);
        _file.WriteStreamObject(objectNumber, compressed, $"/Filter /FlateDecode /Length1 {fontData.Length}");
    }

    private void WriteDescriptor(int objectNumber, TrueTypeFont font, string name, int fontFileObject)
    {
        var scale = 1000.0 / font.UnitsPerEm;

        _file.WriteObject(objectNumber, tokens => tokens.WriteDictionary(descriptor =>
        {
            descriptor.WriteName("Type");
            descriptor.WriteName("FontDescriptor");
            descriptor.WriteName("FontName");
            descriptor.WriteName(name);
            descriptor.WriteName("Flags");
            descriptor.WriteInteger(32);
            descriptor.WriteName("FontBBox");
            descriptor.WriteArray(box =>
            {
                box.WriteInteger(Scale(font.XMin, scale));
                box.WriteInteger(Scale(font.YMin, scale));
                box.WriteInteger(Scale(font.XMax, scale));
                box.WriteInteger(Scale(font.YMax, scale));
            });
            descriptor.WriteName("ItalicAngle");
            descriptor.WriteInteger(0);
            descriptor.WriteName("Ascent");
            descriptor.WriteInteger(Scale(font.Ascender, scale));
            descriptor.WriteName("Descent");
            descriptor.WriteInteger(Scale(font.Descender, scale));
            descriptor.WriteName("CapHeight");
            descriptor.WriteInteger(Scale(font.Ascender, scale));
            descriptor.WriteName("StemV");
            descriptor.WriteInteger(80);
            descriptor.WriteName("FontFile2");
            descriptor.WriteReference(fontFileObject);
        }));
    }

    private void WriteCidFont(int objectNumber, TrueTypeFont font, string name, int descriptorObject, IReadOnlySet<ushort>? usedGlyphs)
    {

        _file.WriteObject(objectNumber, tokens => tokens.WriteDictionary(cidFont =>
        {
            cidFont.WriteName("Type");
            cidFont.WriteName("Font");
            cidFont.WriteName("Subtype");
            cidFont.WriteName("CIDFontType2");
            cidFont.WriteName("BaseFont");
            cidFont.WriteName(name);
            cidFont.WriteName("CIDSystemInfo");
            cidFont.WriteDictionary(systemInfo =>
            {
                systemInfo.WriteName("Registry");
                systemInfo.WriteLiteralString("Adobe");
                systemInfo.WriteName("Ordering");
                systemInfo.WriteLiteralString("Identity");
                systemInfo.WriteName("Supplement");
                systemInfo.WriteInteger(0);
            });
            cidFont.WriteName("FontDescriptor");
            cidFont.WriteReference(descriptorObject);
            WriteWidths(cidFont, font, usedGlyphs);
            cidFont.WriteName("CIDToGIDMap");
            cidFont.WriteName("Identity");
        }));
    }

    private void WriteToUnicode(int objectNumber, IReadOnlyList<(ushort GlyphId, string Text)> mappings)
        => _file.WriteStreamObject(objectNumber, PdfToUnicodeCmap.Build(mappings));

    private void WriteType0(int objectNumber, string name, int cidFontObject, int toUnicodeObject)
    {

        _file.WriteObject(objectNumber, tokens => tokens.WriteDictionary(type0 =>
        {
            type0.WriteName("Type");
            type0.WriteName("Font");
            type0.WriteName("Subtype");
            type0.WriteName("Type0");
            type0.WriteName("BaseFont");
            type0.WriteName(name);
            type0.WriteName("Encoding");
            type0.WriteName("Identity-H");
            type0.WriteName("DescendantFonts");
            type0.WriteArray(descendants => descendants.WriteReference(cidFontObject));
            type0.WriteName("ToUnicode");
            type0.WriteReference(toUnicodeObject);
        }));
    }

    /// <summary>
    /// The /W array: for a subset only the used glyphs, else all glyphs. Runs of equal widths
    /// are written as <c>first last width</c>, other runs as <c>first [w1 w2 …]</c>.
    /// </summary>
    private static void WriteWidths(PdfTokenWriter tokens, TrueTypeFont font, IReadOnlySet<ushort>? usedGlyphs)
    {
        var scale = 1000.0 / font.UnitsPerEm;
        var glyphs = usedGlyphs is null
            ? Enumerable.Range(0, font.NumGlyphs).ToList()
            : usedGlyphs.Where(glyph => glyph < font.NumGlyphs).Select(glyph => (int)glyph).Order().ToList();

        tokens.WriteName("DW");
        tokens.WriteInteger(1000);
        tokens.WriteName("W");
        tokens.WriteArray(widths =>
        {
            var index = 0;
            while (index < glyphs.Count)
            {
                // A run of consecutive glyph IDs.
                var start = index;
                while (index + 1 < glyphs.Count && glyphs[index + 1] == glyphs[index] + 1)
                {
                    index++;
                }

                var values = glyphs.Skip(start).Take(index - start + 1).Select(glyph => WidthOf(font, glyph, scale)).ToList();
                widths.WriteInteger(glyphs[start]);
                if (values.Count > 1 && values.All(value => value == values[0]))
                {
                    widths.WriteInteger(glyphs[index]);
                    widths.WriteInteger(values[0]);
                }
                else
                {
                    widths.WriteArray(group =>
                    {
                        foreach (var value in values)
                        {
                            group.WriteInteger(value);
                        }
                    });
                }

                index++;
            }
        });
    }

    private static long WidthOf(TrueTypeFont font, int glyph, double scale)
        => (long)Math.Round(font.GetAdvanceWidth((ushort)glyph) * scale, MidpointRounding.AwayFromZero);

    private static long Scale(int value, double scale)
        => (long)Math.Round(value * scale, MidpointRounding.AwayFromZero);
}
