using Avalonia.Controls;
using Avalonia.Headless;
using Textalonia.Controls;
using Textalonia.Layout;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.PackageSmoke;

internal static class StoryExample
{
    internal static async Task VerifyAsync(TextaloniaEditor editor, Window window)
    {
        editor.Document = new FlowDocument([new Paragraph("Packaged body"), new Paragraph("Second page") { Style = new() { PageBreakBefore = true } }])
        { Sections = [new DocumentSection()] };
        editor.Session.Select(4, 4);
        editor.EditHeader();
        window.KeyTextInput("Packaged header ");
        editor.InsertPageField(PageFieldKind.Page);
        var headerId = editor.ActiveStoryId;
        editor.CloseStory();
        if (editor.ActiveStoryId != Guid.Empty || editor.Session.Selection.Active != 4)
            throw new InvalidOperationException("Packaged story activation did not restore body selection.");
        editor.InsertFootnote("*");
        window.KeyTextInput("Packaged note");
        var noteId = editor.ActiveStoryId;
        var edited = DocumentFormats.Json.Serialize(editor.Document);
        editor.Undo(); editor.Redo();
        if (DocumentFormats.Json.Serialize(editor.Document) != edited)
            throw new InvalidOperationException("Packaged story undo/redo failed.");
        editor.CloseStory(); window.UpdateLayout();
        foreach (var format in new TextDocumentFormat[] { DocumentFormats.Json, DocumentFormats.Xaml })
            if (DocumentFormats.Json.Serialize(format.Parse(format.Serialize(editor.Document))) != edited)
                throw new InvalidOperationException("Packaged native story round trip failed.");
        using var encoded = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(editor.Document, encoded); encoded.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(encoded);
        var header = loaded.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary);
        if (header is null || !loaded.GetStoryDocument(header.Id).PlainText.StartsWith("Packaged header ", StringComparison.Ordinal) ||
            loaded.Notes.Length != 1 || loaded.Notes[0].CustomMark != "*" || loaded.GetStoryDocument(loaded.Notes[0].StoryId).Text != "Packaged note")
            throw new InvalidOperationException("Packaged DOCX story round trip failed.");
        using var paginator = new PaginationEngine();
        using var pages = paginator.Paginate(editor.Document);
        var repeated = pages.StoryRegions.Where(r => r.StoryId == headerId).ToArray();
        var note = pages.StoryRegions.FirstOrDefault(r => r.StoryId == noteId);
        if (pages.Pages.Length != 2 || repeated.Length != 2 || note is null ||
            repeated[0].Context.PageNumber == repeated[1].Context.PageNumber ||
            pages.HitTestStory(note.Bounds.Center).StoryId != noteId)
            throw new InvalidOperationException("Packaged page contexts, note regions or story hit testing failed.");
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Packaged stories did not render.");
    }
}
