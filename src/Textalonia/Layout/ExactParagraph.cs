using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Model;

namespace Textalonia.Layout;

internal sealed class ShapingEnvironment : IDisposable
{
    private int _references = 1;
    public DocumentFontService Fonts { get; }
    public ShapedLayoutCache Cache { get; } = new(() => { });
    public FontFamily Font { get; }
    public IBrush Foreground { get; }
    public ShapingEnvironment(FlowDocument document, FontFamily font, IBrush foreground)
    { Font = font; Foreground = foreground; Fonts = new(document, font); }
    public void AddRef() => _references++;
    public void Dispose() { if (--_references == 0) { Cache.Clear(); Fonts.Dispose(); } }
}

internal sealed record ExactLine(ParagraphLayout.Page Window, int Index, int Start, int End, double Height, double Baseline);

// A measurement owns only exact line checkpoints. Shared glyph windows remain in the bounded cache.
// Snapshots retain the measurement, so eviction or disposal of an engine never invalidates a displayed page.
internal sealed class ExactParagraph
{
    private int _references = 1;
    private readonly ShapingEnvironment _environment;
    public Paragraph Paragraph { get; }
    public Paragraph Source { get; }
    public double Width { get; }
    public int MaxCharacters { get; }
    public ParagraphLayout Layout { get; }
    public ExactLine[] Lines { get; }
    public double Height { get; }
    public int Offset { get; }
    public ExactParagraph(Paragraph paragraph, double width, int offset, ShapingEnvironment environment, int maxCharacters)
    {
        Source = paragraph; Width = width; MaxCharacters = maxCharacters; Offset = offset;
        Paragraph = offset == 0 ? paragraph : paragraph with { Runs = paragraph.Slice(offset, paragraph.Length - offset),
            Style = paragraph.Style with { FirstLineIndent = 0 } };
        var context = paragraph.Style.RightToLeft || ParagraphText.For(paragraph).RequiresBidiContext;
        if (context && maxCharacters > 0 && paragraph.Length > maxCharacters)
            throw new ShapingLimitExceededException(paragraph.Id, maxCharacters, paragraph.Length);
        _environment = environment; environment.AddRef();
        Layout = new(Paragraph, width, (p, start, text) => context && offset + start > 0
            ? ShapingTextLayout.CreateContinuation(paragraph, width, environment.Font, environment.Foreground,
                offset + start, text, environment.Fonts)
            : DocumentLayout.CreateTextLayout(p, width, environment.Font, environment.Foreground, start, text, environment.Fonts),
            () => { }, environment.Cache, maxCharacters, requiresBidiContext: context);
        try
        {
            var lines = new List<ExactLine>();
            foreach (var window in Layout.View(0, double.PositiveInfinity))
            {
                using var lease = Layout.Acquire(window);
                for (var i = 0; i < window.LineCount; i++)
                {
                    var line = lease.Layout.TextLines[i];
                    var height = paragraph.Style is { SnapToGrid: true, EastAsianGrid.LineSpacing: > 0 } style
                        ? Math.Ceiling(line.Height / style.EastAsianGrid.LineSpacing) * style.EastAsianGrid.LineSpacing : line.Height;
                    lines.Add(new(window, i, offset + window.Start + line.FirstTextSourceIndex,
                        Math.Min(paragraph.Length, offset + window.Start + line.FirstTextSourceIndex + line.Length), height, line.Baseline));
                }
            }
            Lines = lines.ToArray(); Height = Lines.Sum(l => l.Height);
        }
        catch { Layout.Dispose(); environment.Dispose(); throw; }
    }
    public void AddRef() => _references++;
    public void Release() { if (--_references == 0) { Layout.Dispose(); _environment.Dispose(); } }
}
