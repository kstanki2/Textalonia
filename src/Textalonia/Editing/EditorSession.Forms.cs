using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    /// <summary>The innermost form control containing the current story-local selection.</summary>
    public DocumentContentControl? CurrentContentControl => Document.ContentControls
        .Where(control => control.Start.StoryId == ActiveStoryId && control.Start.Resolve(Document) <= Selection.Start &&
            control.End.Resolve(Document) >= Selection.End && (!control.IsAtomic || !Selection.IsEmpty || control.End.Resolve(Document) > Selection.Start))
        .OrderBy(control => control.End.Resolve(Document) - control.Start.Resolve(Document)).FirstOrDefault();

    /// <summary>Authors a control around the selection, or replaces it with an initial text/atomic value.</summary>
    public DocumentContentControl? InsertContentControl(DocumentContentControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (IsReadOnly) return null;
        if (!control.SupportsInteraction) throw new NotSupportedException("Picture, repeating-section and gallery controls support imported preservation only.");
        if (Document.ContentControls.Any(existing => existing.Id == control.Id)) throw new ArgumentException("The content-control identifier already exists.", nameof(control));
        var source = ActiveDocument;
        var start = Selection.Start; var end = Selection.End;
        if (control.IsAtomic)
        {
            if (control.ValidateValue(control.Value) is { } error) throw new ArgumentException(error, nameof(control));
            control = control with { IsChecked = control.Kind == ContentControlKind.CheckBox && (control.IsChecked || control.Value is "true" or "1") };
            if (control.Kind == ContentControlKind.CheckBox) control = control with { Value = control.IsChecked ? "true" : "false" };
            (source, end) = ReplaceRange(source, Selection, [new Paragraph([new RichRun(FormDescriptor(control), TypingStyle)]) { Style = Index.At(start).Paragraph.Style }]);
        }
        else if (control.Value.Length != 0)
        {
            if (control.ValidateValue(control.Value) is { } error) throw new ArgumentException(error, nameof(control));
            (source, end) = ReplaceRange(source, Selection, FormParagraphs(control.Value, Index.At(start).Paragraph.Style));
        }
        control = control with
        {
            Start = DocumentAnchor.Create(source, Guid.Empty, start, AnchorAffinity.Before),
            End = DocumentAnchor.Create(source, Guid.Empty, end, AnchorAffinity.After),
            Value = control.IsAtomic ? control.Value : new DocumentIndex(source).ReadPlainText(start, end - start)
        };
        if (control.ValidateValue(control.Value) is { } valueError) throw new ArgumentException(valueError, nameof(control));
        source = source with { ContentControls = source.ContentControls.Add(control) };
        var document = DocumentAnchors.WithStory(Document, ActiveStoryId, source);
        document.Validate();
        if (!Commit(document, new(start, end), editedRange: Selection, wholeDocument: true)) return null;
        return Document.ContentControls.First(item => item.Id == control.Id);
    }

    /// <summary>Updates a supported value as one transaction. Invalid, locked or policy-denied changes return false.</summary>
    public bool SetContentControlValue(Guid controlId, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var control = Document.ContentControls.FirstOrDefault(item => item.Id == controlId);
        value = FlowDocument.NormalizeNewlines(value);
        if (IsReadOnly || control is null || control.LockContents || control.ValidateValue(value) is not null) return false;
        var storyId = control.Start.StoryId;
        var start = control.Start.Resolve(Document); var end = control.End.Resolve(Document);
        var projection = Document.GetStoryDocument(storyId);
        var projected = projection.ContentControls.First(item => item.Id == controlId);
        var updated = projected with { Value = value, IsChecked = control.Kind == ContentControlKind.CheckBox ? value is "true" or "1" : control.IsChecked };
        if (control.Kind == ContentControlKind.CheckBox) updated = updated with { Value = updated.IsChecked ? "true" : "false" };
        if (control.IsAtomic)
        {
            projection = projection.RewriteParagraphs(paragraph => [paragraph with { Runs = paragraph.Runs.Select(run =>
                run.Inline?.Payload is FormControlInlinePayload form && form.ControlId == controlId
                    ? run with { Inline = FormDescriptor(updated) with { Id = run.Inline.Id } } : run).ToImmutableArray() }]);
            projection = projection with { ContentControls = projection.ContentControls.Replace(projected, updated) };
        }
        else
        {
            var style = new DocumentIndex(projection).At(start).Paragraph;
            projection = projection with { ContentControls = projection.ContentControls.Remove(projected) };
            (projection, end) = ReplaceRange(projection, new(start, end), FlowDocument.NormalizeNewlines(value).Split('\n')
                .Select(text => new Paragraph(text, style.StyleAt(start - new DocumentIndex(projection).At(start).Start)) { Style = style.Style }).ToImmutableArray());
            updated = updated with { Start = DocumentAnchor.Create(projection, Guid.Empty, start, AnchorAffinity.Before),
                End = DocumentAnchor.Create(projection, Guid.Empty, end, AnchorAffinity.After) };
            projection = projection with { ContentControls = projection.ContentControls.Add(updated) };
        }
        var document = DocumentAnchors.WithStory(Document, storyId, projection);
        var selection = storyId == ActiveStoryId ? new TextSelection(start, end) : Selection;
        return Commit(document, selection, wholeDocument: true, formControlId: controlId);
    }

    /// <summary>Explicitly evaluates a host-supplied binding source and applies its value through normal form policy.</summary>
    public bool ApplyContentControlBinding(Guid controlId, string storeItemId, string xml)
    {
        ArgumentNullException.ThrowIfNull(storeItemId); ArgumentNullException.ThrowIfNull(xml);
        var binding = Document.ContentControls.FirstOrDefault(control => control.Id == controlId)?.Binding;
        return binding is not null && string.Equals(binding.StoreItemId, storeItemId, StringComparison.OrdinalIgnoreCase) &&
            binding.TryReadValue(xml, out var value) && SetContentControlValue(controlId, value);
    }

    public bool ToggleContentControl(Guid controlId)
    {
        var control = Document.ContentControls.FirstOrDefault(item => item.Id == controlId);
        return control?.Kind == ContentControlKind.CheckBox && SetContentControlValue(controlId, control.IsChecked ? "false" : "true");
    }

    /// <summary>Removes the wrapper while retaining its visible value. Control locks are enforced.</summary>
    public bool RemoveContentControl(Guid controlId)
    {
        var control = Document.ContentControls.FirstOrDefault(item => item.Id == controlId);
        if (IsReadOnly || control is null || control.LockControl) return false;
        var storyId = control.Start.StoryId;
        var projection = Document.GetStoryDocument(storyId);
        projection = projection with { ContentControls = projection.ContentControls.Where(item => item.Id != controlId).ToImmutableArray() };
        if (control.IsAtomic)
        {
            var position = control.Start.Resolve(Document);
            var paragraph = new DocumentIndex(projection).At(position).Paragraph;
            (projection, _) = ReplaceRange(projection, new(position, position + 1), [new Paragraph(control.DisplayText, paragraph.StyleAt(control.Start.Offset)) { Style = paragraph.Style }]);
        }
        return Commit(DocumentAnchors.WithStory(Document, storyId, projection), Selection, wholeDocument: true);
    }

    public bool SelectContentControl(Guid controlId)
    {
        var control = Document.ContentControls.FirstOrDefault(item => item.Id == controlId);
        if (control is null) return false;
        SwitchStory(control.Start.StoryId);
        Select(control.Start.Resolve(Document), control.End.Resolve(Document));
        return true;
    }

    /// <summary>Cycles through editable controls, including secondary stories. Returns false when none exist.</summary>
    public bool SelectNextContentControl(bool backwards = false)
    {
        var stories = Document.Stories.Keys.OrderBy(id => id).Prepend(Guid.Empty).Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        var controls = Document.ContentControls.Where(control => control.SupportsInteraction && !control.LockContents)
            .OrderBy(control => stories[control.Start.StoryId]).ThenBy(control => control.Start.Resolve(Document)).ToArray();
        if (controls.Length == 0) return false;
        var current = CurrentContentControl;
        var at = Array.FindIndex(controls, control => control.Id == current?.Id);
        if (at < 0)
        {
            var candidates = controls.Select((control, ordinal) => (control, ordinal)).Where(item => backwards
                ? stories[item.control.Start.StoryId] < stories[ActiveStoryId] || item.control.Start.StoryId == ActiveStoryId && item.control.Start.Resolve(Document) < Selection.Start
                : stories[item.control.Start.StoryId] > stories[ActiveStoryId] || item.control.Start.StoryId == ActiveStoryId && item.control.Start.Resolve(Document) >= Selection.End).ToArray();
            at = candidates.Length == 0 ? backwards ? controls.Length - 1 : 0 : backwards ? candidates[^1].ordinal : candidates[0].ordinal;
        }
        else at = (at + (backwards ? controls.Length - 1 : 1)) % controls.Length;
        return SelectContentControl(controls[at].Id);
    }

    private ImmutableArray<Paragraph> FormParagraphs(string value, ParagraphStyle style) => FlowDocument.NormalizeNewlines(value).Split('\n')
        .Select(text => new Paragraph(text, TypingStyle) { Style = style }).ToImmutableArray();

    internal static InlineDescriptor FormDescriptor(DocumentContentControl control) => new()
    {
        Payload = new FormControlInlinePayload(control.Id), AltText = control.DisplayText,
        Width = control.Kind == ContentControlKind.CheckBox ? 22 : Math.Clamp(Math.Max(control.DisplayText.Length, 4) * 8 + 12, 44, 600), Height = 22
    };
}
