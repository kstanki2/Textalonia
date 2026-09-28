using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Textalonia.Model;
using Textalonia.Proofing;

namespace Textalonia.Controls;

// Automatic opportunities exist only in a shaping projection. Neither the document nor
// any of its UTF-16 positions gains a character. The map is shared by every shaped line.
internal sealed class HyphenationProjection
{
    private readonly int[] _projectedToSource;
    private readonly int[] _sourceToProjected;
    public Paragraph Paragraph { get; }

    private HyphenationProjection(Paragraph paragraph, int[] projectedToSource, int[] sourceToProjected)
    { Paragraph = paragraph; _projectedToSource = projectedToSource; _sourceToProjected = sourceToProjected; }

    public int ToSource(int projected) => _projectedToSource[Math.Clamp(projected, 0, _projectedToSource.Length - 1)];
    public int ToProjected(int source) => _sourceToProjected[Math.Clamp(source, 0, _sourceToProjected.Length - 1)];

    public static HyphenationProjection? Create(Paragraph paragraph, int start, string text, IHyphenationService? service,
        int maxShapingCharacters = 0)
    {
        if (service is null || paragraph.Style.SuppressHyphenation || text.Length < 5) return null;
        var breaks = new SortedSet<int>();
        var storage = ParagraphText.For(paragraph);
        for (var position = 0; position < text.Length;)
        {
            if (!char.IsLetter(text[position])) { position++; continue; }
            var wordStart = position;
            while (position < text.Length && (char.IsLetter(text[position]) ||
                   CharUnicodeInfo.GetUnicodeCategory(text, position) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)) position++;
            var wordEnd = position;
            if (wordEnd - wordStart < 5 ||
                wordStart == 0 && start > 0 && char.IsLetter(storage.Read(start - 1, 1)[0]) ||
                wordEnd == text.Length && start + wordEnd < paragraph.Length &&
                    char.IsLetter(storage.Read(start + wordEnd, 1)[0])) continue;
            var language = paragraph.StyleAt(start + wordStart).Language;
            if (Enumerable.Range(wordStart + 1, wordEnd - wordStart - 1)
                .Any(offset => !string.Equals(paragraph.StyleAt(start + offset).Language, language, StringComparison.OrdinalIgnoreCase))) continue;
            var word = text[wordStart..wordEnd];
            var graphemes = StringInfo.ParseCombiningCharacters(word);
            if (!paragraph.Style.HyphenateCaps && word.All(ch => !char.IsLetter(ch) || char.IsUpper(ch))) continue;
            CultureInfo culture;
            try { culture = language is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(language); }
            catch (CultureNotFoundException) { continue; }
            foreach (var offset in service.BreakPositions(word, culture))
            {
                if (offset < 2 || offset > word.Length - 2 ||
                    !char.IsLetter(word[offset - 1]) || !char.IsLetter(word[offset])) continue;
                var location = wordStart + offset;
                if (graphemes.Contains(offset) &&
                    text[location - 1] != '\u00AD' && text[location] != '\u00AD') breaks.Add(location);
            }
        }
        if (maxShapingCharacters > 0)
        {
            var allowance = Math.Max(0, maxShapingCharacters - text.Length);
            while (breaks.Count > allowance) breaks.Remove(breaks.Max);
        }
        if (breaks.Count == 0) return null;
        var runs = new List<RichRun>();
        var cursor = 0;
        foreach (var location in breaks)
        {
            runs.AddRange(paragraph.Slice(start + cursor, location - cursor));
            runs.Add(new RichRun("\u00AD", paragraph.StyleAt(start + location - 1)));
            cursor = location;
        }
        runs.AddRange(paragraph.Slice(start + cursor, text.Length - cursor));
        var projected = paragraph with
        {
            Runs = Paragraph.Normalize(runs),
            Style = start == 0 ? paragraph.Style : paragraph.Style with { FirstLineIndent = 0 }
        };
        var projectedToSource = new int[text.Length + breaks.Count + 1];
        var sourceToProjected = new int[text.Length + 1];
        var index = 0;
        for (var source = 0; source < text.Length; source++)
        {
            if (breaks.Contains(source)) projectedToSource[++index] = source;
            projectedToSource[++index] = source + 1;
        }
        for (var projectedIndex = 0; projectedIndex <= index; projectedIndex++)
            sourceToProjected[projectedToSource[projectedIndex]] = projectedIndex;
        return new(projected, projectedToSource, sourceToProjected);
    }
}

// Translates line hit, caret and selection positions back to storage coordinates.
// The inner line still draws its soft hyphen only when the formatter chose that break.
internal sealed class HyphenatedTextLine(TextLine line, HyphenationProjection projection,
    int projectedOffset = 0, int sourceOffset = 0) : TextLine
{
    private int Source(int projectedIndex) => projection.ToSource(projectedOffset + projectedIndex) - sourceOffset;
    private int Projected(int sourceIndex) => projection.ToProjected(sourceOffset + sourceIndex) - projectedOffset;
    internal int SourceRunIndex(int projectedIndex) => Source(projectedIndex);
    public override IReadOnlyList<TextRun> TextRuns => line.TextRuns;
    public override int FirstTextSourceIndex => Source(line.FirstTextSourceIndex);
    public override int Length => Source(line.FirstTextSourceIndex + line.Length) - FirstTextSourceIndex;
    public override TextLineBreak? TextLineBreak => line.TextLineBreak;
    public override double Baseline => line.Baseline;
    public override double Extent => line.Extent;
    public override bool HasCollapsed => line.HasCollapsed;
    public override bool HasOverflowed => line.HasOverflowed;
    public override double Height => line.Height;
    public override int NewLineLength => line.NewLineLength;
    public override double OverhangAfter => line.OverhangAfter;
    public override double OverhangLeading => line.OverhangLeading;
    public override double OverhangTrailing => line.OverhangTrailing;
    public override double Start => line.Start;
    public override int TrailingWhitespaceLength => line.TrailingWhitespaceLength;
    public override double Width => line.Width;
    public override double WidthIncludingTrailingWhitespace => line.WidthIncludingTrailingWhitespace;
    public override void Draw(DrawingContext context, Point origin) => line.Draw(context, origin);
    public override TextLine Collapse(params TextCollapsingProperties?[] collapsingProperties) => line.Collapse(collapsingProperties);
    public override void Justify(JustificationProperties justificationProperties) => line.Justify(justificationProperties);

    private CharacterHit ToSource(CharacterHit hit)
    {
        var first = Source(hit.FirstCharacterIndex);
        var end = Source(hit.FirstCharacterIndex + hit.TrailingLength);
        return new(first, end - first);
    }
    private CharacterHit ToProjected(CharacterHit hit)
    {
        var first = Projected(hit.FirstCharacterIndex);
        var end = Projected(hit.FirstCharacterIndex + hit.TrailingLength);
        return new(first, end - first);
    }
    public override CharacterHit GetCharacterHitFromDistance(double distance) => ToSource(line.GetCharacterHitFromDistance(distance));
    public override double GetDistanceFromCharacterHit(CharacterHit characterHit) =>
        line.GetDistanceFromCharacterHit(ToProjected(characterHit));
    public override CharacterHit GetNextCaretCharacterHit(CharacterHit characterHit)
    {
        var current = ToProjected(characterHit);
        for (var i = 0; i < 2; i++)
        {
            current = line.GetNextCaretCharacterHit(current);
            var mapped = ToSource(current);
            if (mapped != characterHit) return mapped;
        }
        return ToSource(current);
    }
    public override CharacterHit GetPreviousCaretCharacterHit(CharacterHit characterHit)
    {
        var current = ToProjected(characterHit);
        for (var i = 0; i < 2; i++)
        {
            current = line.GetPreviousCaretCharacterHit(current);
            var mapped = ToSource(current);
            if (mapped != characterHit) return mapped;
        }
        return ToSource(current);
    }
    public override CharacterHit GetBackspaceCaretCharacterHit(CharacterHit characterHit) =>
        ToSource(line.GetBackspaceCaretCharacterHit(ToProjected(characterHit)));
    public override IReadOnlyList<TextBounds> GetTextBounds(int firstTextSourceCharacterIndex, int textLength)
    {
        var start = Projected(firstTextSourceCharacterIndex);
        var end = Projected(firstTextSourceCharacterIndex + textLength);
        return line.GetTextBounds(start, end - start);
    }
    public override void Dispose() { }
}
