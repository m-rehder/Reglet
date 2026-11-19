using System.Runtime.CompilerServices;

// The TrueType parser is internal; the PDF writer uses it to embed and reduce fonts.
[assembly: InternalsVisibleTo("Reglet.Pdf")]
[assembly: InternalsVisibleTo("Reglet.Docx")]
[assembly: InternalsVisibleTo("Reglet.Odt")]
[assembly: InternalsVisibleTo("Reglet.CompatibilityTests")]
