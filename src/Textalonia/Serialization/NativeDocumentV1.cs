using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>The frozen v1 vocabulary. Do not add current-model properties to these DTOs.</summary>
internal static class NativeDocumentV1
{
    private sealed record Envelope(int Version, Document? Document);

    private sealed record Document
    {
        public ImmutableArray<Node> Blocks { get; init; } = [new ParagraphNode()];
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(ParagraphNode), "paragraph")]
    [JsonDerivedType(typeof(SectionNode), "section")]
    [JsonDerivedType(typeof(TableNode), "table")]
    private abstract record Node
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public abstract Block Migrate();
    }

    private sealed record ParagraphNode : Node
    {
        public ImmutableArray<Run> Runs { get; init; } = [];
        public ParagraphFormatting? Style { get; init; } = new();
        public CharacterFormatting? DefaultStyle { get; init; } = new();

        public override Paragraph Migrate() => new()
        {
            Id = Id,
            Runs = Required(Runs).Select(run => run is null ? throw Invalid() : run.Migrate()).ToImmutableArray(),
            Style = Style?.Migrate() ?? throw Invalid(),
            DefaultStyle = DefaultStyle?.Migrate() ?? throw Invalid()
        };
    }

    private sealed record SectionNode : Node
    {
        public ImmutableArray<Node> Blocks { get; init; } = [new ParagraphNode()];
        public string? Background { get; init; }
        public string? BorderColor { get; init; }
        public double Padding { get; init; } = 12;

        public override Block Migrate() => new Section
        {
            Id = Id, Blocks = MigrateBlocks(Blocks), Background = Background,
            BorderColor = BorderColor, Padding = Padding
        };
    }

    private sealed record TableNode : Node
    {
        public ImmutableArray<ImmutableArray<Cell>> Rows { get; init; } = [];

        public override Block Migrate() => new Table
        {
            Id = Id,
            Rows = Required(Rows).Select(row => Required(row)
                .Select(cell => cell is null ? throw Invalid() : cell.Migrate()).ToImmutableArray()).ToImmutableArray()
        };
    }

    private sealed record Cell
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public ImmutableArray<ParagraphNode> Paragraphs { get; init; } = [new ParagraphNode()];
        public int ColumnSpan { get; init; } = 1;
        public int RowSpan { get; init; } = 1;
        public string? Background { get; init; }
        public ImmutableArray<ParagraphNode> MergeOriginal { get; init; } = [];

        public TableCell Migrate() => new()
        {
            Id = Id,
            Blocks = Required(Paragraphs).Select(p => (Block)(p?.Migrate() ?? throw Invalid())).ToImmutableArray(),
            ColumnSpan = ColumnSpan, RowSpan = RowSpan, Background = Background,
            MergeOriginalBlocks = Required(MergeOriginal).Select(p => (Block)(p?.Migrate() ?? throw Invalid())).ToImmutableArray()
        };
    }

    private sealed record Run
    {
        public string? Text { get; init; }
        public CharacterFormatting? Style { get; init; }
        public RichRun Migrate() => new(Text ?? throw Invalid(), Style?.Migrate() ?? throw Invalid());
    }

    private sealed record CharacterFormatting
    {
        public string? FontFamily { get; init; }
        public double FontSize { get; init; } = 16;
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public bool Underline { get; init; }
        public bool Strikethrough { get; init; }
        public string? Foreground { get; init; }
        public string? Background { get; init; }
        public string? Hyperlink { get; init; }
        public Baseline Baseline { get; init; }

        public TextStyle Migrate() => new()
        {
            FontFamily = FontFamily, FontSize = FontSize, Bold = Bold, Italic = Italic,
            Underline = Underline, Strikethrough = Strikethrough, Foreground = Foreground,
            Background = Background, Hyperlink = Hyperlink, Baseline = Baseline
        };
    }

    private sealed record ParagraphFormatting
    {
        public ParagraphAlignment Alignment { get; init; }
        public ListKind List { get; init; }
        public int ListLevel { get; init; }
        public int HeadingLevel { get; init; }
        public double SpaceBefore { get; init; }
        public double SpaceAfter { get; init; } = 8;
        public double Indent { get; init; }
        public bool RightToLeft { get; init; }

        public ParagraphStyle Migrate() => new()
        {
            Alignment = Alignment, List = List, ListLevel = ListLevel, HeadingLevel = HeadingLevel,
            SpaceBefore = SpaceBefore, SpaceAfter = SpaceAfter, Indent = Indent, RightToLeft = RightToLeft
        };
    }

    internal static FlowDocument Read(JsonElement json, JsonSerializerOptions options)
    {
        var envelope = json.Deserialize<Envelope>(options) ?? throw Invalid();
        if (envelope.Document is null) throw new FormatException("Missing document.");
        return new FlowDocument { Blocks = MigrateBlocks(envelope.Document.Blocks) };
    }

    private static ImmutableArray<Block> MigrateBlocks(ImmutableArray<Node> nodes) =>
        Required(nodes).Select(node => node?.Migrate() ?? throw Invalid()).ToImmutableArray();

    private static ImmutableArray<T> Required<T>(ImmutableArray<T> value) => value.IsDefault ? throw Invalid() : value;
    private static FormatException Invalid() => new("Invalid version 1 document structure.");
}
