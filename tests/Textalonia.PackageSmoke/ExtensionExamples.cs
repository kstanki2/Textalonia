using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.PackageSmoke;

// These are executable adoption examples. They compile against the package only.
internal static class ExtensionExamples
{
    internal static async Task VerifyAsync(SmokeWindow window)
    {
        IDocumentFormat format = new HostTextFormat();
        var editor = window.FindControl<TextaloniaEditor>("Editor")!;
        TableListExample.Verify();
        await MailMergeExample.VerifyAsync(editor);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("Host codec"));
        await editor.LoadAsync(source, format);
        using var saved = new MemoryStream();
        await editor.SaveAsync(saved, format);
        Require(source.CanRead && saved.CanWrite && Encoding.UTF8.GetString(saved.ToArray()) == "Host codec", "Custom codec/stream ownership");

        var keyboard = new HostKeyboard();
        editor.KeyboardComponent = keyboard;
        editor.FocusDocument();
        editor.Session.SelectAll();
        window.KeyPress(Key.F8, RawInputModifiers.None, PhysicalKey.None, null);
        Require(editor.Text == "Host key" && keyboard.AttachCount == 1, "Custom keyboard");
        editor.KeyboardComponent = new DefaultKeyboardComponent();
        Require(keyboard.DetachCount == 1, "Keyboard replacement detach");

        var resolver = new HostResources();
        using (var cache = new InlineImageCache(resolver))
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cache.Changed += (_, _) => ready.TrySetResult();
            cache.Request("host-pixel", null);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(cache.Request("host-pixel", null) is { PixelSize.Width: 1 } && resolver.StreamDisposed, "Host resource decode/disposal");
        }

        var factory = new HostControlFactory();
        var viewer = new TextaloniaViewer { InlineControlFactories = new InlineControlFactoryRegistry() };
        viewer.InlineControlFactories.Register("host-label", factory);
        viewer.Document = new FlowDocument([new Paragraph([new RichRun(new InlineDescriptor
        {
            AltText = "Host label", Payload = new ControlInlinePayload("host-label"), Width = 80, Height = 24
        })])]);
        window.Content = viewer;
        window.UpdateLayout();
        viewer.Session.SelectAll();
        var before = viewer.Document;
        viewer.InsertText("blocked");
        Require(viewer.IsReadOnly && ReferenceEquals(before, viewer.Document) &&
            viewer.SelectedText == "Host label" && factory.Created == 1, "Viewer/read-only/host control");
        window.Content = null;
        Require(factory.Released == factory.Created, "View-owned control release");
    }

    private static void Require(bool value, string scenario)
    {
        if (!value) throw new InvalidOperationException("Package extension example failed: " + scenario);
    }

    private sealed class HostTextFormat : IDocumentFormat
    {
        public string Name => "Host text";
        public IReadOnlyList<string> Extensions => [".host"];
        public async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            return FlowDocument.FromText(await reader.ReadToEndAsync(cancellationToken));
        }
        public async Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default) =>
            await stream.WriteAsync(Encoding.UTF8.GetBytes(document.PlainText), cancellationToken);
    }

    private sealed class HostKeyboard : DocumentInputComponent, IKeyboardComponent
    {
        private readonly DefaultKeyboardComponent _fallback = new();
        public int AttachCount { get; private set; }
        public int DetachCount { get; private set; }
        protected override void OnAttached() { AttachCount++; _fallback.Attach(Context); }
        protected override void OnDetached() { DetachCount++; _fallback.Detach(); }
        public void KeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.F8) { Context.Session.InsertText("Host key"); e.Handled = true; }
            else _fallback.KeyDown(e);
        }
    }

    private sealed class HostResources : IInlineResourceResolver
    {
        public bool StreamDisposed { get; private set; }
        public ValueTask<Stream?> OpenReadAsync(string resourceId, DocumentResource? resource, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stream? stream = resourceId == "host-pixel" ? new OwnedStream(
                Pixel(),
                () => StreamDisposed = true) : null;
            return ValueTask.FromResult(stream);
        }
        private static byte[] Pixel()
        {
            // One 24-bit BMP pixel plus row padding, independent of an external image fixture.
            var bytes = new byte[58];
            bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
            bytes[2] = 58; bytes[10] = 54; bytes[14] = 40;
            bytes[18] = 1; bytes[22] = 1; bytes[26] = 1; bytes[28] = 24; bytes[34] = 4;
            return bytes;
        }
        private sealed class OwnedStream(byte[] bytes, Action disposed) : MemoryStream(bytes, writable: false)
        {
            protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) disposed(); }
        }
    }

    private sealed class HostControlFactory : IInlineControlFactory
    {
        public int Created { get; private set; }
        public int Released { get; private set; }
        public Control Create(InlineDescriptor descriptor) { Created++; return new TextBlock { Text = descriptor.AltText }; }
        public void Release(Control control) { Released++; }
    }
}
