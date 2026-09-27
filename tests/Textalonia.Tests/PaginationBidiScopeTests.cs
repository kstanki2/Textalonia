using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Textalonia.Layout;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class PaginationBidiScopeTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact(Skip = "Avalonia 12.1.3 BidiData.Reset fixes HasEmbeddings/HasIsolates to false after first shaping; see docs/PAGINATION.md.")]
    public Task Unequal_column_continuation_closes_explicit_override_and_preserves_caret_positions() => fixture.Session.Dispatch(() =>
    {
        var overridden = "\u202e" + string.Concat(Enumerable.Repeat("abcdefgh ijklmnop qrstuvw xyz ", 16));
        var close = overridden.Length;
        var text = overridden + "\u202c " + string.Concat(Enumerable.Repeat("ordinary latin content follows here ", 16));
        var paragraph = new Paragraph(text)
        {
            Style = new() { LineSpacingMode = LineSpacingMode.Exact, LineSpacing = 20,
                SpaceBefore = 0, SpaceAfter = 0, WidowControl = false }
        };
        var document = new FlowDocument([paragraph])
        {
            Sections = [new DocumentSection { PageSettings = new PageSettings
            {
                Width = 340, Height = 100, Margins = new(10, 10, 10, 10),
                Columns = [new(1), new(2)], ColumnSpacing = 20, BalanceColumns = false
            } }]
        };
        using var engine = new PaginationEngine();
        using var pages = engine.Paginate(document, new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter"));

        var covered = 0;
        foreach (var fragment in pages.Fragments)
        {
            Assert.Equal(covered, fragment.TextStart);
            Assert.True(fragment.TextEnd >= fragment.TextStart);
            covered = fragment.TextEnd;
        }
        Assert.Equal(text.Length, covered);

        // Exclude the single line containing PDF so each assertion covers one unambiguous scope.
        var inside = pages.Fragments.Where(f => f.TextStart > 0 && f.TextEnd <= close).ToArray();
        var outside = pages.Fragments.Where(f => f.TextStart > close + 1).ToArray();
        Assert.NotEmpty(inside); Assert.NotEmpty(outside);
        Assert.Contains(inside, f => f.ColumnIndex == 1 && f.Measurement.Offset > 0);
        Assert.Contains(outside, f => f.ColumnIndex == 1 && f.Measurement.Offset > close);

        CheckScope(inside, 1);
        CheckScope(outside, 0);
        CheckInteriorCaret(inside.First(f => f.ColumnIndex == 1 && f.Length > 5));
        CheckInteriorCaret(outside.First(f => f.ColumnIndex == 1 && f.Length > 5));

        static void CheckScope(IEnumerable<LineFragment> fragments, int expectedParity)
        {
            foreach (var fragment in fragments)
            {
                using var lease = fragment.Acquire();
                var alphabeticRuns = lease.Layout.TextLines[fragment.Line.Index].TextRuns.OfType<ShapedTextRun>()
                    .Where(run => run.Text.Span.IndexOfAny("abcdefghijklmnopqrstuvwxyz".AsSpan()) >= 0).ToArray();
                Assert.NotEmpty(alphabeticRuns);
                Assert.All(alphabeticRuns, run => Assert.Equal(expectedParity, run.ShapedBuffer.BidiLevel % 2));
            }
        }

        void CheckInteriorCaret(LineFragment fragment)
        {
            var position = Enumerable.Range(fragment.TextStart + 1, fragment.Length - 2)
                .First(index => char.IsAsciiLetter(text[index - 1]) && char.IsAsciiLetter(text[index]));
            var caret = pages.Caret(position);
            Assert.True(fragment.Clip.Contains(new Point(caret.X, caret.Y + caret.Height / 2)));
            Assert.Equal(position, pages.HitTest(new Point(caret.X, caret.Y + caret.Height / 2)));
        }
    }, CancellationToken.None);
}
