using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    /// <summary>Replaces the style catalog as one undoable edit; all dependants are invalidated.</summary>
    public void SetStyles(DocumentStyleCatalog styles)
    {
        ArgumentNullException.ThrowIfNull(styles);
        if (IsReadOnly) return;
        var document = Document with { Styles = styles };
        document.Validate();
        Commit(document, Selection);
    }

    public void SetDefaults(DocumentDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (IsReadOnly) return;
        var document = Document with { Defaults = defaults };
        document.Validate();
        Commit(document, Selection);
    }

    public void SetTheme(DocumentTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (IsReadOnly) return;
        var document = Document with { Theme = theme };
        document.Validate();
        Commit(document, Selection);
    }

    /// <summary>Applies a paragraph style. Clearing direct formatting also lets its character formatting flow into runs.</summary>
    public void ApplyNamedParagraphStyle(string id, bool clearDirectFormatting = true)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (IsReadOnly) return;
        if (!Document.Styles.Paragraphs.ContainsKey(id)) throw new ArgumentException("Unknown paragraph style.", nameof(id));
        TextStyle ClearText(TextStyle value) => clearDirectFormatting ? value with { Overrides = new() } : value;
        var document = ChangeParagraphs(entry =>
        {
            var paragraph = entry.Paragraph;
            return paragraph with
            {
                Style = paragraph.Style with { StyleId = id, Overrides = clearDirectFormatting ? new() :
                    paragraph.Style.Overrides ?? ParagraphStyleOverrides.FromStyle(paragraph.Style) },
                DefaultStyle = ClearText(paragraph.DefaultStyle),
                Runs = paragraph.Runs.Select(run => run with { Style = ClearText(run.Style) }).ToImmutableArray()
            };
        });
        document.Validate();
        Commit(document, Selection, editedRange: Selection, typingStyle: ClearText(TypingStyle));
    }

    /// <summary>Applies a character style to the selection, or to subsequently typed text.</summary>
    public void ApplyNamedCharacterStyle(string id, bool clearDirectFormatting = true)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (IsReadOnly) return;
        if (!Document.Styles.Characters.ContainsKey(id)) throw new ArgumentException("Unknown character style.", nameof(id));
        ChangeStoredTextStyle(value => value with { StyleId = id, Overrides = clearDirectFormatting ? new() :
            value.Overrides ?? TextStyleOverrides.FromStyle(value) });
    }

    /// <summary>Changes sparse character overrides. Set a member to default to resume inheritance.</summary>
    public void ChangeTextOverrides(Func<TextStyleOverrides, TextStyleOverrides> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (IsReadOnly) return;
        ChangeStoredTextStyle(value => value with { Overrides = change(value.Overrides ?? TextStyleOverrides.FromStyle(value)) });
    }

    /// <summary>Changes sparse paragraph overrides. Set a member to default to resume inheritance.</summary>
    public void ChangeParagraphOverrides(Func<ParagraphStyleOverrides, ParagraphStyleOverrides> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (IsReadOnly) return;
        var document = ChangeParagraphs(entry => entry.Paragraph with
        {
            Style = entry.Paragraph.Style with { Overrides = change(entry.Paragraph.Style.Overrides ??
                ParagraphStyleOverrides.FromStyle(entry.Paragraph.Style)) }
        });
        document.Validate();
        Commit(document, Selection, editedRange: Selection);
    }

    private void ChangeStoredTextStyle(Func<TextStyle, TextStyle> change)
    {
        var typing = change(TypingStyle);
        var selection = Selection;
        var document = selection.IsEmpty ? Document : ChangeParagraphs(entry =>
        {
            var paragraph = entry.Paragraph;
            var from = Math.Max(0, selection.Start - entry.Start);
            var to = Math.Min(paragraph.Length, selection.End - entry.Start);
            return to > from ? paragraph.Format(from, to - from, change) :
                paragraph with { DefaultStyle = change(paragraph.DefaultStyle) };
        });
        document.Validate();
        (document with { Blocks = [new Paragraph("", typing)], Sections = [] }).Validate();
        Commit(document, selection, editedRange: selection, typingStyle: typing);
    }

    private ParagraphStyle FollowingParagraphStyle(ParagraphStyle style)
    {
        if (style.StyleId is { } id && Document.Styles.Paragraphs.TryGetValue(id, out var definition) && definition.NextStyle is { } next)
            return ParagraphStyle.ForStyle(next);
        return ChangeParagraphStyle(style, value => value with { ListRestart = false, ListStart = null, PageBreakBefore = false, ColumnBreakBefore = false }, new(Document));
    }

    private static TextStyle ChangeTextStyle(Paragraph paragraph, TextStyle stored,
        Func<TextStyle, TextStyle> change, DocumentStyleResolver resolver)
    {
        var effective = resolver.ResolveText(paragraph, stored);
        var changed = change(effective) ?? throw new ArgumentException("Formatting cannot be null.", nameof(change));
        return stored.Overrides is null ? changed : stored with
        { Overrides = TextStyleOverrides.Difference(effective, changed, stored.Overrides) };
    }

    private static ParagraphStyle ChangeParagraphStyle(ParagraphStyle stored,
        Func<ParagraphStyle, ParagraphStyle> change, DocumentStyleResolver resolver)
    {
        var effective = resolver.ResolveParagraphStyle(stored);
        var changed = change(effective) ?? throw new ArgumentException("Formatting cannot be null.", nameof(change));
        return stored.Overrides is null ? changed : stored with
        { Overrides = ParagraphStyleOverrides.Difference(effective, changed, stored.Overrides) };
    }
}
