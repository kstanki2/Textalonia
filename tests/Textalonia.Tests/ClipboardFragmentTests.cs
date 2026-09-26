using System.Collections.Immutable;
using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class ClipboardFragmentTests
{
    [Fact]
    public void Partial_sections_retain_decoration_and_clip_boundary_paragraphs()
    {
        var section = new Section { Background = "#123456", Blocks = [new Paragraph("alpha"), new Paragraph("bravo")] };
        var source = new EditorSession(new FlowDocument([section]));
        source.Select(2, 9);
        var fragment = source.CopyFragment();
        var copy = Assert.IsType<Section>(Assert.Single(fragment.Document.Blocks));
        Assert.Equal("#123456", copy.Background);
        Assert.Equal("pha\nbra", fragment.Document.Text);
        Assert.True(fragment.StartsInsideParagraph); Assert.True(fragment.EndsInsideParagraph);
        var parsed = ClipboardInterchange.Parse(ClipboardInterchange.Serialize(fragment));
        Assert.Equal(fragment.Document.Text, parsed.Document.Text);
        Assert.True(parsed.StartsInsideParagraph); Assert.True(parsed.EndsInsideParagraph);
    }

    [Fact]
    public void Whole_paragraph_paste_splits_an_interior_destination_but_partial_text_merges()
    {
        var source = new EditorSession(FlowDocument.FromText("whole")); source.SelectAll();
        var destination = new EditorSession(FlowDocument.FromText("abcd")); destination.Select(2, 2);
        destination.InsertFragment(source.CopyFragment()); Assert.Equal("ab\nwhole\ncd", destination.Document.Text);
        destination.Undo(); destination.Select(2, 2); source.Select(1, 4);
        destination.InsertFragment(source.CopyFragment()); Assert.Equal("abholcd", destination.Document.Text);
        destination.Undo(); destination.Select(4, 4); source.SelectAll();
        destination.InsertFragment(source.CopyFragment()); Assert.Equal("abcdwhole", destination.Document.Text);
    }

    [Fact]
    public void Whole_section_ending_with_empty_paragraph_preserves_exactly_one_trailing_break()
    {
        var source = new EditorSession(new FlowDocument([new Section { Blocks = [new Paragraph("one"), new Paragraph()] }]));
        source.SelectAll();
        var fragment = source.CopyFragment(); Assert.Equal("one\n", fragment.Document.Text);
        Assert.Single(fragment.Document.Blocks);
    }
    [Fact]
    public void Rectangular_copy_expands_merged_edges_and_retains_nested_content_and_backups()
    {
        var nested = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("nested")] });
        var table = Table.Create(2, 3).SetCell(0, 0, new TableCell { Blocks = [nested] })
            .SetCell(0, 1, new TableCell { Blocks = [new Paragraph("original")] }).MergeCells(0, 0, 1, 2);
        var source = new EditorSession(new FlowDocument([table]));
        var fragment = source.CopyCells(table.Id, 0, 1, 1, 1);
        var copied = Assert.IsType<Table>(Assert.Single(fragment.Document.Blocks));
        Assert.Single(copied.Rows); Assert.Equal(2, copied.ColumnCount);
        Assert.Equal(2, copied.Rows[0][0].ColumnSpan);
        Assert.IsType<Table>(copied.Rows[0][0].Blocks[0]);
        Assert.NotEmpty(copied.Rows[0][0].MergeOriginalBlocks);
        Assert.Equal("nested", new FlowDocument(copied.SplitCell(0, 0).Rows[0][0].Blocks).Text);
        fragment.Validate();
    }

    [Fact]
    public void Partial_merged_selection_does_not_copy_unselected_split_backups()
    {
        var table = Table.Create(1, 2).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("alpha")] })
            .SetCell(0, 1, new TableCell { Blocks = [new Paragraph("secret")] }).MergeCells(0, 0, 1, 2);
        var source = new EditorSession(new FlowDocument([table])); source.Select(1, 4);
        var copied = Assert.IsType<Table>(Assert.Single(source.CopySelection().Blocks));
        Assert.Empty(copied.Rows[0][0].MergeOriginalBlocks);
        Assert.Equal("", new FlowDocument(copied.Rows[0][1].Blocks).Text);
        Assert.Equal("lph", new FlowDocument([copied]).Text);
    }

    [Fact]
    public void Repeated_structural_paste_remaps_lists_and_all_ids_and_undo_is_atomic()
    {
        var list = Guid.NewGuid();
        var table = Table.Create(1, 2).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("one")
            { Style = ParagraphStyle.Default with { List = ListKind.Numbered, ListId = list } }] });
        var source = new EditorSession(new FlowDocument([new Section { Blocks = [table.MergeCells(0, 0, 1, 2)] }]));
        source.SelectAll(); var fragment = source.CopyFragment();
        var destination = new EditorSession(FlowDocument.FromText("prefixsuffix")); destination.Select(6, 6);
        destination.InsertFragment(fragment);
        Assert.Equal("prefix\none\n\nsuffix", destination.Document.Text);
        var first = destination.Document;
        destination.InsertFragment(fragment); destination.Document.Validate();
        Assert.Equal(2, destination.Document.Blocks.OfType<Section>().Count());
        var ids = destination.Index.Paragraphs.Where(p => p.Paragraph.Style.ListId is not null).Select(p => p.Paragraph.Style.ListId).ToArray();
        Assert.Equal(2, ids.Distinct().Count()); Assert.DoesNotContain(list, ids);
        destination.Undo(); Assert.Same(first, destination.Document);
        destination.Undo(); Assert.Equal("prefixsuffix", destination.Document.Text);
    }

    [Fact]
    public void Structured_paste_inserts_inside_destination_cell()
    {
        var table = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("ab")] });
        var destination = new EditorSession(new FlowDocument([table])); destination.Select(1, 1);
        destination.InsertDocument(new FlowDocument([new Section { Blocks = [new Paragraph("X")] }]));
        var blocks = Assert.IsType<Table>(destination.Document.Blocks[0]).Rows[0][0].Blocks;
        Assert.Equal(3, blocks.Length); Assert.IsType<Section>(blocks[1]);
        Assert.Equal("a\nX\nb", destination.Document.Text);
    }

    [Fact]
    public void Structured_replacement_retains_destination_table_shell_and_undoes_atomically()
    {
        var table = Table.Create(1, 2).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("left")] })
            .SetCell(0, 1, new TableCell { Blocks = [new Paragraph("right")] });
        var original = new FlowDocument([new Paragraph("before"), table, new Paragraph("after")]);
        var destination = new EditorSession(original);
        var first = destination.Index.Paragraphs[1]; var last = destination.Index.Paragraphs[2];
        destination.Select(first.Start, last.End);
        destination.InsertFragment(new() { Document = new FlowDocument([new Section { Blocks = [new Paragraph("pasted")] }]) });
        var retained = Assert.IsType<Table>(destination.Document.Blocks[1]);
        Assert.Equal(table.Id, retained.Id); Assert.Equal(2, retained.ColumnCount);
        Assert.IsType<Section>(retained.Rows[0][0].Blocks[0]);
        Assert.Equal("", new FlowDocument(retained.Rows[0][1].Blocks).Text);
        destination.Document.Validate(); destination.Undo(); Assert.Same(original, destination.Document);
    }
    [Fact]
    public void Hidden_resource_collisions_are_remapped_and_survive_native_transfer()
    {
        var image = new InlineDescriptor { AltText = "photo", Payload = new ImageInlinePayload("image") };
        var resource = new DocumentResource { MediaType = "image/png", Data = ImmutableArray.Create<byte>(1, 2, 3) };
        var table = Table.Create(1, 2).SetCell(0, 0, new TableCell { Blocks = [new Paragraph([new RichRun(image)])] })
            .MergeCells(0, 0, 1, 2);
        var source = new EditorSession(new FlowDocument([table]) { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", resource) });
        source.SelectAll(); var fragment = ClipboardInterchange.Parse(ClipboardInterchange.Serialize(source.CopyFragment()));
        var destination = new EditorSession();
        destination.InsertInline(image with { Id = Guid.NewGuid() }, resource with { Data = ImmutableArray.Create<byte>(4) });
        destination.InsertFragment(fragment); destination.Document.Validate();
        Assert.Equal(2, destination.Document.Resources.Count);
        var pasted = destination.Document.Blocks.OfType<Table>().Single();
        var hidden = Assert.IsType<Paragraph>(pasted.Rows[0][0].MergeOriginalBlocks[0]).Runs[0].Inline!;
        Assert.NotEqual("image", Assert.IsType<ImageInlinePayload>(hidden.Payload).ResourceId);
    }

    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("{\"fragmentVersion\":1}")]
    [InlineData("{\"fragmentVersion\":1,\"fragmentVersion\":1}")]
    public void Malformed_native_fragments_are_rejected(string payload) => Assert.ThrowsAny<Exception>(() => ClipboardInterchange.Parse(payload));

    [Fact]
    public void Future_native_version_is_rejected_and_older_document_payload_is_accepted()
    {
        Assert.Throws<NotSupportedException>(() => ClipboardInterchange.Parse("{\"fragmentVersion\":99}"));
        var legacy = ClipboardInterchange.Parse(DocumentFormats.Json.Serialize(FlowDocument.FromText("legacy")));
        Assert.Equal("legacy", legacy.Document.Text);
    }

    [Fact]
    public void Windows_html_offsets_count_utf8_bytes_and_are_used_without_markers()
    {
        const string body = "<p>café 日本語 😀</p>";
        var payload = ClipboardInterchange.EncodeWindowsHtml("<html><body>" + body + "</body></html>");
        Assert.Equal(body, ClipboardInterchange.DecodeWindowsHtml(payload));
        var withoutMarkers = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace("StartFragment-->", "OtherFragment-->").Replace("EndFragment-->", "EndReplaced-->"));
        Assert.Equal(body, ClipboardInterchange.DecodeWindowsHtml(withoutMarkers));
        Assert.Throws<FormatException>(() => ClipboardInterchange.DecodeWindowsHtml(Encoding.UTF8.GetBytes("StartFragment:999\r\nEndFragment:1000\r\n<p>x</p>")));
    }
}

public class ClipboardControlTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private sealed class TestClipboard : IEditorClipboard
    {
        internal IAsyncDataTransfer? Data;
        internal Func<Task>? BeforeSet;
        internal Func<Task>? BeforeGet;
        public async Task SetDataAsync(DataTransfer dataTransfer) { if (BeforeSet is { } before) await before(); Data = dataTransfer; }
        public async Task<IAsyncDataTransfer?> TryGetDataAsync() { if (BeforeGet is { } before) await before(); return Data; }
        internal Task SetTextAsync(string text)
        {
            var data = new DataTransfer(); var item = new DataTransferItem(); item.SetText(text); data.Add(item);
            return SetDataAsync(data);
        }
    }

    [Fact]
    public Task Stale_or_failed_cut_keeps_content_and_successful_cut_undoes_once() => fixture.Session.Dispatch(async () =>
    {
        var clipboard = new TestClipboard();
        var editor = new TextaloniaEditor { Text = "original", ClipboardProvider = () => clipboard };
        editor.SelectAll();
        clipboard.BeforeSet = () => { editor.Session.Select(1, 3); return Task.CompletedTask; };
        await editor.CutAsync(); Assert.Equal("original", editor.Text);
        editor.SelectAll(); clipboard.BeforeSet = () => throw new IOException("unavailable");
        await Assert.ThrowsAsync<IOException>(editor.CutAsync); Assert.Equal("original", editor.Text);
        clipboard.BeforeSet = null; await editor.CutAsync(); Assert.Equal("", editor.Text);
        editor.Undo(); Assert.Equal("original", editor.Text);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Copy_reports_html_merge_history_loss_while_native_fragment_retains_it() => fixture.Session.Dispatch(async () =>
    {
        var clipboard = new TestClipboard();
        var table = Table.Create(1, 2).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("one")] })
            .SetCell(0, 1, new TableCell { Blocks = [new Paragraph("two")] }).MergeCells(0, 0, 1, 2);
        var editor = new TextaloniaEditor { Document = new FlowDocument([table]), ClipboardProvider = () => clipboard };
        editor.SelectAll(); await editor.CopyAsync();
        Assert.Contains(editor.LastConversionReport.Diagnostics, d => d.Code == "conversion.merge-history" && d.Fallback.StartsWith("HTML fallback:", StringComparison.Ordinal));
        var native = await clipboard.Data!.TryGetValueAsync(DataFormat.CreateStringApplicationFormat("org.textalonia.fragment"));
        var copied = Assert.IsType<Table>(ClipboardInterchange.Parse(native!).Document.Blocks[0]);
        Assert.NotEmpty(copied.Rows[0][0].MergeOriginalBlocks);
        Assert.Equal("two", new FlowDocument(copied.Rows[0][1].Blocks).Text);
        return true;
    }, CancellationToken.None);
    [Fact]
    public Task Native_rejection_falls_back_to_html_then_text_and_stale_paste_is_ignored() => fixture.Session.Dispatch(async () =>
    {
        var clipboard = new TestClipboard();
        var editor = new TextaloniaEditor { ClipboardProvider = () => clipboard };
        var data = new DataTransfer(); var item = new DataTransferItem();
        item.Set(DataFormat.CreateStringApplicationFormat("org.textalonia.fragment"), "{\"fragmentVersion\":99}");
        if (OperatingSystem.IsWindows()) item.Set(DataFormat.CreateBytesPlatformFormat("HTML Format"), ClipboardInterchange.EncodeWindowsHtml("<p><b>html</b></p>"));
        else item.Set(DataFormat.CreateStringPlatformFormat(OperatingSystem.IsMacOS() ? "public.html" : "text/html"), "<p><b>html</b></p>");
        item.SetText("plain"); data.Add(item); await clipboard.SetDataAsync(data);
        await editor.PasteAsync(); Assert.Equal("html", editor.Text);
        Assert.True(editor.Session.Index.Paragraphs[0].Paragraph.Runs[0].Style.Bold);
        Assert.Contains(editor.LastConversionReport.Diagnostics, d => d.Code == "clipboard.native-rejected");
        editor.Undo(); Assert.Equal("", editor.Text);
        await clipboard.SetTextAsync("later");
        clipboard.BeforeGet = () => { editor.InsertText("intervening"); return Task.CompletedTask; };
        await editor.PasteAsync(); Assert.Equal("intervening", editor.Text);
        clipboard.BeforeGet = null; await clipboard.SetTextAsync("plain"); await editor.PasteAsync();
        Assert.Equal("interveningplain", editor.Text);
        return true;
    }, CancellationToken.None);
}
