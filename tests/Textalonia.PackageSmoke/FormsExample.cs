using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.PackageSmoke;

internal static class FormsExample
{
    internal static void Verify()
    {
        var session = new EditorSession(FlowDocument.FromText("Name: "));
        session.Select(6, 6);
        var name = session.InsertContentControl(new() { Kind = ContentControlKind.PlainText, Value = "Guest", LockControl = true })!;
        if (!session.Protect(new() { Mode = DocumentProtectionMode.FormsOnly }, "demo-password") || !session.SetContentControlValue(name.Id, "Ada"))
            throw new InvalidOperationException("Packaged protected form editing failed.");
        session.Select(0, 4); session.InsertText("blocked");
        if (session.Document.Text != "Name: Ada") throw new InvalidOperationException("Packaged policy enforcement failed.");
        using var output = new MemoryStream();
        DocumentFormats.Docx.SaveAsync(session.Document, output).GetAwaiter().GetResult();
        output.Position = 0;
        var reopened = new EditorSession(DocumentFormats.Docx.LoadAsync(output).GetAwaiter().GetResult());
        if (reopened.Document.ContentControls.Single().Value != "Ada" || reopened.TryUnprotect("wrong") || !reopened.TryUnprotect("demo-password"))
            throw new InvalidOperationException("Packaged DOCX forms/protection round trip failed.");
    }
}
