using System.Globalization;

namespace Reglet;

/// <summary>Stable names for the styles per document section (1-based).</summary>
internal static class OdtSectionNames
{
    public static string PageLayout(int index) => "pm" + Number(index);

    /// <summary>The first master page is named like the ODF standard page <c>Standard</c>.</summary>
    public static string MasterPage(int index) => index == 1 ? "Standard" : "Standard" + Number(index);

    public static string SectionStyle(int index) => "Sect" + Number(index);

    /// <summary>Paragraph style that switches the master with a page break.</summary>
    public static string BreakStyle(int index) => "SectBreak" + Number(index);

    private static string Number(int index) => index.ToString(CultureInfo.InvariantCulture);
}
