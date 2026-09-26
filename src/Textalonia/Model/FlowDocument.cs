using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Textalonia.Model;

/// <summary>An immutable document snapshot, safe to serialize on a background thread.</summary>
public sealed record FlowDocument
{
    private SnapshotArray<Block> _blocks = SnapshotArray<Block>.From([new Paragraph()]);
    public ImmutableArray<Block> Blocks { get => _blocks.Read(); init => _blocks = SnapshotArray<Block>.From(value); }
    public ImmutableDictionary<string, DocumentResource> Resources { get; init; } = ImmutableDictionary<string, DocumentResource>.Empty;
    internal FlowDocument WithChildren(StorageTree<OrderKey, DocumentNode>? children) => this with
    { _blocks = new(() => children!.Items().Select(p => (Block)p.Value.Source!).ToImmutableArray()) };
    public FlowDocument() { }
    public FlowDocument(IEnumerable<Block> blocks)
    {
        Blocks = blocks.ToImmutableArray();
        if (Blocks.IsEmpty) Blocks = [new Paragraph()];
    }

    public static FlowDocument FromText(string text, TextStyle? style = null) =>
        new(NormalizeNewlines(text).Split('\n').Select(line => new Paragraph(line, style)));

    public static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    [JsonIgnore] public string Text => new DocumentIndex(this).Text;
    /// <summary>Visible text with inline alternative text. Its offsets are not document positions.</summary>
    [JsonIgnore] public string PlainText { get { var index = new DocumentIndex(this); return index.ReadPlainText(0, index.Length); } }

    /// <summary>Releases resources unused by visible content, covered cells, or retained merge backups.</summary>
    public FlowDocument PruneUnusedResources()
    {
        if (Resources.IsEmpty) return this;
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Visit(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
                switch (block)
                {
                    case Paragraph paragraph:
                        foreach (var run in paragraph.Runs)
                            if (run.Inline?.Payload is ImageInlinePayload image) used.Add(image.ResourceId);
                        break;
                    case Section section: Visit(section.Blocks); break;
                    case Table table:
                        foreach (var row in table.Rows)
                            foreach (var cell in row) { Visit(cell.Blocks); Visit(cell.MergeOriginalBlocks); }
                        break;
                }
        }
        Visit(Blocks);
        var resources = Resources.RemoveRange(Resources.Keys.Where(key => !used.Contains(key)));
        return ReferenceEquals(resources, Resources) ? this : this with { Resources = resources };
    }

    public FlowDocument RewriteParagraphs(Func<Paragraph, IEnumerable<Paragraph>> change)
    {
        ImmutableArray<Block> Rewrite(ImmutableArray<Block> blocks) => blocks.SelectMany<Block, Block>(block =>
            block switch
            {
                Paragraph p => change(p),
                Section s => [s with { Blocks = EnsureBlocks(Rewrite(s.Blocks)) }],
                Table t => [t with { Rows = t.Rows.Select((row, r) => row.Select((cell, c) =>
                    t.IsCovered(r, c) ? cell : cell with
                    { Blocks = EnsureBlocks(Rewrite(cell.Blocks)) }
                    ).ToImmutableArray()).ToImmutableArray() }],
                _ => throw new NotSupportedException()
            }).ToImmutableArray();
        return this with { Blocks = EnsureBlocks(Rewrite(Blocks)) };
    }

    public FlowDocument ReplaceBlock(Guid id, Block replacement)
    {
        ImmutableArray<Block> Rewrite(ImmutableArray<Block> blocks) => blocks.Select(b =>
            b.Id == id ? replacement : b switch
            {
                Section section => section with { Blocks = Rewrite(section.Blocks) },
                Table table => table with { Rows = table.Rows.Select((row, r) => row.Select((cell, c) =>
                    table.IsCovered(r, c) ? cell : cell with { Blocks = Rewrite(cell.Blocks) }).ToImmutableArray()).ToImmutableArray() },
                _ => b
            }).ToImmutableArray();
        return this with { Blocks = Rewrite(Blocks) };
    }

    internal static ImmutableArray<Block> EnsureBlocks(ImmutableArray<Block> blocks) =>
        blocks.IsEmpty ? [new Paragraph()] : blocks;
    internal static ImmutableArray<Paragraph> EnsureParagraphs(ImmutableArray<Paragraph> paragraphs) =>
        paragraphs.IsEmpty ? [new Paragraph()] : paragraphs;

    /// <summary>Checks document invariants before accepting external data.</summary>
    public void Validate()
    {
        if (Resources is null || Resources.Count > 4096) throw new FormatException("Invalid document resources.");
        long resourceBytes = 0;
        foreach (var resource in Resources)
        {
            if (!InlineDescriptor.ValidKey(resource.Key) || resource.Value is null) throw new FormatException("Invalid resource identifier.");
            resource.Value.Validate();
            resourceBytes += resource.Value.Data.Length;
        }
        if (resourceBytes > DocumentResource.MaximumDocumentEmbeddedBytes) throw new FormatException("Document embedded resources exceed the size limit.");
        var ids = new HashSet<Guid>();
        var count = 0;
        void Identify(Guid id)
        {
            if (id == Guid.Empty || !ids.Add(id)) throw new FormatException("Document identifiers must be unique and nonempty.");
            if (++count > 100_000) throw new FormatException("Document contains too many elements.");
        }
        void ValidateTextStyle(TextStyle style)
        {
            if (!double.IsFinite(style.FontSize) || style.FontSize is < 1 or > 512)
                throw new FormatException("Font size must be between 1 and 512.");
            if (style.FontWeight is < 1 or > 1000 || style.FontStretch is < 1 or > 9)
                throw new FormatException("Invalid font weight or stretch.");
            if (!Enum.IsDefined(style.Baseline)) throw new FormatException("Unknown baseline.");
            ValidateColor(style.Foreground); ValidateColor(style.Background);
            if (style.Hyperlink is not null && !IsSafeHyperlink(style.Hyperlink))
                throw new FormatException("Links must use http, https, or mailto.");
        }
        void Visit(ImmutableArray<Block> blocks, int depth)
        {
            if (depth > 32 || blocks.IsDefaultOrEmpty) throw new FormatException("Invalid document structure.");
            foreach (var block in blocks)
            {
                if (block is null) throw new FormatException("Null block.");
                Identify(block.Id);
                switch (block)
                {
                    case Paragraph p:
                        if (p.Runs.IsDefault || p.Style is null || p.DefaultStyle is null)
                            throw new FormatException("Invalid paragraph.");
                        ValidateTextStyle(p.DefaultStyle);
                        ListNumbering.ValidateStyle(p.Style);
                        if (!Enum.IsDefined(p.Style.Alignment) || !Enum.IsDefined(p.Style.List) ||
                            p.Style.HeadingLevel is < 0 or > 6 || p.Style.ListLevel is < 0 or > 8 ||
                            !double.IsFinite(p.Style.Indent) || p.Style.Indent is < 0 or > 1000 ||
                            !double.IsFinite(p.Style.SpaceBefore) || p.Style.SpaceBefore is < 0 or > 1000 ||
                            !double.IsFinite(p.Style.SpaceAfter) || p.Style.SpaceAfter is < 0 or > 1000 ||
                            !double.IsFinite(p.Style.RightIndent) || p.Style.RightIndent is < 0 or > 100000 ||
                            !double.IsFinite(p.Style.FirstLineIndent) || p.Style.FirstLineIndent is < -100000 or > 100000 ||
                            !double.IsFinite(p.Style.LetterSpacing) || p.Style.LetterSpacing is < -1000 or > 1000 ||
                            p.Style.LineHeight is { } lineHeight && (!double.IsFinite(lineHeight) || lineHeight <= 0 || lineHeight > 10000))
                            throw new FormatException("Invalid paragraph formatting.");
                        foreach (var run in p.Runs)
                        {
                            if (run is null || run.Style is null || run.Text is null || run.Text.Contains('\n') || run.Text.Contains('\r'))
                                throw new FormatException("Paragraph runs cannot contain hard paragraph breaks.");
                            ValidateTextStyle(run.Style);
                            if (run.Inline is { } inline) { inline.Validate(); Identify(inline.Id); }
                        }
                        break;
                    case Section s:
                        ValidateColor(s.Background); ValidateColor(s.BorderColor);
                        ValidateEdges(s.PaddingEdges); ValidateBorders(s.Borders);
                        if (!double.IsFinite(s.Padding) || s.Padding is < 0 or > 1000) throw new FormatException("Invalid section padding.");
                        Visit(s.Blocks, depth + 1);
                        break;
                    case Table t:
                        if (t.Rows.IsDefaultOrEmpty || t.Rows.Length > 1000 || t.ColumnCount is < 1 or > 100 ||
                            t.Rows.Any(row => row.IsDefault || row.Length != t.ColumnCount))
                            throw new FormatException("Tables must have a rectangular cell grid.");
                        if (t.ColumnWidths.IsDefault || !t.ColumnWidths.IsEmpty &&
                            (t.ColumnWidths.Length != t.ColumnCount || t.ColumnWidths.Any(width => !double.IsFinite(width) || width <= 0 || width > 100000)))
                            throw new FormatException("Column widths must be positive and match the table columns.");
                        if (t.RowSizing.IsDefault || !t.RowSizing.IsEmpty && (t.RowSizing.Length != t.Rows.Length ||
                            t.RowSizing.Any(sizing => sizing is null || !Enum.IsDefined(sizing.Mode) || !double.IsFinite(sizing.Height) ||
                                sizing.Height < 0 || sizing.Height > 100000 || sizing.Mode != TableRowHeightMode.Auto && sizing.Height == 0)))
                            throw new FormatException("Invalid row sizing policies.");
                        var occupied = new bool[t.Rows.Length, t.ColumnCount];
                        for (var r = 0; r < t.Rows.Length; r++)
                            for (var c = 0; c < t.ColumnCount; c++)
                            {
                                var cell = t.Rows[r][c];
                                if (cell is null) throw new FormatException("Null table cell.");
                                Identify(cell.Id); ValidateColor(cell.Background);
                                ValidateEdges(cell.Padding); ValidateBorders(cell.Borders);
                                if (cell.Blocks.IsDefaultOrEmpty || cell.MergeOriginalBlocks.IsDefault)
                                    throw new FormatException("Invalid table cell content.");
                                if (!cell.MergeOriginalBlocks.IsEmpty)
                                {
                                    // Backups are historical snapshots: v1 allowed their IDs to
                                    // overlap live content, but their own structure must be unique.
                                    var liveIds = ids;
                                    ids = [];
                                    Visit(cell.MergeOriginalBlocks, depth + 1);
                                    ids = liveIds;
                                }
                                if (cell.RowSpan < 1 || cell.ColumnSpan < 1 || r + cell.RowSpan > t.Rows.Length || c + cell.ColumnSpan > t.ColumnCount)
                                    throw new FormatException("Invalid cell span.");
                                Visit(cell.Blocks, depth + 1);
                                if (occupied[r, c])
                                {
                                    if (cell.RowSpan != 1 || cell.ColumnSpan != 1) throw new FormatException("Overlapping merged cells.");
                                    continue;
                                }
                                for (var y = r; y < r + cell.RowSpan; y++)
                                    for (var x = c; x < c + cell.ColumnSpan; x++)
                                    {
                                        if (occupied[y, x]) throw new FormatException("Overlapping merged cells.");
                                        occupied[y, x] = true;
                                    }
                            }
                        break;
                    default: throw new FormatException("Unknown block type.");
                }
            }
        }
        Visit(Blocks, 0);
    }

    private static void ValidateEdges(EdgeInsets? edges)
    {
        if (edges is null) return;
        foreach (var value in new[] { edges.Left, edges.Top, edges.Right, edges.Bottom })
            if (!double.IsFinite(value) || value < 0 || value > 1000) throw new FormatException("Invalid padding.");
    }

    private static void ValidateBorders(BlockBorders? borders)
    {
        if (borders is null) return;
        foreach (var side in new[] { borders.Left, borders.Top, borders.Right, borders.Bottom })
        {
            if (side is null) continue;
            if (!double.IsFinite(side.Width) || side.Width < 0 || side.Width > 1000) throw new FormatException("Invalid border width.");
            ValidateColor(side.Color);
        }
    }

    public static bool IsSafeHyperlink(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" or "mailto";

    public static void ValidateColor(string? color)
    {
        if (color is not null && !Regex.IsMatch(color, "^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant))
            throw new FormatException("Colors must be #RRGGBB or #AARRGGBB.");
    }
}

public sealed record ParagraphPosition(Paragraph Paragraph, int Start, Guid ContainerId, Guid TopLevelBlockId)
{
    public int End => Start + Paragraph.Length;
}
