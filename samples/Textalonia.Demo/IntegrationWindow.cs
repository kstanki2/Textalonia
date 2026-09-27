using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.Demo;

/// <summary>A host-owned example of parsing, resource resolution and optional token styling.</summary>
public sealed class IntegrationWindow : Window
{
    private const string Sample = """
        # Markdown integrations

        Edit this source. **Bold**, *emphasis*, `inline code`, and [links](https://example.com) use the shared document viewer.

        - First item
          - Nested item
        - Last item

        > A quoted paragraph.

        ```csharp
        public class Welcome
        {
            public string Message = "Hello, Textalonia";
        }
        ```

        ![Host image](resource:demo.logo)

        <b>Raw HTML is displayed as text.</b>
        """;

    public IntegrationWindow()
    {
        Title = "Markdown and data XAML";
        Width = 1100; Height = 760;
        var source = new TextBox { AcceptsReturn = true, Text = Sample, TextWrapping = TextWrapping.Wrap };
        var viewer = new MarkdownViewer { InlineResourceResolver = new DemoResourceResolver() };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var highlight = new CheckBox { Content = "Highlight C# keywords", IsChecked = true };
        var append = new Button { Content = "Append paragraph" };
        var export = new Button { Content = "Export preview as XAML" };
        var import = new Button { Content = "Import XAML into viewer" };
        var xamlSource = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var xamlViewer = new TextaloniaViewer { InlineResourceResolver = new DemoResourceResolver() };
        var diagnostics = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        viewer.CodeHighlighter = new DemoCodeHighlighter();
        viewer.HyperlinkActivated += (_, e) => status.Text = "Host received link: " + e.Uri;
        xamlViewer.HyperlinkActivated += (_, e) => status.Text = "Host received link: " + e.Uri;
        void ShowReport(ConversionReport report) => diagnostics.Text = report.Diagnostics.IsEmpty
            ? "No conversion losses."
            : string.Join("\n\n", report.Diagnostics.Select(d => $"{d.Code}: {d.UnsupportedFeature}\n{d.Fallback}"));
        async Task UpdateAsync()
        {
            await viewer.UpdateMarkdownAsync(source.Text ?? "");
            status.Text = viewer.ParseError?.Message ?? "Preview updated. Links are handled by this window.";
            ShowReport(viewer.ParseReport);
        }
        source.TextChanged += async (_, _) => await UpdateAsync();
        highlight.IsCheckedChanged += (_, _) => viewer.CodeHighlighter = highlight.IsChecked == true ? new DemoCodeHighlighter() : null;
        append.Click += (_, _) => source.Text += "\n\nAnother streamed paragraph.";
        export.Click += async (_, _) =>
        {
            try
            {
                using var stream = new MemoryStream();
                var result = await DocumentFormats.Xaml.SaveWithReportAsync(viewer.Document, stream);
                xamlSource.Text = Encoding.UTF8.GetString(stream.ToArray());
                ShowReport(result.Report); status.Text = "XAML exported. Open the XAML tab to inspect or edit it.";
            }
            catch (Exception error) { status.Text = error.Message; }
        };
        import.Click += async (_, _) =>
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xamlSource.Text ?? ""));
                var result = await DocumentFormats.Xaml.LoadWithReportAsync(stream);
                xamlViewer.Document = result.Document;
                ShowReport(result.Report); status.Text = "Data-only XAML imported.";
            }
            catch (Exception error) { status.Text = error.Message; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        buttons.Children.Add(highlight); buttons.Children.Add(append); buttons.Children.Add(export); buttons.Children.Add(import);
        static Grid Pair(Control left, Control right)
        {
            var grid = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
            grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid;
        }
        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Markdown", Content = Pair(source, viewer) },
                new TabItem { Header = "Data XAML", Content = Pair(xamlSource, xamlViewer) },
                new TabItem { Header = "Diagnostics", Content = diagnostics }
            }
        };
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new("Auto,*,Auto"), RowSpacing = 12 };
        layout.Children.Add(buttons); Grid.SetRow(tabs, 1); layout.Children.Add(tabs);
        Grid.SetRow(status, 2); layout.Children.Add(status); Content = layout;
        Opened += async (_, _) => await UpdateAsync();
    }
}

/// <summary>Illustrative keyword adapter only; it deliberately makes no full C# grammar claim.</summary>
public sealed class DemoCodeHighlighter : ICodeHighlighter
{
    public ValueTask<IReadOnlyList<CodeHighlightToken>> TokenizeAsync(string? language, string code, CancellationToken cancellationToken)
    {
        var result = new List<CodeHighlightToken>();
        if (language is "csharp" or "cs")
            for (var position = 0; position < code.Length; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!char.IsLetter(code[position]) && code[position] != '_') continue;
                var start = position;
                while (position < code.Length && (char.IsLetterOrDigit(code[position]) || code[position] == '_')) position++;
                if (code[start..position] is "public" or "class" or "string" or "return" or "new")
                    result.Add(new(start, position - start, "keyword"));
            }
        return ValueTask.FromResult<IReadOnlyList<CodeHighlightToken>>(result);
    }

    public CodeHighlightStyle? GetStyle(string? language, string tokenKind) =>
        tokenKind == "keyword" ? new(Foreground: "#8B5CF6", Bold: true) : null;
}

/// <summary>Only one explicit opaque resource is granted access; everything else uses embedded-only defaults.</summary>
public sealed class DemoResourceResolver : IInlineResourceResolver
{
    private static readonly byte[] Logo = Convert.FromBase64String(
        "Qk1GAAAAAAAAADYAAAAoAAAAAgAAAAIAAAABABgAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAA9oI79oI7AAD2gjv2gjsAAA==");

    public ValueTask<Stream?> OpenReadAsync(string resourceId, DocumentResource? resource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return resource?.Location == "resource:demo.logo" || resourceId == "demo.logo"
            ? ValueTask.FromResult<Stream?>(new MemoryStream(Logo, writable: false))
            : EmbeddedInlineResourceResolver.Instance.OpenReadAsync(resourceId, resource, cancellationToken);
    }
}
