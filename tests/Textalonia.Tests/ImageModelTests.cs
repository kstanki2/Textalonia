using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class ImageModelTests
{
    private static DocumentResource Bytes(byte seed = 1, int count = 32) => new()
    { Data = Enumerable.Repeat(seed, count).ToImmutableArray(), MediaType = "application/octet-stream" };
    private static InlineDescriptor Image() => new()
    { Payload = new ImageInlinePayload("original") { PreviewResourceId = "preview" }, Width = 120, Height = 60, AltText = "Image" };
    private static InlineDescriptor Ole() => new()
    { Payload = new OleInlinePayload("package", "preview") { FileName = "data.bin", ProgramId = "Package" }, AltText = "Attachment" };
    private static InlineDescriptor At(EditorSession session, int offset = 0)
    {
        var entry = session.Index.At(offset); var local = offset - entry.Start;
        foreach (var run in entry.Paragraph.Runs)
        {
            if (local < run.Text.Length) return run.Inline!;
            local -= run.Text.Length;
        }
        throw new InvalidOperationException("Missing inline.");
    }

    [Fact]
    public void Geometry_resize_crop_and_rotation_are_atomic_undoable_and_read_only()
    {
        var session = new EditorSession(); var image = Image();
        session.InsertImage(image, Bytes(), Bytes(2));
        var placement = new ImagePlacement { Anchor = ImageAnchorKind.Page, X = 12, Y = 24, Rotation = 30,
            Wrap = ImageWrapKind.Contour, Contour = [new(0, 0), new(1, 0), new(.5, 1)], Crop = new() { Left = .1, Bottom = .2 } };
        session.UpdateImage(image.Id, placement, width: 240);
        Assert.Equal(240, At(session).Width); Assert.Equal(120, At(session).Height); Assert.Equal(placement, At(session).Placement);
        session.Undo(); Assert.Equal(image, At(session)); session.Redo(); Assert.Equal(placement, At(session).Placement);
        var saved = session.Document; session.IsReadOnly = true;
        session.UpdateImage(image.Id, new(), 500); session.RemoveInline(image.Id); session.InsertOle(Ole(), Bytes(), Bytes());
        session.SetWatermark(Guid.Empty, new() { Text = "Draft" });
        Assert.Same(saved, session.Document);
    }

    [Fact]
    public void Invalid_geometry_and_resource_collisions_leave_document_and_history_unchanged()
    {
        var session = new EditorSession(); var image = Image(); session.InsertImage(image, Bytes(), Bytes(2));
        var saved = session.Document;
        foreach (var placement in new[] { new ImagePlacement { X = double.NaN }, new() { Rotation = double.PositiveInfinity },
            new() { Crop = new() { Left = .5, Right = .5 } }, new() { Contour = [new(2, 0), new(1, 1), new(0, 1)] } })
            Assert.Throws<FormatException>(() => session.UpdateImage(image.Id, placement));
        Assert.Throws<ArgumentException>(() => session.InsertOle(Ole(), Bytes(), Bytes(3)));
        Assert.Same(saved, session.Document);
        session.Undo(); Assert.Empty(session.Document.Text);
    }

    [Fact]
    public void Ole_and_original_image_resources_survive_copy_paste_collision_and_deletion()
    {
        var source = new EditorSession(); var image = Image(); var ole = Ole();
        source.InsertImage(image, Bytes(1), Bytes(2)); source.InsertOle(ole, Bytes(3, 8192), Bytes(2)); source.SelectAll();
        var fragment = ClipboardInterchange.Parse(ClipboardInterchange.Serialize(source.CopyFragment()));
        var destination = new EditorSession(); destination.InsertOle(Ole(), Bytes(4), Bytes(5));
        destination.InsertFragment(fragment); destination.Document.Validate();
        var pastedImage = Assert.IsType<ImageInlinePayload>(At(destination, 1).Payload);
        var pastedOle = Assert.IsType<OleInlinePayload>(At(destination, 2).Payload);
        Assert.NotEqual("package", pastedOle.ResourceId); Assert.NotEqual("preview", pastedOle.PreviewResourceId);
        Assert.Equal(pastedImage.PreviewResourceId, pastedOle.PreviewResourceId);
        Assert.Equal(8192, destination.ExtractOle(At(destination, 2).Id)!.Data.Length);
        destination.RemoveInline(At(destination, 2).Id);
        Assert.DoesNotContain(pastedOle.ResourceId, destination.Document.Resources.Keys);
        Assert.Contains(pastedOle.PreviewResourceId, destination.Document.Resources.Keys);
        destination.Undo(); Assert.Equal(8192, destination.ExtractOle(At(destination, 2).Id)!.Data.Length);
        destination.IsReadOnly = true; Assert.Equal(8192, destination.ExtractOle(At(destination, 2).Id)!.Data.Length);
    }

    [Fact]
    public void Watermark_removal_is_section_specific_and_preserves_shared_resources()
    {
        var first = new Paragraph("first"); var second = new Paragraph("second");
        var section1 = new DocumentSection(); var section2 = new DocumentSection { StartParagraphId = second.Id };
        var session = new EditorSession(new FlowDocument([first, second]) { Sections = [section1, section2] });
        var watermark = new DocumentWatermark { ResourceId = "mark" };
        session.SetWatermark(section1.Id, watermark, Bytes()); session.SetWatermark(section2.Id, watermark);
        session.SetWatermark(section1.Id, null);
        Assert.Null(session.Document.Sections[0].Watermark); Assert.Equal(watermark, session.Document.Sections[1].Watermark);
        Assert.Single(session.Document.Resources);
        session.SetWatermark(section2.Id, null); Assert.Empty(session.Document.Resources);
        session.Undo(); Assert.Equal(watermark, session.Document.Sections[1].Watermark); Assert.Single(session.Document.Resources);
        session.ActivateHeaderFooter(section1.Id, false); session.SetWatermark(section1.Id, new() { Text = "Draft" });
        Assert.Equal("Draft", session.Document.Sections[0].Watermark!.Text);
    }

    [Fact]
    public void Secondary_story_resource_edits_keep_body_and_watermark_ownership()
    {
        var session = new EditorSession(); session.SetWatermark(Guid.Empty, new() { ResourceId = "mark" }, Bytes());
        var bodyImage = Image(); session.InsertImage(bodyImage, Bytes(), Bytes(2));
        session.ActivateHeaderFooter(session.Document.Sections[0].Id, false);
        var ole = Ole(); session.InsertOle(ole, Bytes(3), Bytes(2));
        session.RemoveInline(ole.Id); Assert.DoesNotContain("package", session.Document.Resources.Keys);
        Assert.Equal(3, session.Document.Resources.Count);
        session.Undo(); Assert.Equal(4, session.Document.Resources.Count); Assert.Equal(Bytes(3), session.ExtractOle(ole.Id));
        session.ReturnToBody(); Assert.Equal(bodyImage, At(session));
    }

    [Fact]
    public void History_accounts_for_packages_and_hidden_merge_backups()
    {
        var ole = Ole(); var table = Table.Create(1, 2);
        table = table.SetCell(0, 1, table.Rows[0][1] with { Blocks = [new Paragraph([new RichRun(ole)])] }).MergeCells(0, 0, 1, 2);
        var document = new FlowDocument([table]) { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("package", Bytes(1, 8192)).Add("preview", Bytes(2)) };
        Assert.Equal(2, document.PruneUnusedResources().Resources.Count);
        var session = new EditorSession(document); session.SelectAll(); session.DeleteForward();
        Assert.Empty(session.Document.Resources); Assert.True(session.RetainedHistoryBytes >= 8192);
        session.HistoryByteLimit = 0; Assert.Equal(0, session.RetainedHistoryBytes);
    }
}
