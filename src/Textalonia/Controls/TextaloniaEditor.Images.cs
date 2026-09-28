using System.Collections.Immutable;
using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    /// <summary>The single image selected or adjacent to the caret in the active story.</summary>
    public InlineDescriptor? CurrentImage => CurrentImageOrOle?.Payload is ImageInlinePayload ? CurrentImageOrOle : null;
    /// <summary>The single embedded object selected or adjacent to the caret in the active story.</summary>
    public InlineDescriptor? CurrentOleObject => CurrentImageOrOle?.Payload is OleInlinePayload ? CurrentImageOrOle : null;
    /// <summary>The single picture or embedded object selected or adjacent to the caret.</summary>
    public InlineDescriptor? CurrentImageOrOle
    {
        get
        {
            var selection = Session.Selection;
            if (selection.Length > 1 || CellSelection is not null) return null;
            InlineDescriptor? preceding = null;
            foreach (var entry in Session.Index.Enumerate(Math.Max(0, selection.Start - 1), Math.Min(Session.Index.Length, selection.End + 1)))
            {
                var offset = entry.Start;
                foreach (var run in entry.Paragraph.Runs)
                {
                    if (run.Inline is { Payload: ImageInlinePayload or OleInlinePayload } descriptor)
                    {
                        if (offset == selection.Start) return descriptor;
                        if (selection.IsEmpty && offset + 1 == selection.Start) preceding = descriptor;
                    }
                    offset += run.Storage.Length;
                }
            }
            return preceding;
        }
    }

    public void UpdateImage(Guid id, ImagePlacement placement, double? width = null, double? height = null) => Session.UpdateImage(id, placement, width, height);
    public void InsertImage(InlineDescriptor descriptor, DocumentResource original, DocumentResource? preview = null) => Session.InsertImage(descriptor, original, preview);
    public void InsertOle(InlineDescriptor descriptor, DocumentResource package, DocumentResource preview) => Session.InsertOle(descriptor, package, preview);
    public DocumentResource? ExtractOle(Guid id) => Session.ExtractOle(id);
    public void RemoveInline(Guid id) => Session.RemoveInline(id);
    public void SetWatermark(Guid sectionId, DocumentWatermark? watermark, DocumentResource? resource = null) => Session.SetWatermark(sectionId, watermark, resource);
    public void RemoveCurrentImageOrOle() { if (CurrentImageOrOle is { } descriptor) RemoveInline(descriptor.Id); }
    public void RemoveWatermark() => SetWatermark(WatermarkSectionId, null);

    private Guid WatermarkSectionId => _headerFooterSection ?? Session.CurrentSection?.Id ?? Guid.Empty;

    /// <summary>Edits placement, crop, rotation and dimensions in one undoable operation.</summary>
    public Task<bool> ShowImagePropertiesDialogAsync()
    {
        if (Session.IsReadOnly || CurrentImageOrOle is not { } descriptor) return Task.FromResult(false);
        var placement = descriptor.Placement ?? new ImagePlacement();
        var crop = placement.Crop;
        var width = descriptor.Width; var height = descriptor.Height;
        var ratio = width / height;
        var dialog = new FormattingDialog(descriptor.Payload is OleInlinePayload ? "Embedded object preview" : "Picture properties");
        dialog.Body.Children.Add(new TextBlock { Text = "Measurements use DIP (96 per inch). Crop values are percentages of the original image.", TextWrapping = TextWrapping.Wrap });
        dialog.EnumField("Anchor", new FormattingValue<ImageAnchorKind>(placement.Anchor), value => placement = placement with { Anchor = value });
        dialog.EnumField("Text wrapping", new FormattingValue<ImageWrapKind>(placement.Wrap), value => placement = placement with { Wrap = value });
        dialog.NumberField("Left (DIP)", new(placement.X), -100000, 100000, value => placement = placement with { X = value });
        dialog.NumberField("Top (DIP)", new(placement.Y), -100000, 100000, value => placement = placement with { Y = value });
        dialog.NumberField("Distance from text (DIP)", new(placement.Distance), 0, 1000, value => placement = placement with { Distance = value });
        var resizing = false;
        NumericUpDown? widthControl = null; NumericUpDown? heightControl = null;
        widthControl = dialog.NumberField("Width (DIP)", new(width), 0.01m, 10000, value =>
        {
            if (resizing) return;
            width = value;
            if (placement.LockAspectRatio && heightControl is not null)
            {
                resizing = true; height = width / ratio; heightControl.Value = (decimal)height; resizing = false;
            }
        });
        heightControl = dialog.NumberField("Height (DIP)", new(height), 0.01m, 10000, value =>
        {
            if (resizing) return;
            height = value;
            if (placement.LockAspectRatio)
            {
                resizing = true; width = height * ratio; widthControl.Value = (decimal)width; resizing = false;
            }
        });
        dialog.Flag("Lock aspect ratio", new(placement.LockAspectRatio), value => { placement = placement with { LockAspectRatio = value }; ratio = width / height; });
        dialog.NumberField("Rotation (degrees)", new(placement.Rotation), -360, 360, value => placement = placement with { Rotation = value });
        dialog.NumberField("Crop left (%)", new(crop.Left * 100), 0, 99.9m, value => crop = crop with { Left = value / 100 });
        dialog.NumberField("Crop top (%)", new(crop.Top * 100), 0, 99.9m, value => crop = crop with { Top = value / 100 });
        dialog.NumberField("Crop right (%)", new(crop.Right * 100), 0, 99.9m, value => crop = crop with { Right = value / 100 });
        dialog.NumberField("Crop bottom (%)", new(crop.Bottom * 100), 0, 99.9m, value => crop = crop with { Bottom = value / 100 });
        var contour = dialog.TextField("Contour points (x,y; x,y; normalized 0 to 1)", new(string.Join("; ", placement.Contour.Select(point => FormattableString.Invariant($"{point.X},{point.Y}")))), _ => { });
        var alt = dialog.TextField("Alternative text", new(descriptor.AltText), _ => { });
        return ShowFormattingDialog(dialog, () => Session.UpdateInline(descriptor.Id, current => current with
        {
            Width = width, Height = height, AltText = alt.Text ?? "",
            Placement = placement with { Crop = crop, Contour = ParseContour(contour.Text) }
        }));
    }

    private static ImmutableArray<ImageContourPoint> ParseContour(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var points = ImmutableArray.CreateBuilder<ImageContourPoint>();
        foreach (var pair in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var coordinates = pair.Split(',', StringSplitOptions.TrimEntries);
            if (coordinates.Length != 2 || !double.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
                throw new FormatException("Enter contour points as x,y pairs between 0 and 1, separated by semicolons.");
            points.Add(new(x, y));
        }
        return points.ToImmutable();
    }

    /// <summary>Opens a bounded image file and inserts its embedded bytes in the active story.</summary>
    public Task<bool> ShowInsertImageDialogAsync() => ShowImageResourcePickerAsync("Insert picture", async owner =>
    {
        var guard = CaptureResourceEdit();
        var picked = await PickImageAsync(owner, "Insert picture");
        if (picked is null) return false;
        guard();
        var id = Guid.NewGuid().ToString("N");
        Session.InsertInline(new() { Payload = new ImageInlinePayload(id), AltText = picked.Value.Name,
            Width = picked.Value.Width, Height = picked.Value.Height }, picked.Value.Resource);
        return true;
    });

    /// <summary>Edits this section's text or existing embedded image watermark.</summary>
    public Task<bool> ShowWatermarkDialogAsync() => ShowWatermarkDialogCore(null);

    /// <summary>Picks a new image and edits its section watermark settings before committing.</summary>
    public Task<bool> ShowImageWatermarkDialogAsync() => ShowImageResourcePickerAsync("Image watermark", async owner =>
    {
        var guard = CaptureResourceEdit();
        var picked = await PickImageAsync(owner, "Choose watermark image");
        if (picked is null) return false;
        guard();
        return await ShowWatermarkDialogCore(picked);
    });

    private Task<bool> ShowWatermarkDialogCore((DocumentResource Resource, string Name, double Width, double Height)? picked)
    {
        if (Session.IsReadOnly) return Task.FromResult(false);
        var sectionId = WatermarkSectionId;
        var section = Session.Document.Sections.FirstOrDefault(value => value.Id == sectionId);
        var watermark = section?.Watermark ?? new DocumentWatermark { Text = "CONFIDENTIAL" };
        var resourceId = picked is null ? watermark.ResourceId : Guid.NewGuid().ToString("N");
        if (picked is { } image) watermark = watermark with { Text = null, ResourceId = resourceId, Width = image.Width, Height = image.Height };
        var imageMode = watermark.ResourceId is not null;
        var dialog = new FormattingDialog("Section watermark");
        dialog.Body.Children.Add(new TextBlock { Text = "This watermark applies to the current section. Remove it without changing other sections.", TextWrapping = TextWrapping.Wrap });
        dialog.Flag("Use image watermark", new(imageMode), value => imageMode = value);
        var text = dialog.TextField("Watermark text", new(watermark.Text), _ => { });
        var imageIds = Session.Document.Resources.Where(pair => pair.Value.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToList();
        if (resourceId is not null && !imageIds.Contains(resourceId)) imageIds.Add(resourceId);
        var images = new ComboBox { ItemsSource = imageIds, SelectedItem = resourceId, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(images, "Watermark image resource");
        dialog.Body.Children.Add(new TextBlock { Text = "Watermark image resource" }); dialog.Body.Children.Add(images);
        dialog.NumberField("Watermark width (DIP)", new(watermark.Width), 1, 10000, value => watermark = watermark with { Width = value });
        dialog.NumberField("Watermark height (DIP)", new(watermark.Height), 1, 10000, value => watermark = watermark with { Height = value });
        dialog.NumberField("Watermark rotation (degrees)", new(watermark.Rotation), -360, 360, value => watermark = watermark with { Rotation = value });
        dialog.NumberField("Watermark opacity (%)", new(watermark.Opacity * 100), 0, 100, value => watermark = watermark with { Opacity = value / 100 });
        dialog.TextField("Watermark font family", new(watermark.FontFamily), value => watermark = watermark with { FontFamily = value ?? "Arial" });
        dialog.NumberField("Watermark font size (DIP)", new(watermark.FontSize), 1, 512, value => watermark = watermark with { FontSize = value });
        dialog.TextField("Watermark color", new(watermark.Color), value => watermark = watermark with { Color = value ?? "#808080" });
        dialog.Button("Remove watermark from section", () => dialog.Commit(() => SetWatermark(sectionId, null)));
        return ShowFormattingDialog(dialog, () =>
        {
            if (imageMode && images.SelectedItem is not string) throw new FormatException("Choose an image resource, or use the Image watermark command to import one.");
            if (!imageMode && string.IsNullOrWhiteSpace(text.Text)) throw new FormatException("Enter watermark text.");
            var selectedId = imageMode ? (string?)images.SelectedItem : null;
            var value = watermark with { Text = imageMode ? null : text.Text, ResourceId = selectedId };
            SetWatermark(sectionId, value, picked is { } newImage && selectedId == resourceId ? newImage.Resource : null);
        });
    }

    /// <summary>Inserts an embedded file and a separately supplied image preview without activating the file.</summary>
    public Task<bool> ShowInsertOleDialogAsync() => ShowImageResourcePickerAsync("Insert embedded object", async owner =>
    {
        var guard = CaptureResourceEdit();
        using var file = (await owner.StorageProvider.OpenFilePickerAsync(new() { Title = "Choose embedded file", AllowMultiple = false })).FirstOrDefault();
        if (file is null) return false;
        var package = await ReadEmbeddedFileAsync(file, "application/octet-stream");
        guard();
        var preview = await PickImageAsync(owner, "Choose the embedded object's preview image");
        if (preview is null) return false;
        guard();
        var descriptor = new InlineDescriptor
        {
            Payload = new OleInlinePayload(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")) { FileName = file.Name },
            AltText = file.Name, Width = preview.Value.Width, Height = preview.Value.Height
        };
        InsertOle(descriptor, package, preview.Value.Resource);
        return true;
    });

    /// <summary>Saves the selected object's original bytes to the chosen destination; never opens the package.</summary>
    public Task<bool> ShowExtractOleDialogAsync() => ShowImageResourcePickerAsync("Extract embedded object", async owner =>
    {
        if (CurrentOleObject is not { Payload: OleInlinePayload ole } descriptor || ExtractOle(descriptor.Id) is not { } resource) return false;
        if (resource.Kind != DocumentResourceKind.Embedded) throw new NotSupportedException("Only embedded package bytes can be extracted.");
        if (!owner.StorageProvider.CanSave) throw new NotSupportedException("A save-file picker is unavailable on this platform.");
        using var file = await owner.StorageProvider.SaveFilePickerAsync(new() { Title = "Extract embedded object", SuggestedFileName = Path.GetFileName(ole.FileName ?? "EmbeddedObject.bin"), ShowOverwritePrompt = true });
        if (file is null) return false;
        await using var output = await file.OpenWriteAsync();
        if (output.CanSeek) { output.Position = 0; output.SetLength(0); }
        await output.WriteAsync(resource.Data.ToArray());
        await output.FlushAsync();
        return true;
    }, editing: false);

    private Action CaptureResourceEdit()
    {
        var revision = Session.Revision; var selection = Session.Selection; var story = ActiveStoryId; var cells = CellSelection;
        return () =>
        {
            if (Session.IsReadOnly) throw new InvalidOperationException("The document is read-only.");
            if (Session.Revision != revision || Session.Selection != selection || ActiveStoryId != story || CellSelection != cells)
                throw new InvalidOperationException("The document or selection changed. Please reopen the command.");
        };
    }

    private async Task<bool> ShowImageResourcePickerAsync(string title, Func<Window, Task<bool>> operation, bool editing = true)
    {
        if (editing && Session.IsReadOnly || TopLevel.GetTopLevel(this) is not Window owner) return false;
        try
        {
            LastError = null;
            if (editing && !owner.StorageProvider.CanOpen) throw new NotSupportedException("An open-file picker is unavailable on this platform.");
            return await operation(owner);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) { ReportError(exception); await ShowOutputErrorAsync(owner, exception, title); return false; }
        finally { FocusDocument(); }
    }

    private async Task<(DocumentResource Resource, string Name, double Width, double Height)?> PickImageAsync(Window owner, string title)
    {
        using var file = (await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp", "*.svg", "*.ico"] }]
        })).FirstOrDefault();
        if (file is null) return null;
        var mediaType = Path.GetExtension(file.Name).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp",
            ".webp" => "image/webp", ".svg" => "image/svg+xml", ".ico" => "image/x-icon", _ => "application/octet-stream"
        };
        var resource = await ReadEmbeddedFileAsync(file, mediaType);
        using var bitmap = BoundedImageDecoder.Decode(resource.Data.AsMemory(), mediaType, InlineImageOptions);
        var scale = Math.Min(1, 480d / Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height));
        return (resource, file.Name, bitmap.PixelSize.Width * scale, bitmap.PixelSize.Height * scale);
    }

    private static async Task<DocumentResource> ReadEmbeddedFileAsync(IStorageFile file, string mediaType)
    {
        await using var input = await file.OpenReadAsync();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, DocumentResource.MaximumEmbeddedBytes - output.Length + 1L)));
            if (read == 0) break;
            if (output.Length + read > DocumentResource.MaximumEmbeddedBytes) throw new InvalidDataException("The selected file exceeds the embedded resource size limit.");
            output.Write(buffer, 0, read);
        }
        return new() { Kind = DocumentResourceKind.Embedded, MediaType = mediaType, Data = output.ToArray().ToImmutableArray() };
    }
}
