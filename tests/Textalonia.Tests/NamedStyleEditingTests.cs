using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class NamedStyleEditingTests
{
    internal static FlowDocument StyledDocument()
    {
        var style = new ParagraphStyleDefinition { Id = "Body", NextStyle = "Body",
            TextFormatting = new() { FontSize = 24, Bold = true, Foreground = "#336699" },
            Formatting = new() { SpaceAfter = 20 } };
        return new FlowDocument([new Paragraph("first"), new Paragraph("second")])
        {
            Styles = new()
            {
                Paragraphs = ImmutableDictionary<string, ParagraphStyleDefinition>.Empty.Add(style.Id, style),
                Characters = ImmutableDictionary<string, CharacterStyleDefinition>.Empty.Add("Emphasis",
                    new() { Id = "Emphasis", Formatting = new() { Italic = true } })
            }
        };
    }

    [Fact]
    public void Style_edits_update_dependants_preserve_overrides_and_undo_atomically()
    {
        var session = new EditorSession(StyledDocument());
        session.SelectAll(); session.ApplyNamedParagraphStyle("Body");
        Assert.True(session.FormattingState.Bold.Value);
        Assert.Equal(24, session.FormattingState.FontSize.Value);
        session.Select(0, 5); session.ToggleBold();
        Assert.False(session.FormattingState.Bold.Value);
        session.SelectAll();
        Assert.True(session.FormattingState.Bold.IsMixed);
        var definition = session.Document.Styles.Paragraphs["Body"];
        session.SetStyles(session.Document.Styles with { Paragraphs = session.Document.Styles.Paragraphs.SetItem("Body",
            definition with { TextFormatting = definition.TextFormatting with { FontSize = 32 } }) });
        Assert.Equal(32, session.FormattingState.FontSize.Value);
        Assert.True(session.FormattingState.Bold.IsMixed);
        session.Undo(); Assert.Equal(24, session.FormattingState.FontSize.Value);
        session.Redo(); Assert.Equal(32, session.FormattingState.FontSize.Value);
        session.ChangeTextOverrides(value => value with { Bold = default });
        Assert.False(session.FormattingState.Bold.IsMixed); Assert.True(session.FormattingState.Bold.Value);
    }

    [Fact]
    public void Explicit_zero_false_and_cleared_color_survive_native_and_xaml()
    {
        var session = new EditorSession(StyledDocument()); session.SelectAll(); session.ApplyNamedParagraphStyle("Body");
        session.ChangeTextOverrides(value => value with { Bold = false, Foreground = new((string?)null) });
        session.ChangeParagraphOverrides(value => value with { SpaceAfter = 0 });
        foreach (var format in new TextDocumentFormat[] { DocumentFormats.Json, DocumentFormats.Xaml })
        {
            var loaded = format.Parse(format.Serialize(session.Document));
            Assert.Equal(DocumentFormats.Json.Serialize(session.Document), DocumentFormats.Json.Serialize(loaded));
            var p = new DocumentStyleResolver(loaded).ResolveParagraph((Paragraph)loaded.Blocks[0]);
            Assert.False(p.Runs[0].Style.Bold); Assert.Null(p.Runs[0].Style.Foreground); Assert.Equal(0, p.Style.SpaceAfter);
        }
    }

    [Fact]
    public void Character_style_does_not_hide_paragraph_defaults_and_partial_formatting_remains_sparse()
    {
        var session = new EditorSession(StyledDocument()); session.SelectAll(); session.ApplyNamedParagraphStyle("Body");
        session.Select(1, 4); session.ApplyNamedCharacterStyle("Emphasis");
        session.ApplyStyle(value => value with { Underline = true });
        Assert.True(session.FormattingState.Italic.Value); Assert.True(session.FormattingState.Underline.Value);
        Assert.Equal(24, session.FormattingState.FontSize.Value);
        session.Select(0, 1); Assert.False(session.FormattingState.Italic.Value);
        Assert.False(session.FormattingState.Underline.Value);
        session.Select(1, 4); session.ChangeTextOverrides(value => value with { Underline = default });
        Assert.False(session.FormattingState.Underline.Value);
    }

    [Fact]
    public void Typing_and_next_paragraph_style_keep_catalog_and_inheritance()
    {
        var document = StyledDocument();
        document = document with { Styles = document.Styles with { Paragraphs = document.Styles.Paragraphs
            .SetItem("Title", new() { Id = "Title", NextStyle = "Body", TextFormatting = new() { FontSize = 40 } }) } };
        var session = new EditorSession(document); session.SelectAll(); session.ApplyNamedParagraphStyle("Title");
        session.InsertText("title\nbody");
        Assert.Equal("Title", ((Paragraph)session.Document.Blocks[0]).Style.StyleId);
        Assert.Equal("Body", ((Paragraph)session.Document.Blocks[1]).Style.StyleId);
        Assert.Equal(24, new DocumentStyleResolver(session.Document).ResolveParagraph((Paragraph)session.Document.Blocks[1]).Runs[0].Style.FontSize);
        session.SelectAll(); session.InsertText("replacement");
        Assert.True(session.Document.Styles.Paragraphs.ContainsKey("Title")); session.Document.Validate();
    }

    [Fact]
    public void Readonly_and_invalid_catalog_changes_leave_document_and_history_unchanged()
    {
        var session = new EditorSession(StyledDocument());
        var original = session.Document;
        Assert.Throws<FormatException>(() => session.SetStyles(original.Styles with
        { Paragraphs = original.Styles.Paragraphs.SetItem("Body", original.Styles.Paragraphs["Body"] with { BasedOn = "Body" }) }));
        Assert.Same(original, session.Document); Assert.False(session.CanUndo);
        session.IsReadOnly = true; session.ApplyNamedParagraphStyle("Body"); session.SetTheme(new() { Name = "Different" });
        Assert.Same(original, session.Document);
    }

    [Fact]
    public void Legacy_v4_loads_as_explicit_formatting_and_saves_current_schema()
    {
        var loaded = DocumentFormats.Json.Parse("{\"version\":4,\"document\":{\"blocks\":[{\"kind\":\"paragraph\",\"id\":\"00000000-0000-4000-8000-000000000001\",\"runs\":[{\"text\":\"legacy\",\"style\":{\"fontSize\":20,\"bold\":false}}]}]}}");
        Assert.Null(((Paragraph)loaded.Blocks[0]).Runs[0].Style.Overrides);
        Assert.Contains("\"version\": 8", DocumentFormats.Json.Serialize(loaded));
    }

    [Fact]
    public async Task Lossy_export_resolves_appearance_and_strict_reports_inheritance_loss()
    {
        var session = new EditorSession(StyledDocument()); session.SelectAll(); session.ApplyNamedParagraphStyle("Body");
        var html = DocumentFormats.Html.Serialize(session.Document);
        var loaded = DocumentFormats.Html.Parse(html);
        Assert.Equal(24, ((Paragraph)loaded.Blocks[0]).Runs[0].Style.FontSize);
        using var stream = new MemoryStream();
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Html.SaveWithReportAsync(session.Document, stream,
            new() { Mode = ConversionMode.Strict }));
        Assert.Contains(exception.Report.Diagnostics, d => d.Code == "conversion.named-styles"); Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void Removing_a_style_used_only_by_caret_typing_is_rejected_before_history_changes()
    {
        var session = new EditorSession(StyledDocument());
        session.ApplyNamedCharacterStyle("Emphasis");
        var document = session.Document; var revision = session.Revision;
        var retained = session.RetainedHistoryBytes;
        Assert.Throws<FormatException>(() => session.SetStyles(document.Styles with
        { Characters = ImmutableDictionary<string, CharacterStyleDefinition>.Empty }));
        Assert.Same(document, session.Document);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(retained, session.RetainedHistoryBytes);
        Assert.True(session.FormattingState.Italic.Value);
        session.InsertText("x");
        session.Document.Validate();
    }

    [Fact]
    public void Inherited_lists_exit_on_empty_enter_and_can_be_continued()
    {
        var listId = Guid.NewGuid();
        var document = StyledDocument() with
        { Blocks = [new Paragraph() { Style = ParagraphStyle.ForStyle("Body"), DefaultStyle = TextStyle.ForStyle(null) }, new Paragraph("plain")] };
        document = document with { Styles = document.Styles with { Paragraphs = document.Styles.Paragraphs.SetItem("Body",
            document.Styles.Paragraphs["Body"] with { Formatting = new() { List = ListKind.Numbered, ListId = listId } }) } };
        var session = new EditorSession(document);
        session.InsertParagraph();
        Assert.Equal("\nplain", session.Document.Text);
        Assert.Equal(ListKind.None, session.FormattingState.List.Value);
        session.Undo();
        session.Select(session.Index.Length, session.Index.Length);
        session.ContinueList(listId);
        Assert.Equal(ListKind.Numbered, session.FormattingState.List.Value);
        session.Document.Validate();
    }

    [Fact]
    public void New_paragraph_typing_uses_next_style_and_rejects_invalid_partial_formatting_atomically()
    {
        var document = StyledDocument() with
        { Blocks = [new Paragraph("title", TextStyle.ForStyle(null)) { Style = ParagraphStyle.ForStyle("Title") }] };
        document = document with { Styles = document.Styles with { Paragraphs = document.Styles.Paragraphs
            .Add("Title", new() { Id = "Title", NextStyle = "Body", TextFormatting = new() { FontSize = 40 } }) } };
        var session = new EditorSession(document);
        session.Select(5, 5); session.InsertParagraph();
        Assert.Equal(24, session.FormattingState.FontSize.Value);
        session.InsertText("body");
        var paragraph = Assert.IsType<Paragraph>(session.Document.Blocks[1]);
        Assert.Equal(24, new DocumentStyleResolver(session.Document).ResolveParagraph(paragraph).Runs[0].Style.FontSize);
        session.SelectAll();
        var before = session.Document; var revision = session.Revision;
        Assert.Throws<FormatException>(() => session.ApplyStyle(style => style.FontSize == 40 ? style with { FontSize = -1 } : style));
        Assert.Same(before, session.Document);
        Assert.Equal(revision, session.Revision);
    }

    [Fact]
    public void History_accounts_for_per_script_font_names_and_releases_evicted_typing_states()
    {
        var session = new EditorSession { HistoryByteLimit = 2048 };
        session.ApplyStyle(style => style with { EastAsianFontFamily = new string('a', 5000), ComplexScriptFontFamily = new string('b', 5000) });
        session.Execute(document => document);
        session.ApplyStyle(_ => TextStyle.Default);
        Assert.False(session.CanUndo);
        Assert.Equal(0, session.RetainedHistoryBytes);
    }

}

