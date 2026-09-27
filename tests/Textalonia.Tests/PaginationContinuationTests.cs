using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Textalonia.Layout;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class PaginationContinuationTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact(Skip = "Avalonia 12.1.3 BidiData.Reset fixes HasEmbeddings/HasIsolates to false after first shaping; see docs/PAGINATION.md.")]
    public Task Repeated_native_override_layout_retains_direction() => fixture.Session.Dispatch(() =>
    {
        var paragraph = new Paragraph("\u202e" + string.Concat(Enumerable.Repeat("abcdefgh ijklmnop qrstuvw xyz ", 15)) + "\u202c")
        { Style = new() { LineSpacingMode = LineSpacingMode.Exact, LineSpacing = 20 } };
        var font = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter");
        using var first = Textalonia.Controls.DocumentLayout.CreateTextLayout(paragraph, 100, font, Brushes.Black, text: new string(paragraph.Text.AsSpan()));
        using var second = Textalonia.Controls.DocumentLayout.CreateTextLayout(paragraph, 100, font, Brushes.Black, text: new string(paragraph.Text.AsSpan()));
        Assert.All(first.TextLines[0].TextRuns.OfType<ShapedTextRun>().Where(r => r.Text.Span.IndexOfAny("abcdefghijklmnopqrstuvwxyz".AsSpan()) >= 0), r => Assert.Equal(1, r.ShapedBuffer.BidiLevel % 2));
        Assert.All(second.TextLines[0].TextRuns.OfType<ShapedTextRun>().Where(r => r.Text.Span.IndexOfAny("abcdefghijklmnopqrstuvwxyz".AsSpan()) >= 0), r => Assert.Equal(1, r.ShapedBuffer.BidiLevel % 2));
    }, CancellationToken.None);
    [Fact(Skip = "Avalonia 12.1.3 BidiData.Reset fixes HasEmbeddings/HasIsolates to false after first shaping; see docs/PAGINATION.md.")]
    public Task Unequal_column_continuation_preserves_paragraph_scoped_bidi_override() => fixture.Session.Dispatch(() =>
    {
        var paragraph = new Paragraph("\u202e" + string.Concat(Enumerable.Repeat("abcdefgh ijklmnop qrstuvw xyz ", 15)) + "\u202c")
        { Style = new() { LineSpacingMode = LineSpacingMode.Exact, LineSpacing = 20, SpaceBefore = 0, SpaceAfter = 0, WidowControl = false } };
        var document = new FlowDocument([paragraph]) { Sections = [new() { PageSettings = new()
            { Width = 340, Height = 100, Margins = new(10, 10, 10, 10), Columns = [new(1), new(2)], ColumnSpacing = 20, BalanceColumns = false } }] };
        using var engine = new PaginationEngine();
        using var pages = engine.Paginate(document, new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter"));
        using var initialLease = pages.Fragments[0].Acquire();
        Assert.Contains(initialLease.Layout.TextLines[0].TextRuns.OfType<ShapedTextRun>(), r => r.ShapedBuffer.BidiLevel % 2 == 1);
        Assert.Contains(initialLease.Layout.TextLines[5].TextRuns.OfType<ShapedTextRun>(), r => r.ShapedBuffer.BidiLevel % 2 == 1);
        var continuation = pages.Fragments.First(f => f.ColumnIndex == 1);
        Assert.True(continuation.Measurement.Offset > 0);
        using var lease = continuation.Acquire();
        var line = lease.Layout.TextLines[continuation.Line.Index];
        var shaped = line.TextRuns.OfType<ShapedTextRun>().Where(r => r.Length > 0 && r.Text.Span.IndexOfAny("abcdefghijklmnopqrstuvwxyz".AsSpan()) >= 0).ToArray();
        Assert.NotEmpty(shaped);
        Assert.All(shaped, run => Assert.Equal(1, run.ShapedBuffer.BidiLevel % 2));
        Assert.True(lease.Layout.ContextCharacters > 0);
        Assert.Equal(paragraph.Length, pages.Fragments.Sum(f => f.Length));
    }, CancellationToken.None);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 12)]
    public Task Unequal_columns_preserve_ordinary_mixed_direction_and_caret_geometry(bool rightToLeft, double indent) => fixture.Session.Dispatch(() =>
    {
        var text = string.Concat(Enumerable.Repeat("\u05d0\u05d1\u05d2\u05d3 latin \u0627\u0628\u062c \u05d4\u05d5\u05d6 123 ", 18));
        var paragraph = new Paragraph(text) { Style = new() { RightToLeft = rightToLeft, FirstLineIndent = indent,
            LineSpacingMode = LineSpacingMode.Exact, LineSpacing = 20, SpaceBefore = 0, SpaceAfter = 0, WidowControl = false } };
        var document = new FlowDocument([paragraph]) { Sections = [new() { PageSettings = new()
            { Width = 340, Height = 100, Margins = new(10, 10, 10, 10), Columns = [new(1), new(2)], ColumnSpacing = 20, BalanceColumns = false } }] };
        using var engine = new PaginationEngine();
        using var pages = engine.Paginate(document, new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter"));
        Assert.Contains(pages.Fragments, fragment => fragment.ColumnIndex == 1 && fragment.Measurement.Offset > 0);
        var covered = 0;
        foreach (var fragment in pages.Fragments)
        {
            Assert.Equal(covered, fragment.TextStart);
            covered = fragment.TextEnd;
            using var lease = fragment.Acquire();
            var directionalRuns = lease.Layout.TextLines[fragment.Line.Index].TextRuns.OfType<ShapedTextRun>()
                .Where(run => run.Text.Span.IndexOfAny("\u05d0\u05d1\u05d2\u05d3\u05d4\u05d5\u05d6\u0627\u0628\u062c".AsSpan()) >= 0).ToArray();
            Assert.All(directionalRuns, run => Assert.Equal(1, run.ShapedBuffer.BidiLevel % 2));
        }
        Assert.Equal(text.Length, covered);
        var continuation = pages.Fragments.First(fragment => fragment.ColumnIndex == 1 && fragment.Length > 5 &&
            Enumerable.Range(fragment.TextStart + 1, fragment.Length - 2).Any(index => text[index - 1] is >= '\u05d0' and <= '\u05ea' && text[index] is >= '\u05d0' and <= '\u05ea'));
        var position = Enumerable.Range(continuation.TextStart + 1, continuation.Length - 2)
            .First(index => text[index - 1] is >= '\u05d0' and <= '\u05ea' && text[index] is >= '\u05d0' and <= '\u05ea');
        var caret = pages.Caret(position);
        Assert.True(continuation.Clip.Contains(new Point(caret.X, caret.Y + caret.Height / 2)));
        Assert.Equal(position, pages.HitTest(new Point(caret.X, caret.Y + caret.Height / 2)));
    }, CancellationToken.None);

    [Fact]
    public Task Balancing_keeps_the_full_height_of_a_single_line() => fixture.Session.Dispatch(() =>
    {
        var paragraph = new Paragraph("one") { Style = new() { LineSpacingMode = LineSpacingMode.Exact,
            LineSpacing = 20, SpaceBefore = 0, SpaceAfter = 0, WidowControl = false } };
        var document = new FlowDocument([paragraph]) { Sections = [new() { PageSettings = new()
            { Width = 340, Height = 100, Margins = new(10, 10, 10, 10), Columns = [new(1), new(1)], BalanceColumns = true } }] };
        using var engine = new PaginationEngine();
        using var pages = engine.Paginate(document);
        var fragment = Assert.Single(pages.Fragments);
        Assert.Equal(20, fragment.Bounds.Height);
        Assert.True(fragment.Clip.Height >= 20);
    }, CancellationToken.None);

}
