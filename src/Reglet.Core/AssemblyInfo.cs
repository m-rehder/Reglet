using System.Runtime.CompilerServices;

// Internal helpers of Core (e.g. ImageLayout) are visible to the writers.
[assembly: InternalsVisibleTo("Reglet")]
[assembly: InternalsVisibleTo("Reglet.Docx")]
[assembly: InternalsVisibleTo("Reglet.Odt")]
[assembly: InternalsVisibleTo("Reglet.Html")]
[assembly: InternalsVisibleTo("Reglet.Pdf")]
[assembly: InternalsVisibleTo("Reglet.CompatibilityTests")]
