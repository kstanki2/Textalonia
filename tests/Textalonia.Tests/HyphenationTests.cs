using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using System.IO.Compression;
using System.Xml.Linq;
using System.Text;
using Textalonia.Controls;
using Textalonia.Layout;
using Textalonia.Model;
using Textalonia.Proofing;
using Textalonia.Serialization;
using Textalonia.Pdf.Skia;
using Textalonia.Printing;
using Textalonia.Export;
using Xunit;

namespace Textalonia.Tests;

public class HyphenationTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static readonly FontFamily Font = new("avares://Avalonia.Fonts.Inter/Assets#Inter");
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static DictionaryHyphenationService Dictionary(params string[] terms)
    {
        var service = new DictionaryHyphenationService();
        service.Register(new WordListHyphenationDictionary("en-US", "test-word-list", terms));
        return service;
    }
    private static Paragraph Word(string text, ParagraphStyle? style = null) => new(text, new() { Language = "en-US" })
    { Style = style ?? new() { SpaceAfter = 0 } };
    private static double BreakWidth(string prefix)
    {
        using var line = DocumentLayout.CreateTextLayout(Word(prefix + "-"), 1000, Font, Brushes.Black);
        using var natural = DocumentLayout.CreateTextLayout(Word(prefix + "o"), 1000, Font, Brushes.Black);
        return Math.Max(line.Width, natural.Width) + 1;
    }

    [Fact]
    public Task Dictionary_breaks_are_discretionary_and_keep_source_offsets() => Run(() =>
    {
        var paragraph = Word("development");
        var service = Dictionary("de·vel·op·ment");
        var width = BreakWidth("devel");
        using var layout = DocumentLayout.CreateTextLayout(paragraph, width, Font, Brushes.Black, hyphenation: service);
        Assert.True(layout.TextLines.Count >= 2);
        Assert.Equal(5, layout.TextLines[0].Length);
        Assert.Equal(paragraph.Length, layout.TextLines.Sum(line => line.Length));
        Assert.Equal("development", paragraph.Text);
        var first = layout.TextLines[0];
        Assert.Equal(5, first.GetCharacterHitFromDistance(first.Width + 1).FirstCharacterIndex);
        Assert.True(first.GetDistanceFromCharacterHit(new CharacterHit(5, 0)) > 0);
    });

    [Fact]
    public Task Suppression_caps_and_languages_choose_the_right_dictionary() => Run(() =>
    {
        var service = Dictionary("de·vel·op·ment");
        service.Register(new WordListHyphenationDictionary("de-DE", "test-german", ["Sil·ben·tren·nung"]));
        var width = BreakWidth("devel");
        using var suppressed = DocumentLayout.CreateTextLayout(Word("development", new() { SuppressHyphenation = true }), width,
            Font, Brushes.Black, hyphenation: service);
        using var caps = DocumentLayout.CreateTextLayout(Word("DEVELOPMENT"), width, Font, Brushes.Black, hyphenation: service);
        using var capsAllowed = DocumentLayout.CreateTextLayout(Word("DEVELOPMENT", new() { HyphenateCaps = true }), width,
            Font, Brushes.Black, hyphenation: service);
        Assert.Null(HyphenationProjection.Create(Word("development", new() { SuppressHyphenation = true }), 0,
            "development", service));
        Assert.Null(HyphenationProjection.Create(Word("DEVELOPMENT"), 0, "DEVELOPMENT", service));
        Assert.NotNull(HyphenationProjection.Create(Word("DEVELOPMENT", new() { HyphenateCaps = true }), 0,
            "DEVELOPMENT", service));
        Assert.Equal(5, capsAllowed.TextLines[0].Length);
        var german = new Paragraph([new RichRun("development ", new() { Language = "en-US" }),
            new RichRun("Silbentrennung", new() { Language = "de-DE" })]);
        Assert.NotNull(HyphenationProjection.Create(german, 0, german.Text, service));
        Assert.Equal([2, 5, 7], service.BreakPositions("development", System.Globalization.CultureInfo.GetCultureInfo("en-US")));
        Assert.Empty(service.BreakPositions("development", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")));
        service.Register(new WordListHyphenationDictionary("zh-Hant", "test-script", ["ab·cdef"]));
        Assert.Equal([2], service.BreakPositions("abcdef", System.Globalization.CultureInfo.GetCultureInfo("zh-Hant-TW")));
    });

    [Fact]
    public Task Simple_and_page_geometry_reflow_after_dictionary_change() => Run(() =>
    {
        var paragraph = Word("development");
        var service = new DictionaryHyphenationService();
        var width = BreakWidth("devel");
        var document = new FlowDocument([paragraph])
        { Sections = [new DocumentSection { PageSettings = new PageSettings
            { Width = width + 20, Height = 160, Margins = new EdgeInsets(10, 10, 10, 10) } }] };
        using var simple = new DocumentLayout();
        using var pages = new PaginationEngine { HyphenationService = service };
        simple.Build(document, width, Font, Brushes.Black, Brushes.Gray, new Thickness(0),
            new Rect(0, 0, width, 1000), hyphenation: service);
        using var before = pages.Paginate(document, Font);
        var beforeEnd = before.Fragments[0].TextEnd;
        var beforeSimple = simple.Paragraphs[0].Page.Layout!.TextLines[0].Length;
        service.Register(new WordListHyphenationDictionary("en-US", "test-word-list", ["de·vel·op·ment"]));
        simple.Build(document, width, Font, Brushes.Black, Brushes.Gray, new Thickness(0),
            new Rect(0, 0, width, 1000), hyphenation: service);
        using var after = pages.Paginate(document, Font);
        Assert.NotEqual(beforeEnd, after.Fragments[0].TextEnd);
        Assert.NotEqual(beforeSimple, simple.Paragraphs[0].Page.Layout!.TextLines[0].Length);
        Assert.Equal(5, after.Fragments[0].TextEnd);
        Assert.Equal(5, simple.Paragraphs[0].Page.Layout!.TextLines[0].Length);
        Assert.Equal(paragraph.Length, after.Fragments.Sum(fragment => fragment.Length));
        var fragment = after.Fragments[0];
        using var lease = fragment.Acquire();
        var line = lease.Layout.TextLines[fragment.Line.Index];
        var caret = after.Caret(5);
        Assert.InRange(caret.X, fragment.Origin.X, fragment.Origin.X + line.Width + 1);
        Assert.Equal(5, after.HitTest(new Point(fragment.Origin.X + line.Width - 1, fragment.Origin.Y + line.Height / 2)));
        Assert.NotEmpty(after.SelectionRects(0, 5));
    });

    [Fact]
    public Task Explicit_soft_hyphen_remains_in_storage_and_breaks_without_a_service() => Run(() =>
    {
        var paragraph = Word("de\u00ADvelopment", new() { SuppressHyphenation = true });
        using var layout = DocumentLayout.CreateTextLayout(paragraph, BreakWidth("de"), Font, Brushes.Black);
        Assert.Equal(paragraph.Length, layout.TextLines.Sum(line => line.Length));
        Assert.Equal(3, layout.TextLines[0].Length);
        Assert.Equal("de\u00ADvelopment", paragraph.Text);
    });

    [Fact]
    public Task Automatic_breaks_keep_inline_object_offsets_in_simple_and_page_views() => Run(() =>
    {
        var image = new InlineDescriptor { Payload = new ImageInlinePayload("missing"), Width = 12, Height = 12 };
        var paragraph = new Paragraph([new RichRun("development", new() { Language = "en-US" }), new RichRun(image)]);
        var width = BreakWidth("devel");
        var document = new FlowDocument([paragraph])
        { Sections = [new DocumentSection { PageSettings = new PageSettings
            { Width = width + 20, Height = 180, Margins = new EdgeInsets(10, 10, 10, 10) } }] };
        var service = Dictionary("de·vel·op·ment");
        using var simple = new DocumentLayout();
        simple.Build(document, width, Font, Brushes.Black, Brushes.Gray, new Thickness(0),
            new Rect(0, 0, width, 1000), hyphenation: service);
        Assert.Equal(11, Assert.Single(simple.InlineVisuals()).Position);
        using var engine = new PaginationEngine { HyphenationService = service };
        using var page = engine.Paginate(document, Font);
        Assert.Equal(11, Assert.Single(page.InlineVisuals()).Position);
    });

    [Fact]
    public async Task Hyphenation_paragraph_settings_round_trip_in_native_xaml_and_docx()
    {
        var source = new FlowDocument([Word("development", new() { SuppressHyphenation = true, HyphenateCaps = true })]);
        foreach (var format in new IDocumentFormat[] { DocumentFormats.Json, DocumentFormats.Xaml, DocumentFormats.Docx })
        {
            using var stream = new MemoryStream();
            await format.SaveAsync(source, stream);
            if (format == DocumentFormats.Docx)
            {
                stream.Position = 0;
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
                using var part = zip.GetEntry("word/document.xml")!.Open();
                var xml = XDocument.Load(part);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                Assert.NotNull(xml.Descendants(w + "suppressAutoHyphens").SingleOrDefault());
            }
            stream.Position = 0;
            var loaded = await format.LoadAsync(stream);
            var style = Assert.IsType<Paragraph>(loaded.Blocks[0]).Style;
            Assert.True(style.SuppressHyphenation);
            Assert.True(style.HyphenateCaps);
        }
    }

    [Fact]
    public Task Projected_opportunities_obey_the_shaping_character_limit() => Run(() =>
    {
        var text = string.Concat(Enumerable.Repeat("development ", 170));
        var paragraph = Word(text);
        var service = Dictionary("de·vel·op·ment");
        var projection = HyphenationProjection.Create(paragraph, 0, text, service, text.Length + 8);
        Assert.NotNull(projection);
        Assert.Equal(text.Length + 8, projection.Paragraph.Length);
        Assert.Null(HyphenationProjection.Create(paragraph, 0, text, service, text.Length));
    });

    [Fact]
    public Task Rtl_continuation_reuses_full_paragraph_context_and_storage_offsets() => Run(() =>
    {
        var paragraph = Word("development development", new() { RightToLeft = true });
        var service = Dictionary("de·vel·op·ment");
        using var continuation = ShapingTextLayout.CreateContinuation(paragraph, BreakWidth("devel"), Font,
            Brushes.Black, 12, paragraph.Text[12..], null, service);
        Assert.Equal(5, continuation.TextLines[0].Length);
        Assert.Equal(paragraph.Length - 12, continuation.TextLines.Sum(line => line.Length));
        Assert.Equal(5, continuation.TextLines[0].GetCharacterHitFromDistance(0).FirstCharacterIndex);
    });

    [Fact]
    public Task Captured_page_keeps_its_breaks_after_dictionary_changes_and_glyph_eviction() => Run(() =>
    {
        var service = new DictionaryHyphenationService();
        var width = BreakWidth("devel");
        var paragraph = Word("development");
        var document = new FlowDocument([paragraph])
        { Sections = [new DocumentSection { PageSettings = new PageSettings
            { Width = width + 20, Height = 180, Margins = new EdgeInsets(10, 10, 10, 10) } }] };
        using var engine = new PaginationEngine { HyphenationService = service };
        using var captured = engine.Paginate(document, Font);
        var first = captured.Fragments[0];
        var oldEnd = first.TextEnd;
        first.Line.Window.Owner.Release(first.Line.Window);
        service.Register(new WordListHyphenationDictionary("en-US", "later-revision", ["de·vel·op·ment"]));
        using (var lease = first.Acquire())
            Assert.Equal(oldEnd, lease.Layout.TextLines[first.Line.Index].Length);
        using var updated = engine.Paginate(document, Font);
        Assert.NotEqual(oldEnd, updated.Fragments[0].TextEnd);
        Assert.Equal(5, updated.Fragments[0].TextEnd);
    });

    [Fact]
    public Task Print_and_pdf_capture_hyphenated_editor_pages() => fixture.Session.Dispatch(async () =>
    {
        var width = BreakWidth("devel");
        var paragraph = Word("development");
        var document = new FlowDocument([paragraph])
        { Sections = [new DocumentSection { PageSettings = new PageSettings
            { Width = width + 20, Height = 180, Margins = new EdgeInsets(10, 10, 10, 10) } }] };
        var service = new CapturingPrintService();
        var editor = new TextaloniaEditor
        {
            Document = document, FontFamily = Font, HyphenationService = Dictionary("de·vel·op·ment"),
            PrintService = service, PdfExporter = new PdfExporter()
        };
        using (var renderer = editor.CreateOutputRenderer())
        {
            Assert.Equal(5, renderer.Snapshot.Fragments[0].TextEnd);
            var drawing = new DrawingGroup();
            using (var context = drawing.Open()) renderer.DrawPage(context, 0);
            Assert.NotEmpty(drawing.Children!);
        }
        var printed = await editor.PrintAsync(showDialog: false);
        Assert.Equal(5, service.FirstLineEnd);
        Assert.NotNull(printed);
        using var pdf = new MemoryStream();
        var exported = await editor.ExportPdfAsync(pdf);
        Assert.True(exported.PageCount > 0);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(pdf.ToArray()));
        return true;
    }, CancellationToken.None);

    private sealed class CapturingPrintService : IPrintService
    {
        public int FirstLineEnd { get; private set; }
        public Task<PrintCapabilities> GetCapabilitiesAsync(string? printerName, CancellationToken cancellationToken) =>
            Task.FromResult(new PrintCapabilities());
        public Task<PrintOptions?> ShowDialogAsync(PrintDialogRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<PrintOptions?>(request.InitialOptions);
        public Task PrintAsync(PrintJob job, IProgress<PagedOutputProgress>? progress, CancellationToken cancellationToken)
        {
            FirstLineEnd = job.Snapshot.Fragments[0].TextEnd;
            var drawing = new DrawingGroup();
            using (var context = drawing.Open()) job.DrawPage(context, 0);
            Assert.NotEmpty(drawing.Children!);
            return Task.CompletedTask;
        }
    }
}
