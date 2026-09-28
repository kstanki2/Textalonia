using Avalonia.Input;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    private static readonly DataFormat<string> DragTokenFormat = DataFormat.CreateStringApplicationFormat("org.textalonia.drag-token");
    private static readonly DataFormat<string> PortableHtmlFormat = DataFormat.CreateStringPlatformFormat("text/html");
    // Tokens are valid only during an active native operation in this process. External
    // payloads cannot authorize deletion merely by claiming to originate in an editor.
    private static readonly Dictionary<string, ContentDragSnapshot> ActiveContentDrags = new(StringComparer.Ordinal);

    internal DataTransfer CreateContentDragData(ContentDragSnapshot snapshot, string token)
    {
        var item = new DataTransferItem();
        item.Set(DragTokenFormat, token);
        item.SetText(snapshot.Source.Index.ReadPlainText(snapshot.Selection.Start, snapshot.Selection.Length));
        item.Set(FragmentClipboardFormat, ClipboardInterchange.Serialize(snapshot.Fragment));
        item.Set(NativeClipboardFormat, DocumentFormats.Json.Serialize(snapshot.Fragment.Document));
        var html = DocumentFormats.Html.Serialize(snapshot.Fragment.Document);
        item.Set(PortableHtmlFormat, html);
        if (OperatingSystem.IsWindows()) item.Set(WindowsHtmlFormat, ClipboardInterchange.EncodeWindowsHtml(html));
        else item.Set(HtmlClipboardFormat, html);
        var data = new DataTransfer(); data.Add(item); return data;
    }

    internal static string RegisterContentDrag(ContentDragSnapshot snapshot)
    {
        var token = Guid.NewGuid().ToString("N"); ActiveContentDrags.Add(token, snapshot); return token;
    }
    internal static void FinishContentDrag(string token)
    {
        if (ActiveContentDrags.Remove(token, out var snapshot)) snapshot.Completed = true;
    }
    private static ContentDragSnapshot? ResolveContentDrag(IDataTransfer data) =>
        data.TryGetValue(DragTokenFormat) is { } token && ActiveContentDrags.TryGetValue(token, out var snapshot) ? snapshot : null;

    internal DragDropEffects GetContentDropEffect(IDataTransfer data, int offset, int revision, KeyModifiers modifiers, DragDropEffects allowed)
    {
        var source = ResolveContentDrag(data);
        if (!Session.CanDropContent(source, offset, revision)) return DragDropEffects.None;
        if (source is null && !data.Formats.Any(format => format.Equals(FragmentClipboardFormat) || format.Equals(NativeClipboardFormat) ||
            format.Equals(WindowsHtmlFormat) || format.Equals(HtmlClipboardFormat) || format.Equals(PortableHtmlFormat) || format.Equals(DataFormat.Text)))
            return DragDropEffects.None;
        var copy = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta) ||
            OperatingSystem.IsMacOS() && modifiers.HasFlag(KeyModifiers.Alt);
        // Same-editor operations move by default; cross-editor operations copy unless Shift
        // explicitly requests a move. Outside-process transfers are always copies.
        var move = source is not null && !source.Source.IsReadOnly && !copy &&
            (ReferenceEquals(source.Source, Session) || modifiers.HasFlag(KeyModifiers.Shift));
        var desired = move ? DragDropEffects.Move : DragDropEffects.Copy;
        return allowed.HasFlag(desired) ? desired : allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    internal DragDropEffects DropContent(IDataTransfer data, int offset, int revision, KeyModifiers modifiers, DragDropEffects allowed)
    {
        var effect = GetContentDropEffect(data, offset, revision, modifiers, allowed);
        if (effect == DragDropEffects.None) return effect;
        using var diagnostics = ConversionDiagnostics.Begin();
        var source = ResolveContentDrag(data);
        var fragment = source?.Fragment ?? ReadDroppedFragment(data, offset);
        if (fragment is null) return DragDropEffects.None;
        var result = Session.DropContent(fragment, offset, revision, source, effect == DragDropEffects.Move);
        PublishConversion(diagnostics.ToReport());
        return result switch { ContentDropResult.Move => DragDropEffects.Move, ContentDropResult.Copy => DragDropEffects.Copy, _ => DragDropEffects.None };
    }

    private DocumentFragment? ReadDroppedFragment(IDataTransfer data, int offset)
    {
        foreach (var format in new[] { FragmentClipboardFormat, NativeClipboardFormat })
        {
            if (data.TryGetValue(format) is not { } native) continue;
            try { return ClipboardInterchange.Parse(native); }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
            { ConversionDiagnostics.Report("drop.native-rejected", "Invalid or unsupported native drop payload", "Try HTML, then plain text."); }
        }
        // Accept the operating system's native HTML flavor before portable text/html.
        if (data.TryGetValue(WindowsHtmlFormat) is { } bytes)
        {
            try { return new() { Document = DocumentFormats.Html.Parse(ClipboardInterchange.DecodeWindowsHtml(bytes)) }; }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
            { ConversionDiagnostics.Report("drop.html-rejected", "Invalid or unsupported native HTML drop payload", "Try portable HTML, then plain text."); }
        }
        foreach (var format in new[] { HtmlClipboardFormat, PortableHtmlFormat }.Distinct())
        {
            try
            {
                if (data.TryGetValue(format) is { } html)
                    return new() { Document = DocumentFormats.Html.Parse(ClipboardInterchange.ExtractHtmlFragment(html)) };
            }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
            { ConversionDiagnostics.Report("drop.html-rejected", "Invalid or unsupported HTML drop payload", "Try the next HTML flavor, then plain text."); }
        }
        var text = data.TryGetText();
        if (string.IsNullOrEmpty(text)) return null;
        text = FlowDocument.NormalizeNewlines(text).Replace("\0", "");
        if (text.Length == 0) return null;
        var paragraph = Session.Index.At(Math.Clamp(offset, 0, Session.Index.Length));
        var style = new DocumentStyleResolver(Session.Document).ResolveText(paragraph.Paragraph,
            paragraph.Paragraph.StyleAt(Math.Clamp(offset - paragraph.Start, 0, paragraph.Paragraph.Length)));
        return new() { Document = new FlowDocument(text.Split('\n').Select(value => new Paragraph(value, style))),
            StartsInsideParagraph = true, EndsInsideParagraph = true };
    }
}
