using Avalonia.Input;
using Textalonia.Editing;
using Textalonia.Serialization;

namespace Textalonia.Controls;

/// <summary>The clipboard representation to use for Paste Special.</summary>
public enum PasteSpecialFormat { NativeFragment, Html, PlainText }

public partial class TextaloniaEditor
{
    /// <summary>
    /// Pastes only the requested clipboard representation. Returns false when that representation
    /// is unavailable, invalid, or the selection changes while the clipboard is being read.
    /// </summary>
    public async Task<bool> PasteSpecialAsync(PasteSpecialFormat format, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        if (!CanEdit(EditOperation.Clipboard) || Clipboard is not { } clipboard) return false;
        var revision = Session.Revision;
        var selection = Session.Selection;
        var story = Session.ActiveStoryId;
        var cells = CellSelection;
        cancellationToken.ThrowIfCancellationRequested();
        using var diagnostics = ConversionDiagnostics.Begin();
        using var data = await clipboard.TryGetDataAsync();
        if (data is null) return false;

        DocumentFragment? fragment = null;
        string? text = null;
        switch (format)
        {
            case PasteSpecialFormat.NativeFragment:
                foreach (var nativeFormat in new[] { FragmentClipboardFormat, NativeClipboardFormat })
                {
                    var native = await data.TryGetValueAsync(nativeFormat);
                    if (native is null) continue;
                    try { fragment = ClipboardInterchange.Parse(native); break; }
                    catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or NotSupportedException)
                    {
                        ConversionDiagnostics.Report("clipboard.native-rejected", "Invalid or unsupported native clipboard payload",
                            "The selected native representation could not be pasted.");
                    }
                }
                break;
            case PasteSpecialFormat.Html:
                try
                {
                    var html = OperatingSystem.IsWindows()
                        ? (await data.TryGetValueAsync(WindowsHtmlFormat) is { } bytes ? ClipboardInterchange.DecodeWindowsHtml(bytes) : null)
                        : await data.TryGetValueAsync(HtmlClipboardFormat);
                    if (html is not null)
                        fragment = new() { Document = DocumentFormats.Html.Parse(ClipboardInterchange.ExtractHtmlFragment(html)) };
                }
                catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or NotSupportedException)
                {
                    ConversionDiagnostics.Report("clipboard.html-rejected", "Invalid or unsupported HTML clipboard payload",
                        "The selected HTML representation could not be pasted.");
                }
                break;
            case PasteSpecialFormat.PlainText:
                text = await data.TryGetTextAsync();
                break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!CanEdit(EditOperation.Clipboard) || revision != Session.Revision || Session.Selection != selection ||
            Session.ActiveStoryId != story || CellSelection != cells)
            return false;
        if (fragment is not null) Session.InsertFragment(fragment);
        else if (text is not null) Session.InsertText(text);
        else
        {
            PublishConversion(diagnostics.ToReport());
            return false;
        }
        PublishConversion(diagnostics.ToReport());
        return Session.Revision != revision;
    }
}
