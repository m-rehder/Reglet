using System.Globalization;

namespace Reglet;

/// <summary>
/// an RGB color value without a dependency on backend types. The channels are on 0–255
/// limited; hex values are validated.
/// </summary>
public readonly record struct Color
{
    /// <summary>Creates a color from the three color channels.</summary>
    public Color(byte red, byte green, byte blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    /// <summary>Rotkanal (0–255).</summary>
    public byte Red { get; }

    /// <summary>Green channel (0–255).</summary>
    public byte Green { get; }

    /// <summary>Blaukanal (0–255).</summary>
    public byte Blue { get; }

    /// <summary>Creates a color from the three color channels.</summary>
    public static Color FromRgb(byte red, byte green, byte blue) => new(red, green, blue);

    /// <summary>
    /// creates a color from a hex value of the form <c>RRGGBB</c> or <c>#RRGGBB</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a valid six-digit hex value.</exception>
    public static Color FromHex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var hex = value.StartsWith('#') ? value[1..] : value;
        if (hex.Length != 6
            || !hex.All(char.IsAsciiHexDigit)
            || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            throw new ArgumentException(
                $"Invalid color value '{value}'. Expected e.g. '#1A2B3C'.",
                nameof(value));
        }

        return new Color((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
    }

    /// <summary>
    /// parses a hex value of the form <c>RRGGBB</c> or <c>#RRGGBB</c> without throwing.
    /// </summary>
    /// <param name="value">The value to parse; null or an invalid value yields false.</param>
    /// <param name="color">The parsed color; the black color if the value is invalid.</param>
    public static bool TryParseHex(string? value, out Color color)
    {
        color = Black;
        if (value is null)
        {
            return false;
        }

        var hex = value.StartsWith('#') ? value[1..] : value;
        if (hex.Length != 6
            || !hex.All(char.IsAsciiHexDigit)
            || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = new Color((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        return true;
    }

    /// <summary>Schwarz (0, 0, 0).</summary>
    public static Color Black => new(0, 0, 0);

    /// <summary>White (255, 255, 255).</summary>
    public static Color White => new(255, 255, 255);

    /// <inheritdoc />
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"#{Red:X2}{Green:X2}{Blue:X2}");
}
