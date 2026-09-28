using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Textalonia.Model;

/// <summary>Distinguishes inheritance from an explicit value, including false, zero and null.</summary>
public readonly record struct StyleValue<T>
{
    public bool IsSet { get; init; }
    public T Value { get; init; }
    [JsonConstructor]
    public StyleValue(bool isSet, T value) { IsSet = isSet; Value = value; }
    public StyleValue(T value) : this(true, value) { }
    public static implicit operator StyleValue<T>(T value) => new(value);
}

/// <summary>A theme color; Tint ranges from -1 (black) to 1 (white).</summary>
public sealed record ThemeColorReference(string Name, double Tint = 0);
public sealed record ThemeFontReference(string Name);

/// <summary>Physical document colors and fonts; independent of application chrome.</summary>
public sealed record DocumentTheme
{
    public string? Name { get; init; }
    public ImmutableDictionary<string, string> Colors { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> Fonts { get; init; } = ImmutableDictionary<string, string>.Empty;
}

public sealed record DocumentDefaults
{
    public TextStyle Text { get; init; } = TextStyle.Default;
    public ParagraphStyle Paragraph { get; init; } = ParagraphStyle.Default;
}

public sealed record CharacterStyleDefinition
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? BasedOn { get; init; }
    public string? LinkedStyle { get; init; }
    public TextStyleOverrides Formatting { get; init; } = new();
}

public sealed record ParagraphStyleDefinition
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? BasedOn { get; init; }
    public string? LinkedStyle { get; init; }
    public string? NextStyle { get; init; }
    public ParagraphStyleOverrides Formatting { get; init; } = new();
    public TextStyleOverrides TextFormatting { get; init; } = new();
}

public sealed record TableStyleDefinition
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? BasedOn { get; init; }
    public TableStyleOverrides Formatting { get; init; } = new();
    public ImmutableDictionary<TableStyleRegion, TableStyleOverrides> Conditions { get; init; } = ImmutableDictionary<TableStyleRegion, TableStyleOverrides>.Empty;
}

/// <summary>Immutable named formatting. Identifiers are scoped to their style kind.</summary>
public sealed record DocumentStyleCatalog
{
    public ImmutableDictionary<string, CharacterStyleDefinition> Characters { get; init; } = ImmutableDictionary<string, CharacterStyleDefinition>.Empty;
    public ImmutableDictionary<string, ParagraphStyleDefinition> Paragraphs { get; init; } = ImmutableDictionary<string, ParagraphStyleDefinition>.Empty;
    public ImmutableDictionary<string, TableStyleDefinition> Tables { get; init; } = ImmutableDictionary<string, TableStyleDefinition>.Empty;
    public string? DefaultCharacterStyleId { get; init; }
    public string? DefaultParagraphStyleId { get; init; }
    public string? DefaultTableStyleId { get; init; }

    public void Validate()
    {
        if (Characters is null || Paragraphs is null || Tables is null ||
            Characters.Count + Paragraphs.Count + Tables.Count > 4096)
            throw new FormatException("Invalid document style catalog.");
        ValidateGraph(Characters, x => x.Id, x => x.BasedOn);
        ValidateGraph(Paragraphs, x => x.Id, x => x.BasedOn);
        ValidateGraph(Tables, x => x.Id, x => x.BasedOn);
        Reference(Characters, DefaultCharacterStyleId); Reference(Paragraphs, DefaultParagraphStyleId); Reference(Tables, DefaultTableStyleId);
        foreach (var style in Characters.Values)
        {
            if (style.Formatting is null) throw new FormatException("Missing character style formatting.");
            Reference(Paragraphs, style.LinkedStyle);
        }
        foreach (var style in Paragraphs.Values)
        {
            if (style.Formatting is null || style.TextFormatting is null) throw new FormatException("Missing paragraph style formatting.");
            Reference(Characters, style.LinkedStyle); Reference(Paragraphs, style.NextStyle);
        }
        foreach (var style in Tables.Values)
            if (style.Formatting is null || style.Conditions is null || style.Conditions.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value is null))
                throw new FormatException("Invalid table style formatting or conditional regions.");
    }

    private static void ValidateGraph<T>(ImmutableDictionary<string, T> styles, Func<T, string> id, Func<T, string?> parent)
    {
        foreach (var pair in styles)
        {
            if (pair.Value is null || !InlineDescriptor.ValidKey(pair.Key) || pair.Key != id(pair.Value))
                throw new FormatException("Style keys must match their nonempty identifiers.");
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string? current = pair.Key;
            while (current is not null)
            {
                if (!visited.Add(current)) throw new FormatException("Style inheritance contains a cycle.");
                if (visited.Count > 256) throw new FormatException("Style inheritance exceeds the depth limit.");
                Reference(styles, current);
                current = parent(styles[current]);
            }
        }
    }

    internal static void Reference<T>(ImmutableDictionary<string, T> styles, string? id)
    {
        if (id is not null && (!InlineDescriptor.ValidKey(id) || !styles.ContainsKey(id)))
            throw new FormatException($"Unknown style '{id}'.");
    }
}

/// <summary>Effective table and cell formatting after the style cascade.</summary>
public sealed record TableStyle
{
    public BlockBorders? OutsideBorders { get; init; }
    public BorderSide? InsideHorizontal { get; init; }
    public BorderSide? InsideVertical { get; init; }
    public TableCellVerticalAlignment VerticalAlignment { get; init; }
    public TableCellTextDirection TextDirection { get; init; }
    public string? Background { get; init; }
    public EdgeInsets? Padding { get; init; }
    public BlockBorders? Borders { get; init; }
}

public sealed record TableStyleOverrides
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<BlockBorders?> OutsideBorders { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<BorderSide?> InsideHorizontal { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<BorderSide?> InsideVertical { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<TableCellVerticalAlignment> VerticalAlignment { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<TableCellTextDirection> TextDirection { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<string?> Background { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<EdgeInsets?> Padding { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<BlockBorders?> Borders { get; init; }
    public TableStyle Apply(TableStyle value) => value with
    {
        OutsideBorders = OutsideBorders.IsSet ? OutsideBorders.Value : value.OutsideBorders,
        InsideHorizontal = InsideHorizontal.IsSet ? InsideHorizontal.Value : value.InsideHorizontal,
        InsideVertical = InsideVertical.IsSet ? InsideVertical.Value : value.InsideVertical,
        VerticalAlignment = VerticalAlignment.IsSet ? VerticalAlignment.Value : value.VerticalAlignment,
        TextDirection = TextDirection.IsSet ? TextDirection.Value : value.TextDirection,
        Background = Background.IsSet ? Background.Value : value.Background,
        Padding = Padding.IsSet ? Padding.Value : value.Padding,
        Borders = Borders.IsSet ? Borders.Value : value.Borders
    };
}

public sealed partial record TextStyle
{
    public ThemeFontReference? ThemeFont { get; init; }
    public string? EastAsianFontFamily { get; init; }
    public string? ComplexScriptFontFamily { get; init; }
    public ThemeFontReference? EastAsianThemeFont { get; init; }
    public ThemeFontReference? ComplexScriptThemeFont { get; init; }
    public ThemeColorReference? ThemeForeground { get; init; }
    public ThemeColorReference? ThemeBackground { get; init; }
}

internal readonly record struct DocumentFormattingKey(DocumentStyleCatalog Styles, DocumentDefaults Defaults,
    DocumentTheme Theme, ImmutableArray<DocumentFontDefinition> Fonts, object? ResourcesIdentity);

/// <summary>
/// Resolves document defaults, default named styles, inherited/applied styles and direct
/// overrides, in that order. Theme references are evaluated after the cascade. Legacy
/// formatting (Overrides == null) is fully explicit. One resolver belongs to one snapshot.
/// </summary>
public sealed class DocumentStyleResolver
{
    private readonly FlowDocument _document;
    private readonly Dictionary<string, ParagraphStyle> _paragraphs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextStyle> _paragraphText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TableStyle> _tables = new(StringComparer.Ordinal);
    public DocumentStyleResolver(FlowDocument document) => _document = document ?? throw new ArgumentNullException(nameof(document));
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImmutableDictionary<string, DocumentResource>, object> ResourceIdentities = new();
    // Layout caches retain formatting identity, never the document/paragraph tree.
    internal DocumentFormattingKey FormattingKey => new(_document.Styles, _document.Defaults, _document.Theme, _document.Fonts,
        _document.Fonts.IsEmpty ? null : ResourceIdentities.GetValue(_document.Resources, _ => new object()));

    private IEnumerable<T> Chain<T>(ImmutableDictionary<string, T> styles, string? id, Func<T, string?> parent)
    {
        var stack = new Stack<T>(); var visited = new HashSet<string>(StringComparer.Ordinal);
        while (id is not null)
        {
            if (!visited.Add(id) || visited.Count > 256) throw new FormatException("Cyclic or overly deep style inheritance.");
            DocumentStyleCatalog.Reference(styles, id);
            var style = styles[id]; stack.Push(style); id = parent(style);
        }
        return stack;
    }

    public ParagraphStyle ResolveParagraphStyle(ParagraphStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (style.Overrides is null) return style.StyleId is null ? style : style with { StyleId = null };
        var result = _document.Defaults.Paragraph;
        result = result.Overrides?.Apply(ParagraphStyle.Default) ?? result;
        var defaultId = _document.Styles.DefaultParagraphStyleId;
        if (defaultId is not null) result = ApplyParagraph(result, defaultId);
        if (style.StyleId is { } id) result = ApplyParagraph(result, id);
        result = style.Overrides?.Apply(result) ?? style;
        return result with { StyleId = null, Overrides = null };
    }

    private ParagraphStyle ApplyParagraph(ParagraphStyle basis, string id)
    {
        // The full cascade, including defaults, is deterministic for this snapshot.
        if (_paragraphs.TryGetValue(id, out var cached)) return cached;
        foreach (var item in Chain(_document.Styles.Paragraphs, id, x => x.BasedOn)) basis = item.Formatting.Apply(basis);
        _paragraphs[id] = basis;
        return basis;
    }

    public TextStyle ResolveText(Paragraph paragraph, TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(paragraph); ArgumentNullException.ThrowIfNull(style);
        if (style.Overrides is null)
        {
            var explicitStyle = ResolveTheme(style);
            return explicitStyle.StyleId is null ? explicitStyle : explicitStyle with { StyleId = null };
        }
        var result = _document.Defaults.Text;
        result = result.Overrides?.Apply(TextStyle.Default) ?? result;
        var defaultParagraph = _document.Styles.DefaultParagraphStyleId;
        if (defaultParagraph is not null) result = ApplyParagraphText(result, defaultParagraph);
        if (paragraph.Style.StyleId is { } paragraphId) result = ApplyParagraphText(result, paragraphId);
        if (_document.Styles.DefaultCharacterStyleId is { } defaultCharacter) result = ApplyCharacter(result, defaultCharacter);
        if (style.StyleId is { } characterId) result = ApplyCharacter(result, characterId);
        result = style.Overrides?.Apply(result) ?? style;
        return ResolveTheme(result) with { StyleId = null, Overrides = null };
    }

    /// <summary>Resolves marker-only character formatting without changing the list's body runs.</summary>
    public TextStyle ResolveListMarkerStyle(Paragraph paragraph, ListLevelDefinition level)
    {
        var style = ResolveText(paragraph, paragraph.DefaultStyle);
        if (level.CharacterStyleId is { } id) style = ApplyCharacter(style, id);
        return ResolveTheme(level.MarkerFormatting.Apply(style)) with { StyleId = null, Overrides = null };
    }

    private TextStyle ApplyParagraphText(TextStyle basis, string id)
    {
        if (_paragraphText.TryGetValue(id, out var cached)) return cached;
        foreach (var item in Chain(_document.Styles.Paragraphs, id, x => x.BasedOn)) basis = item.TextFormatting.Apply(basis);
        _paragraphText[id] = basis;
        return basis;
    }

    private TextStyle ApplyCharacter(TextStyle basis, string id)
    {
        // A character style may be applied on multiple different paragraph styles.
        foreach (var item in Chain(_document.Styles.Characters, id, x => x.BasedOn)) basis = item.Formatting.Apply(basis);
        return basis;
    }

    public TableStyle ResolveTableStyle(Table table)
    {
        var result = new TableStyle();
        if (_document.Styles.DefaultTableStyleId is { } defaultId) result = ApplyTable(result, defaultId);
        if (table.StyleId is { } id) result = ApplyTable(result, id);
        return table.StyleOverrides?.Apply(result) ?? result;
    }

    /// <summary>Resolves bands, edges and headers in enum order, followed by table and cell direct overrides.</summary>
    public TableCell ResolveTableCell(Table table, int row, int column)
    {
        var cell = table.Rows[row][column];
        var style = new TableStyle();
        ApplyFormatting(_document.Styles.DefaultTableStyleId);
        ApplyFormatting(table.StyleId);
        void ApplyFormatting(string? id)
        {
            foreach (var definition in Chain(_document.Styles.Tables, id, x => x.BasedOn)) Apply(definition.Formatting);
        }
        // Resolve grid borders at each cascade layer so a conditional/direct border
        // overrides the corresponding whole-table edge instead of losing its provenance.
        void Apply(TableStyleOverrides formatting)
        {
            style = formatting.Apply(style);
            if (!formatting.OutsideBorders.IsSet && !formatting.InsideHorizontal.IsSet && !formatting.InsideVertical.IsSet) return;
            var borders = style.Borders ?? new();
            var first = column == 0; var last = column + cell.ColumnSpan == table.ColumnCount;
            var left = table.RightToLeft ? last : first; var right = table.RightToLeft ? first : last;
            if (formatting.OutsideBorders.IsSet)
                borders = borders with
                {
                    Left = left ? formatting.OutsideBorders.Value?.Left : borders.Left,
                    Right = right ? formatting.OutsideBorders.Value?.Right : borders.Right,
                    Top = row == 0 ? formatting.OutsideBorders.Value?.Top : borders.Top,
                    Bottom = row + cell.RowSpan == table.Rows.Length ? formatting.OutsideBorders.Value?.Bottom : borders.Bottom
                };
            if (formatting.InsideVertical.IsSet) borders = borders with
            { Left = !left ? formatting.InsideVertical.Value : borders.Left, Right = !right ? formatting.InsideVertical.Value : borders.Right };
            if (formatting.InsideHorizontal.IsSet) borders = borders with
            { Top = row > 0 ? formatting.InsideHorizontal.Value : borders.Top,
                Bottom = row + cell.RowSpan < table.Rows.Length ? formatting.InsideHorizontal.Value : borders.Bottom };
            style = style with { Borders = borders };
        }
        foreach (var region in Enum.GetValues<TableStyleRegion>())
        {
            var applies = region switch
            {
                TableStyleRegion.OddColumnBand => column % 2 == 0,
                TableStyleRegion.EvenColumnBand => column % 2 == 1,
                TableStyleRegion.OddRowBand => (row - table.RepeatHeaderRows) % 2 == 0 && row >= table.RepeatHeaderRows,
                TableStyleRegion.EvenRowBand => (row - table.RepeatHeaderRows) % 2 == 1 && row >= table.RepeatHeaderRows,
                TableStyleRegion.FirstColumn => column == 0,
                TableStyleRegion.LastColumn => column + cell.ColumnSpan == table.ColumnCount,
                TableStyleRegion.FirstRow => row == 0,
                TableStyleRegion.LastRow => row + cell.RowSpan == table.Rows.Length,
                TableStyleRegion.HeaderRow => row < table.RepeatHeaderRows,
                _ => false
            };
            if (!applies) continue;
            ApplyConditions(_document.Styles.DefaultTableStyleId);
            ApplyConditions(table.StyleId);
            void ApplyConditions(string? id)
            {
                foreach (var definition in Chain(_document.Styles.Tables, id, x => x.BasedOn))
                    if (definition.Conditions.TryGetValue(region, out var condition)) Apply(condition);
            }
        }
        if (table.StyleOverrides is { } directTable) Apply(directTable);
        style = style with
        {
            Background = cell.Background ?? style.Background, Padding = cell.Padding ?? style.Padding,
            Borders = cell.Borders ?? style.Borders,
            VerticalAlignment = cell.VerticalAlignment != TableCellVerticalAlignment.Top ? cell.VerticalAlignment : style.VerticalAlignment,
            TextDirection = cell.TextDirection != TableCellTextDirection.Inherit ? cell.TextDirection : style.TextDirection
        };
        if (cell.StyleOverrides is { } directCell) Apply(directCell);
        return cell with { Background = style.Background, Padding = style.Padding, Borders = style.Borders,
            VerticalAlignment = style.VerticalAlignment, TextDirection = style.TextDirection, StyleOverrides = null };
    }

    private TableStyle ApplyTable(TableStyle basis, string id)
    {
        if (_tables.TryGetValue(id, out var cached)) return cached;
        foreach (var item in Chain(_document.Styles.Tables, id, x => x.BasedOn)) basis = item.Formatting.Apply(basis);
        _tables[id] = basis;
        return basis;
    }

    public Paragraph ResolveParagraph(Paragraph paragraph) => paragraph with
    {
        Style = ResolveParagraphStyle(paragraph.Style),
        DefaultStyle = ResolveText(paragraph, paragraph.DefaultStyle),
        Runs = paragraph.Runs.Select(run => run with { Style = ResolveText(paragraph, run.Style) }).ToImmutableArray()
    };

    /// <summary>Materializes effective formatting for formats without named-style support.</summary>
    public FlowDocument ResolveDocument()
    {
        ImmutableArray<Block> Visit(ImmutableArray<Block> blocks) => blocks.Select<Block, Block>(block => block switch
        {
            Paragraph paragraph => ResolveListParagraph(paragraph),
            Section section => section with { Blocks = Visit(section.Blocks) },
            Table table => ResolveTable(table),
            _ => block
        }).ToImmutableArray();
        Paragraph ResolveListParagraph(Paragraph paragraph)
        {
            var resolved = ResolveParagraph(paragraph);
            if (resolved.Style.ListDefinition is not { } definition) return resolved;
            return resolved with { Style = resolved.Style with { ListDefinition = definition with
            { Levels = definition.Levels.Select(level => level with { ParagraphStyleId = null, CharacterStyleId = null,
                MarkerFormatting = level.CharacterStyleId is null && !level.MarkerFormatting.ThemeFont.IsSet &&
                    !level.MarkerFormatting.ThemeForeground.IsSet && !level.MarkerFormatting.ThemeBackground.IsSet &&
                    !level.MarkerFormatting.EastAsianThemeFont.IsSet && !level.MarkerFormatting.ComplexScriptThemeFont.IsSet
                    ? level.MarkerFormatting : TextStyleOverrides.FromStyle(ResolveListMarkerStyle(paragraph, level)) }).ToImmutableArray() } } };
        }
        Table ResolveTable(Table table)
        {
            return table with
            {
                StyleId = null, StyleOverrides = null,
                Rows = table.Rows.Select((row, r) => row.Select((cell, c) => ResolveTableCell(table, r, c) with
                {
                    Blocks = Visit(cell.Blocks), MergeOriginalBlocks = Visit(cell.MergeOriginalBlocks)
                }).ToImmutableArray()).ToImmutableArray()
            };
        }
        return _document with { Blocks = Visit(_document.Blocks), Stories = _document.Stories.ToImmutableDictionary(pair => pair.Key, pair => pair.Value with { Blocks = Visit(pair.Value.Blocks) }), Styles = new(), Defaults = new(), Theme = new() };
    }

    private TextStyle ResolveTheme(TextStyle style) => style.ThemeFont is null && style.EastAsianThemeFont is null &&
        style.ComplexScriptThemeFont is null && style.ThemeForeground is null && style.ThemeBackground is null ? style : style with
    {
        ThemeFont = null, EastAsianThemeFont = null, ComplexScriptThemeFont = null, ThemeForeground = null, ThemeBackground = null,
        FontFamily = Font(style.ThemeFont, style.FontFamily),
        EastAsianFontFamily = Font(style.EastAsianThemeFont, style.EastAsianFontFamily),
        ComplexScriptFontFamily = Font(style.ComplexScriptThemeFont, style.ComplexScriptFontFamily),
        Foreground = Color(style.ThemeForeground, style.Foreground), Background = Color(style.ThemeBackground, style.Background)
    };

    private string? Font(ThemeFontReference? reference, string? fallback) =>
        reference is not null && _document.Theme.Fonts.TryGetValue(reference.Name, out var value) ? value : fallback;

    private string? Color(ThemeColorReference? reference, string? fallback)
    {
        if (reference is null || !_document.Theme.Colors.TryGetValue(reference.Name, out var value)) return fallback;
        if (reference.Tint == 0) return value;
        var rgb = value.AsSpan(value.Length - 6);
        static int Component(ReadOnlySpan<char> text) => int.Parse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        int Tint(int channel) => Math.Clamp((int)Math.Round(reference.Tint < 0 ? channel * (1 + reference.Tint) : channel + (255 - channel) * reference.Tint), 0, 255);
        var alpha = value.Length == 9 ? value.Substring(1, 2) : "";
        return $"#{alpha}{Tint(Component(rgb[..2])):X2}{Tint(Component(rgb.Slice(2, 2))):X2}{Tint(Component(rgb.Slice(4, 2))):X2}";
    }
}

