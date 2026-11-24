using System.Xml.Linq;

namespace Reglet.RelaxNg;

/// <summary>
/// Validates an element tree against a <see cref="RngSchema"/> with James Clark's derivative algorithm. Each element
/// that does not fit gets one finding; validation then goes on with its parent as if the element had fitted, so one
/// fault does not cascade. One instance validates one part; its derived patterns live in its own child factory, so
/// several validators may share a schema across threads.
/// </summary>
internal sealed class RngValidator(RngSchema schema, Action<XObject, string> report)
{
    private readonly RngPatterns _p = new(schema.Patterns);
    private readonly Dictionary<(int, QualifiedName), Pattern> _open = [];
    private readonly Dictionary<int, Pattern> _close = [];
    private readonly Dictionary<int, Pattern> _end = [];

    public void Validate(XElement root)
    {
        var after = Element(schema.Start, root);
        if (after is Pattern.NotAllowed)
        {
            return;
        }
    }

    private Pattern Element(Pattern state, XElement element)
    {
        ReaderGuard.EnsureStack();
        var name = new QualifiedName(element.Name.NamespaceName, element.Name.LocalName);
        var opened = StartTagOpen(state, name);
        if (opened is Pattern.NotAllowed)
        {
            report(element, $"The element {schema.Describe(name)} is not allowed here{Expected(state)}.");
            return state;
        }

        var current = opened;
        var attributeFault = false;
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            var attributeName = new QualifiedName(attribute.Name.NamespaceName, attribute.Name.LocalName);
            var next = AttributeDeriv(current, attributeName, attribute.Value);
            if (next is Pattern.NotAllowed)
            {
                report(element, AllowsName(current, attributeName)
                    ? $"The value '{Shorten(attribute.Value)}' of {schema.Describe(attributeName)} on {schema.Describe(name)} is not valid."
                    : $"The attribute {schema.Describe(attributeName)} is not allowed on {schema.Describe(name)}.");
                attributeFault = true;
                continue;
            }

            current = next;
        }

        var closed = StartTagClose(current);
        if (closed is Pattern.NotAllowed)
        {
            // A required attribute whose value was just reported is not reported again as missing.
            if (!attributeFault)
            {
                report(element, $"{schema.Describe(name)} lacks a required attribute{List(RequiredAttributes(current))}.");
            }

            return Continuation(opened);
        }

        current = closed;
        var children = element.Nodes().Where(node => node is XElement or XText).ToList();
        var texts = children.OfType<XText>().ToList();
        if (children.Count == 0 || (children.Count == texts.Count))
        {
            // Only text (or nothing): whitespace may also stand for no content.
            var text = string.Concat(texts.Select(node => node.Value));
            var derived = TextDeriv(current, text);
            current = IsWhitespace(text) ? _p.Choice(current, derived) : derived;
            if (current is Pattern.NotAllowed)
            {
                report(element, $"The text '{Shorten(text)}' is not valid in {schema.Describe(name)}.");
                return Continuation(closed);
            }
        }
        else
        {
            foreach (var child in children)
            {
                if (child is XElement childElement)
                {
                    current = Element(current, childElement);
                }
                else if (child is XText text && !IsWhitespace(text.Value))
                {
                    var derived = TextDeriv(current, text.Value);
                    if (derived is Pattern.NotAllowed)
                    {
                        report(element, $"Text is not allowed in {schema.Describe(name)}: '{Shorten(text.Value)}'.");
                        continue;
                    }

                    current = derived;
                }
            }
        }

        var ended = EndTag(current);
        if (ended is Pattern.NotAllowed)
        {
            report(element, $"{schema.Describe(name)} is incomplete{Expected(current)}.");
            return Continuation(current);
        }

        return ended;
    }

    private Pattern StartTagOpen(Pattern p, QualifiedName name)
    {
        if (_open.TryGetValue((p.Id, name), out var cached))
        {
            return cached;
        }

        var result = p switch
        {
            Pattern.Choice choice => _p.Choice(StartTagOpen(choice.First, name), StartTagOpen(choice.Second, name)),
            Pattern.Element element => element.Name.Contains(name) ? _p.After(element.Content, _p.Empty) : _p.NotAllowed,
            Pattern.Interleave interleave => _p.Choice(
                ApplyAfter(x => _p.Interleave(x, interleave.Second), StartTagOpen(interleave.First, name)),
                ApplyAfter(x => _p.Interleave(interleave.First, x), StartTagOpen(interleave.Second, name))),
            Pattern.OneOrMore more => ApplyAfter(x => _p.Group(x, _p.Choice(more, _p.Empty)), StartTagOpen(more.Content, name)),
            Pattern.Group group => GroupOpen(group, name),
            Pattern.After after => ApplyAfter(x => _p.After(x, after.Second), StartTagOpen(after.First, name)),
            _ => _p.NotAllowed,
        };
        _open[(p.Id, name)] = result;
        return result;
    }

    private Pattern GroupOpen(Pattern.Group group, QualifiedName name)
    {
        var first = ApplyAfter(x => _p.Group(x, group.Second), StartTagOpen(group.First, name));
        return group.First.Nullable ? _p.Choice(first, StartTagOpen(group.Second, name)) : first;
    }

    private Pattern ApplyAfter(Func<Pattern, Pattern> f, Pattern p) => p switch
    {
        Pattern.After after => _p.After(after.First, f(after.Second)),
        Pattern.Choice choice => _p.Choice(ApplyAfter(f, choice.First), ApplyAfter(f, choice.Second)),
        _ => _p.NotAllowed,
    };

    private Pattern AttributeDeriv(Pattern p, QualifiedName name, string value) => p switch
    {
        Pattern.After after => _p.After(AttributeDeriv(after.First, name, value), after.Second),
        Pattern.Choice choice => _p.Choice(AttributeDeriv(choice.First, name, value), AttributeDeriv(choice.Second, name, value)),
        Pattern.Group group => _p.Choice(
            _p.Group(AttributeDeriv(group.First, name, value), group.Second),
            _p.Group(group.First, AttributeDeriv(group.Second, name, value))),
        Pattern.Interleave interleave => _p.Choice(
            _p.Interleave(AttributeDeriv(interleave.First, name, value), interleave.Second),
            _p.Interleave(interleave.First, AttributeDeriv(interleave.Second, name, value))),
        Pattern.OneOrMore more => _p.Group(AttributeDeriv(more.Content, name, value), _p.Choice(more, _p.Empty)),
        Pattern.Attribute attribute => attribute.Name.Contains(name) && ValueMatch(attribute.Content, value) ? _p.Empty : _p.NotAllowed,
        _ => _p.NotAllowed,
    };

    private bool ValueMatch(Pattern p, string value) => (p.Nullable && IsWhitespace(value)) || TextDeriv(p, value).Nullable;

    private Pattern StartTagClose(Pattern p)
    {
        if (_close.TryGetValue(p.Id, out var cached))
        {
            return cached;
        }

        var result = p switch
        {
            Pattern.After after => _p.After(StartTagClose(after.First), after.Second),
            Pattern.Choice choice => _p.Choice(StartTagClose(choice.First), StartTagClose(choice.Second)),
            Pattern.Group group => _p.Group(StartTagClose(group.First), StartTagClose(group.Second)),
            Pattern.Interleave interleave => _p.Interleave(StartTagClose(interleave.First), StartTagClose(interleave.Second)),
            Pattern.OneOrMore more => _p.OneOrMore(StartTagClose(more.Content)),
            Pattern.Attribute => _p.NotAllowed,
            _ => p,
        };
        _close[p.Id] = result;
        return result;
    }

    private Pattern TextDeriv(Pattern p, string text) => p switch
    {
        Pattern.Choice choice => _p.Choice(TextDeriv(choice.First, text), TextDeriv(choice.Second, text)),
        Pattern.Interleave interleave => _p.Choice(
            _p.Interleave(TextDeriv(interleave.First, text), interleave.Second),
            _p.Interleave(interleave.First, TextDeriv(interleave.Second, text))),
        Pattern.Group group => group.First.Nullable
            ? _p.Choice(_p.Group(TextDeriv(group.First, text), group.Second), TextDeriv(group.Second, text))
            : _p.Group(TextDeriv(group.First, text), group.Second),
        Pattern.After after => _p.After(TextDeriv(after.First, text), after.Second),
        Pattern.OneOrMore more => _p.Group(TextDeriv(more.Content, text), _p.Choice(more, _p.Empty)),
        Pattern.Text => p,
        Pattern.Value value => value.Type.Equal(value.Literal, text) ? _p.Empty : _p.NotAllowed,
        Pattern.Data data => data.Type.Allows(text) && (data.Except is null || !TextDeriv(data.Except, text).Nullable) ? _p.Empty : _p.NotAllowed,
        Pattern.List list => ListDeriv(list.Content, text).Nullable ? _p.Empty : _p.NotAllowed,
        _ => _p.NotAllowed,
    };

    private Pattern ListDeriv(Pattern p, string text)
    {
        foreach (var token in text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            p = TextDeriv(p, token);
            if (p is Pattern.NotAllowed)
            {
                break;
            }
        }

        return p;
    }

    private Pattern EndTag(Pattern p)
    {
        if (_end.TryGetValue(p.Id, out var cached))
        {
            return cached;
        }

        var result = p switch
        {
            Pattern.Choice choice => _p.Choice(EndTag(choice.First), EndTag(choice.Second)),
            Pattern.After after => after.First.Nullable ? after.Second : _p.NotAllowed,
            _ => _p.NotAllowed,
        };
        _end[p.Id] = result;
        return result;
    }

    /// <summary>Where the parent goes on after an element that failed: the second parts of the open element's states.</summary>
    private Pattern Continuation(Pattern p) => p switch
    {
        Pattern.After after => after.Second,
        Pattern.Choice choice => _p.Choice(Continuation(choice.First), Continuation(choice.Second)),
        _ => _p.NotAllowed,
    };

    private string Expected(Pattern state)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        Firsts(state, names, new HashSet<int>());
        return names.Count == 0 ? string.Empty : "; expected " + string.Join(", ", names.Take(12)) + (names.Count > 12 ? ", …" : string.Empty);
    }

    private void Firsts(Pattern p, SortedSet<string> names, HashSet<int> seen)
    {
        if (!seen.Add(p.Id) || names.Count > 12)
        {
            return;
        }

        switch (p)
        {
            case Pattern.After after:
                Firsts(after.First, names, seen);
                break;
            case Pattern.Choice choice:
                Firsts(choice.First, names, seen);
                Firsts(choice.Second, names, seen);
                break;
            case Pattern.Interleave interleave:
                Firsts(interleave.First, names, seen);
                Firsts(interleave.Second, names, seen);
                break;
            case Pattern.Group group:
                Firsts(group.First, names, seen);
                if (group.First.Nullable)
                {
                    Firsts(group.Second, names, seen);
                }

                break;
            case Pattern.OneOrMore more:
                Firsts(more.Content, names, seen);
                break;
            case Pattern.Element element:
                foreach (var name in element.Name.Names())
                {
                    names.Add(schema.Describe(name));
                }

                break;
            case Pattern.Text or Pattern.Data or Pattern.Value or Pattern.List:
                names.Add("text");
                break;
        }
    }

    private SortedSet<string> RequiredAttributes(Pattern p)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        Collect(p, names, new HashSet<int>());
        return names;

        void Collect(Pattern pattern, SortedSet<string> found, HashSet<int> seen)
        {
            if (!seen.Add(pattern.Id) || pattern.Nullable)
            {
                return;
            }

            switch (pattern)
            {
                case Pattern.After after:
                    Collect(after.First, found, seen);
                    break;
                case Pattern.Choice choice:
                    Collect(choice.First, found, seen);
                    Collect(choice.Second, found, seen);
                    break;
                case Pattern.Group or Pattern.Interleave:
                    var (first, second) = pattern is Pattern.Group g ? (g.First, g.Second) : (((Pattern.Interleave)pattern).First, ((Pattern.Interleave)pattern).Second);
                    Collect(first, found, seen);
                    Collect(second, found, seen);
                    break;
                case Pattern.OneOrMore more:
                    Collect(more.Content, found, seen);
                    break;
                case Pattern.Attribute attribute:
                    foreach (var name in attribute.Name.Names())
                    {
                        found.Add(schema.Describe(name));
                    }

                    break;
            }
        }
    }

    private static bool AllowsName(Pattern p, QualifiedName name) => p switch
    {
        Pattern.After after => AllowsName(after.First, name),
        Pattern.Choice choice => AllowsName(choice.First, name) || AllowsName(choice.Second, name),
        Pattern.Group group => AllowsName(group.First, name) || AllowsName(group.Second, name),
        Pattern.Interleave interleave => AllowsName(interleave.First, name) || AllowsName(interleave.Second, name),
        Pattern.OneOrMore more => AllowsName(more.Content, name),
        Pattern.Attribute attribute => attribute.Name.Contains(name),
        _ => false,
    };

    private static string List(SortedSet<string> names) => names.Count == 0 ? string.Empty : " (" + string.Join(", ", names.Take(8)) + ")";

    private static bool IsWhitespace(string text) => text.All(character => character is ' ' or '\t' or '\n' or '\r');

    private static string Shorten(string text) => text.Length > 60 ? text[..60] + "…" : text;
}
