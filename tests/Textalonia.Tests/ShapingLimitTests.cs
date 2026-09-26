using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class ShapingLimitTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static FontFamily Font => new("avares://Avalonia.Fonts.Inter/Assets#Inter");

    [Theory]
    [InlineData("bidi")]
    [InlineData("grapheme")]
    [InlineData("line")]
    public Task Strict_limit_rejects_before_oversized_shaping_and_disposes_partial_work(string kind) => fixture.Session.Dispatch(() =>
    {
        var paragraph = kind switch
        {
            "bidi" => new Paragraph("abc \u202b" + new string('x', 8000) + "\u202c"),
            "grapheme" => new Paragraph("a" + new string('\u0301', 8000)),
            _ => new Paragraph(new string('i', 8000), TextStyle.Default with { FontSize = 1 })
        };
        var largest = 0; var created = 0; var disposed = 0;
        var cache = new ShapedLayoutCache(() => disposed++);
        using var layout = new ParagraphLayout(paragraph, 10000, (p, start, text) =>
        {
            largest = Math.Max(largest, text.Length); created++;
            return DocumentLayout.CreateTextLayout(p, 10000, Font, Brushes.Black, start, text);
        }, () => disposed++, cache, 2048);
        var error = Assert.Throws<ShapingLimitExceededException>(() => layout.At(0));
        Assert.Equal(paragraph.Id, error.ParagraphId);
        Assert.Equal(2048, error.CharacterLimit);
        Assert.True(error.RequestedCharacters > error.CharacterLimit);
        Assert.InRange(largest, 0, 2048);
        Assert.Equal(created, disposed);
        Assert.Equal(0, cache.Bytes);
        Assert.Equal(0, cache.Count);
        Assert.InRange(cache.PeakBytes, 0, ShapedLayoutCache.ByteLimit + 256 + 2048 * 32);
    }, CancellationToken.None);

    [Fact]
    public Task Exact_context_within_the_limit_matches_unrestricted_geometry() => fixture.Session.Dispatch(() =>
    {
        var document = new FlowDocument([new Paragraph("abc \u202b" + new string('x', 3000) + "\u202c tail")]);
        using var bounded = new DocumentLayout(); using var full = new DocumentLayout();
        bounded.Build(document, 800, Font, Brushes.Black, Brushes.Gray, new Thickness(28), maxShapingCharacters: 4096);
        full.Build(document, 800, Font, Brushes.Black, Brushes.Gray, new Thickness(28));
        foreach (var offset in new[] { 0, 2048, document.Text.Length }) Assert.Equal(full.Caret(offset), bounded.Caret(offset));
        Assert.InRange(bounded.LargestShapingWindow, 1, 4096);
        Assert.InRange(bounded.PeakLayoutBytes, 1, ShapedLayoutCache.ByteLimit + 256 + 4096 * 32);
    }, CancellationToken.None);

    [Fact]
    public Task Control_reports_once_keeps_data_and_history_and_recovers_after_policy_or_content_changes() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { SynchronizeText = false, MaxShapingCharacters = 2048, FontFamily = Font };
        Assert.Throws<ArgumentException>(() => editor.MaxShapingCharacters = 1);
        var errors = 0;
        editor.OperationFailed += (_, e) => { Assert.IsType<ShapingLimitExceededException>(e.Exception); errors++; };
        var window = new Window { Width = 800, Height = 500, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout(); editor.FocusDocument();
            editor.InsertText("abc \u202b" + new string('x', 8000) + "\u202c");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var error = Assert.IsType<ShapingLimitExceededException>(editor.LayoutError);
            Assert.Same(error, editor.LastError); Assert.Equal(1, errors);
            var document = editor.Document; var selection = editor.Session.Selection;
            Assert.True(editor.Session.CanUndo);
            Assert.Equal(document.Text, DocumentFormats.Json.Parse(DocumentFormats.Json.Serialize(document)).Text);
            using (var frame = window.CaptureRenderedFrame()) Assert.NotNull(frame);
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            Assert.Equal(default, surface.CaretRectangle);
            Assert.Equal(0, surface.Layout.CachedLayouts);
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            surface.RaiseEvent(request);
            Assert.Equal(default, Assert.IsAssignableFrom<TextInputMethodClient>(request.Client).CursorRectangle);
            window.KeyPress(Key.Down, RawInputModifiers.None);
            Assert.Same(document, editor.Document); Assert.Equal(selection, editor.Session.Selection); Assert.Equal(1, errors);
            editor.MaxShapingCharacters = 16384;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Null(editor.LayoutError); Assert.Null(editor.LastError); Assert.True(surface.CaretRectangle.Height > 0);
            Assert.Same(document, editor.Document); Assert.True(editor.Session.CanUndo);
            editor.MaxShapingCharacters = 2048;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.NotNull(editor.LayoutError); Assert.Equal(2, errors);
            editor.Undo(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Null(editor.LayoutError); Assert.Equal("", editor.Document.Text);
            editor.Redo(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.NotNull(editor.LayoutError);
            editor.MaxShapingCharacters = 0;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Null(editor.LayoutError); Assert.Same(document, editor.Document);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Offscreen_target_obeys_limit_and_loading_a_new_document_recovers() => fixture.Session.Dispatch(() =>
    {
        var paragraphs = Enumerable.Range(0, 1000).Select(i => new Paragraph($"row {i}")).ToList();
        paragraphs.Add(new Paragraph("abc \u202b" + new string('x', 8000) + "\u202c"));
        var editor = new TextaloniaEditor { MaxShapingCharacters = 2048, Document = new FlowDocument(paragraphs) };
        var window = new Window { Width = 800, Height = 500, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout(); editor.FocusDocument();
            Assert.Null(editor.LayoutError);
            editor.Session.Select(editor.Session.Index.Length, editor.Session.Index.Length);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.NotNull(editor.LayoutError);
            editor.Document = FlowDocument.FromText("recovered");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Null(editor.LayoutError);
            Assert.True(editor.GetVisualDescendants().OfType<DocumentSurface>().Single().CaretRectangle.Height > 0);
        }
        finally { window.Close(); }
    }, CancellationToken.None);
}
