using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class ContentControlTests
{
    [Fact]
    public void Checkbox_owns_atomic_inline_and_value_edit_is_undoable()
    {
        var session = new EditorSession(FlowDocument.FromText("Accept: "));
        session.Select(8, 8);
        var control = session.InsertContentControl(new() { Kind = ContentControlKind.CheckBox, Title = "Accept" })!;
        Assert.NotNull(control);
        Assert.Equal(1, control.End.Resolve(session.Document) - control.Start.Resolve(session.Document));
        Assert.IsType<FormControlInlinePayload>(Assert.IsType<Paragraph>(session.Document.Blocks[0]).Runs[^1].Inline!.Payload);
        Assert.True(session.ToggleContentControl(control.Id));
        Assert.True(session.Document.ContentControls[0].IsChecked);
        Assert.Equal("Accept: \u2612", session.Document.PlainText);
        session.Undo();
        Assert.False(session.Document.ContentControls[0].IsChecked);
        session.Redo();
        Assert.Equal("Accept: \u2612", session.Document.PlainText);
        session.Document.Validate();
    }

    [Fact]
    public void Plain_control_typing_updates_its_value_and_tracks_prefix_edits()
    {
        var session = new EditorSession(FlowDocument.FromText("Name: Joe."));
        session.Select(6, 9);
        var control = session.InsertContentControl(new() { Kind = ContentControlKind.PlainText })!;
        session.Select(0, 0); session.InsertText("The ");
        Assert.Equal(10, session.Document.ContentControls[0].Start.Resolve(session.Document));
        session.SelectContentControl(control.Id); session.InsertText("Ada");
        Assert.Equal("Ada", session.Document.ContentControls[0].Value);
        Assert.Equal("The Name: Ada.", session.Document.Text);
        session.Document.Validate();
    }

    [Fact]
    public void Protected_forms_fill_plain_and_checkbox_values_without_changing_surrounding_text()
    {
        var session = new EditorSession(FlowDocument.FromText("Name: ___ Accept: "));
        session.Select(6, 9); var text = session.InsertContentControl(new() { Kind = ContentControlKind.PlainText })!;
        session.Select(session.Index.Length, session.Index.Length); var check = session.InsertContentControl(new() { Kind = ContentControlKind.CheckBox })!;
        Assert.True(session.Protect(new() { Mode = DocumentProtectionMode.FormsOnly }));
        Assert.True(session.SetContentControlValue(text.Id, "Ada"));
        Assert.True(session.ToggleContentControl(check.Id));
        Assert.Equal("Name: Ada Accept: \u2612", session.Document.PlainText);
        var document = session.Document;
        session.Select(0, 0); session.InsertText("forbidden");
        Assert.Same(document, session.Document);
        Assert.False(session.RemoveContentControl(text.Id));
    }

    [Fact]
    public void Dropdown_and_date_reject_invalid_values_without_history_changes()
    {
        var session = new EditorSession();
        var control = session.InsertContentControl(new() { Kind = ContentControlKind.DropDown, Items = [new("One", "1"), new("Two", "2")], Placeholder = "Choose" })!;
        var document = session.Document; var revision = session.Revision;
        Assert.False(session.SetContentControlValue(control.Id, "3"));
        Assert.Same(document, session.Document); Assert.Equal(revision, session.Revision);
        Assert.True(session.SetContentControlValue(control.Id, "2"));
        Assert.Equal("Two", session.Document.PlainText);
        session.Select(session.Index.Length, session.Index.Length);
        var date = session.InsertContentControl(new() { Kind = ContentControlKind.Date })!;
        Assert.False(session.SetContentControlValue(date.Id, "2026-02-30"));
        Assert.True(session.SetContentControlValue(date.Id, "2026-09-28"));
    }

    [Fact]
    public void Tab_navigation_switches_stories_and_skips_locked_controls()
    {
        var session = new EditorSession(FlowDocument.FromText("body"));
        session.SelectAll(); var body = session.InsertContentControl(new() { Kind = ContentControlKind.PlainText })!;
        session.ActivateHeaderFooter(Guid.Empty, false);
        var storyId = session.ActiveStoryId;
        var header = session.InsertContentControl(new() { Kind = ContentControlKind.PlainText, Value = "header" })!;
        session.SelectContentControl(body.Id);
        Assert.True(session.SelectNextContentControl());
        Assert.Equal(storyId, session.ActiveStoryId); Assert.Equal(header.Id, session.CurrentContentControl!.Id);
        Assert.True(session.SelectNextContentControl());
        Assert.Equal(body.Id, session.CurrentContentControl!.Id);
        Assert.True(session.SelectNextContentControl(true));
        Assert.Equal(header.Id, session.CurrentContentControl!.Id);
    }

    [Fact]
    public void Clipboard_remaps_atomic_control_and_range_identities()
    {
        var source = new EditorSession(FlowDocument.FromText("Form: "));
        source.Select(6, 6); var original = source.InsertContentControl(new() { Kind = ContentControlKind.CheckBox, IsChecked = true })!;
        source.SelectAll(); var fragment = source.CopyFragment(); fragment.Validate();
        Assert.NotEqual(original.Id, fragment.Document.ContentControls[0].Id);
        var destination = new EditorSession(FlowDocument.FromText("Other "));
        destination.Select(6, 6); destination.InsertFragment(fragment);
        destination.Document.Validate();
        Assert.NotEqual(fragment.Document.ContentControls[0].Id, destination.Document.ContentControls[0].Id);
        Assert.Equal("Other Form: \u2612", destination.Document.PlainText);
        Assert.True(destination.ToggleContentControl(destination.Document.ContentControls[0].Id));
    }

    [Fact]
    public void Deleting_unlocked_atomic_control_removes_metadata_and_undo_restores_both()
    {
        var session = new EditorSession();
        var control = session.InsertContentControl(new() { Kind = ContentControlKind.CheckBox })!;
        session.SelectContentControl(control.Id); session.InsertText("");
        Assert.Empty(session.Document.ContentControls); session.Document.Validate();
        session.Undo(); Assert.Single(session.Document.ContentControls); session.Document.Validate();
    }

    [Fact]
    public void Permission_ranges_track_text_edits_in_secondary_story()
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("one two")] };
        var document = new FlowDocument { Stories = new Dictionary<Guid, DocumentStory> { [story.Id] = story }.ToImmutableDictionary() };
        document = document with { PermissionRanges = [new() { Start = DocumentAnchor.Create(document, story.Id, 4, AnchorAffinity.Before), End = DocumentAnchor.Create(document, story.Id, 7), IsReadOnly = true }] };
        var session = new EditorSession(document); session.SwitchStory(story.Id); session.InsertText("prefix ");
        var range = session.Document.PermissionRanges[0];
        Assert.Equal(11, range.Start.Resolve(session.Document)); Assert.Equal(14, range.End.Resolve(session.Document));
        session.Document.Validate();
    }
    [Fact]
    public void Protected_whole_text_control_accepts_typing_and_clipboard_without_losing_protection()
    {
        var session = new EditorSession(FlowDocument.FromText("before"));
        session.SelectAll(); var control = session.InsertContentControl(new() { Kind = ContentControlKind.PlainText })!;
        Assert.True(session.Protect(new() { Mode = DocumentProtectionMode.FormsOnly }));
        session.SelectContentControl(control.Id); session.InsertText("typed");
        Assert.Equal("typed", session.Document.ContentControls[0].Value);
        session.SelectContentControl(control.Id); session.InsertDocument(FlowDocument.FromText("pasted"));
        Assert.Equal("pasted", session.Document.ContentControls[0].Value);
        Assert.Equal(DocumentProtectionMode.FormsOnly, session.Document.Protection.Mode);
        Assert.True(session.SetContentControlValue(control.Id, "api"));
        Assert.Equal("api", session.Document.Text);
    }

    [Fact]
    public void Plain_control_rejects_multiline_typing_without_changing_history()
    {
        var session = new EditorSession(FlowDocument.FromText("name"));
        session.SelectAll(); var control = session.InsertContentControl(new() { Kind = ContentControlKind.PlainText })!;
        var document = session.Document; var revision = session.Revision;
        session.SelectContentControl(control.Id); session.InsertText("one\ntwo");
        Assert.Same(document, session.Document); Assert.Equal(revision, session.Revision);
    }

    [Fact]
    public void Rich_control_allows_multiple_paragraph_value_in_protected_form()
    {
        var session = new EditorSession(FlowDocument.FromText("description"));
        session.SelectAll(); var control = session.InsertContentControl(new() { Kind = ContentControlKind.RichText })!;
        Assert.True(session.Protect(new() { Mode = DocumentProtectionMode.FormsOnly }));
        Assert.True(session.SetContentControlValue(control.Id, "first\nsecond"));
        Assert.Equal("first\nsecond", session.Document.ContentControls[0].Value);
        session.Document.Validate();
    }

    [Fact]
    public void Binding_reads_only_bounded_host_xml_and_updates_through_form_policy()
    {
        var binding = new ContentControlBinding { StoreItemId = "data", XPath = "/d:form/d:name[2]/@value", PrefixMappings = "xmlns:d='urn:form'" };
        const string xml = "<form xmlns='urn:form'><name value='First'/><name value='Second'/></form>";
        Assert.True(binding.TryReadValue(xml, out var value)); Assert.Equal("Second", value);
        Assert.False((binding with { XPath = "//d:name" }).TryReadValue(xml, out _));
        Assert.False((binding with { XPath = "/d:form/d:name[last()]" }).TryReadValue(xml, out _));
        Assert.False(binding.TryReadValue("<!DOCTYPE form [<!ENTITY x SYSTEM 'file:///private'>]><form>&x;</form>", out _));
        var session = new EditorSession();
        var control = session.InsertContentControl(new() { Kind = ContentControlKind.PlainText, Binding = binding })!;
        Assert.True(session.Protect(new() { Mode = DocumentProtectionMode.FormsOnly }));
        Assert.False(session.ApplyContentControlBinding(control.Id, "other-data", xml));
        Assert.True(session.ApplyContentControlBinding(control.Id, "data", xml));
        Assert.Equal("Second", session.Document.Text);
    }

    [Fact]
    public void Arbitrary_paragraph_split_remaps_permission_to_the_trailing_piece()
    {
        var document = FlowDocument.FromText("one two");
        var paragraph = (Paragraph)document.Blocks[0];
        document = document with { PermissionRanges = [new() { Start = DocumentAnchor.Create(document, Guid.Empty, 4, AnchorAffinity.Before),
            End = DocumentAnchor.Create(document, Guid.Empty, 7), IsReadOnly = true }] };
        var session = new EditorSession(document);
        session.Execute(current => current with { Blocks = [paragraph with { Runs = [new RichRun("one")] }, new Paragraph("two")] });
        Assert.Equal("one\ntwo", session.Document.Text);
        var range = session.Document.PermissionRanges[0];
        Assert.NotEqual(paragraph.Id, range.Start.ParagraphId);
        Assert.Equal(4, range.Start.Resolve(session.Document)); Assert.Equal(7, range.End.Resolve(session.Document));
    }

    [Fact]
    public void Plain_control_model_rejects_embedded_inline_and_soft_line_breaks()
    {
        var document = new FlowDocument([new Paragraph([new RichRun(new InlineDescriptor { Payload = new ControlInlinePayload("sample"), AltText = "value" })])]);
        document = document with { ContentControls = [new() { Kind = ContentControlKind.PlainText, Value = "value",
            Start = DocumentAnchor.Create(document, Guid.Empty, 0, AnchorAffinity.Before), End = DocumentAnchor.Create(document, Guid.Empty, 1) }] };
        Assert.Throws<FormatException>(() => document.Validate());
        Assert.NotNull(new DocumentContentControl().ValidateValue("first\u2028second"));
        Assert.NotNull(new DocumentContentControl().ValidateValue("first\u2029second"));
    }

}

