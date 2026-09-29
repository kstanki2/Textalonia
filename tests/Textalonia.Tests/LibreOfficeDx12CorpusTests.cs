using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class LibreOfficeDx12CorpusTests
{
    [Fact]
    public async Task Libreoffice_generated_docx_survives_import_edit_export_and_reopen()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Interchange", "libreoffice-dx12-source.docx");
        await using var input = File.OpenRead(path);
        var imported = await DocumentFormats.Docx.LoadWithReportAsync(input);
        Assert.Contains("External writer sample", imported.Document.PlainText);
        Assert.Contains("bold caf\u00E9 \u03B1", imported.Document.PlainText);
        Assert.Contains("A1", imported.Document.PlainText);
        Assert.Contains("B2", imported.Document.PlainText);
        Assert.Contains(imported.Document.Blocks, block => block is Table);

        var editor = new EditorSession(imported.Document);
        editor.Select(editor.Index.Length, editor.Index.Length);
        editor.InsertText(" Edited");
        using var output = new MemoryStream();
        await DocumentFormats.Docx.SaveWithReportAsync(editor.Document, output);
        output.Position = 0;
        var reopened = await DocumentFormats.Docx.LoadWithReportAsync(output);
        Assert.Contains("bold caf\u00E9 \u03B1", reopened.Document.PlainText);
        Assert.Contains("Edited", reopened.Document.PlainText);
        Assert.Contains(reopened.Document.Blocks, block => block is Table);
        reopened.Document.Validate();
    }
}
