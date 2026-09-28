using Avalonia;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class NamedStyleLayoutTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static FontFamily Font => new("avares://Avalonia.Fonts.Inter/Assets#Inter");
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static void Build(DocumentLayout layout, FlowDocument document) => layout.Build(document, 500, Font,
        Brushes.Black, Brushes.Gray, new Thickness(0), new Rect(0, 0, 500, 10000));

    [Fact]
    public Task Catalog_update_invalidates_unchanged_paragraphs_and_undo_restores_geometry() => Run(() =>
    {
        var session = new EditorSession(NamedStyleEditingTests.StyledDocument()); session.SelectAll(); session.ApplyNamedParagraphStyle("Body");
        using var layout = new DocumentLayout(); Build(layout, session.Document);
        var before = layout.Caret(0).Height;
        var definition = session.Document.Styles.Paragraphs["Body"];
        session.SetStyles(session.Document.Styles with { Paragraphs = session.Document.Styles.Paragraphs.SetItem("Body",
            definition with { TextFormatting = definition.TextFormatting with { FontSize = 48 } }) });
        Build(layout, session.Document);
        Assert.True(layout.Caret(0).Height > before * 1.5);
        Assert.True(layout.Caret(session.Index.Length).Height > before * 1.5);
        session.Undo(); Build(layout, session.Document); Assert.Equal(before, layout.Caret(0).Height, 5);
    });

    [Fact]
    public Task Contextual_spacing_and_paragraph_decoration_share_geometry() => Run(() =>
    {
        var session = new EditorSession(NamedStyleEditingTests.StyledDocument()); session.SelectAll(); session.ApplyNamedParagraphStyle("Body");
        using var layout = new DocumentLayout(); Build(layout, session.Document); var before = layout.Height;
        session.ApplyParagraphStyle(value => value with { ContextualSpacing = true, Shading = "#FFEEDD",
            Borders = new(new(1, "#FF0000"), new(1, "#FF0000"), new(1, "#FF0000"), new(1, "#FF0000")) });
        Build(layout, session.Document); Assert.Equal(before - 20, layout.Height, 4);
        Assert.Equal(2, layout.Decorations.Count(d => d.Fill is SolidColorBrush b && b.Color == Color.Parse("#FFEEDD")));
    });

    [Fact]
    public Task Applying_another_table_style_recomputes_inherited_cell_padding_and_fill() => Run(() =>
    {
        var table = Table.Create(1, 1) with { StyleId = "narrow" };
        var document = new FlowDocument([table])
        {
            Styles = new() { Tables = System.Collections.Immutable.ImmutableDictionary<string, TableStyleDefinition>.Empty
                .Add("narrow", new() { Id = "narrow", Formatting = new() { Padding = new EdgeInsets(2, 2, 2, 2), Background = "#112233" } })
                .Add("wide", new() { Id = "wide", Formatting = new() { Padding = new EdgeInsets(22, 22, 22, 22), Background = "#778899" } }) }
        };
        using var layout = new DocumentLayout();
        Build(layout, document);
        var before = layout.Caret(0);
        document = document.ReplaceBlock(table.Id, table with { StyleId = "wide" });
        Build(layout, document);
        Assert.Equal(before.X + 20, layout.Caret(0).X, 4);
        Assert.Equal(before.Y + 20, layout.Caret(0).Y, 4);
        Assert.Contains(layout.Decorations, d => d.Fill is SolidColorBrush b && b.Color == Color.Parse("#778899"));
    });

    [Fact]
    public Task Ime_preview_inherits_named_size_and_cancel_does_not_edit_document() => Run(() =>
    {
        var editor = new TextaloniaEditor { Document = NamedStyleEditingTests.StyledDocument(), CompositionComponent = new DefaultCompositionComponent() };
        var window = new Avalonia.Controls.Window { Width = 700, Height = 400, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument();
        try
        {
            editor.Session.SelectAll(); editor.Session.ApplyNamedParagraphStyle("Body"); editor.Session.Select(2, 2);
            var original = editor.Document;
            var component = Assert.IsType<DefaultCompositionComponent>(editor.CompositionComponent);
            component.SetPreedit("test", 2);
            var preview = component.PreviewDocument!;
            var paragraph = new DocumentIndex(preview).At(2).Paragraph;
            var effective = new DocumentStyleResolver(preview).ResolveText(paragraph, paragraph.StyleAt(2));
            Assert.Equal(24, effective.FontSize); Assert.True(effective.Underline);
            component.Cancel(); Assert.Same(original, editor.Document);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Exact_line_spacing_controls_caret_geometry_below_natural_font_height() => Run(() =>
    {
        var paragraph = new Paragraph("one\u2028two") { Style = new() { LineSpacingMode = LineSpacingMode.Exact, LineSpacing = 9 } };
        using var layout = new DocumentLayout(); Build(layout, new([paragraph]));
        Assert.Equal(9, layout.Caret(0).Height, 5);
        Assert.Equal(9, layout.Caret(4).Y - layout.Caret(0).Y, 5);
    });

}

