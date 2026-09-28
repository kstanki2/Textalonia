using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.PackageSmoke;

internal static class ImageExample
{
    internal static void Verify()
    {
        var session = new EditorSession();
        var original = new DocumentResource { MediaType = "image/x-emf", Data = ImmutableArray.Create<byte>(1, 2, 3) };
        var preview = new DocumentResource { MediaType = "image/png", Data = ImmutableArray.Create<byte>(4, 5, 6) };
        var image = new InlineDescriptor { Width = 100, Height = 50, AltText = "Preview",
            Payload = new ImageInlinePayload("original") { PreviewResourceId = "preview" } };
        session.InsertImage(image, original, preview);
        session.UpdateImage(image.Id, new() { Anchor = ImageAnchorKind.Page, X = 80, Y = 80,
            Crop = new() { Left = .1 }, Rotation = 15 }, width: 200);
        session.SetWatermark(Guid.Empty, new() { Text = "DRAFT" });
        var ole = new InlineDescriptor { Payload = new OleInlinePayload("package", "preview") { FileName = "sample.bin" } };
        var package = new DocumentResource { Data = ImmutableArray.Create<byte>(7, 8, 9) };
        session.InsertOle(ole, package, preview);
        var reopened = DocumentFormats.Xaml.Parse(DocumentFormats.Xaml.Serialize(session.Document));
        if (DocumentFormats.Json.Serialize(reopened) != DocumentFormats.Json.Serialize(session.Document) || session.ExtractOle(ole.Id) != package)
            throw new InvalidOperationException("Packaged picture/watermark/OLE round trip failed.");
        session.RemoveInline(ole.Id);
        if (session.Document.Resources.ContainsKey("package")) throw new InvalidOperationException("Packaged OLE removal failed.");
        session.Undo();
        if (session.ExtractOle(ole.Id) != package) throw new InvalidOperationException("Packaged OLE undo failed.");
    }
}
