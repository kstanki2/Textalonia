using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    private EditPolicy _editPolicy = new();
    private EditIdentity _identity = new();
    private bool _changingProtection;

    public EditPolicy EditPolicy
    {
        get => _editPolicy;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Commands is null || value.Commands.Any(p => !Enum.IsDefined(p.Key) || !Enum.IsDefined(p.Value)))
                throw new ArgumentException("Invalid edit capabilities.", nameof(value));
            _editPolicy = value; BreakUndoGroup(); OnChanged();
        }
    }

    public EditIdentity Identity
    {
        get => _identity;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Groups is null || value.Groups.Any(g => !InlineDescriptor.ValidKey(g)) || value.User is not null && !InlineDescriptor.ValidKey(value.User))
                throw new ArgumentException("Invalid editing identity.", nameof(value));
            _identity = value; BreakUndoGroup(); OnChanged();
        }
    }

    /// <summary>Returns host visibility and current selection permissions. A transaction is checked again before publication.</summary>
    public CommandCapability GetCapability(EditOperation operation)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        var capability = EditPolicy.GetCapability(operation);
        if (capability != CommandCapability.Enabled) return capability;
        if (IsReadOnly) return CommandCapability.Disabled;
        if (operation == EditOperation.Undo) return CanUndo ? capability : CommandCapability.Disabled;
        if (operation == EditOperation.Redo) return CanRedo ? capability : CommandCapability.Disabled;
        return RangeAllowed(Document, ActiveStoryId, Selection.Start, Selection.End, operation) ? capability : CommandCapability.Disabled;
    }

    /// <summary>Enables restrictions and clears prior history, so undo cannot remove a password requirement.</summary>
    public bool Protect(DocumentProtection protection, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(protection);
        if (IsReadOnly || !Enabled(EditOperation.Metadata) || Document.Protection.Enforce && Document.Protection.Mode != DocumentProtectionMode.None) return false;
        var updated = Document with { Protection = protection with { Password = password is null ? protection.Password : DocumentProtectionPassword.Create(password) } };
        updated.Validate();
        return ChangeProtection(updated);
    }

    /// <summary>Checks the edit-protection verifier. This does not decrypt an Office package.</summary>
    public bool TryUnprotect(string? password = null)
    {
        if (IsReadOnly || !Enabled(EditOperation.Metadata)) return false;
        if (Document.Protection.Password is { } verifier && (password is null || !verifier.Verify(password))) return false;
        return ChangeProtection(Document with { Protection = new() });
    }

    private bool ChangeProtection(FlowDocument updated)
    {
        _changingProtection = true;
        bool changed;
        try { changed = Commit(updated, Selection, wholeDocument: true); }
        finally { _changingProtection = false; }
        if (!changed) return false;
        foreach (var state in _undo) _retained.Remove(state);
        _undo.Clear(); ClearRedo(); BreakUndoGroup(); OnChanged();
        return true;
    }

    private bool AllowsPlainTextFragment(DocumentFragment fragment, int start, int end)
    {
        var plainTarget = Document.ContentControls.Any(c => c.Kind == ContentControlKind.PlainText && c.Start.StoryId == ActiveStoryId &&
            Touches(start, end, c.Start.Resolve(Document), c.End.Resolve(Document)));
        return !plainTarget || fragment.Document.Blocks is [Paragraph plain] && !plain.Runs.Any(r => r.Inline is not null) &&
            plain.Text.IndexOfAny(['\n', '\r', '\u2028', '\u2029']) < 0;
    }

    private bool Enabled(EditOperation operation) => EditPolicy.GetCapability(operation) == CommandCapability.Enabled;

    private bool CanRestore(State state, EditOperation operation) => !IsReadOnly && Enabled(operation) &&
        AllowsTransaction(state.Document, state.TypingStyle, state.StoryId, state.Selection);

    private bool HasRestrictions => Document.Protection.Enforce && Document.Protection.Mode != DocumentProtectionMode.None ||
        !Document.PermissionRanges.IsEmpty || !Document.ContentControls.IsEmpty || !EditPolicy.Commands.IsEmpty;

    private bool Matches(DocumentPermissionRange range) => range.User == "everyone" || range.Group == "everyone" ||
        range.User is null && range.Group is null || range.User is not null && string.Equals(range.User, Identity.User, StringComparison.Ordinal) ||
        range.Group is not null && Identity.Groups.Contains(range.Group);

    private static bool Touches(int start, int end, int rangeStart, int rangeEnd) => start == end
        ? start >= rangeStart && start <= rangeEnd
        : start < rangeEnd && end > rangeStart || rangeStart == rangeEnd && start <= rangeStart && end >= rangeEnd;

    private bool IsProtected(FlowDocument document, Guid storyId, int start, int end)
    {
        var protection = document.Protection;
        if (!protection.Enforce || protection.Mode == DocumentProtectionMode.None) return false;
        if (protection.ProtectedSectionIds.IsEmpty || storyId != Guid.Empty) return true;
        var index = document.GetStoryIndex(storyId);
        for (var i = 0; i < document.Sections.Length; i++)
        {
            var section = document.Sections[i];
            if (!protection.ProtectedSectionIds.Contains(section.Id)) continue;
            var from = i == 0 ? 0 : index.ById(section.StartParagraphId).Start;
            var to = i + 1 == document.Sections.Length ? index.Length : index.ById(document.Sections[i + 1].StartParagraphId).Start;
            if (start == end ? start >= from && (start < to || to == index.Length) : start < to && end > from) return true;
        }
        return false;
    }

    private bool RangeAllowed(FlowDocument document, Guid storyId, int start, int end, EditOperation operation)
    {
        if (!Enabled(operation)) return false;
        foreach (var range in document.PermissionRanges.Where(r => r.IsReadOnly && r.Start.StoryId == storyId))
            if (Touches(start, end, range.Start.Resolve(document), range.End.Resolve(document))) return false;
        var controls = document.ContentControls.Where(c => c.Start.StoryId == storyId).ToArray();
        foreach (var control in controls)
        {
            if (!Touches(start, end, control.Start.Resolve(document), control.End.Resolve(document))) continue;
            if (control.LockContents || control.Kind is ContentControlKind.Picture or ContentControlKind.RepeatingSection or ContentControlKind.BuildingBlockGallery) return false;
            if (control.Kind == ContentControlKind.PlainText && operation is EditOperation.InlineObjects or EditOperation.Tables or EditOperation.Structure) return false;
            if (control.IsAtomic && operation != EditOperation.Forms && (control.LockControl || IsProtected(document, storyId, start, end))) return false;
        }
        if (!IsProtected(document, storyId, start, end)) return true;
        if (document.PermissionRanges.Any(r => !r.IsReadOnly && r.Start.StoryId == storyId && Matches(r) &&
            start >= r.Start.Resolve(document) && end <= r.End.Resolve(document))) return true;
        if (document.Protection.Mode != DocumentProtectionMode.FormsOnly) return false;
        return controls.Any(c => !c.LockContents && start >= c.Start.Resolve(document) && end <= c.End.Resolve(document) &&
            (operation == EditOperation.Forms && c.Kind is not (ContentControlKind.Picture or ContentControlKind.RepeatingSection or ContentControlKind.BuildingBlockGallery) ||
             !c.IsAtomic && (operation == EditOperation.Text || operation == EditOperation.Clipboard || operation == EditOperation.Formatting && c.Kind == ContentControlKind.RichText)));
    }

    // Full snapshot comparison covers Execute, history, drag/drop, fields and hidden table content.
    // Ambiguous structural edits conservatively affect their entire story; mixed edits are atomic.
    private bool AllowsTransaction(FlowDocument after, TextStyle? typingStyle = null, Guid? typingStory = null, TextSelection? typingSelection = null)
    {
        if (IsReadOnly) return false;
        if (_changingProtection) { _changingProtection = false; return true; }
        var before = Document;
        if (before.Protection != after.Protection) return false;
        if (!HasRestrictions && after.PermissionRanges.IsEmpty && after.ContentControls.IsEmpty) return true;
        if (typingStyle is not null && typingStyle != TypingStyle)
        {
            var selection = typingSelection ?? Selection;
            if (!RangeAllowed(before, typingStory ?? ActiveStoryId, selection.Start, selection.End, EditOperation.Formatting)) return false;
        }
        var expected = DocumentAnchors.Reconcile(before, after with { PermissionRanges = before.PermissionRanges, ContentControls = before.ContentControls, Bookmarks = before.Bookmarks, Fields = before.Fields });
        if (!expected.PermissionRanges.SequenceEqual(after.PermissionRanges))
        {
            if (before.Protection.Enforce && before.Protection.Mode != DocumentProtectionMode.None || !Enabled(EditOperation.Metadata)) return false;
            // Existing explicit restrictions cannot be removed or relocated by a user edit.
            if (expected.PermissionRanges.Any(r => !after.PermissionRanges.Contains(r))) return false;
        }
        if (after.ContentControls.Any(c => c.Kind == ContentControlKind.PlainText && !ContentControlValidation.HasPlainTextContent(after, c))) return false;
        if (after.ContentControls.Any(c => c.SupportsInteraction && c.ValidateValue(c.Value) is not null &&
            before.ContentControls.Any(old => old.Id == c.Id && old.Value != c.Value))) return false;
        var configurationChanged = false;
        foreach (var control in before.ContentControls)
        {
            var next = after.ContentControls.FirstOrDefault(c => c.Id == control.Id);
            var mapped = expected.ContentControls.FirstOrDefault(c => c.Id == control.Id);
            if (next is null)
            {
                if (control.LockControl || control.LockContents || IsProtected(before, control.Start.StoryId, control.Start.Resolve(before), control.End.Resolve(before))) return false;
                configurationChanged = true; continue;
            }
            if (mapped is null) return false;
            var config = next with { Start = mapped.Start, End = mapped.End, Value = mapped.Value, IsChecked = mapped.IsChecked };
            if (config != mapped || next.Start != mapped.Start || next.End != mapped.End)
            {
                if (control.LockControl || control.LockContents || IsProtected(before, control.Start.StoryId, control.Start.Resolve(before), control.End.Resolve(before))) return false;
                configurationChanged = true;
            }
            if (control.Value != next.Value || control.IsChecked != next.IsChecked)
                if (!RangeAllowed(before, control.Start.StoryId, control.Start.Resolve(before), control.End.Resolve(before), EditOperation.Forms)) return false;
        }
        if (after.ContentControls.Any(c => !before.ContentControls.Any(old => old.Id == c.Id))) configurationChanged = true;
        if (configurationChanged && (!Enabled(EditOperation.Forms) || before.Protection.Enforce && before.Protection.Mode != DocumentProtectionMode.None)) return false;

        var globalChanged = before.Styles != after.Styles || before.Defaults != after.Defaults || before.Theme != after.Theme ||
            !before.Fonts.SequenceEqual(after.Fonts) || !before.Sections.SequenceEqual(after.Sections) || !before.Notes.SequenceEqual(after.Notes) ||
            before.Stories.Any(p => after.Stories.TryGetValue(p.Key, out var story) && p.Value != (story with { Blocks = p.Value.Blocks })) ||
            before.FootnoteSettings != after.FootnoteSettings || before.EndnoteSettings != after.EndnoteSettings ||
            !before.Properties.OrderBy(p => p.Key).SequenceEqual(after.Properties.OrderBy(p => p.Key)) ||
            !before.Resources.OrderBy(p => p.Key).SequenceEqual(after.Resources.OrderBy(p => p.Key));
        if (globalChanged && (!Enabled(EditOperation.Metadata) || !Enabled(EditOperation.Formatting) ||
            before.Protection.Enforce && before.Protection.Mode != DocumentProtectionMode.None || !before.PermissionRanges.IsEmpty || before.ContentControls.Any(c => c.LockContents))) return false;
        var anchorsChanged = !expected.Bookmarks.SequenceEqual(after.Bookmarks) || !expected.Fields.SequenceEqual(after.Fields);
        if (anchorsChanged && (!Enabled(EditOperation.Metadata) || before.Protection.Enforce && before.Protection.Mode != DocumentProtectionMode.None)) return false;

        var storyIds = before.Stories.Keys.Concat(after.Stories.Keys).Append(Guid.Empty).Distinct();
        foreach (var storyId in storyIds)
        {
            if (storyId != Guid.Empty && (!before.Stories.ContainsKey(storyId) || !after.Stories.ContainsKey(storyId)))
            {
                if (!Enabled(EditOperation.Structure) || !Enabled(EditOperation.Metadata) || !Enabled(EditOperation.Text) || !Enabled(EditOperation.InlineObjects) || !Enabled(EditOperation.Formatting) || before.Protection.Enforce && before.Protection.Mode != DocumentProtectionMode.None ||
                    before.PermissionRanges.Any(r => r.Start.StoryId == storyId) || before.ContentControls.Any(c => c.Start.StoryId == storyId && (c.LockContents || c.LockControl))) return false;
                continue;
            }
            var oldStory = before.GetStoryDocument(storyId); var newStory = after.GetStoryDocument(storyId);
            if (oldStory.Blocks == newStory.Blocks) continue;
            var oldIndex = new DocumentIndex(oldStory); var newIndex = new DocumentIndex(newStory);
            var oldText = oldIndex.Text; var newText = newIndex.Text;
            var start = 0; while (start < Math.Min(oldText.Length, newText.Length) && oldText[start] == newText[start]) start++;
            var end = oldText.Length; var nextEnd = newText.Length;
            while (end > start && nextEnd > start && oldText[end - 1] == newText[nextEnd - 1]) { end--; nextEnd--; }
            if (!StructureEquivalent(oldStory.Blocks, newStory.Blocks))
            {
                var textOnlyStructure = oldText != newText && oldStory.Blocks.All(b => b is Paragraph) && newStory.Blocks.All(b => b is Paragraph);
                if (textOnlyStructure)
                {
                    if (!Enabled(EditOperation.Structure) || !RangeAllowed(before, storyId, start, end, EditOperation.Text)) return false;
                }
                else
                {
                    var operation = ContainsTable(oldStory.Blocks) || ContainsTable(newStory.Blocks) ? EditOperation.Tables : EditOperation.Structure;
                    if (!RangeAllowed(before, storyId, 0, oldIndex.Length, operation)) return false;
                }
            }
            if (oldText != newText && !RangeAllowed(before, storyId, start, end, EditOperation.Text)) return false;
            if (!PreservedFormattingAllowed(before, storyId, oldIndex, newIndex, 0, 0, start) ||
                !PreservedFormattingAllowed(before, storyId, oldIndex, newIndex, end, nextEnd, oldText.Length - end)) return false;
            if (!Enabled(EditOperation.InlineObjects) && !oldIndex.Paragraphs.SelectMany(p => p.Paragraph.Runs).Where(r => r.Inline is not null && r.Inline.Payload is not FormControlInlinePayload).Select(r => r.Inline)
                .SequenceEqual(newIndex.Paragraphs.SelectMany(p => p.Paragraph.Runs).Where(r => r.Inline is not null && r.Inline.Payload is not FormControlInlinePayload).Select(r => r.Inline))) return false;
            var nextParagraphs = newIndex.Paragraphs.ToDictionary(p => p.Paragraph.Id);
            foreach (var oldParagraph in oldIndex.Paragraphs)
            {
                var next = nextParagraphs.GetValueOrDefault(oldParagraph.Paragraph.Id);
                if (next is null || ReferenceEquals(next.Paragraph, oldParagraph.Paragraph)) continue;
                var old = oldParagraph.Paragraph; var current = next.Paragraph;
                if ((old.Style != current.Style || old.DefaultStyle != current.DefaultStyle) &&
                    !RangeAllowed(before, storyId, oldParagraph.Start, oldParagraph.End, EditOperation.Formatting)) return false;
                if (old.Text == current.Text)
                {
                    var boundaries = RunBoundaries(old).Concat(RunBoundaries(current)).Distinct().Order().ToArray();
                    for (var i = 0; i + 1 < boundaries.Length; i++)
                    {
                        var from = boundaries[i]; var to = boundaries[i + 1];
                        var oldRun = RunAt(old, from); var newRun = RunAt(current, from);
                        if (oldRun.Style != newRun.Style && !RangeAllowed(before, storyId, oldParagraph.Start + from, oldParagraph.Start + to, EditOperation.Formatting)) return false;
                        if (oldRun.Inline != newRun.Inline)
                        {
                            var op = oldRun.Inline?.Payload is FormControlInlinePayload && newRun.Inline?.Payload is FormControlInlinePayload ? EditOperation.Forms : EditOperation.InlineObjects;
                            if (!RangeAllowed(before, storyId, oldParagraph.Start + from, oldParagraph.Start + to, op)) return false;
                        }
                    }
                }
                else
                {
                    // Inline insertion/deletion must also obey the inline capability.
                    var oldInlines = old.Runs.Where(r => r.Inline is not null).Select(r => r.Inline!);
                    var newInlines = current.Runs.Where(r => r.Inline is not null).Select(r => r.Inline!);
                    if (!oldInlines.SequenceEqual(newInlines) && !Enabled(EditOperation.InlineObjects)) return false;
                    if (!Enabled(EditOperation.Formatting) && current.Runs.Any(r => !old.Runs.Any(o => o.Style == r.Style) && r.Style != old.DefaultStyle)) return false;
                }
            }
        }
        return true;
    }

    private bool PreservedFormattingAllowed(FlowDocument document, Guid storyId, DocumentIndex oldIndex, DocumentIndex newIndex, int oldStart, int newStart, int length)
    {
        var consumed = 0;
        while (consumed < length)
        {
            var oldPosition = oldStart + consumed; var nextPosition = newStart + consumed;
            var oldEntry = oldIndex.At(oldPosition); var nextEntry = newIndex.At(nextPosition);
            if (oldPosition == oldEntry.End || nextPosition == nextEntry.End) { consumed++; continue; }
            var oldLocal = oldPosition - oldEntry.Start; var nextLocal = nextPosition - nextEntry.Start;
            var oldRun = RunAt(oldEntry.Paragraph, oldLocal); var nextRun = RunAt(nextEntry.Paragraph, nextLocal);
            var count = Math.Min(length - consumed, Math.Min(
                RunBoundaries(oldEntry.Paragraph).First(b => b > oldLocal) - oldLocal,
                RunBoundaries(nextEntry.Paragraph).First(b => b > nextLocal) - nextLocal));
            if ((oldRun.Style != nextRun.Style || oldEntry.Paragraph.Style != nextEntry.Paragraph.Style) &&
                !RangeAllowed(document, storyId, oldPosition, oldPosition + count, EditOperation.Formatting)) return false;
            if (oldRun.Inline != nextRun.Inline)
            {
                var operation = oldRun.Inline?.Payload is FormControlInlinePayload && nextRun.Inline?.Payload is FormControlInlinePayload ? EditOperation.Forms : EditOperation.InlineObjects;
                if (!RangeAllowed(document, storyId, oldPosition, oldPosition + count, operation)) return false;
            }
            consumed += count;
        }
        return true;
    }

    private static IEnumerable<int> RunBoundaries(Paragraph paragraph)
    {
        var offset = 0; yield return offset;
        foreach (var run in paragraph.Runs) { offset += run.Storage.Length; yield return offset; }
    }
    private static RichRun RunAt(Paragraph paragraph, int offset)
    {
        foreach (var run in paragraph.Runs) { if (offset < run.Storage.Length) return run; offset -= run.Storage.Length; }
        throw new InvalidOperationException("Invalid run boundary.");
    }
    private static bool ContainsTable(IEnumerable<Block> blocks) => blocks.Any(b => b is Table || b is Section s && ContainsTable(s.Blocks));
    private static bool StructureEquivalent(ImmutableArray<Block> before, ImmutableArray<Block> after)
    {
        if (before.Length != after.Length) return false;
        for (var i = 0; i < before.Length; i++)
        {
            var a = before[i]; var b = after[i];
            if (a.Id != b.Id || a.GetType() != b.GetType()) return false;
            if (a is Section sa && b is Section sb)
            {
                if (sa != (sb with { Blocks = sa.Blocks }) || !StructureEquivalent(sa.Blocks, sb.Blocks)) return false;
            }
            if (a is Table ta && b is Table tb)
            {
                if (ta != (tb with { Rows = ta.Rows }) || ta.Rows.Length != tb.Rows.Length) return false;
                for (var r = 0; r < ta.Rows.Length; r++)
                {
                    if (ta.Rows[r].Length != tb.Rows[r].Length) return false;
                    for (var c = 0; c < ta.Rows[r].Length; c++)
                    {
                        var ca = ta.Rows[r][c]; var cb = tb.Rows[r][c];
                        if (ca != (cb with { Blocks = ca.Blocks }) || !StructureEquivalent(ca.Blocks, cb.Blocks)) return false;
                    }
                }
            }
        }
        return true;
    }
}
