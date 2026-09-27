using Textalonia.Model;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Textalonia.Editing;

// Refcounts refer to graph edges, not a per-snapshot sum. Adding/removing a root
// traverses only newly owned/released branches; shared nodes are charged once.
internal sealed class RetentionGraph
{
    private struct Ownership { public int Total, Current; }
    private readonly Dictionary<object, Ownership> _references = new(ReferenceEqualityComparer.Instance);
    // Reuse the six traversal callbacks instead of allocating two iterator
    // objects per visited node during document ingestion and history changes.
    private readonly Action<object> _addTotal, _addCurrent, _addBoth, _removeTotal, _removeCurrent, _removeBoth;
    public RetentionGraph()
    {
        _addTotal = value => Add(value, true, false);
        _addCurrent = value => Add(value, false, true);
        _addBoth = value => Add(value, true, true);
        _removeTotal = value => Remove(value, true, false);
        _removeCurrent = value => Remove(value, false, true);
        _removeBoth = value => Remove(value, true, true);
    }
    public long Bytes { get; private set; }
    private long _currentBytes;
    public long HistoryBytes => Bytes - _currentBytes;
    public void EnsureCapacity(int count) => _references.EnsureCapacity(count);
    public void Clear()
    {
        _references.Clear(); Bytes = 0; _currentBytes = 0;
    }
    public void Add(object value, bool current = false) => Add(value, true, current);
    private void Add(object value, bool total, bool current)
    {
        // One lookup and one traversal for the two ownership domains. Do not
        // keep the dictionary ref across recursion (a child can resize it).
        ref var ownership = ref CollectionsMarshal.GetValueRefOrAddDefault(_references, value, out _);
        total = total && ownership.Total++ == 0;
        current = current && ownership.Current++ == 0;
        if (!total && !current) return;
        var bytes = Size(value);
        if (total) Bytes += bytes;
        if (current) _currentBytes += bytes;
        VisitChildren(value, total ? current ? _addBoth : _addTotal : _addCurrent);
    }
    public void Remove(object value, bool current = false) => Remove(value, true, current);
    private void Remove(object value, bool total, bool current)
    {
        ref var ownership = ref CollectionsMarshal.GetValueRefOrNullRef(_references, value);
        total = total && --ownership.Total == 0;
        current = current && --ownership.Current == 0;
        if (!total && !current) return;
        if (total) _references.Remove(value);
        var bytes = Size(value);
        if (total) Bytes -= bytes;
        if (current) _currentBytes -= bytes;
        VisitChildren(value, total ? current ? _removeBoth : _removeTotal : _removeCurrent);
    }
    private static long Size(object value) => value switch
    {
        IRetained node => node.Bytes,
        string text => 24 + text.Length * 2L,
        byte[] bytes => 24 + bytes.Length,
        ImmutableDictionary<string, DocumentResource> resources => 56 + resources.Count * 64L,
        ImmutableDictionary<string, string> properties => 56 + properties.Count * 64L,
        DocumentResource => 64,
        InlineDescriptor => 80,
        TableCell[] cells => 24 + cells.Length * 8L,
        double[] widths => 24 + widths.Length * 8L,
        TableRowSizing[] sizing => 24 + sizing.Length * 8L,
        TableCell cell => 96 + (cell.Blocks.Length + cell.MergeOriginalBlocks.Length) * 8L,
        DocumentStyleCatalog catalog => 96 + (catalog.Characters.Count + catalog.Paragraphs.Count + catalog.Tables.Count) * 64L,
        TextStyleOverrides => 1024,
        ParagraphStyleOverrides => 1024,
        DocumentFontDefinition[] fonts => 24 + fonts.Length * 8L,
        TabStop[] tabs => 24 + tabs.Length * 8L,
        TextStyle => 320,
        ParagraphStyle => 256,
        _ => 32
    };
    private static void VisitChildren(object value, Action<object> visit)
    {
        switch (value)
        {
            case DocumentStyleCatalog catalog:
                foreach (var pair in catalog.Characters) { visit(pair.Key); visit(pair.Value); }
                foreach (var pair in catalog.Paragraphs) { visit(pair.Key); visit(pair.Value); }
                foreach (var pair in catalog.Tables) { visit(pair.Key); visit(pair.Value); }
                if (catalog.DefaultCharacterStyleId is { } c) visit(c);
                if (catalog.DefaultParagraphStyleId is { } p) visit(p);
                if (catalog.DefaultTableStyleId is { } t) visit(t);
                break;
            case DocumentDefaults defaults: visit(defaults.Text); visit(defaults.Paragraph); break;
            case DocumentTheme theme:
                if (theme.Name is not null) visit(theme.Name);
                visit(theme.Colors); visit(theme.Fonts); break;
            case DocumentFontDefinition[] fonts:
                foreach (var font in fonts) visit(font);
                break;
            case DocumentFontDefinition font: visit(font.FamilyName); visit(font.ResourceId); break;
            case CharacterStyleDefinition or ParagraphStyleDefinition or TableStyleDefinition or TextStyleOverrides or ParagraphStyleOverrides or TableStyleOverrides:
                // These infrequent immutable records have only model properties. Unwrap
                // presence values so strings/arrays retained solely by history are counted.
                foreach (var property in value.GetType().GetProperties())
                {
                    var child = property.GetValue(value);
                    if (child is null) continue;
                    if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(StyleValue<>))
                        child = property.PropertyType.GetProperty("Value")!.GetValue(child);
                    if (child is ImmutableArray<TabStop> tabs && !tabs.IsDefaultOrEmpty)
                        visit(ImmutableCollectionsMarshal.AsArray(tabs)!);
                    else if (child is not null && !child.GetType().IsValueType) visit(child);
                }
                break;
            case TabStop[] stops: foreach (var stop in stops) visit(stop); break;
            case ImmutableDictionary<string, DocumentResource> resources:
                foreach (var item in resources) { visit(item.Key); visit(item.Value); }
                break;
            case ImmutableDictionary<string, string> properties:
                foreach (var item in properties) { visit(item.Key); visit(item.Value); }
                break;
            case DocumentResource resource:
                visit(resource.MediaType);
                if (resource.Location is not null) visit(resource.Location);
                if (!resource.Data.IsDefaultOrEmpty) visit(ImmutableCollectionsMarshal.AsArray(resource.Data)!);
                break;
            case InlineDescriptor inline:
                visit(inline.AltText); visit(inline.Payload);
                break;
            case ImageInlinePayload image:
                visit(image.ResourceId);
                break;
            case MergeFieldInlinePayload field:
                visit(field.Name);
                if (field.Format is not null) visit(field.Format);
                if (field.FallbackText is not null) visit(field.FallbackText);
                break;
            case ControlInlinePayload control:
                visit(control.Type); visit(control.Properties);
                break;
            case IRetained node:
                node.VisitReferences(visit);
                break;
            // Row arrays contribute allocation only. Visible cells are already
            // owned by indexed nodes; covered cells are in HiddenCellStorage.
            case TableCell cell:
                foreach (var block in cell.Blocks) visit(DocumentNode.HiddenBlock(block));
                foreach (var block in cell.MergeOriginalBlocks) visit(DocumentNode.HiddenBlock(block));
                if (cell.Borders is not null) visit(cell.Borders);
                if (cell.Padding is not null) visit(cell.Padding);
                if (cell.Background is not null) visit(cell.Background);
                break;
            case TableRowSizing[] sizing:
                foreach (var row in sizing) visit(row);
                break;
            case ParagraphStyle paragraph:
                if (paragraph.StyleId is not null) visit(paragraph.StyleId);
                if (paragraph.Overrides is not null) visit(paragraph.Overrides);
                if (paragraph.Borders is not null) visit(paragraph.Borders);
                if (paragraph.Shading is not null) visit(paragraph.Shading);
                if (paragraph.EastAsianGrid is not null) visit(paragraph.EastAsianGrid);
                if (!paragraph.TabStops.IsDefaultOrEmpty) visit(ImmutableCollectionsMarshal.AsArray(paragraph.TabStops)!);
                if (paragraph.ListDefinition is not null) visit(paragraph.ListDefinition);
                break;
            case ListDefinition definition:
                foreach (var level in definition.Levels) visit(level);
                break;
            case ListLevelDefinition level:
                if (level.Text is not null) visit(level.Text);
                visit(level.Prefix); visit(level.Suffix);
                break;
            case BlockBorders borders:
                if (borders.Left is not null) visit(borders.Left);
                if (borders.Top is not null) visit(borders.Top);
                if (borders.Right is not null) visit(borders.Right);
                if (borders.Bottom is not null) visit(borders.Bottom);
                break;
            case BorderSide side:
                if (side.Color is not null) visit(side.Color);
                break;
            case ThemeFontReference reference: visit(reference.Name); break;
            case ThemeColorReference reference: visit(reference.Name); break;
            case TextStyle style:
                if (style.StyleId is not null) visit(style.StyleId);
                if (style.Overrides is not null) visit(style.Overrides);
                if (style.Language is not null) visit(style.Language);
                if (style.UnderlineColor is not null) visit(style.UnderlineColor);
                if (style.ThemeFont is not null) visit(style.ThemeFont);
                if (style.EastAsianThemeFont is not null) visit(style.EastAsianThemeFont);
                if (style.ComplexScriptThemeFont is not null) visit(style.ComplexScriptThemeFont);
                if (style.EastAsianFontFamily is not null) visit(style.EastAsianFontFamily);
                if (style.ComplexScriptFontFamily is not null) visit(style.ComplexScriptFontFamily);
                if (style.ThemeForeground is not null) visit(style.ThemeForeground);
                if (style.ThemeBackground is not null) visit(style.ThemeBackground);
                if (style.FontFamily is not null) visit(style.FontFamily);
                if (style.Foreground is not null) visit(style.Foreground);
                if (style.Background is not null) visit(style.Background);
                if (style.Hyperlink is not null) visit(style.Hyperlink);
                break;
        }
    }
}

/// <summary>A paragraph-relative position valid only for its originating session revision.</summary>
public readonly record struct DocumentPosition(int Revision, Guid ParagraphId, int Offset)
{
    internal Guid Scope { get; init; }
}

internal sealed record DocumentEdit(int BeforeRevision, int AfterRevision, int Start, int RemovedLength,
    int InsertedLength, IReadOnlyCollection<Guid> ChangedParagraphs, bool Reset = false);
