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
        ImmutableDictionary<TableStyleRegion, TableStyleOverrides> conditions => 56 + conditions.Count * 64L,
        TableStyleOverrides => 192,
        DocumentResource => 64,
        InlineDescriptor => 88,
        ImagePlacement placement => 112 + placement.Contour.Length * 16L,
        DocumentWatermark => 104,
        TableCell[] cells => 24 + cells.Length * 8L,
        double[] widths => 24 + widths.Length * 8L,
        TableRowSizing[] sizing => 24 + sizing.Length * 8L,
        TableCell cell => 96 + (cell.Blocks.Length + cell.MergeOriginalBlocks.Length) * 8L,
        DocumentStyleCatalog catalog => 96 + (catalog.Characters.Count + catalog.Paragraphs.Count + catalog.Tables.Count) * 64L,
        TextStyleOverrides => 1024,
        ParagraphStyleOverrides => 1024,
        DocumentSection[] sections => 24 + sections.Length * 8L,
        PageColumn[] columns => 24 + columns.Length * 8L,
        PageSettings => 144,
        DocumentSection => 96,
        ImmutableDictionary<Guid, DocumentStory> stories => 56 + stories.Count * 64L,
        ImmutableDictionary<Guid, TextSelection> selections => 56 + selections.Count * 64L,
        DocumentStory story => 64 + story.Blocks.Length * 8L,
        DocumentNote[] notes => 24 + notes.Length * 8L,
        DocumentNote => 64,
        DocumentBookmark[] bookmarks => 24 + bookmarks.Length * 8L,
        DocumentField[] fields => 24 + fields.Length * 8L,
        DocumentBookmark => 64,
        DocumentField => 88,
        DocumentContentControl[] controls => 24 + controls.Length * 8L,
        DocumentPermissionRange[] ranges => 24 + ranges.Length * 8L,
        ContentControlItem[] items => 24 + items.Length * 8L,
        DocumentContentControl => 192,
        DocumentPermissionRange => 80,
        DocumentProtection protection => 48 + protection.ProtectedSectionIds.Length * 16L,
        DocumentAnchor => 64,
        InternalLinkDestination => 40,
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
            case DocumentSection[] sections: foreach (var section in sections) visit(section); break;
            case DocumentSection section: visit(section.PageSettings); visit(section.HeaderFooter); if (section.Watermark is { } sectionWatermark) visit(sectionWatermark); break;
            case ImmutableDictionary<Guid, DocumentStory> stories: foreach (var story in stories.Values) visit(story); break;
            case DocumentStory story: foreach (var block in story.Blocks) visit(DocumentNode.HiddenBlock(block)); break;
            case DocumentNote[] notes: foreach (var note in notes) visit(note); break;
            case DocumentNote note: if (note.CustomMark is not null) visit(note.CustomMark); break;
            case DocumentBookmark[] bookmarks: foreach (var bookmark in bookmarks) visit(bookmark); break;
            case DocumentBookmark bookmark: visit(bookmark.Name); visit(bookmark.Start); visit(bookmark.End); break;
            case DocumentField[] fields: foreach (var field in fields) visit(field); break;
            case DocumentField field:
                visit(field.Instruction); visit(field.Start); visit(field.End);
                if (field.LegacyMergeField is not null) visit(field.LegacyMergeField);
                break;
            case InternalLinkDestination link: visit(link.BookmarkName); if (link.Tooltip is not null) visit(link.Tooltip); break;
            case NoteSettings settings: visit(settings.SeparatorText); visit(settings.ContinuationSeparatorText); break;
            case HeaderFooterSettings settings:
                foreach (var footer in new[] { false, true }) foreach (var variant in Enum.GetValues<HeaderFooterVariant>()) visit(settings.GetReference(footer, variant)); break;
            case PageColumn[] columns: foreach (var column in columns) visit(column); break;
            case PageSettings settings:
                visit(settings.Margins);
                if (!settings.Columns.IsDefaultOrEmpty) visit(ImmutableCollectionsMarshal.AsArray(settings.Columns)!);
                if (settings.Background is not null) visit(settings.Background);
                if (settings.Borders is not null) visit(settings.Borders);
                if (settings.Grid is not null) visit(settings.Grid);
                if (settings.LineNumbering is not null) visit(settings.LineNumbering);
                break;
            case DocumentContentControl[] controls:
                foreach (var control in controls) visit(control); break;
            case DocumentPermissionRange[] ranges:
                foreach (var range in ranges) visit(range); break;
            case DocumentPermissionRange range:
                visit(range.Start); visit(range.End);
                if (range.User is not null) visit(range.User);
                if (range.Group is not null) visit(range.Group); break;
            case DocumentContentControl control:
                visit(control.Start); visit(control.End); visit(control.Tag); visit(control.Title); visit(control.Placeholder);
                visit(control.Value); visit(control.DateFormat); visit(control.Data);
                if (control.Binding is not null) visit(control.Binding);
                if (!control.Items.IsDefaultOrEmpty) visit(ImmutableCollectionsMarshal.AsArray(control.Items)!); break;
            case ContentControlItem[] items: foreach (var item in items) visit(item); break;
            case ContentControlItem item: visit(item.DisplayText); visit(item.Value); break;
            case ContentControlBinding binding: visit(binding.StoreItemId); visit(binding.XPath); visit(binding.PrefixMappings); break;
            case DocumentProtection protection: if (protection.Password is not null) visit(protection.Password); break;
            case DocumentProtectionPassword password: visit(password.Algorithm); visit(password.Salt); visit(password.Hash); break;
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
            case ImmutableDictionary<TableStyleRegion, TableStyleOverrides> conditions:
                foreach (var condition in conditions.Values) visit(condition);
                break;
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
                visit(inline.AltText); visit(inline.Payload); if (inline.Placement is { } inlinePlacement) visit(inlinePlacement);
                break;
            case ImageInlinePayload image:
                visit(image.ResourceId); if (image.PreviewResourceId is { } preview) visit(preview);
                break;
            case OleInlinePayload ole:
                visit(ole.ResourceId); visit(ole.PreviewResourceId); visit(ole.ProgramId); visit(ole.FileName);
                break;
            case ImagePlacement placement: visit(placement.Crop); break;
            case DocumentWatermark watermark:
                if (watermark.Text is { } text) visit(text);
                if (watermark.ResourceId is { } resourceId) visit(resourceId);
                visit(watermark.FontFamily); visit(watermark.Color); break;
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
                visit(cell.PreferredWidth);
                if (cell.StyleOverrides is not null) visit(cell.StyleOverrides);
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
                if (paragraph.Frame is not null) visit(paragraph.Frame);
                if (!paragraph.TabStops.IsDefaultOrEmpty) visit(ImmutableCollectionsMarshal.AsArray(paragraph.TabStops)!);
                if (paragraph.ListDefinition is not null) visit(paragraph.ListDefinition);
                break;
            case ListDefinition definition:
                foreach (var level in definition.Levels) visit(level);
                break;
            case ListLevelDefinition level:
                if (level.Text is not null) visit(level.Text);
                visit(level.MarkerFormatting);
                if (level.CharacterStyleId is not null) visit(level.CharacterStyleId);
                if (level.ParagraphStyleId is not null) visit(level.ParagraphStyleId);
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
                if (style.InternalLink is not null) visit(style.InternalLink);
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
