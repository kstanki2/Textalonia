using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Textalonia.Model;

public enum ContentControlKind { PlainText, RichText, CheckBox, ComboBox, DropDown, Date, Picture, RepeatingSection, BuildingBlockGallery }

public sealed record ContentControlItem(string DisplayText, string Value);

/// <summary>Retained OOXML binding metadata. XPath is never executed implicitly or allowed to access external data.</summary>
public sealed record ContentControlBinding
{
    public string StoreItemId { get; init; } = "";
    public string XPath { get; init; } = "";
    public string PrefixMappings { get; init; } = "";

    /// <summary>Reads host-supplied XML using a bounded absolute-name XPath subset; never fetches data.</summary>
    public bool TryReadValue(string xml, out string value) => ContentControlBindingReader.TryRead(this, xml, out value);
}

/// <summary>A structured form field. Text controls own anchored content; atomic controls own one form inline.</summary>
public sealed record DocumentContentControl
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ContentControlKind Kind { get; init; }
    public DocumentAnchor Start { get; init; } = new() { Affinity = AnchorAffinity.Before };
    public DocumentAnchor End { get; init; } = new();
    public string Tag { get; init; } = "";
    public string Title { get; init; } = "";
    public string Placeholder { get; init; } = "";
    public bool LockContents { get; init; }
    public bool LockControl { get; init; }
    public string Value { get; init; } = "";
    public bool IsChecked { get; init; }
    public ImmutableArray<ContentControlItem> Items { get; init; } = [];
    public ImmutableDictionary<string, string> Data { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ContentControlBinding? Binding { get; init; }
    public string DateFormat { get; init; } = "yyyy-MM-dd";
    public bool IsLegacyFormField { get; init; }

    [JsonIgnore] public bool IsAtomic => Kind is ContentControlKind.CheckBox or ContentControlKind.ComboBox or ContentControlKind.DropDown or ContentControlKind.Date;
    [JsonIgnore] public bool SupportsInteraction => Kind is ContentControlKind.PlainText or ContentControlKind.RichText || IsAtomic;
    [JsonIgnore] public string DisplayText
    {
        get
        {
            if (Kind == ContentControlKind.CheckBox) return IsChecked ? "\u2612" : "\u2610";
            if (Value.Length == 0) return Placeholder;
            if (Kind is ContentControlKind.ComboBox or ContentControlKind.DropDown) return Items.FirstOrDefault(item => item.Value == Value)?.DisplayText ?? Value;
            if (Kind == ContentControlKind.Date && DateOnly.TryParseExact(Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                try { return date.ToString(DateFormat, CultureInfo.InvariantCulture); } catch (FormatException) { return Value; }
            return Value;
        }
    }

    /// <summary>Returns a validation error, or null. Empty values are permitted; date values use ISO yyyy-MM-dd.</summary>
    public string? ValidateValue(string value)
    {
        if (value is null || value.Length > 1_048_576 || value.Contains('\0')) return "The value is too long or contains a null character.";
        if (IsAtomic && value.Length > 16384) return "Atomic values cannot exceed 16384 characters.";
        if (!SupportsInteraction) return "This imported content-control kind supports preservation only.";
        if (Kind == ContentControlKind.PlainText && value.IndexOfAny(['\r', '\n', '\u2028', '\u2029']) >= 0) return "Plain-text controls accept a single paragraph.";
        if (Kind == ContentControlKind.CheckBox && value is not ("" or "true" or "false" or "1" or "0")) return "Checkbox values must be true or false.";
        if (Kind == ContentControlKind.DropDown && value.Length != 0 && !Items.Any(item => item.Value == value)) return "The value must match a dropdown item.";
        if (Kind == ContentControlKind.Date && value.Length != 0 && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return "Dates must use yyyy-MM-dd.";
        return null;
    }
}

internal static class ContentControlValidation
{
    internal static void Validate(FlowDocument document)
    {
        if (document.ContentControls.IsDefault || document.ContentControls.Length > 10000) throw new FormatException("Invalid content controls.");
        if (document.ContentControls.IsEmpty) return;
        var controls = new Dictionary<Guid, DocumentContentControl>();
        foreach (var control in document.ContentControls)
        {
            if (control is null || control.Id == Guid.Empty || !controls.TryAdd(control.Id, control) || !Enum.IsDefined(control.Kind) ||
                control.Start is null || control.End is null || control.Start.StoryId != control.End.StoryId ||
                control.Start.Resolve(document) > control.End.Resolve(document) || control.Items.IsDefault || control.Items.Length > 10000 ||
                control.Tag is null || control.Title is null || control.Placeholder is null || control.Value is null ||
                control.Tag.Length > 16384 || control.Title.Length > 16384 || control.Placeholder.Length > 16384 || control.Value.Length > 1048576 ||
                control.DateFormat is null || control.DateFormat.Length > 256 || control.Data is null || control.Data.Count > 128 ||
                control.Data.Any(item => !InlineDescriptor.ValidKey(item.Key) || item.Value is null || item.Value.Length > 16384))
                throw new FormatException("Invalid content-control metadata or range.");
            if (control.Kind is ContentControlKind.PlainText or ContentControlKind.RichText && control.Value != document.GetStoryIndex(control.Start.StoryId)
                .ReadPlainText(control.Start.Resolve(document), control.End.Resolve(document) - control.Start.Resolve(document)))
                throw new FormatException("Text-control values must match their anchored content.");
            if (control.Items.Any(item => item is null || item.DisplayText is null || item.Value is null || item.DisplayText.Length > 16384 || item.Value.Length > 16384))
                throw new FormatException("Invalid content-control list items.");
            if (control.SupportsInteraction && control.ValidateValue(control.Value) is { } valueError) throw new FormatException(valueError);
            if (control.Kind == ContentControlKind.PlainText && !HasPlainTextContent(document, control))
                throw new FormatException("Plain-text controls cannot contain inline objects.");
            if (control.Binding is { } binding && (binding.StoreItemId is null || binding.StoreItemId.Length > 256 || binding.XPath is null ||
                binding.XPath.Length > 4096 || binding.PrefixMappings is null || binding.PrefixMappings.Length > 16384))
                throw new FormatException("Invalid content-control binding metadata.");
        }
        var references = new HashSet<Guid>();
        foreach (var storyId in document.Stories.Keys.Prepend(Guid.Empty))
        {
            var index = document.GetStoryIndex(storyId);
            foreach (var entry in index.Paragraphs)
            {
                var position = entry.Start;
                foreach (var run in entry.Paragraph.Runs)
                {
                    if (run.Inline?.Payload is FormControlInlinePayload form && (!references.Add(form.ControlId) ||
                        !controls.TryGetValue(form.ControlId, out var control) || !control.IsAtomic || control.Start.StoryId != storyId ||
                        control.Start.Resolve(document) != position || control.End.Resolve(document) != position + 1))
                        throw new FormatException("An atomic form inline must belong to exactly one matching content control.");
                    position += run.Storage.Length;
                }
            }
        }
        if (controls.Values.Any(control => control.IsAtomic && !references.Contains(control.Id)))
            throw new FormatException("An atomic content control requires its form inline.");
        foreach (var story in controls.Values.GroupBy(control => control.Start.StoryId))
        {
            var ends = new Stack<int>();
            foreach (var control in story.OrderBy(control => control.Start.Resolve(document)).ThenByDescending(control => control.End.Resolve(document)))
            {
                var start = control.Start.Resolve(document); var end = control.End.Resolve(document);
                while (ends.Count > 0 && start >= ends.Peek()) ends.Pop();
                if (ends.Count > 0 && end > ends.Peek()) throw new FormatException("Content controls cannot partially overlap.");
                ends.Push(end);
                if (ends.Count > 32) throw new FormatException("Content-control nesting exceeds 32 levels.");
            }
        }
    }

    internal static bool HasPlainTextContent(FlowDocument document, DocumentContentControl control)
    {
        var start = control.Start.Resolve(document); var end = control.End.Resolve(document);
        foreach (var entry in document.GetStoryIndex(control.Start.StoryId).Enumerate(start, end))
        {
            var position = entry.Start;
            foreach (var run in entry.Paragraph.Runs)
            {
                if (run.Inline is not null && position < end && position + run.Storage.Length > start) return false;
                position += run.Storage.Length;
            }
        }
        return true;
    }

    internal static FlowDocument ReconcileAtomicRanges(FlowDocument document)
    {
        if (!document.ContentControls.Any(control => control.IsAtomic)) return document;
        var positions = new Dictionary<Guid, (Guid StoryId, int Position)>();
        foreach (var storyId in document.Stories.Keys.Prepend(Guid.Empty))
            foreach (var entry in document.GetStoryIndex(storyId).Paragraphs)
            {
                var position = entry.Start;
                foreach (var run in entry.Paragraph.Runs)
                {
                    if (run.Inline?.Payload is FormControlInlinePayload form) positions.TryAdd(form.ControlId, (storyId, position));
                    position += run.Storage.Length;
                }
            }
        return document with { ContentControls = document.ContentControls.Where(control => !control.IsAtomic || positions.ContainsKey(control.Id))
            .Select(control => !control.IsAtomic ? control : control with
            {
                Start = DocumentAnchor.Create(document, positions[control.Id].StoryId, positions[control.Id].Position, AnchorAffinity.Before),
                End = DocumentAnchor.Create(document, positions[control.Id].StoryId, positions[control.Id].Position + 1, AnchorAffinity.After)
            }).ToImmutableArray() };
    }

    internal static FlowDocument SynchronizeValues(FlowDocument document) => document.ContentControls.IsEmpty ? document : document with
    {
        ContentControls = document.ContentControls.Select(control => control.Kind is ContentControlKind.PlainText or ContentControlKind.RichText
            ? control with { Value = document.GetStoryIndex(control.Start.StoryId).ReadPlainText(control.Start.Resolve(document), control.End.Resolve(document) - control.Start.Resolve(document)) }
            : control).ToImmutableArray()
    };
}
