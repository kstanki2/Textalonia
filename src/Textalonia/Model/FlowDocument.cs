using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Textalonia.Model;

/// <summary>An immutable document snapshot, safe to serialize on a background thread.</summary>
public sealed record FlowDocument
{
    public ImmutableArray<Block> Blocks { get; init; } = [new Paragraph()];
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

    public FlowDocument RewriteParagraphs(Func<Paragraph, IEnumerable<Paragraph>> change)
    {
        ImmutableArray<Block> Rewrite(ImmutableArray<Block> blocks) => blocks.SelectMany<Block, Block>(block =>
            block switch
            {
                Paragraph p => change(p),
                Section s => [s with { Blocks = EnsureBlocks(Rewrite(s.Blocks)) }],
                Table t => [t with { Rows = t.Rows.Select((row, r) => row.Select((cell, c) =>
                    t.IsCovered(r, c) ? cell : cell with
                    { Paragraphs = EnsureParagraphs(cell.Paragraphs.SelectMany(change).ToImmutableArray()) }
                    ).ToImmutableArray()).ToImmutableArray() }],
                _ => throw new NotSupportedException()
            }).ToImmutableArray();
        return this with { Blocks = EnsureBlocks(Rewrite(Blocks)) };
    }

    public FlowDocument ReplaceBlock(Guid id, Block replacement)
    {
        ImmutableArray<Block> Rewrite(ImmutableArray<Block> blocks) => blocks.Select(b =>
            b.Id == id ? replacement : b is Section s ? s with { Blocks = Rewrite(s.Blocks) } : b).ToImmutableArray();
        return this with { Blocks = Rewrite(Blocks) };
    }

    internal static ImmutableArray<Block> EnsureBlocks(ImmutableArray<Block> blocks) =>
        blocks.IsEmpty ? [new Paragraph()] : blocks;
    internal static ImmutableArray<Paragraph> EnsureParagraphs(ImmutableArray<Paragraph> paragraphs) =>
        paragraphs.IsEmpty ? [new Paragraph()] : paragraphs;

    /// <summary>Checks document invariants before accepting external data.</summary>
    public void Validate()
    {
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
                        if (!Enum.IsDefined(p.Style.Alignment) || !Enum.IsDefined(p.Style.List) ||
                            p.Style.HeadingLevel is < 0 or > 6 || p.Style.ListLevel is < 0 or > 8 ||
                            !double.IsFinite(p.Style.Indent) || p.Style.Indent is < 0 or > 1000 ||
                            !double.IsFinite(p.Style.SpaceBefore) || p.Style.SpaceBefore is < 0 or > 1000 ||
                            !double.IsFinite(p.Style.SpaceAfter) || p.Style.SpaceAfter is < 0 or > 1000)
                            throw new FormatException("Invalid paragraph formatting.");
                        foreach (var run in p.Runs)
                        {
                            if (run is null || run.Style is null || run.Text is null || run.Text.Contains('\n') || run.Text.Contains('\r'))
                                throw new FormatException("Paragraph runs cannot contain hard paragraph breaks.");
                            ValidateTextStyle(run.Style);
                        }
                        break;
                    case Section s:
                        ValidateColor(s.Background); ValidateColor(s.BorderColor);
                        if (!double.IsFinite(s.Padding) || s.Padding is < 0 or > 1000) throw new FormatException("Invalid section padding.");
                        Visit(s.Blocks, depth + 1);
                        break;
                    case Table t:
                        if (t.Rows.IsDefaultOrEmpty || t.Rows.Length > 1000 || t.ColumnCount is < 1 or > 100 ||
                            t.Rows.Any(row => row.IsDefault || row.Length != t.ColumnCount))
                            throw new FormatException("Tables must have a rectangular cell grid.");
                        var occupied = new bool[t.Rows.Length, t.ColumnCount];
                        for (var r = 0; r < t.Rows.Length; r++)
                            for (var c = 0; c < t.ColumnCount; c++)
                            {
                                var cell = t.Rows[r][c];
                                if (cell is null) throw new FormatException("Null table cell.");
                                Identify(cell.Id); ValidateColor(cell.Background);
                                if (cell.Paragraphs.IsDefaultOrEmpty || cell.MergeOriginal.IsDefault)
                                    throw new FormatException("Invalid table cell content.");
                                if (!cell.MergeOriginal.IsEmpty) new FlowDocument(cell.MergeOriginal).Validate();
                                if (cell.RowSpan < 1 || cell.ColumnSpan < 1 || r + cell.RowSpan > t.Rows.Length || c + cell.ColumnSpan > t.ColumnCount)
                                    throw new FormatException("Invalid cell span.");
                                Visit(cell.Paragraphs.Cast<Block>().ToImmutableArray(), depth + 1);
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

/// <summary>Maps UTF-16 text positions to visible paragraphs, including table cells.</summary>
public sealed class DocumentIndex
{
    public ImmutableArray<ParagraphPosition> Paragraphs { get; }
    public string Text { get; }
    public int Length => Text.Length;

    public DocumentIndex(FlowDocument document)
    {
        var result = ImmutableArray.CreateBuilder<ParagraphPosition>();
        var offset = 0;
        void Visit(IEnumerable<Block> blocks, Guid container, Guid top)
        {
            foreach (var block in blocks)
            {
                var owner = top == Guid.Empty ? block.Id : top;
                switch (block)
                {
                    case Paragraph p:
                        result.Add(new(p, offset, container, owner));
                        offset += p.Length + 1;
                        break;
                    case Section s: Visit(s.Blocks, s.Id, owner); break;
                    case Table t:
                        for (var r = 0; r < t.Rows.Length; r++)
                            for (var c = 0; c < t.ColumnCount; c++)
                                if (!t.IsCovered(r, c)) Visit(t.Rows[r][c].Paragraphs, t.Rows[r][c].Id, owner);
                        break;
                }
            }
        }
        Visit(document.Blocks, Guid.Empty, Guid.Empty);
        Paragraphs = result.ToImmutable();
        Text = string.Join("\n", Paragraphs.Select(p => p.Paragraph.Text));
    }

    public ParagraphPosition At(int offset)
    {
        if (Paragraphs.IsEmpty) throw new InvalidOperationException("Document has no paragraphs.");
        offset = Math.Clamp(offset, 0, Length);
        var low = 0; var high = Paragraphs.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Paragraphs[mid].Start <= offset) low = mid;
            else high = mid - 1;
        }
        return Paragraphs[low];
    }
}
