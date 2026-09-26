using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class AccessibilityTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);
    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface) Create(FlowDocument document)
    {
        var editor = new TextaloniaEditor { SynchronizeText = false, Document = document, ShowToolbar = false };
        var window = new Window { Width = 600, Height = 350, Content = editor };
        window.Show(); window.UpdateLayout();
        var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
        return (window, editor, surface);
    }

    [Fact]
    public void Pinned_Avalonia_public_assembly_has_no_text_pattern_contract()
    {
        var assembly = typeof(IValueProvider).Assembly;
        Assert.Null(assembly.GetType("Avalonia.Automation.Provider.ITextProvider"));
        Assert.Null(assembly.GetType("Avalonia.Automation.Provider.ITextRangeProvider"));
        Assert.NotNull(assembly.GetType("Avalonia.Automation.Provider.IValueProvider"));
    }

    [Fact]
    public Task Ranges_navigate_graphemes_words_paragraphs_and_report_reverse_selection() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "a\U0001F469\u200D\U0001F4BB e\u0301\nsecond" };
        var provider = editor.Accessibility;
        Assert.False(provider.HasNativeTextPattern);
        var emoji = provider.Range(1, 1).Expand(DocumentTextUnit.Character);
        Assert.Equal("\U0001F469\u200D\U0001F4BB", emoji.GetText());
        Assert.Equal(6, emoji.End);
        Assert.Equal(1, provider.Range(2, 5).Start);
        Assert.Equal(1, provider.Range(2, 5).End);
        var word = provider.Range(0, 0).MoveEndpoint(true, DocumentTextUnit.Word, 1);
        Assert.Equal("a\U0001F469\u200D\U0001F4BB ", word.GetText());
        Assert.Equal("a\U0001F469\u200D\U0001F4BB e\u0301\n", provider.Range(1, 1).Expand(DocumentTextUnit.Paragraph).GetText());
        provider.Range(7, 9).Select(reverse: true);
        Assert.Equal("e\u0301", provider.SelectionRange.GetText());
        Assert.True(provider.IsSelectionReversed);
        Assert.Equal(7, provider.CaretPosition);
        Assert.Equal(7, provider.CaretRange.Start);
        editor.IsReadOnly = true;
        provider.Range(0, 1).Select();
        Assert.Equal("a", editor.SelectedText);
        var collapsed = provider.Range(0, 1).MoveEndpoint(false, DocumentTextUnit.Document, 1);
        Assert.Equal(editor.Session.Index.Length, collapsed.Start);
        Assert.Equal(collapsed.Start, collapsed.End);
    });

    [Fact]
    public Task Ranges_reject_stale_revisions_and_change_events_do_not_materialize_full_text() => Run(() =>
    {
        var editor = new TextaloniaEditor { SynchronizeText = false, Document = FlowDocument.FromText("before") };
        var provider = editor.Accessibility;
        var range = provider.DocumentRange;
        var changes = new List<DocumentTextChangedEventArgs>();
        provider.Changed += (_, e) => changes.Add(e);
        var fullReads = editor.Session.Index.FullTextReads;
        Assert.Equal("bef", range.GetText(3));
        range.Select(reverse: true);
        Assert.Single(changes);
        Assert.True(changes[0].SelectionChanged);
        Assert.False(changes[0].DocumentChanged);
        Assert.Equal(fullReads, editor.Session.Index.FullTextReads);
        editor.Session.InsertText("after");
        Assert.True(changes[^1].DocumentChanged);
        Assert.Equal(0, editor.Session.Index.FullTextReads);
        Assert.Throws<InvalidOperationException>(() => range.GetText());
        Assert.Throws<InvalidOperationException>(() => range.Select());
        Assert.Equal("after", provider.DocumentRange.GetText());
        editor.IsReadOnly = true;
        Assert.True(changes[^1].ReadOnlyChanged);
        Assert.True(provider.IsReadOnly);
    });

    [Fact]
    public Task Native_peer_preserves_value_pattern_and_reports_value_and_readonly_changes() => Run(() =>
    {
        var (window, editor, surface) = Create(FlowDocument.FromText("hello"));
        try
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(surface)!;
            Assert.Same(editor.Accessibility, peer.GetProvider<DocumentTextProvider>());
            var value = Assert.IsAssignableFrom<IValueProvider>(peer.GetProvider<IValueProvider>());
            var changes = 0;
            peer.PropertyChanged += (_, _) => changes++;
            Assert.Equal("hello", value.Value);
            value.SetValue("updated");
            Assert.Equal("updated", editor.Accessibility.DocumentRange.GetText());
            Assert.True(changes > 0);
            var previous = changes;
            editor.IsReadOnly = true;
            Assert.True(changes > previous);
            Assert.Throws<InvalidOperationException>(() => value.SetValue("forbidden"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Offscreen_bounds_and_scroll_resolve_only_target_geometry_and_preserve_selection() => Run(() =>
    {
        var document = new FlowDocument { Blocks = Enumerable.Range(0, 5000).Select(i => (Block)new Paragraph($"Paragraph {i}: content")).ToImmutableArray() };
        var (window, editor, surface) = Create(document);
        try
        {
            var provider = editor.Accessibility;
            var target = editor.Session.Index.Paragraphs[4500];
            var before = surface.Layout.ShapedParagraphs;
            var range = provider.Range(target.Start, target.Start + 9);
            var bounds = range.GetBoundingRectangles();
            Assert.NotEmpty(bounds);
            Assert.All(bounds, b => Assert.True(b.Top > 10000 && b.Width > 0 && b.Height > 0));
            Assert.InRange(surface.Layout.ShapedParagraphs - before, 1, 80);
            Assert.InRange(surface.Layout.CachedParagraphs, 1, 256);
            Assert.Equal(0, editor.Session.Index.FullTextReads);
            var selection = editor.Session.Selection;
            range.ScrollIntoView();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(editor.Scroller!.Offset.Y > 10000);
            Assert.Equal(selection, editor.Session.Selection);
            Assert.NotEmpty(provider.GetVisibleRanges());
            Assert.InRange(surface.Layout.CachedParagraphs, 1, 256);
            Assert.Throws<InvalidOperationException>(() => provider.DocumentRange.GetBoundingRectangles(1));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Line_navigation_point_ranges_and_caret_bounds_share_document_layout() => Run(() =>
    {
        var (window, editor, _) = Create(FlowDocument.FromText("first line\nsecond line"));
        try
        {
            var provider = editor.Accessibility;
            editor.FocusDocument();
            Assert.True(provider.IsCaretActive);
            Assert.Equal("first line\n", provider.CaretRange.Expand(DocumentTextUnit.Line).GetText());
            var second = provider.CaretRange.MoveEndpoint(true, DocumentTextUnit.Line, 1);
            Assert.Equal(11, second.End);
            var rect = Assert.Single(provider.CaretRange.GetBoundingRectangles());
            Assert.Equal(0, provider.RangeFromPoint(new Point(rect.Left, rect.Top + rect.Height / 2)).Start);
            Assert.True(rect.Height > 0);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Preedit_never_enters_committed_accessibility_ranges_or_returns_mismatched_geometry() => Run(() =>
    {
        var (window, editor, surface) = Create(FlowDocument.FromText("committed"));
        try
        {
            var provider = editor.Accessibility;
            var changed = 0;
            provider.Changed += (_, _) => changed++;
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            surface.RaiseEvent(request);
            Assert.IsAssignableFrom<TextInputMethodClient>(request.Client).SetPreeditText("preview");
            Assert.Equal("committed", provider.DocumentRange.GetText());
            Assert.Equal(0, changed);
            Assert.Throws<InvalidOperationException>(() => provider.CaretRange.GetBoundingRectangles());
            provider.Range(1, 3).Select();
            Assert.False(surface.HasComposition);
            Assert.Equal("om", provider.SelectionRange.GetText());
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Table_and_inline_descriptions_keep_atomic_utf16_coordinates_and_do_not_need_a_visual_factory() => Run(() =>
    {
        var image = new InlineDescriptor { AltText = "A red balloon", Payload = new ImageInlinePayload("missing-image") };
        var table = Table.Create(2, 2).SetCell(1, 1, new TableCell { Blocks = [new Paragraph("target")] });
        var editor = new TextaloniaEditor
        {
            SynchronizeText = false,
            Document = new FlowDocument { Blocks = [new Paragraph([new RichRun("A"), new RichRun(image), new RichRun("B")]), table] }
        };
        var provider = editor.Accessibility;
        Assert.Equal("\uFFFC", provider.Range(1, 2).GetText());
        var description = Assert.Single(provider.Range(1, 2).GetDescriptions());
        Assert.Equal(new DocumentTextDescription(1, 1, "image", "A red balloon"), description);
        var paragraph = editor.Session.Index.Paragraphs[^1];
        var descriptions = provider.Range(paragraph.Start, paragraph.End).GetDescriptions();
        Assert.Contains(descriptions, d => d.Kind == "table" && d.Text == "Table, 2 rows, 2 columns");
        Assert.Contains(descriptions, d => d.Kind == "cell" && d.Text.Contains("Row 2, column 2"));
        Assert.Equal(0, editor.Session.Index.FullTextReads);
    });
    [Fact]
    public Task Detached_geometry_and_rendering_limits_fail_explicitly_while_text_remains_readable() => Run(() =>
    {
        var (window, editor, _) = Create(FlowDocument.FromText("abc \u202b" + new string('x', 8000) + "\u202c"));
        try
        {
            editor.MaxShapingCharacters = 2048;
            window.UpdateLayout();
            var provider = editor.Accessibility;
            Assert.Equal("abc", provider.DocumentRange.GetText(3));
            Assert.Throws<ShapingLimitExceededException>(() => provider.CaretRange.GetBoundingRectangles());
            editor.MaxShapingCharacters = 16384;
            window.UpdateLayout();
            Assert.NotEmpty(provider.CaretRange.GetBoundingRectangles());
            window.Content = null;
            Assert.Throws<InvalidOperationException>(() => provider.CaretRange.GetBoundingRectangles());
            Assert.Equal("abc", provider.DocumentRange.GetText(3));
        }
        finally { window.Close(); }
    });
}
