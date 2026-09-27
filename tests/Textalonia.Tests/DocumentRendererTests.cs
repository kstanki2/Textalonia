using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Textalonia.Export;
using Textalonia.Layout;
using Textalonia.Model;
using Textalonia.Rendering;
using Xunit;

namespace Textalonia.Tests;

public class DocumentRendererTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);
    private static FlowDocument Document(params Block[] blocks) => new(blocks)
    {
        Sections = [new DocumentSection { PageSettings = new PageSettings
        { Width = 240, Height = 160, Margins = new EdgeInsets(10, 10, 10, 10) } }]
    };

    [Fact]
    public Task Semantic_geometry_and_links_are_page_local_and_ignore_arrangement() => Run(() =>
    {
        var second = new Paragraph([new RichRun("second link", TextStyle.Default with { Hyperlink = "https://example.com/report" })])
            { Style = ParagraphStyle.Default with { PageBreakBefore = true } };
        var document = Document(new Paragraph("first"), second);
        using var engine = new PaginationEngine();
        using var arranged = engine.Paginate(document, options: new PaginationOptions { PagesPerRow = 2, PageGap = 57 });
        using var renderer = new DocumentRenderer(arranged);
        Assert.Equal(2, renderer.PageCount);
        Assert.True(arranged.Pages[1].Bounds.X > 240);
        var text = Assert.Single(renderer.GetTextLines(1));
        Assert.Equal("second link", text.Text);
        Assert.Equal(arranged.Fragments.Single(f => f.PageIndex == 1).Bounds.X - arranged.Pages[1].Bounds.X, text.Bounds.X);
        var link = Assert.Single(renderer.GetLinks(1));
        Assert.Equal("https://example.com/report", link.Target);
        Assert.InRange(link.Bounds.X, 10, 200);
        Assert.InRange(link.Bounds.Right, 10, 240);
        Assert.Empty(renderer.GetLinks(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.GetLinks(2));
        engine.Clear();
        Assert.Equal("second link", Assert.Single(renderer.GetTextLines(1)).Text);
    });

    [Fact]
    public Task Unsupported_inline_preflight_is_explicit_and_failed_construction_does_not_own_snapshot() => Run(() =>
    {
        var descriptor = new InlineDescriptor { Payload = new ControlInlinePayload("button"), AltText = "Approve" };
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(Document(new Paragraph([new RichRun(descriptor)])));
        var error = Assert.Throws<PagedOutputException>(() => new DocumentRenderer(snapshot, ownsSnapshot: true));
        Assert.Contains(error.Diagnostics, d => d.Code == "output.control.unsupported");
        using var tolerant = new DocumentRenderer(snapshot, new() { UnsupportedContent = UnsupportedContentPolicy.Tolerant });
        Assert.Contains(tolerant.Diagnostics, d => d.Code == "output.control.unsupported");
        Assert.NotEmpty(snapshot.Caret(0).ToString());
    });

    [Fact]
    public Task Print_representations_are_captured_once_and_owned_independently_of_snapshot() => Run(() =>
    {
        var provider = new RepresentationProvider();
        var descriptor = new InlineDescriptor { Payload = new ControlInlinePayload("button"), AltText = "Approve" };
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(Document(new Paragraph([new RichRun(descriptor)])));
        var renderer = new DocumentRenderer(snapshot, new() { InlineProvider = provider });
        Assert.Equal(1, provider.Calls);
        Assert.Empty(renderer.Diagnostics);
        using (var drawing = new DrawingGroup().Open()) renderer.DrawPage(drawing, 0);
        Assert.Equal(1, provider.Representation.Draws);
        renderer.Dispose();
        renderer.Dispose();
        Assert.Equal(1, provider.Representation.Disposals);
        Assert.Throws<ObjectDisposedException>(renderer.Validate);
        _ = snapshot.Caret(0);
    });

    [Fact]
    public Task Draft_and_disposed_snapshots_cannot_be_exported() => Run(() =>
    {
        using var engine = new PaginationEngine();
        using var draft = engine.Paginate(Document(new Paragraph("text")), options: new() { Draft = true });
        Assert.Throws<PagedOutputException>(() => new DocumentRenderer(draft, new() { UnsupportedContent = UnsupportedContentPolicy.Tolerant }));
        var snapshot = engine.Paginate(Document(new Paragraph("text")));
        using var owner = new DocumentRenderer(snapshot, ownsSnapshot: true);
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new DocumentRenderer(snapshot));
    });

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 4)]
    public void Invalid_export_ranges_fail_before_output(int first, int last) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRange(first, last).Resolve(3));

    [Fact]
    public Task Output_text_hook_includes_non_ascii_list_markers_and_line_numbers() => Run(() =>
    {
        var paragraph = new Paragraph("body") { Style = new ParagraphStyle
        {
            List = ListKind.Bullet,
            ListDefinition = new ListDefinition { Levels = [new ListLevelDefinition
                { Kind = ListKind.Bullet, Marker = ListMarkerStyle.Bullet, Text = "\u03A9" }] }
        } };
        var document = Document(paragraph);
        document = document with { Sections = [document.Sections[0] with
            { PageSettings = document.Sections[0].PageSettings with { LineNumbering = new() } }] };
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(document);
        using var renderer = new DocumentRenderer(snapshot);
        var text = new TextRecorder();
        using (var context = new DrawingGroup().Open()) renderer.DrawPage(context, 0, text);
        Assert.Contains("body", text.Lines);
        Assert.Contains("\u03A9", text.Lines);
        Assert.Contains("1", text.Lines);
    });

    private sealed class TextRecorder : IPageTextRenderer
    {
        public List<string> Lines { get; } = [];
        public void DrawLine(DrawingContext context, Avalonia.Media.TextFormatting.TextLine line, Point origin) =>
            Lines.Add(string.Concat(line.TextRuns.Select(run => run.Text.ToString())));
    }
    private sealed class RepresentationProvider : IInlinePrintProvider
    {
        public int Calls;
        public Representation Representation { get; } = new();
        public IInlinePrintRepresentation? TryCreateRepresentation(FlowDocument document, InlineDescriptor descriptor)
        { Calls++; return Representation; }
    }
    private sealed class Representation : IInlinePrintRepresentation
    {
        public int Draws, Disposals;
        public void Draw(DrawingContext context, Rect bounds) { Draws++; context.DrawRectangle(Brushes.Red, null, bounds); }
        public void Dispose() => Disposals++;
    }
}
