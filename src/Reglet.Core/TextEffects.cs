namespace Reglet;

/// <summary>Visual effects of the glyphs; combine with <c>|</c>.</summary>
[Flags]
public enum TextEffects
{
    /// <summary>No effect; switches inherited effects off.</summary>
    None = 0,

    /// <summary>Only the outline of the glyphs is drawn.</summary>
    Outline = 1,

    /// <summary>A shadow behind the text.</summary>
    Shadow = 2,

    /// <summary>Raised letters (relief).</summary>
    Emboss = 4,

    /// <summary>Pressed-in letters (engraved).</summary>
    Engrave = 8,
}
