namespace Reglet;

/// <summary>The kind of a <see cref="Chart"/> (ADR-0025).</summary>
public enum ChartKind
{
    /// <summary>Vertical bars, grouped by category.</summary>
    Column,

    /// <summary>Horizontal bars, grouped by category.</summary>
    Bar,

    /// <summary>Lines with markers through the values of each series.</summary>
    Line,

    /// <summary>Filled areas below the values of each series.</summary>
    Area,

    /// <summary>A pie of the values of the single series.</summary>
    Pie,

    /// <summary>Points at (x, y) pairs; every series needs x values.</summary>
    Scatter,
}

/// <summary>Where the legend of a chart stands.</summary>
public enum ChartLegendPosition
{
    /// <summary>No legend.</summary>
    None,

    /// <summary>Below the plot area.</summary>
    Bottom,

    /// <summary>Right of the plot area.</summary>
    Right,
}

/// <summary>One data series of a <see cref="Chart"/>.</summary>
public sealed class ChartSeries
{
    /// <summary>Creates a series.</summary>
    /// <param name="name">The name shown in the legend; may be empty.</param>
    /// <param name="values">The values (the y values of a scatter); at least one, all finite.</param>
    /// <param name="xValues">The x values of a scatter, as many as <paramref name="values"/>; null for another kind.</param>
    /// <exception cref="ArgumentException">There are no values, a value is not finite or the x values do not match.</exception>
    public ChartSeries(string name, IEnumerable<double> values, IEnumerable<double>? xValues = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Values = ModelCollections.Snapshot(values, nameof(values));
        if (Values.Count == 0 || Values.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("A series needs at least one value and every value must be finite.", nameof(values));
        }

        if (xValues is not null)
        {
            XValues = ModelCollections.Snapshot(xValues, nameof(xValues));
            if (XValues.Count != Values.Count || XValues.Any(value => !double.IsFinite(value)))
            {
                throw new ArgumentException("The x values must be finite and as many as the values.", nameof(xValues));
            }
        }
    }

    /// <summary>The name shown in the legend.</summary>
    public string Name { get; }

    /// <summary>The values.</summary>
    public IReadOnlyList<double> Values { get; }

    /// <summary>The x values of a scatter; null otherwise.</summary>
    public IReadOnlyList<double>? XValues { get; }

    /// <summary>The color of the series; the palette color when null.</summary>
    public Color? Color { get; init; }
}

/// <summary>
/// A chart (ADR-0025), inline in the text flow like a shape. The PDF writer draws it, DOCX and ODT carry
/// it as a native chart object.
/// </summary>
public sealed class Chart : Block
{
    /// <summary>The palette used when a series has no color (readable for color-blind viewers).</summary>
    public static IReadOnlyList<Color> DefaultPalette { get; } = new[]
    {
        Color.FromHex("#0072B2"), Color.FromHex("#E69F00"), Color.FromHex("#009E73"), Color.FromHex("#CC79A7"),
        Color.FromHex("#56B4E9"), Color.FromHex("#D55E00"), Color.FromHex("#F0E442"), Color.FromHex("#999999"),
    };

    private double? _minimum;
    private double? _maximum;

    /// <summary>Creates a chart.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="series">The series; at least one (exactly one for a pie).</param>
    /// <param name="categories">The category labels (the pie slices); null numbers them 1, 2, 3 ...; ignored by a scatter.</param>
    /// <param name="width">The width; 400 pt when null.</param>
    /// <param name="height">The height; 250 pt when null.</param>
    /// <exception cref="ArgumentException">
    /// There is no series, a pie has several, a scatter series lacks x values or another kind has them,
    /// or the categories are fewer than the longest series.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The kind is unknown or a size is not positive.</exception>
    public Chart(ChartKind kind, IEnumerable<ChartSeries> series, IEnumerable<string>? categories = null, Length? width = null, Length? height = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown chart kind.");
        }

        Series = ModelCollections.Snapshot(series, nameof(series));
        if (Series.Count == 0)
        {
            throw new ArgumentException("A chart needs at least one series.", nameof(series));
        }

        if (kind == ChartKind.Pie && Series.Count != 1)
        {
            throw new ArgumentException("A pie shows exactly one series.", nameof(series));
        }

        if (Series.Any(entry => (entry.XValues is not null) != (kind == ChartKind.Scatter)))
        {
            throw new ArgumentException("Only a scatter has x values, and every series of a scatter needs them.", nameof(series));
        }

        Kind = kind;
        Width = width ?? Length.FromPoints(400);
        Height = height ?? Length.FromPoints(250);
        if (Width.Points <= 0 || Height.Points <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A chart needs a positive size.");
        }

        var longest = Series.Max(entry => entry.Values.Count);
        if (categories is not null)
        {
            Categories = ModelCollections.Snapshot(categories, nameof(categories));
            if (Categories.Count < longest && kind != ChartKind.Scatter)
            {
                throw new ArgumentException("There must be a category label for every value.", nameof(categories));
            }
        }
    }

    /// <summary>The kind.</summary>
    public ChartKind Kind { get; }

    /// <summary>The series.</summary>
    public IReadOnlyList<ChartSeries> Series { get; }

    /// <summary>The category labels; null for numbers.</summary>
    public IReadOnlyList<string>? Categories { get; }

    /// <summary>The width.</summary>
    public Length Width { get; }

    /// <summary>The height.</summary>
    public Length Height { get; }

    /// <summary>The title above the chart.</summary>
    public string? Title { get; init; }

    /// <summary>Where the legend stands; at the bottom by default (a single series without a name shows none).</summary>
    public ChartLegendPosition Legend { get; init; } = ChartLegendPosition.Bottom;

    /// <summary>The title of the category axis (the x axis of a scatter).</summary>
    public string? CategoryAxisTitle { get; init; }

    /// <summary>The title of the value axis.</summary>
    public string? ValueAxisTitle { get; init; }

    /// <summary>The lowest value of the value axis; computed from the data when null.</summary>
    public double? Minimum
    {
        get => _minimum;
        init
        {
            if (value is { } v && !double.IsFinite(v))
            {
                throw new ArgumentOutOfRangeException(nameof(Minimum), "The minimum must be finite.");
            }

            _minimum = value;
        }
    }

    /// <summary>The highest value of the value axis; computed from the data when null.</summary>
    public double? Maximum
    {
        get => _maximum;
        init
        {
            if (value is { } v && !double.IsFinite(v))
            {
                throw new ArgumentOutOfRangeException(nameof(Maximum), "The maximum must be finite.");
            }

            _maximum = value;
        }
    }

    /// <summary>Whether the series of a column, bar or area chart are stacked.</summary>
    public bool Stacked { get; init; }

    /// <summary>The description for screen readers.</summary>
    public string? AltText { get; init; }

    /// <summary>The color of a series: its own, else the palette color of its index.</summary>
    public Color SeriesColor(int index)
        => Series[index].Color ?? DefaultPalette[index % DefaultPalette.Count];
}
