using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
    public static void Main()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(Bootstrap));
        // Keep disposal on the entry thread, outside the headless dispatcher.
        session.Dispatch(() =>
        {
            var window = new SmokeWindow();
            try
            {
                window.Show(); window.UpdateLayout();
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
                    throw new InvalidOperationException("Packaged schema v2 and nested undo round trip failed.");
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException("Packaged theme did not render.");
                Console.WriteLine("Package consumer passed: compiled XAML, themes, input, formatting, schema v2, nested/merged tables, range/position APIs, document mode, history budget, shaping limits, and rendering.");
            }
            finally { window.Close(); }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
