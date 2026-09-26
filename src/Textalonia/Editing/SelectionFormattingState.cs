using Textalonia.Model;

namespace Textalonia.Editing;

/// <summary>A property shared by a selection, or a mixed property. Null can be a uniform value.</summary>
public readonly record struct FormattingValue<T>(T Value, bool IsMixed = false);

/// <summary>Formatting of selected content, independent of the editor control and selection direction.</summary>
public sealed class SelectionFormattingState
{
    private readonly IReadOnlyList<TextStyle> _text;
    private readonly IReadOnlyList<ParagraphStyle> _paragraphs;
    public bool IsCollapsed { get; }

    internal SelectionFormattingState(DocumentIndex index, TextSelection selection, TextStyle typingStyle)
    {
        IsCollapsed = selection.IsEmpty;
        if (IsCollapsed)
        {
            _text = [typingStyle];
            _paragraphs = [index.At(selection.Active).Paragraph.Style];
            return;
        }
        var text = new List<TextStyle>();
        var paragraphs = new List<ParagraphStyle>();
        foreach (var entry in index.Enumerate(selection.Start, selection.End))
        {
            if (entry.Start >= selection.End) break;
            paragraphs.Add(entry.Paragraph.Style);
            var start = Math.Max(0, selection.Start - entry.Start);
            var end = Math.Min(entry.Paragraph.Length, selection.End - entry.Start);
            if (end > start)
            {
                var offset = 0;
                foreach (var run in entry.Paragraph.Runs)
                {
                    if (offset < end && offset + run.Storage.Length > start) text.Add(run.Style);
                    offset += run.Storage.Length;
                    if (offset >= end) break;
                }
            }
            else text.Add(entry.Paragraph.DefaultStyle);
        }
        _text = text;
        _paragraphs = paragraphs;
    }

    /// <summary>Aggregates any character property, including effective or application-defined values.</summary>
    public FormattingValue<T> Text<T>(Func<TextStyle, T> property) => Aggregate(_text, property);
    /// <summary>Aggregates any paragraph property over intersecting paragraphs.</summary>
    public FormattingValue<T> Paragraph<T>(Func<ParagraphStyle, T> property) => Aggregate(_paragraphs, property);
    public FormattingValue<bool> Bold => Text(s => s.EffectiveBold);
    public FormattingValue<bool> Italic => Text(s => s.Italic);
    public FormattingValue<bool> Underline => Text(s => s.Underline);
    public FormattingValue<bool> Strikethrough => Text(s => s.Strikethrough);
    public FormattingValue<string?> FontFamily => Text(s => s.FontFamily);
    public FormattingValue<double> FontSize => Text(s => s.FontSize);
    public FormattingValue<int> FontWeight => Text(s => s.EffectiveFontWeight);
    public FormattingValue<int> FontStretch => Text(s => s.FontStretch);
    public FormattingValue<double> LetterSpacing => Paragraph(s => s.LetterSpacing);
    public FormattingValue<ParagraphAlignment> Alignment => Paragraph(s => s.Alignment);
    public FormattingValue<ListKind> List => Paragraph(s => s.List);
    public FormattingValue<int> ListLevel => Paragraph(s => s.ListLevel);
    public FormattingValue<int> HeadingLevel => Paragraph(s => s.HeadingLevel);

    private static FormattingValue<TValue> Aggregate<TStyle, TValue>(IReadOnlyList<TStyle> styles, Func<TStyle, TValue> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (styles.Count == 0) throw new InvalidOperationException("A selection must contain a paragraph.");
        var value = property(styles[0]);
        for (var i = 1; i < styles.Count; i++)
            if (!EqualityComparer<TValue>.Default.Equals(value, property(styles[i]))) return new(default!, true);
        return new(value);
    }
}
