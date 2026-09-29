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
    public ImmutableArray<DocumentFontDefinition> Fonts { get; init; } = [];
    public DocumentStyleCatalog Styles { get; init; } = new();
    public DocumentDefaults Defaults { get; init; } = new();
    public DocumentTheme Theme { get; init; } = new();
    /// <summary>Ordered physical section settings. Empty uses one default page section.</summary>
    public ImmutableArray<DocumentSection> Sections { get; init; } = [];
    public ImmutableDictionary<Guid, DocumentStory> Stories { get; init; } = ImmutableDictionary<Guid, DocumentStory>.Empty;
    public ImmutableArray<DocumentNote> Notes { get; init; } = [];
    public ImmutableArray<DocumentBookmark> Bookmarks { get; init; } = [];
    public ImmutableArray<DocumentField> Fields { get; init; } = [];
    public ImmutableArray<DocumentContentControl> ContentControls { get; init; } = [];
    public DocumentProtection Protection { get; init; } = new();
    public ImmutableArray<DocumentPermissionRange> PermissionRanges { get; init; } = [];
    public ImmutableDictionary<string, string> Properties { get; init; } = ImmutableDictionary<string, string>.Empty;
    public DocumentCoreProperties CoreProperties { get; init; } = new();
    public ImmutableArray<DocumentCustomProperty> CustomProperties { get; init; } = [];
    public ImmutableArray<DocumentCustomXmlPart> CustomXmlParts { get; init; } = [];
    public DocumentCompatibilitySettings CompatibilitySettings { get; init; } = new();
    public NoteSettings FootnoteSettings { get; init; } = new();
    public NoteSettings EndnoteSettings { get; init; } = new() { Placement = NotePlacement.DocumentEnd };

    /// <summary>Returns the main story for Guid.Empty, or a standalone view of a secondary story.</summary>
    public FlowDocument GetStoryDocument(Guid storyId) => storyId == Guid.Empty ? this :
        Stories.TryGetValue(storyId, out var story) ? this with
        { Blocks = story.Blocks, Sections = [], Stories = ImmutableDictionary<Guid, DocumentStory>.Empty, Notes = [],
            Bookmarks = Bookmarks.Where(b => b.Start.StoryId == storyId).Select(b => b with
                { Start = b.Start with { StoryId = Guid.Empty }, End = b.End with { StoryId = Guid.Empty } }).ToImmutableArray(),
            Fields = Fields.Where(f => f.Start.StoryId == storyId).Select(f => f with
                { Start = f.Start with { StoryId = Guid.Empty }, End = f.End with { StoryId = Guid.Empty } }).ToImmutableArray(),
            ContentControls = ContentControls.Where(c => c.Start.StoryId == storyId).Select(c => c with
                { Start = c.Start with { StoryId = Guid.Empty }, End = c.End with { StoryId = Guid.Empty } }).ToImmutableArray(),
            PermissionRanges = PermissionRanges.Where(r => r.Start.StoryId == storyId).Select(r => r with
                { Start = r.Start with { StoryId = Guid.Empty }, End = r.End with { StoryId = Guid.Empty } }).ToImmutableArray(),
            Protection = Protection with { ProtectedSectionIds = [] } } :
        throw new ArgumentException("The document story does not exist.", nameof(storyId));

    public DocumentIndex GetStoryIndex(Guid storyId) => new(GetStoryDocument(storyId));

    public DocumentStory? ResolveHeaderFooter(int sectionIndex, bool footer, HeaderFooterVariant variant)
    {
        if (sectionIndex < 0 || sectionIndex >= Sections.Length) throw new ArgumentOutOfRangeException(nameof(sectionIndex));
        for (var i = sectionIndex; i >= 0; i--)
        {
            var reference = Sections[i].HeaderFooter.GetReference(footer, variant);
            if (!reference.LinkToPrevious) return reference.StoryId is { } id ? Stories.GetValueOrDefault(id) : null;
        }
        return null;
    }
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
                            switch (run.Inline?.Payload)
                            {
                                case ImageInlinePayload image:
                                    used.Add(image.ResourceId);
                                    if (image.PreviewResourceId is { } preview) used.Add(preview);
                                    break;
                                case OleInlinePayload ole:
                                    used.Add(ole.ResourceId); used.Add(ole.PreviewResourceId); break;
                            }
                        break;
                    case Section section: Visit(section.Blocks); break;
                    case Table table:
                        foreach (var row in table.Rows)
                            foreach (var cell in row) { Visit(cell.Blocks); Visit(cell.MergeOriginalBlocks); }
                        break;
                }
        }
        Visit(Blocks);
        foreach (var story in Stories.Values) Visit(story.Blocks);
        foreach (var font in Fonts) used.Add(font.ResourceId);
        foreach (var section in Sections) if (section.Watermark?.ResourceId is { } watermark) used.Add(watermark);
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
        if (CoreProperties is null || CompatibilitySettings is null || CustomProperties.IsDefault || CustomXmlParts.IsDefault ||
            CustomProperties.Length > 1024 || CustomXmlParts.Length > 256 || Properties is null || Properties.Count > 1024)
            throw new FormatException("Invalid document package metadata.");
        CoreProperties.Validate();
        CompatibilitySettings.Validate();
        var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in CustomProperties)
        {
            if (property is null) throw new FormatException("Null custom document property.");
            property.Validate();
            if (!propertyNames.Add(property.Name)) throw new FormatException("Duplicate custom document property.");
        }
        var partNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long customXmlBytes = 0;
        foreach (var part in CustomXmlParts)
        {
            if (part is null) throw new FormatException("Null custom XML part.");
            part.Validate();
            if (!partNames.Add(part.PartName) || part.PropertiesPartName is { } propertiesPart && !partNames.Add(propertiesPart))
                throw new FormatException("Duplicate custom XML part name.");
            customXmlBytes += System.Text.Encoding.UTF8.GetByteCount(part.Xml) + System.Text.Encoding.UTF8.GetByteCount(part.PropertiesXml ?? "");
        }
        if (customXmlBytes > 8L * 1024 * 1024) throw new FormatException("Custom XML parts exceed the size limit.");
        foreach (var property in Properties)
            if (string.IsNullOrWhiteSpace(property.Key) || property.Key.Length > 255 || property.Value is null || property.Value.Length > 16384)
                throw new FormatException("Invalid document property.");
        if (Resources is null || Resources.Count > 4096) throw new FormatException("Invalid document resources.");
        long resourceBytes = 0;
        foreach (var resource in Resources)
        {
            if (!InlineDescriptor.ValidKey(resource.Key) || resource.Value is null) throw new FormatException("Invalid resource identifier.");
            resource.Value.Validate();
            resourceBytes += resource.Value.Data.Length;
        }
        if (resourceBytes > DocumentResource.MaximumDocumentEmbeddedBytes) throw new FormatException("Document embedded resources exceed the size limit.");
        DocumentStyleValidation.Validate(this);
        DocumentFontValidation.Validate(this);
        DocumentAnchors.Validate(this);
        DocumentProtectionValidation.Validate(this);
        var resolver = new DocumentStyleResolver(this);
        var ids = new HashSet<Guid>();
        var noteReferences = new HashSet<Guid>();
        var secondary = false;
        var hidden = false;
        var visibleNoteReferences = new HashSet<Guid>();
        var count = 0;
        void Identify(Guid id)
        {
            if (id == Guid.Empty || !ids.Add(id)) throw new FormatException("Document identifiers must be unique and nonempty.");
            if (++count > 100_000) throw new FormatException("Document contains too many elements.");
        }
        void ValidateTextStyle(TextStyle style)
        {
            DocumentStyleValidation.Text(style, this);
            if (style.Overrides is { } overrides) DocumentStyleValidation.Text(overrides.Apply(TextStyle.Default), this);
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
                        DocumentStyleValidation.Text(resolver.ResolveText(p, p.DefaultStyle), this);
                        DocumentStyleValidation.Paragraph(p.Style, this);
                        if (p.Style.Overrides is { } paragraphOverrides)
                            DocumentStyleValidation.Paragraph(paragraphOverrides.Apply(ParagraphStyle.Default), this);
                        DocumentStyleValidation.Paragraph(resolver.ResolveParagraphStyle(p.Style), this);
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
                            DocumentStyleValidation.Text(resolver.ResolveText(p, run.Style), this);
                            if (run.Inline is { } inline)
                            {
                                inline.Validate(); Identify(inline.Id);
                                if (inline.Payload is FormControlInlinePayload && ContentControls.IsDefaultOrEmpty)
                                    throw new FormatException("A form inline requires content-control metadata.");
                                if (inline.Payload is NoteInlinePayload note)
                                {
                                    if (secondary) throw new FormatException("Notes can only be referenced from the main story.");
                                    if (!hidden && !visibleNoteReferences.Add(note.NoteId)) throw new FormatException("A note can have only one visible reference.");
                                    noteReferences.Add(note.NoteId);
                                }
                            }
                        }
                        break;
                    case Section s:
                        if (!Enum.IsDefined(s.Semantic) || s.CodeLanguage is not null &&
                            (s.Semantic != SectionSemantic.CodeBlock || s.CodeLanguage.Length > 128 ||
                            s.CodeLanguage.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '+' or '.' or '#'))))
                            throw new FormatException("Invalid section semantics or code language.");
                        ValidateColor(s.Background); ValidateColor(s.BorderColor);
                        ValidateEdges(s.PaddingEdges); ValidateBorders(s.Borders);
                        if (!double.IsFinite(s.Padding) || s.Padding is < 0 or > 1000) throw new FormatException("Invalid section padding.");
                        Visit(s.Blocks, depth + 1);
                        break;
                    case Table t:
                        if (t.Rows.IsDefaultOrEmpty || t.Rows.Length > 1000 || t.ColumnCount is < 1 or > 100 ||
                            t.Rows.Any(row => row.IsDefault || row.Length != t.ColumnCount))
                            throw new FormatException("Tables must have a rectangular cell grid.");
                        DocumentStyleCatalog.Reference(Styles.Tables, t.StyleId);
                        DocumentStyleValidation.Table(resolver.ResolveTableStyle(t));
                        TableFormatting.ValidateWidth(t.PreferredWidth);
                        if (!Enum.IsDefined(t.AutoFit) || !Enum.IsDefined(t.Alignment) || !double.IsFinite(t.Indent) ||
                            t.Indent is < 0 or > 100000 || t.RepeatHeaderRows < 0 || t.RepeatHeaderRows > t.Rows.Length)
                            throw new FormatException("Invalid table layout settings.");
                        if (t.Position is { } position && (!double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
                            !double.IsFinite(position.Distance) || position.X is < 0 or > 100000 || position.Y is < 0 or > 100000 ||
                            position.Distance is < 0 or > 1000)) throw new FormatException("Invalid positioned table settings.");
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
                                TableFormatting.ValidateWidth(cell.PreferredWidth);
                                if (!Enum.IsDefined(cell.VerticalAlignment) || !Enum.IsDefined(cell.TextDirection))
                                    throw new FormatException("Invalid table cell alignment or direction.");
                                DocumentStyleValidation.Table(cell.StyleOverrides?.Apply(new TableStyle()) ?? new TableStyle());
                                if (cell.Blocks.IsDefaultOrEmpty || cell.MergeOriginalBlocks.IsDefault)
                                    throw new FormatException("Invalid table cell content.");
                                if (!cell.MergeOriginalBlocks.IsEmpty)
                                {
                                    // Backups are historical snapshots: v1 allowed their IDs to
                                    // overlap live content, but their own structure must be unique.
                                    var liveIds = ids;
                                    ids = [];
                                    var wasHidden = hidden; hidden = true;
                                    Visit(cell.MergeOriginalBlocks, depth + 1);
                                    hidden = wasHidden; ids = liveIds;
                                }
                                if (cell.RowSpan < 1 || cell.ColumnSpan < 1 || cell.RowSpan > t.Rows.Length - r || cell.ColumnSpan > t.ColumnCount - c)
                                    throw new FormatException("Invalid cell span.");
                                var previousHidden = hidden; hidden |= t.IsCovered(r, c);
                                Visit(cell.Blocks, depth + 1); hidden = previousHidden;
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
        if (Stories is null || Stories.Count > 10000 || Notes.IsDefault || Notes.Length > 10000 || FootnoteSettings is null || EndnoteSettings is null)
            throw new FormatException("Invalid secondary stories.");
        FootnoteSettings.Validate(DocumentNoteKind.Footnote); EndnoteSettings.Validate(DocumentNoteKind.Endnote);
        secondary = true;
        foreach (var pair in Stories)
        {
            if (pair.Value is null || pair.Key != pair.Value.Id || !Enum.IsDefined(pair.Value.Kind)) throw new FormatException("Invalid story identity or kind.");
            Identify(pair.Key); Visit(pair.Value.Blocks, 0);
        }
        var noteIds = new HashSet<Guid>();
        var noteStories = new HashSet<Guid>();
        foreach (var note in Notes)
        {
            if (note is null || !Enum.IsDefined(note.Kind) || !noteIds.Add(note.Id) || !noteStories.Add(note.StoryId) ||
                !Stories.TryGetValue(note.StoryId, out var story) || story.Kind != (note.Kind == DocumentNoteKind.Footnote ? DocumentStoryKind.Footnote : DocumentStoryKind.Endnote) ||
                note.CustomMark is { } mark && (string.IsNullOrWhiteSpace(mark) || mark.Length > 32 || mark.Any(char.IsControl)))
                throw new FormatException("Invalid note or note story reference.");
            Identify(note.Id);
        }
        if (noteReferences.Any(id => !noteIds.Contains(id))) throw new FormatException("A note reference is detached from its note.");
        DocumentSection.Validate(this, ids);
        ContentControlValidation.Validate(this);
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
            if (!double.IsFinite(side.Width) || side.Width < 0 || side.Width > 1000 || !Enum.IsDefined(side.Kind)) throw new FormatException("Invalid border width.");
            ValidateColor(side.Color);
        }
    }

    public static bool IsSafeHyperlink(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" or "mailto";

    public static void ValidateColor(string? color)
    {
        if (color is not null && !Regex.IsMatch(color, "^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\\z", RegexOptions.CultureInvariant))
            throw new FormatException("Colors must be #RRGGBB or #AARRGGBB.");
    }
}

public sealed record ParagraphPosition(Paragraph Paragraph, int Start, Guid ContainerId, Guid TopLevelBlockId)
{
    public int End => Start + Paragraph.Length;
}
