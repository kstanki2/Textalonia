using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Textalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.PackageSmoke;

public static class Bootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SmokeApp>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class SmokeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // Load the control assembly before resolving the theme in the headless session.
        var themeAssembly = typeof(Textalonia.Controls.TextaloniaEditor).Assembly.GetName().Name;
        Styles.Add(new StyleInclude(new Uri("avares://Textalonia.PackageSmoke/"))
        { Source = new Uri($"avares://{themeAssembly}/Themes/Generic.axaml") });
    }
}

public partial class SmokeWindow : Window
{
    public SmokeWindow() => InitializeComponent();
}

internal static class Program
{
    private static void VerifyInterchange()
    {
        var document = new FlowDocument([new Section { Blocks = [new Paragraph("Structured paste"), Table.Create(1, 1)] }]);
        using var output = new MemoryStream();
        var saved = DocumentFormats.Json.SaveWithReportAsync(document, output, new() { Mode = ConversionMode.Strict }).GetAwaiter().GetResult();
        output.Position = 0;
        var loaded = DocumentFormats.Json.LoadWithReportAsync(output, new() { Mode = ConversionMode.Strict }).GetAwaiter().GetResult();
        if (saved.Report.HasLoss || loaded.Report.HasLoss || DocumentFormats.Json.Serialize(loaded.Document) != DocumentFormats.Json.Serialize(document))
            throw new InvalidOperationException("Packaged strict native conversion failed.");
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(DocumentFormats.Json.Serialize(document))!;
        foreach (var version in new[] { 1, 2, 3, 10 })
        {
            envelope["version"] = version;
            try
            {
                DocumentFormats.Json.Parse(envelope.ToJsonString());
                throw new InvalidOperationException($"Packaged native reader accepted unsupported schema {version}.");
            }
            catch (NotSupportedException) { }
        }
        var source = new Textalonia.Editing.EditorSession(document);
        source.SelectAll();
        var fragment = source.CopyFragment();
        var target = new Textalonia.Editing.EditorSession();
        target.InsertFragment(fragment);
        target.InsertFragment(fragment);
        target.Document.Validate();
        if (target.Document.Blocks[0] is not Section ||
            target.Index.Paragraphs.Count(p => p.Paragraph.Text == "Structured paste") != 2)
            throw new InvalidOperationException("Packaged structural fragment insertion failed.");
        using var plain = new MemoryStream();
        var losses = DocumentFormats.PlainText.SaveWithReportAsync(document, plain).GetAwaiter().GetResult();
        if (!losses.Report.Diagnostics.Any(d => d.Code == "text.section"))
            throw new InvalidOperationException("Packaged conversion reports failed.");
    }
    private static void VerifyIntegrationCodecs()
    {
        const string markdown = "# Integration\n\n**Bold** and `code`.\n\n> Quote\n\n```csharp\npublic class Example {}\n```\n";
        var document = DocumentFormats.Markdown.Parse(markdown);
        if (DocumentFormats.ForPath("sample.MD") != DocumentFormats.Markdown ||
            DocumentFormats.ForPath("sample.txaml") != DocumentFormats.Xaml)
            throw new InvalidOperationException("Packaged format selection failed.");
        var xaml = DocumentFormats.Xaml.Serialize(document);
        if (DocumentFormats.Json.Serialize(document) != DocumentFormats.Json.Serialize(DocumentFormats.Xaml.Parse(xaml)))
            throw new InvalidOperationException("Packaged XAML round trip failed.");
        if (DocumentFormats.Markdown.Parse(DocumentFormats.Markdown.Serialize(document)).PlainText != document.PlainText)
            throw new InvalidOperationException("Packaged Markdown round trip failed.");
        using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<b>literal HTML</b>"));
        var result = DocumentFormats.Markdown.LoadWithReportAsync(source).GetAwaiter().GetResult();
        if (!result.Report.HasLoss) throw new InvalidOperationException("Packaged Markdown diagnostics failed.");
    }

    private sealed class SmokeHighlighter : ICodeHighlighter
    {
        public ValueTask<IReadOnlyList<CodeHighlightToken>> TokenizeAsync(string? language, string code, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<CodeHighlightToken>>([new(0, Math.Min(6, code.Length), "keyword")]);
        public CodeHighlightStyle? GetStyle(string? language, string tokenKind) => new(Foreground: "#2255BB", Bold: true);
    }

    public static void Main()
    {
        VerifyInterchange();
        VerifyIntegrationCodecs();
        var fieldSession = new Textalonia.Editing.EditorSession(FlowDocument.FromText("cached"));
        fieldSession.SelectAll();
        fieldSession.InsertField("IF { MERGEFIELD Count } > 1 \"many\" \"one\"", FlowDocument.FromText("cached"));
        fieldSession.UpdateFields(new() { MergeValues = new Dictionary<string, object?> { ["Count"] = 2 } });
        fieldSession.SelectAll(); fieldSession.AddBookmark("result");
        var fieldReopened = DocumentFormats.Json.Parse(DocumentFormats.Json.Serialize(fieldSession.Document));
        if (fieldReopened.Text != "many" || fieldReopened.Fields.Length != 1 || fieldReopened.Bookmarks.Length != 1)
            throw new InvalidOperationException("Packaged general fields and bookmark round trip failed.");
        using var session = HeadlessUnitTestSession.StartNew(typeof(Bootstrap));
        // Keep disposal on the entry thread, outside the headless dispatcher.
        session.Dispatch<bool>(async () =>
        {
            var window = new SmokeWindow();
            try
            {
                window.Show(); window.UpdateLayout();
                var markdown = window.FindControl<MarkdownViewer>("Markdown")
                    ?? throw new InvalidOperationException("Markdown viewer was not instantiated from consumer XAML.");
                await markdown.WaitForParsingAsync();
                await markdown.UpdateMarkdownAsync("# Package Markdown\n\n```cs\npublic class Package {}\n```\n");
                var canonical = DocumentFormats.Json.Serialize(markdown.Document);
                markdown.CodeHighlighter = new SmokeHighlighter();
                await markdown.WaitForHighlightingAsync();
                markdown.Session.SelectAll();
                if (markdown.ParseError is not null || markdown.SelectedText != markdown.Document.PlainText ||
                    DocumentFormats.Json.Serialize(markdown.Document) != canonical || !markdown.IsReadOnly ||
                    markdown.Accessibility.DocumentRange.GetText() != markdown.Document.Text)
                    throw new InvalidOperationException("Packaged Markdown selection, accessibility or highlighting failed.");
                markdown.AppendMarkdown("\nAppended.");
                await markdown.WaitForParsingAsync();
                if (!markdown.Document.PlainText.EndsWith("Appended.", StringComparison.Ordinal))
                    throw new InvalidOperationException("Packaged Markdown append failed.");
                window.UpdateLayout();
                var editor = window.FindControl<Textalonia.Controls.TextaloniaEditor>("Editor")
                    ?? throw new InvalidOperationException("Control was not instantiated from consumer XAML.");
                editor.FocusDocument();
                editor.Session.SelectAll();
                window.KeyTextInput("Installed from NuGet");
                editor.Session.SelectAll(); editor.Session.ToggleBold();
                if (editor.Text != "Installed from NuGet" ||
                    !new DocumentIndex(editor.Document).Paragraphs[0].Paragraph.Runs[0].Style.Bold)
                    throw new InvalidOperationException("Packaged editor input/formatting failed.");
                var serialized = DocumentFormats.Json.Serialize(editor.Document);
                if (DocumentFormats.Json.Parse(serialized).Text != editor.Text)
                    throw new InvalidOperationException("Packaged serializer failed.");
                var position = editor.Session.CreatePosition(2);
                if (!editor.Session.TryResolvePosition(position, out var offset) || offset != 2 ||
                    editor.Session.Index.ReadText(0, 9) != "Installed" || editor.Session.Index.ParagraphCount != 1)
                    throw new InvalidOperationException("Packaged range/position APIs failed.");
                editor.SynchronizeText = false;
                editor.Session.HistoryByteLimit = 8192;
                var published = editor.Text;
                editor.Session.Select(editor.Session.Index.Length, editor.Session.Index.Length);
                editor.InsertText("!");
                if (editor.Text != published || editor.Document.Text != published + "!" ||
                    editor.Session.TryResolvePosition(position, out _) || editor.Session.RetainedHistoryBytes > 8192)
                    throw new InvalidOperationException("Packaged document mode/history budget failed.");
                editor.SynchronizeText = true;
                if (editor.Text != published + "!") throw new InvalidOperationException("Text synchronization failed.");
                editor.MaxShapingCharacters = 2048;
                editor.Text = "abc \u202b" + new string('x', 8000) + "\u202c";
                window.UpdateLayout();
                if (editor.LayoutError is not { CharacterLimit: 2048 } || editor.LastError is not Textalonia.Controls.ShapingLimitExceededException)
                    throw new InvalidOperationException("Packaged shaping limit did not report oversized content.");
                editor.MaxShapingCharacters = 0;
                window.UpdateLayout();
                if (editor.LayoutError is not null || !editor.Text.Contains('\u202b'))
                    throw new InvalidOperationException("Packaged shaping limit did not recover without changing the document.");
                var inner = Table.Create(1, 1);
                inner = inner.SetCell(0, 0, inner.Rows[0][0] with
                {
                    Blocks = [new Paragraph("Nested content", new() { FontWeight = 600, FontStretch = 5 })
                    { Style = new() { LetterSpacing = 1, LineHeight = 24, FirstLineIndent = 4 } }]
                });
                var outer = Table.Create(2, 2);
                outer = outer.SetCell(0, 0, outer.Rows[0][0] with
                { Blocks = [inner], Padding = new(8, 4, 8, 4), Borders = new(Left: new(2, "#335577")) });
                outer = outer.MergeCells(0, 0, 2, 1).InsertRow(1);
                editor.Document = new FlowDocument([outer]);
                editor.Session.Select(0, 0);
                var current = editor.Session.CurrentCell();
                if (current is null || current.Value.Table.Id == outer.Id || outer.Rows[0][0].RowSpan != 3)
                    throw new InvalidOperationException("Packaged nested/merged table semantics failed.");
                var original = DocumentFormats.Json.Serialize(editor.Document);
                editor.InsertText("Edited "); editor.Undo();
                if (DocumentFormats.Json.Serialize(editor.Document) != original ||
                    DocumentFormats.Json.Serialize(DocumentFormats.Json.Parse(original)) != original)
                    throw new InvalidOperationException("Packaged native v7 and nested undo round trip failed.");
                var resizeOriginal = editor.Document;
                if (!editor.BeginTableResize(outer.Id, Textalonia.Controls.TableResizeAxis.Column, 0, 200))
                    throw new InvalidOperationException("Packaged table resize did not start.");
                editor.PreviewTableResize(240);
                if (!ReferenceEquals(resizeOriginal, editor.Document) || !editor.IsResizingTable)
                    throw new InvalidOperationException("Packaged table preview changed committed content.");
                if (!editor.CommitTableResize()) throw new InvalidOperationException("Packaged table resize did not commit.");
                editor.Undo();
                if (!ReferenceEquals(resizeOriginal, editor.Document)) throw new InvalidOperationException("Packaged table resize undo failed.");
                editor.SelectTableCells(outer.Id, 0, 0, 0, 1);
                if (editor.CellSelection is not { RowCount: 3, ColumnCount: 2 })
                    throw new InvalidOperationException("Packaged rectangular merged-cell selection failed.");
                editor.SetTableCellPadding(new EdgeInsets(12, 12, 12, 12));
                editor.Undo(); editor.ClearTableCellSelection();
                editor.Document = new FlowDocument([new Paragraph("\u05d0\u05d1\u05d2") { Style = new() { RightToLeft = true } }]);
                window.UpdateLayout(); editor.Session.Select(2, 2); editor.FocusDocument();
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
                if (editor.Session.Selection.Active != 1) throw new InvalidOperationException("Packaged visual bidi navigation failed.");
                editor.KeyboardComponent = new Textalonia.Controls.DefaultKeyboardComponent();
                editor.CaretComponent = new Textalonia.Controls.DefaultCaretComponent();
                editor.Document = new FlowDocument();
                var inline = new InlineDescriptor { AltText = "A sample image", Width = 64, Height = 32, Payload = new ImageInlinePayload("missing") };
                editor.InsertInline(inline);
                if (editor.Session.Index.Length != 1 || editor.Document.PlainText != "A sample image" ||
                    editor.Accessibility.DocumentRange.GetText() != "\uFFFC")
                    throw new InvalidOperationException("Packaged inline coordinates or accessibility contract failed.");
                var inlineJson = DocumentFormats.Json.Serialize(editor.Document);
                if (DocumentFormats.Json.Parse(inlineJson).PlainText != "A sample image")
                    throw new InvalidOperationException("Packaged native v7 inline round trip failed.");
                editor.UpdateInline(inline.Id, value => value with { Width = 96 });
                editor.Undo(); editor.Redo();
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException("Packaged theme did not render.");
                await StoryExample.VerifyAsync(editor, window);
                await ExtensionExamples.VerifyAsync(window);
                Console.WriteLine("Package consumer passed: general fields/bookmarks, custom codecs/resources/input/viewer lifecycle, compiled XAML, themes, input, formatting, native v9, DX-06 table/list formatting and repeated headers, nested/merged tables, range/position APIs, document mode, history budget, shaping limits, inline descriptors, input components, accessibility contract, strict conversion reports, structured fragments, visual bidi, table interaction APIs, Markdown/XAML integrations, optional highlighting, editable header/note stories, DOCX stories, page regions, and rendering.");
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
