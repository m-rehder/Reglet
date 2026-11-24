namespace Reglet.RelaxNg;

/// <summary>An expanded name: namespace and local name.</summary>
internal readonly record struct QualifiedName(string Namespace, string Local);

/// <summary>A RELAX NG name class (ISO/IEC 19757-2, 4.16).</summary>
internal abstract class NameClass
{
    public abstract bool Contains(QualifiedName name);

    /// <summary>The names this class lists explicitly, for messages.</summary>
    public abstract IEnumerable<QualifiedName> Names();

    public sealed class Single(QualifiedName name) : NameClass
    {
        public QualifiedName Name { get; } = name;

        public override bool Contains(QualifiedName name) => name == Name;

        public override IEnumerable<QualifiedName> Names() => [Name];
    }

    public sealed class Any(NameClass? except) : NameClass
    {
        public override bool Contains(QualifiedName name) => except is null || !except.Contains(name);

        public override IEnumerable<QualifiedName> Names() => [];
    }

    public sealed class Namespace(string ns, NameClass? except) : NameClass
    {
        public override bool Contains(QualifiedName name) => name.Namespace == ns && (except is null || !except.Contains(name));

        public override IEnumerable<QualifiedName> Names() => [];
    }

    public sealed class Choice(NameClass first, NameClass second) : NameClass
    {
        public override bool Contains(QualifiedName name) => first.Contains(name) || second.Contains(name);

        public override IEnumerable<QualifiedName> Names() => first.Names().Concat(second.Names());
    }
}

/// <summary>
/// A simplified RELAX NG pattern for James Clark's derivative algorithm ("An algorithm for RELAX NG validation", 2002).
/// Patterns are created through <see cref="RngPatterns"/>, which shares equal ones, so derivatives can be cached by
/// identity.
/// </summary>
internal abstract class Pattern(int id)
{
    public int Id { get; } = id;

    public abstract bool Nullable { get; }

    public sealed class Empty(int id) : Pattern(id)
    {
        public override bool Nullable => true;
    }

    public sealed class NotAllowed(int id) : Pattern(id)
    {
        public override bool Nullable => false;
    }

    public sealed class Text(int id) : Pattern(id)
    {
        public override bool Nullable => true;
    }

    public sealed class Choice(int id, Pattern first, Pattern second) : Pattern(id)
    {
        public Pattern First { get; } = first;

        public Pattern Second { get; } = second;

        public override bool Nullable { get; } = first.Nullable || second.Nullable;
    }

    public sealed class Interleave(int id, Pattern first, Pattern second) : Pattern(id)
    {
        public Pattern First { get; } = first;

        public Pattern Second { get; } = second;

        public override bool Nullable { get; } = first.Nullable && second.Nullable;
    }

    public sealed class Group(int id, Pattern first, Pattern second) : Pattern(id)
    {
        public Pattern First { get; } = first;

        public Pattern Second { get; } = second;

        public override bool Nullable { get; } = first.Nullable && second.Nullable;
    }

    public sealed class OneOrMore(int id, Pattern content) : Pattern(id)
    {
        public Pattern Content { get; } = content;

        public override bool Nullable => Content.Nullable;
    }

    public sealed class List(int id, Pattern content) : Pattern(id)
    {
        public Pattern Content { get; } = content;

        public override bool Nullable => false;
    }

    public sealed class Data(int id, Datatype type, Pattern? except) : Pattern(id)
    {
        public Datatype Type { get; } = type;

        public Pattern? Except { get; } = except;

        public override bool Nullable => false;
    }

    public sealed class Value(int id, Datatype type, string value) : Pattern(id)
    {
        public Datatype Type { get; } = type;

        public string Literal { get; } = value;

        public override bool Nullable => false;
    }

    public sealed class Attribute(int id, NameClass name, Pattern content) : Pattern(id)
    {
        public NameClass Name { get; } = name;

        public Pattern Content { get; } = content;

        public override bool Nullable => false;
    }

    /// <summary>An element; its content is set after creation, because definitions refer to themselves through elements.</summary>
    public sealed class Element(int id, NameClass name) : Pattern(id)
    {
        public NameClass Name { get; } = name;

        public Pattern Content { get; set; } = null!;

        public override bool Nullable => false;
    }

    public sealed class After(int id, Pattern first, Pattern second) : Pattern(id)
    {
        public Pattern First { get; } = first;

        public Pattern Second { get; } = second;

        public override bool Nullable => false;
    }
}

/// <summary>
/// Creates patterns, shares equal ones and applies the simplifications of Clark's algorithm. A schema owns one factory
/// that is frozen once the schema is loaded; each validation derives its patterns in a child factory that reads the
/// schema's and keeps its own, so nothing grows across validations and validations run in parallel without a lock.
/// </summary>
internal sealed class RngPatterns
{
    /// <summary>The most patterns one validation may derive; a document that needs more stops being checked.</summary>
    public const int MaxDerived = 400_000;

    private readonly RngPatterns? _parent;
    private readonly Dictionary<(int Kind, int First, int Second), Pattern> _shared = [];
    private readonly Dictionary<string, Pattern> _leaves = new(StringComparer.Ordinal);
    private readonly int _first;
    private int _next;

    public RngPatterns()
    {
        Empty = new Pattern.Empty(_next++);
        NotAllowed = new Pattern.NotAllowed(_next++);
        Text = new Pattern.Text(_next++);
    }

    /// <summary>A child factory for one validation: it shares the parent's patterns and keeps the ones it derives.</summary>
    public RngPatterns(RngPatterns parent)
    {
        _parent = parent;
        Empty = parent.Empty;
        NotAllowed = parent.NotAllowed;
        Text = parent.Text;
        _first = _next = parent._next;
    }

    /// <summary>Thrown when one validation derives more than <see cref="MaxDerived"/> patterns.</summary>
    public sealed class BudgetExceededException : Exception
    {
    }

    public Pattern Empty { get; }

    /// <summary>How many patterns this factory has created (with its parent's); for tests.</summary>
    internal int Count => _next;

    public Pattern NotAllowed { get; }

    public Pattern Text { get; }

    public Pattern Choice(Pattern first, Pattern second)
    {
        if (first is Pattern.NotAllowed || ReferenceEquals(first, second))
        {
            return second;
        }

        if (second is Pattern.NotAllowed)
        {
            return first;
        }

        // A flat, ordered set of alternatives keeps equal choices equal, so the caches stay small.
        var items = new SortedDictionary<int, Pattern>();
        Collect(first, items);
        Collect(second, items);
        var list = items.Values.ToList();
        var result = list[^1];
        for (var index = list.Count - 2; index >= 0; index--)
        {
            result = Shared(1, list[index], result, (id, a, b) => new Pattern.Choice(id, a, b));
        }

        return result;

        static void Collect(Pattern pattern, SortedDictionary<int, Pattern> items)
        {
            if (pattern is Pattern.Choice choice)
            {
                Collect(choice.First, items);
                Collect(choice.Second, items);
            }
            else
            {
                items[pattern.Id] = pattern;
            }
        }
    }

    public Pattern Group(Pattern first, Pattern second)
    {
        if (first is Pattern.NotAllowed || second is Pattern.NotAllowed)
        {
            return NotAllowed;
        }

        if (first is Pattern.Empty)
        {
            return second;
        }

        return second is Pattern.Empty ? first : Shared(2, first, second, (id, a, b) => new Pattern.Group(id, a, b));
    }

    public Pattern Interleave(Pattern first, Pattern second)
    {
        if (first is Pattern.NotAllowed || second is Pattern.NotAllowed)
        {
            return NotAllowed;
        }

        if (first is Pattern.Empty)
        {
            return second;
        }

        return second is Pattern.Empty ? first : Shared(3, first, second, (id, a, b) => new Pattern.Interleave(id, a, b));
    }

    public Pattern After(Pattern first, Pattern second)
        => first is Pattern.NotAllowed || second is Pattern.NotAllowed ? NotAllowed : Shared(4, first, second, (id, a, b) => new Pattern.After(id, a, b));

    public Pattern OneOrMore(Pattern content)
        => content is Pattern.NotAllowed or Pattern.Empty ? content : Shared(5, content, content, (id, a, _) => new Pattern.OneOrMore(id, a));

    public Pattern List(Pattern content) => content is Pattern.NotAllowed ? NotAllowed : Shared(6, content, content, (id, a, _) => new Pattern.List(id, a));

    public Pattern Attribute(NameClass name, Pattern content) => new Pattern.Attribute(NextId(), name, content);

    public Pattern.Element Element(NameClass name) => new(NextId(), name);

    public Pattern Data(Datatype type, Pattern? except)
        => Leaf($"d|{type.Key}|{except?.Id}", id => new Pattern.Data(id, type, except));

    public Pattern Value(Datatype type, string value) => Leaf($"v|{type.Key}|{value}", id => new Pattern.Value(id, type, value));

    private Pattern Shared(int kind, Pattern first, Pattern second, Func<int, Pattern, Pattern, Pattern> create)
    {
        if (_parent?._shared.TryGetValue((kind, first.Id, second.Id), out var inherited) == true)
        {
            return inherited!;
        }

        if (!_shared.TryGetValue((kind, first.Id, second.Id), out var pattern))
        {
            pattern = create(NextId(), first, second);
            _shared.Add((kind, first.Id, second.Id), pattern);
        }

        return pattern;
    }

    private Pattern Leaf(string key, Func<int, Pattern> create)
    {
        if (_parent?._leaves.TryGetValue(key, out var inherited) == true)
        {
            return inherited!;
        }

        if (!_leaves.TryGetValue(key, out var pattern))
        {
            pattern = create(NextId());
            _leaves.Add(key, pattern);
        }

        return pattern;
    }

    private int NextId()
    {
        if (_parent is not null && _next - _first >= MaxDerived)
        {
            throw new BudgetExceededException();
        }

        return _next++;
    }
}
