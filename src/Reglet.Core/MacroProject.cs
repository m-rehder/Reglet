namespace Reglet;

/// <summary>
/// The VBA project of a macro-enabled document (DOCX <c>word/vbaProject.bin</c>), carried as opaque bytes
/// (ADR-0028). The library never runs, parses or changes it. Only DOCX stores it; ODT and PDF ignore it.
/// </summary>
public sealed record MacroProject
{
    private byte[] _data = [];

    /// <summary>The bytes of the project; must not be empty. The value is copied.</summary>
    public required ReadOnlyMemory<byte> Data
    {
        get => _data;
        init
        {
            if (value.IsEmpty)
            {
                throw new ArgumentException("The macro project must not be empty.", nameof(Data));
            }

            _data = value.ToArray();
        }
    }
}
