using System.Xml.Linq;

namespace Reglet.RelaxNg;

/// <summary>
/// A RELAX NG schema in XML syntax, simplified into <see cref="Pattern"/>s: grammar, start, define and ref (non-element
/// definitions are expanded where they are referenced, elements are shared so definitions can recurse through them),
/// element, attribute, group, interleave, choice, optional, zeroOrMore, oneOrMore, mixed, list, empty, text, notAllowed,
/// value, data with param and except, and the name classes name, anyName, nsName and choice, with <c>ns</c> and
/// <c>datatypeLibrary</c> inherited. shortcut: include, externalRef, parentRef, nested grammars and combine are not
/// supported (the ODF schemas use none of them); loading fails with <see cref="NotSupportedException"/> for them.
/// </summary>
internal sealed class RngSchema
{
    private static readonly XNamespace Rng = "http://relaxng.org/ns/structure/1.0";
    private readonly Dictionary<string, XElement> _defines = new(StringComparer.Ordinal);
    private readonly Dictionary<XElement, Pattern.Element> _elements = [];
    private readonly HashSet<string> _expanding = new(StringComparer.Ordinal);

    public RngSchema(XElement grammar)
    {
        if (grammar.Name != Rng + "grammar")
        {
            throw new NotSupportedException("The schema root is not rng:grammar.");
        }

        foreach (var define in grammar.Elements(Rng + "define"))
        {
            if (define.Attribute("combine") is not null)
            {
                throw new NotSupportedException("rng:define with combine is not supported.");
            }

            _defines[(string)define.Attribute("name")!] = define;
        }

        Prefixes = grammar.Attributes().Where(attribute => attribute.IsNamespaceDeclaration && attribute.Name.Namespace == XNamespace.Xmlns)
            .GroupBy(attribute => attribute.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name.LocalName, StringComparer.Ordinal);
        var start = grammar.Element(Rng + "start") ?? throw new NotSupportedException("The grammar has no rng:start.");
        Start = Group(start.Elements());

        // Fill element contents breadth-first: an element's content may reach further elements.
        var done = new HashSet<XElement>();
        while (_elements.Keys.FirstOrDefault(element => !done.Contains(element)) is { } next)
        {
            done.Add(next);
            var pattern = _elements[next];
            var children = next.Elements().ToList();
            pattern.Content = Group(next.Attribute("name") is null ? children.Skip(1) : children);
        }

        Namespaces = _elements.Values.SelectMany(element => element.Name.Names()).Select(name => name.Namespace)
            .Concat(AttributeNamespaces(grammar))
            .ToHashSet(StringComparer.Ordinal);
    }

    public RngPatterns Patterns { get; } = new();

    public Pattern Start { get; }

    /// <summary>The namespaces the schema names elements or attributes in; markup in other namespaces is foreign.</summary>
    public HashSet<string> Namespaces { get; }

    /// <summary>Namespace URI → prefix of the grammar's declarations, for messages.</summary>
    public IReadOnlyDictionary<string, string> Prefixes { get; }

    public string Describe(QualifiedName name)
        => name.Namespace.Length == 0 ? name.Local : Prefixes.TryGetValue(name.Namespace, out var prefix) ? prefix + ":" + name.Local : "{" + name.Namespace + "}" + name.Local;

    private IEnumerable<string> AttributeNamespaces(XElement grammar)
        => grammar.Descendants(Rng + "attribute").Select(attribute => (string?)attribute.Attribute("name"))
            .Where(name => name is not null && name.Contains(':'))
            .Select(name => ResolveName(grammar, name!, isAttribute: true).Namespace);

    private Pattern Group(IEnumerable<XElement> children)
    {
        var result = Patterns.Empty;
        foreach (var child in children)
        {
            result = Patterns.Group(result, Convert(child));
        }

        return result;
    }

    private Pattern Choice(IEnumerable<XElement> children)
    {
        Pattern? result = null;
        foreach (var child in children)
        {
            var pattern = Convert(child);
            result = result is null ? pattern : Patterns.Choice(result, pattern);
        }

        return result ?? Patterns.NotAllowed;
    }

    private Pattern Interleave(IEnumerable<XElement> children)
    {
        var result = Patterns.Empty;
        foreach (var child in children)
        {
            result = Patterns.Interleave(result, Convert(child));
        }

        return result;
    }

    private Pattern Convert(XElement node)
    {
        if (node.Name.Namespace != Rng)
        {
            // Annotations in other namespaces carry no meaning.
            return Patterns.Empty;
        }

        switch (node.Name.LocalName)
        {
            case "element":
                if (!_elements.TryGetValue(node, out var element))
                {
                    element = Patterns.Element(NameClassOf(node, isAttribute: false));
                    _elements.Add(node, element);
                }

                return element;
            case "attribute":
                var content = node.Elements().Skip(node.Attribute("name") is null ? 1 : 0).ToList();
                return Patterns.Attribute(NameClassOf(node, isAttribute: true), content.Count == 0 ? Patterns.Text : Group(content));
            case "group":
                return Group(node.Elements());
            case "interleave":
                return Interleave(node.Elements());
            case "choice":
                return Choice(node.Elements());
            case "optional":
                return Patterns.Choice(Group(node.Elements()), Patterns.Empty);
            case "zeroOrMore":
                return Patterns.Choice(Patterns.OneOrMore(Group(node.Elements())), Patterns.Empty);
            case "oneOrMore":
                return Patterns.OneOrMore(Group(node.Elements()));
            case "mixed":
                return Patterns.Interleave(Group(node.Elements()), Patterns.Text);
            case "list":
                return Patterns.List(Group(node.Elements()));
            case "empty":
                return Patterns.Empty;
            case "text":
                return Patterns.Text;
            case "notAllowed":
                return Patterns.NotAllowed;
            case "ref":
                var name = (string)node.Attribute("name")!;
                if (!_defines.TryGetValue(name, out var define))
                {
                    throw new NotSupportedException($"rng:ref to the undefined '{name}'.");
                }

                if (!_expanding.Add(name))
                {
                    throw new NotSupportedException($"The definition '{name}' refers to itself outside an element.");
                }

                try
                {
                    return Group(define.Elements());
                }
                finally
                {
                    _expanding.Remove(name);
                }

            case "value":
                return Patterns.Value(DatatypeOf(node, defaultToken: true), node.Value);
            case "data":
                var except = node.Element(Rng + "except");
                return Patterns.Data(DatatypeOf(node, defaultToken: false), except is null ? null : Choice(except.Elements()));
            default:
                throw new NotSupportedException($"rng:{node.Name.LocalName} is not supported.");
        }
    }

    private Datatype DatatypeOf(XElement node, bool defaultToken)
    {
        if (defaultToken && node.Attribute("type") is null)
        {
            return Datatype.Create(string.Empty, "token", []);
        }

        var library = node.AncestorsAndSelf().Select(ancestor => (string?)ancestor.Attribute("datatypeLibrary")).FirstOrDefault(value => value is not null) ?? string.Empty;
        var parameters = node.Elements(Rng + "param").Select(parameter => ((string)parameter.Attribute("name")!, parameter.Value)).ToList();
        return Datatype.Create(library, (string?)node.Attribute("type") ?? "token", parameters);
    }

    private NameClass NameClassOf(XElement node, bool isAttribute)
    {
        if ((string?)node.Attribute("name") is { } name)
        {
            return new NameClass.Single(ResolveName(node, name, isAttribute));
        }

        return NameClassFrom(node.Elements().First(), isAttribute);
    }

    private NameClass NameClassFrom(XElement node, bool isAttribute)
    {
        var except = node.Element(Rng + "except") is { } exceptNode ? Choices(exceptNode.Elements(), isAttribute) : null;
        return node.Name.LocalName switch
        {
            "name" => new NameClass.Single(ResolveName(node, node.Value.Trim(), isAttribute)),
            "anyName" => new NameClass.Any(except),
            "nsName" => new NameClass.Namespace(InheritedNs(node), except),
            "choice" => Choices(node.Elements(), isAttribute),
            _ => throw new NotSupportedException($"The name class rng:{node.Name.LocalName} is not supported."),
        };
    }

    private NameClass Choices(IEnumerable<XElement> nodes, bool isAttribute)
        => nodes.Select(node => NameClassFrom(node, isAttribute)).Aggregate((first, second) => new NameClass.Choice(first, second));

    /// <summary>A prefixed name takes its prefix's namespace; an unprefixed one the inherited ns (attributes: none, unless the node says).</summary>
    private static QualifiedName ResolveName(XElement node, string name, bool isAttribute)
    {
        var colon = name.IndexOf(':');
        if (colon > 0)
        {
            var ns = node.GetNamespaceOfPrefix(name[..colon]) ?? throw new NotSupportedException($"The prefix of '{name}' is not declared.");
            return new QualifiedName(ns.NamespaceName, name[(colon + 1)..]);
        }

        return new QualifiedName(isAttribute ? (string?)node.Attribute("ns") ?? string.Empty : InheritedNs(node), name);
    }

    private static string InheritedNs(XElement node)
        => node.AncestorsAndSelf().Select(ancestor => (string?)ancestor.Attribute("ns")).FirstOrDefault(value => value is not null) ?? string.Empty;
}
