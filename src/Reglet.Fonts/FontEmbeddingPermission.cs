namespace Reglet;

/// <summary>
/// embedding right of a font per OS/2 <c>fsType</c> (bits 0–3). If several bits are
/// set, the least restrictive right applies.
/// </summary>
public enum FontEmbeddingPermission
{
    /// <summary>Installable: embedding without restriction allowed.</summary>
    Installable,

    /// <summary>Editable: embedding allowed; the document may be edited.</summary>
    Editable,

    /// <summary>Preview and print: embedding allowed; the document may only be viewed and printed.</summary>
    PreviewAndPrint,

    /// <summary>Restricted license: the font must not be embedded.</summary>
    Restricted,
}
