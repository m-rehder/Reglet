using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// Applies Office's markup compatibility (ECMA-376 part 3) before schema validation, as Office 2019 reads a part:
/// <c>mc:AlternateContent</c> becomes its first choice whose namespaces are understood, else its fallback; elements and
/// attributes of Office 2010+ extension namespaces are understood and set aside unvalidated; those of other namespaces
/// listed in <c>mc:Ignorable</c> are dropped. An extension namespace used without being ignorable is reported, because
/// Office 2007 and other ECMA-only readers cannot open the part.
/// </summary>
internal static class MarkupCompatibility
{
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public static void Apply(XElement root, string part, IssueList issues)
    {
        var ignorable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Attribute(Mc + "Ignorable") is { } attribute)
            {
                foreach (var prefix in attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (element.GetNamespaceOfPrefix(prefix) is { } ns)
                    {
                        ignorable.Add(ns.NamespaceName);
                    }
                }
            }
        }

        foreach (var alternate in root.DescendantsAndSelf(Mc + "AlternateContent").Reverse().ToList())
        {
            var choice = alternate.Elements(Mc + "Choice").FirstOrDefault(candidate =>
                    ((string?)candidate.Attribute("Requires") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .All(prefix => candidate.GetNamespaceOfPrefix(prefix) is { } ns && Understood(ns.NamespaceName)))
                ?? alternate.Element(Mc + "Fallback");
            if (choice is null)
            {
                alternate.Remove();
            }
            else
            {
                alternate.ReplaceWith(choice.Nodes());
            }
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        void Report(string ns, int? line)
        {
            if (!ignorable.Contains(ns) && reported.Add(ns))
            {
                issues.Warning(part, line, $"Uses the Office extension namespace {ns} without declaring it in mc:Ignorable; Office 2007 and other ECMA-only readers cannot open the part.");
            }
        }

        foreach (var element in root.Descendants().Where(element => Drop(element.Name.NamespaceName, ignorable)).ToList())
        {
            if (OoxmlSchemas.IsOfficeExtension(element.Name.NamespaceName))
            {
                Report(element.Name.NamespaceName, Line(element));
            }

            element.Remove();
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration
                && (attribute.Name.Namespace == Mc || Drop(attribute.Name.NamespaceName, ignorable))).ToList())
            {
                if (OoxmlSchemas.IsOfficeExtension(attribute.Name.NamespaceName))
                {
                    Report(attribute.Name.NamespaceName, Line(element));
                }

                attribute.Remove();
            }
        }
    }

    private static bool Understood(string ns) => OoxmlSchemas.IsKnown(ns) || OoxmlSchemas.IsOfficeExtension(ns);

    private static bool Drop(string ns, HashSet<string> ignorable)
        => ns.Length > 0 && !OoxmlSchemas.IsKnown(ns) && (OoxmlSchemas.IsOfficeExtension(ns) || ignorable.Contains(ns));

    internal static int? Line(XObject node) => node is System.Xml.IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;
}
