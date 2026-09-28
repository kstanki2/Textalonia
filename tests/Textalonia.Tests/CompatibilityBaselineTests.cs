using System.Text.Json;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class CompatibilityBaselineTests
{
    [Fact]
    public void Coordinates_are_UTF16_with_LF_and_soft_breaks_and_reverse_selection()
    {
        var editor = new EditorSession(FlowDocument.FromText("A👩‍💻\r\ne\u0301\u2028Z\r"));
        Assert.Equal("A👩‍💻\ne\u0301\u2028Z\n", editor.Index.Text);
        Assert.Equal(new[] { 0, 7, 12 }, editor.Index.Paragraphs.Select(p => p.Start));
        editor.Select(10, 3);
        Assert.Equal(new TextSelection(10, 1), editor.Selection);
        editor.InsertText("X");
        editor.Undo();
        Assert.Equal(new TextSelection(10, 1), editor.Selection);
    }

    [Fact]
    public void Duplicate_and_empty_IDs_are_rejected_including_hidden_cells()
    {
        var p = new Paragraph("x");
        Assert.Throws<FormatException>(() => new FlowDocument([p, p]).Validate());
        Assert.Throws<FormatException>(() => new FlowDocument([p with { Id = Guid.Empty }]).Validate());
        var table = Table.Create(1, 2).MergeCells(0, 0, 1, 2);
        table = table.SetCell(0, 1, table.Rows[0][1] with { Id = table.Rows[0][0].Id });
        Assert.Throws<FormatException>(() => new FlowDocument([table]).Validate());
    }

    [Fact]
    public void Native_reader_rejects_unknown_members_and_missing_or_new_versions()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "native-basic.json"));
        Assert.Throws<JsonException>(() => DocumentFormats.Json.Parse(json.Replace("\"version\": 4", "\"unknown\": true, \"version\": 4")));
        var missing = Assert.Throws<NotSupportedException>(() => DocumentFormats.Json.Parse(json.Replace("\"version\": 4,", "")));
        Assert.Equal("Document version is missing. Supported version is 10.", missing.Message);
        Assert.Throws<NotSupportedException>(() => DocumentFormats.Json.Parse(json.Replace("\"version\": 4", "\"version\": 99")));
    }

    [Fact]
    public void Host_load_is_allowed_in_readonly_and_resets_history_and_selection()
    {
        var editor = new EditorSession(FlowDocument.FromText("before"));
        editor.InsertText("edit");
        editor.SelectAll();
        editor.IsReadOnly = true;
        editor.Load(FlowDocument.FromText("loaded"));
        Assert.Equal("loaded", editor.Index.Text);
        Assert.Equal(default, editor.Selection);
        editor.IsReadOnly = false;
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("txt")]
    [InlineData("html")]
    [InlineData("rtf")]
    [InlineData("docx")]
    public async Task Cancelled_codecs_leave_caller_streams_open(string extension)
    {
        var format = DocumentFormats.ForPath("test." + extension);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var input = new MemoryStream([1, 2, 3]);
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => format.LoadAsync(input, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => format.SaveAsync(FlowDocument.FromText("data"), output, cancellation.Token));
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
        Assert.Equal(0, output.Length);
    }
}
