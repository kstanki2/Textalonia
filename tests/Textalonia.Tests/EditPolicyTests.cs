using System.Collections.Immutable;
using System.Text;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class EditPolicyTests
{
    private static DocumentPermissionRange Range(FlowDocument document, int start, int end,
        bool readOnly = true, string? user = null, string? group = null, Guid storyId = default) => new()
    {
        Start = DocumentAnchor.Create(document, storyId, start, AnchorAffinity.Before),
        End = DocumentAnchor.Create(document, storyId, end, AnchorAffinity.After),
        IsReadOnly = readOnly, User = user, Group = group
    };

    private static void AssertRejected(EditorSession session, Action edit)
    {
        var document = session.Document;
        var selection = session.Selection;
        var typingStyle = session.TypingStyle;
        var revision = session.Revision;
        var history = session.RetainedHistoryBytes;
        var canUndo = session.CanUndo;
        var canRedo = session.CanRedo;
        edit();
        Assert.Same(document, session.Document);
        Assert.Equal(selection, session.Selection);
        Assert.Equal(typingStyle, session.TypingStyle);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(history, session.RetainedHistoryBytes);
        Assert.Equal(canUndo, session.CanUndo);
        Assert.Equal(canRedo, session.CanRedo);
    }

    [Theory]
    [InlineData(CommandCapability.Disabled)]
    [InlineData(CommandCapability.Hidden)]
    public void Disabled_and_hidden_text_commands_are_enforced_in_the_session(CommandCapability capability)
    {
        var session = new EditorSession(FlowDocument.FromText("original"))
        {
            EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty.Add(EditOperation.Text, capability) }
        };
        session.Select(2, 5);
        Assert.Equal(capability, session.GetCapability(EditOperation.Text));
        AssertRejected(session, () => session.InsertText("replacement"));
        AssertRejected(session, () => session.DeleteBackward());
        AssertRejected(session, () => session.DeleteForward());
        AssertRejected(session, () => session.InsertParagraph());
        AssertRejected(session, () => session.Execute(document => document.ReplaceBlock(document.Blocks[0].Id,
            ((Paragraph)document.Blocks[0]) with { Runs = [new RichRun("bypass")] })));
    }

    [Fact]
    public void Formatting_capability_is_independent_of_typing_and_includes_the_typing_style()
    {
        var session = new EditorSession(FlowDocument.FromText("text"))
        {
            EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty.Add(EditOperation.Formatting, CommandCapability.Disabled) }
        };
        session.Select(1, 1);
        AssertRejected(session, session.ToggleBold);
        session.Select(1, 3);
        AssertRejected(session, session.ToggleItalic);
        AssertRejected(session, () => session.ApplyParagraphStyle(style => style with { Alignment = ParagraphAlignment.Right }));
        session.InsertText("ok");
        Assert.Equal("tokt", session.Document.Text);
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void Readonly_ranges_reject_text_formatting_and_mixed_selections_atomically()
    {
        var document = FlowDocument.FromText("open locked tail");
        document = document with { PermissionRanges = [Range(document, 5, 11)] };
        var session = new EditorSession(document);
        session.Select(7, 6);
        AssertRejected(session, () => session.InsertText("x"));
        AssertRejected(session, session.ToggleBold);
        session.Select(2, 8);
        AssertRejected(session, () => session.InsertText("mixed"));
        AssertRejected(session, session.ToggleBold);
        session.Select(6, 6);
        AssertRejected(session, () => session.ApplyParagraphStyle(style => style with { Alignment = ParagraphAlignment.Center }));
        session.Select(1, 3);
        session.InsertText("K");
        Assert.Equal("oKn locked tail", session.Document.Text);
    }

    [Fact]
    public void Permission_anchors_follow_insertions_and_paragraph_splits_before_the_lock()
    {
        var document = FlowDocument.FromText("open locked tail");
        var permission = Range(document, 5, 11);
        var session = new EditorSession(document with { PermissionRanges = [permission] });
        session.Select(2, 2);
        session.InsertText("new\n");
        Assert.Equal("opnew\nen locked tail", session.Document.Text);
        var transformed = Assert.Single(session.Document.PermissionRanges);
        Assert.Equal(permission.Id, transformed.Id);
        Assert.Equal(9, transformed.Start.Resolve(session.Document));
        Assert.Equal(15, transformed.End.Resolve(session.Document));
        Assert.Equal("locked", session.Document.Text[transformed.Start.Resolve(session.Document)..transformed.End.Resolve(session.Document)]);
        session.Select(11, 11);
        AssertRejected(session, () => session.InsertText("bypass"));
        session.Undo();
        Assert.Equal("open locked tail", session.Document.Text);
        Assert.Equal(5, Assert.Single(session.Document.PermissionRanges).Start.Resolve(session.Document));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Host_identity_grants_only_the_matching_user_or_group_permission(bool userPermission)
    {
        var document = FlowDocument.FromText("open locked tail");
        var permission = Range(document, 5, 11, readOnly: false,
            user: userPermission ? "alice" : null, group: userPermission ? null : "editors");
        var session = new EditorSession(document with
        {
            Protection = new() { Mode = DocumentProtectionMode.ReadOnly }, PermissionRanges = [permission]
        });
        session.Select(6, 8);
        AssertRejected(session, () => session.InsertText("denied"));
        session.Identity = new() { User = "alice", Groups = ImmutableHashSet.Create("editors") };
        session.InsertText("OK");
        Assert.Equal("open lOKked tail", session.Document.Text);
        session.Select(1, 2);
        AssertRejected(session, () => session.InsertText("outside"));
        session.Identity = new() { User = "bob", Groups = ImmutableHashSet.Create("readers") };
        session.Select(6, 8);
        AssertRejected(session, () => session.InsertText("denied again"));
    }

    [Fact]
    public void Readonly_range_wins_over_an_overlapping_identity_exception()
    {
        var document = FlowDocument.FromText("allowed locked");
        var session = new EditorSession(document with
        {
            Protection = new() { Mode = DocumentProtectionMode.ReadOnly },
            PermissionRanges = [Range(document, 0, 14, readOnly: false, user: "alice"), Range(document, 8, 14)]
        }) { Identity = new() { User = "alice" } };
        session.Select(10, 11);
        AssertRejected(session, () => session.InsertText("x"));
        session.Select(1, 2);
        session.InsertText("L");
        Assert.Equal("aLlowed locked", session.Document.Text);
    }

    [Fact]
    public void Replace_all_rejects_the_whole_batch_if_one_match_is_protected()
    {
        var document = FlowDocument.FromText("cat cat cat");
        var session = new EditorSession(document with { PermissionRanges = [Range(document, 4, 7)] });
        session.Select(2, 1);
        AssertRejected(session, () => Assert.Equal(0, session.ReplaceAll("cat", "dog")));
        Assert.Equal("cat cat cat", session.Document.Text);
    }

    [Fact]
    public void Execute_cannot_remove_protection_or_permission_locks()
    {
        var document = FlowDocument.FromText("protected");
        var session = new EditorSession(document with
        {
            Protection = new() { Mode = DocumentProtectionMode.ReadOnly }, PermissionRanges = [Range(document, 1, 5)]
        });
        AssertRejected(session, () => session.Execute(current => current with { Protection = new(), PermissionRanges = [] }));
        Assert.True(session.TryUnprotect());
        AssertRejected(session, () => session.Execute(current => current with { PermissionRanges = [] }));
        AssertRejected(session, () => session.Execute(current => current with
        {
            PermissionRanges = [], Blocks = [new Paragraph("replacement")]
        }));
    }

    [Fact]
    public void Execute_cannot_hide_locked_formatting_behind_an_unprotected_text_change()
    {
        var document = FlowDocument.FromText("open locked tail");
        var session = new EditorSession(document with { PermissionRanges = [Range(document, 5, 11)] });
        AssertRejected(session, () => session.Execute(current => current.ReplaceBlock(current.Blocks[0].Id,
            ((Paragraph)current.Blocks[0]) with
            {
                Runs = [new RichRun("Open "), new RichRun("locked", new TextStyle { Bold = true }), new RichRun(" tail")]
            })));
    }

    [Fact]
    public void Execute_cannot_change_locked_styles_by_replacing_paragraph_identities()
    {
        var document = FlowDocument.FromText("open locked tail");
        var session = new EditorSession(document with { PermissionRanges = [Range(document, 5, 11)] });
        var replacement = new Paragraph(
            [new RichRun("Open "), new RichRun("locked", new TextStyle { Bold = true }), new RichRun(" tail")]);
        Assert.NotEqual(document.Blocks[0].Id, replacement.Id);
        AssertRejected(session, () => session.Execute(current => current with { Blocks = [replacement] }));
        Assert.False(((Paragraph)session.Document.Blocks[0]).StyleAt(6).Bold);
        Assert.Equal("open locked tail", session.Document.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plaintext_forms_reject_inline_insertion_without_changing_document_or_history(bool protectedForm)
    {
        var session = new EditorSession(FlowDocument.FromText("field"));
        session.SelectAll();
        Assert.NotNull(session.InsertContentControl(new() { Kind = ContentControlKind.PlainText }));
        if (protectedForm) Assert.True(session.Protect(new() { Mode = DocumentProtectionMode.FormsOnly }));
        session.Select(2, 3);
        AssertRejected(session, () => session.InsertInline(InlineDescriptor.PageField(PageFieldKind.Page)));
        Assert.Equal("field", Assert.Single(session.Document.ContentControls).Value);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void Plaintext_forms_reject_inline_table_and_multiple_paragraph_pastes(bool protectedForm, int fragmentKind)
    {
        var session = new EditorSession(FlowDocument.FromText("field"));
        session.SelectAll();
        Assert.NotNull(session.InsertContentControl(new() { Kind = ContentControlKind.PlainText }));
        if (protectedForm) Assert.True(session.Protect(new() { Mode = DocumentProtectionMode.FormsOnly }));
        session.Select(2, 3);
        var fragment = fragmentKind switch
        {
            0 => new FlowDocument([new Paragraph([new RichRun(InlineDescriptor.PageField(PageFieldKind.Page))])]),
            1 => new FlowDocument([Table.Create(1, 1)]),
            _ => FlowDocument.FromText("first\nsecond")
        };
        AssertRejected(session, () => session.InsertDocument(fragment));
        Assert.Equal("field", Assert.Single(session.Document.ContentControls).Value);
    }

    [Fact]
    public void Clipboard_capability_rejects_fragments_and_drops_but_keeps_typing_enabled()
    {
        var session = new EditorSession(FlowDocument.FromText("original"))
        {
            EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty.Add(EditOperation.Clipboard, CommandCapability.Disabled) }
        };
        var fragment = new DocumentFragment { Document = FlowDocument.FromText("pasted") };
        session.Select(2, 5);
        AssertRejected(session, () => session.InsertFragment(fragment));
        AssertRejected(session, () => Assert.Equal(ContentDropResult.None, session.DropContent(fragment, 1, session.Revision)));
        session.InsertText("typed");
        Assert.Equal("ortypednal", session.Document.Text);
    }

    [Fact]
    public void Table_structure_and_inline_updates_cannot_bypass_document_readonly()
    {
        var inline = InlineDescriptor.PageField(PageFieldKind.Page);
        var table = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("cell")] });
        var paragraph = new Paragraph([new RichRun("text"), new RichRun(inline)]);
        var session = new EditorSession(new FlowDocument([paragraph, table])
        {
            Protection = new() { Mode = DocumentProtectionMode.ReadOnly }
        });
        AssertRejected(session, () => session.InsertTable());
        AssertRejected(session, () => session.InsertInline(InlineDescriptor.PageField(PageFieldKind.NumPages)));
        AssertRejected(session, () => session.UpdateInline(inline.Id, current => current with { AltText = "changed" }));
        var cellStart = session.Index.ById(((Paragraph)table.Rows[0][0].Blocks[0]).Id).Start;
        session.Select(cellStart, cellStart);
        AssertRejected(session, () => session.UpdateCurrentTable((current, _, _) => current.InsertRow(1)));
        AssertRejected(session, session.DeleteCurrentTable);
    }

    [Fact]
    public void Protected_sections_leave_other_sections_editable()
    {
        var first = new Paragraph("free");
        var second = new Paragraph("locked");
        var firstSection = new DocumentSection();
        var protectedSection = new DocumentSection { StartParagraphId = second.Id };
        var session = new EditorSession(new FlowDocument([first, second])
        {
            Sections = [firstSection, protectedSection],
            Protection = new() { Mode = DocumentProtectionMode.FormsOnly, ProtectedSectionIds = [protectedSection.Id] }
        });
        session.Select(1, 2);
        session.InsertText("R");
        Assert.Equal("fRee\nlocked", session.Document.Text);
        session.Select(7, 8);
        AssertRejected(session, () => session.InsertText("x"));
        session.Select(2, 8);
        AssertRejected(session, () => session.InsertText("mixed"));
        Assert.Equal(protectedSection.Id, session.Document.Sections[1].Id);
    }

    [Fact]
    public void Secondary_story_permissions_are_independent_of_body_offsets()
    {
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("header locked")] };
        var document = FlowDocument.FromText("body editable") with
        {
            Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header)
        };
        var session = new EditorSession(document with { PermissionRanges = [Range(document, 7, 13, storyId: header.Id)] });
        session.Select(8, 9);
        session.InsertText("X");
        Assert.Equal("body ediXable", session.Document.Text);
        session.SwitchStory(header.Id);
        session.Select(8, 9);
        AssertRejected(session, () => session.InsertText("denied"));
        Assert.Equal("header locked", session.Document.GetStoryDocument(header.Id).Text);
    }

    [Fact]
    public void Execute_cannot_delete_a_story_that_contains_readonly_content()
    {
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("locked header")] };
        var document = FlowDocument.FromText("editable body") with
        {
            Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header)
        };
        var session = new EditorSession(document with { PermissionRanges = [Range(document, 0, 6, storyId: header.Id)] });
        AssertRejected(session, () => session.Execute(current => current with { Stories = current.Stories.Remove(header.Id) }));
        Assert.Single(session.Document.PermissionRanges);
    }

    [Fact]
    public void Deleting_a_note_reference_cannot_prune_its_readonly_story_after_policy_checks()
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [new Paragraph("locked footnote")] };
        var note = new DocumentNote { Kind = DocumentNoteKind.Footnote, StoryId = story.Id };
        var document = new FlowDocument([new Paragraph([new RichRun("a"), new RichRun(InlineDescriptor.Note(note.Id)), new RichRun("b")])])
        {
            Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story), Notes = [note]
        };
        var session = new EditorSession(document with { PermissionRanges = [Range(document, 0, 6, storyId: story.Id)] });
        session.Select(1, 2);
        AssertRejected(session, () => session.DeleteForward());
        Assert.True(session.Document.Stories.ContainsKey(story.Id));
        Assert.Single(session.Document.Notes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resource_catalog_changes_obey_readonly_and_metadata_capabilities(bool readonlyDocument)
    {
        var resource = new DocumentResource { MediaType = "application/octet-stream", Data = [1, 2, 3] };
        var session = new EditorSession(FlowDocument.FromText("body") with
        {
            Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("existing", resource),
            Protection = new() { Mode = readonlyDocument ? DocumentProtectionMode.ReadOnly : DocumentProtectionMode.None }
        });
        if (!readonlyDocument)
            session.EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty.Add(EditOperation.Metadata, CommandCapability.Disabled) };
        AssertRejected(session, () => session.Execute(current => current with { Resources = current.Resources.Add("added", resource) }));
        AssertRejected(session, () => session.Execute(current => current with { Resources = current.Resources.Remove("existing") }));
    }

    [Fact]
    public void Protection_changed_callbacks_cannot_reenter_with_unrestricted_edits()
    {
        var session = new EditorSession(FlowDocument.FromText("original"));
        var attempted = false;
        session.Changed += (_, _) =>
        {
            if (attempted || session.Document.Protection.Mode != DocumentProtectionMode.ReadOnly) return;
            attempted = true;
            AssertRejected(session, () => session.InsertText("bypass"));
            AssertRejected(session, () => session.Execute(document => document with { Protection = new() }));
        };
        Assert.True(session.Protect(new() { Mode = DocumentProtectionMode.ReadOnly }, "secret"));
        Assert.True(attempted);
        Assert.Equal("original", session.Document.Text);
        Assert.Equal(DocumentProtectionMode.ReadOnly, session.Document.Protection.Mode);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void Adding_secondary_story_text_cannot_bypass_the_text_capability()
    {
        var session = new EditorSession(FlowDocument.FromText("body"))
        {
            EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty.Add(EditOperation.Text, CommandCapability.Disabled) }
        };
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("bypass")] };
        AssertRejected(session, () => session.Execute(document => document with { Stories = document.Stories.Add(header.Id, header) }));
    }

    [Fact]
    public void Atomic_form_filling_uses_its_form_capability_when_other_inlines_are_disabled()
    {
        var session = new EditorSession();
        var checkbox = session.InsertContentControl(new() { Kind = ContentControlKind.CheckBox });
        Assert.NotNull(checkbox);
        Assert.True(session.Protect(new() { Mode = DocumentProtectionMode.FormsOnly }));
        session.EditPolicy = new()
        {
            Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty.Add(EditOperation.InlineObjects, CommandCapability.Disabled)
        };
        Assert.True(session.SetContentControlValue(checkbox.Id, "true"));
        Assert.True(Assert.Single(session.Document.ContentControls).IsChecked);
        session.Undo();
        Assert.False(Assert.Single(session.Document.ContentControls).IsChecked);
    }

    [Fact]
    public void Undo_and_redo_recheck_current_identity_without_consuming_denied_history()
    {
        var document = FlowDocument.FromText("editable");
        var session = new EditorSession(document with
        {
            Protection = new() { Mode = DocumentProtectionMode.ReadOnly },
            PermissionRanges = [Range(document, 0, 8, readOnly: false, user: "alice")]
        }) { Identity = new() { User = "alice" } };
        session.Select(2, 4);
        session.InsertText("IT");
        Assert.Equal("edITable", session.Document.Text);
        session.Identity = new() { User = "bob" };
        AssertRejected(session, session.Undo);
        session.Identity = new() { User = "alice" };
        session.Undo();
        Assert.Equal("editable", session.Document.Text);
        session.Identity = new() { User = "bob" };
        AssertRejected(session, session.Redo);
        session.Identity = new() { User = "alice" };
        session.Redo();
        Assert.Equal("edITable", session.Document.Text);
    }

    [Fact]
    public void Clipboard_fragment_and_drop_are_rejected_at_protected_destinations()
    {
        var document = FlowDocument.FromText("open locked tail");
        var target = new EditorSession(document with { PermissionRanges = [Range(document, 5, 11)] });
        var source = new EditorSession(FlowDocument.FromText("source"));
        source.Select(1, 4);
        var drag = source.CaptureContentDrag()!;
        target.Select(8, 7);
        AssertRejected(target, () => target.InsertFragment(drag.Fragment));
        AssertRejected(source, () => AssertRejected(target, () =>
            Assert.Equal(ContentDropResult.None, target.DropContent(drag.Fragment, 7, target.Revision, drag, move: true))));
        Assert.False(drag.Completed);
    }

    [Fact]
    public void Cross_editor_move_never_deletes_a_protected_source()
    {
        var document = FlowDocument.FromText("source");
        var source = new EditorSession(document with { PermissionRanges = [Range(document, 1, 4)] });
        var target = new EditorSession(FlowDocument.FromText("target"));
        source.Select(1, 4);
        var drag = source.CaptureContentDrag()!;
        AssertRejected(source, () =>
        {
            var result = target.DropContent(drag.Fragment, 2, target.Revision, drag, move: true);
            Assert.Equal(ContentDropResult.Copy, result);
            Assert.Equal("taourrget", target.Document.Text);
        });
    }

    [Fact]
    public void Password_protection_requires_the_verifier_and_cannot_be_undone_to_unlock()
    {
        var session = new EditorSession(FlowDocument.FromText("original"));
        session.Select(8, 8);
        session.InsertText("!");
        Assert.True(session.Protect(new() { Mode = DocumentProtectionMode.ReadOnly }, "correct horse"));
        Assert.NotNull(session.Document.Protection.Password);
        AssertRejected(session, () => Assert.False(session.TryUnprotect("wrong")));
        AssertRejected(session, () => Assert.False(session.TryUnprotect()));
        AssertRejected(session, session.Undo);
        Assert.True(session.TryUnprotect("correct horse"));
        Assert.Equal(DocumentProtectionMode.None, session.Document.Protection.Mode);
        session.InsertText(" editable");
        Assert.EndsWith(" editable", session.Document.Text);
    }

    [Fact]
    public void Password_verifiers_use_independent_salts_and_match_the_pbkdf2_sha256_vector()
    {
        var first = DocumentProtectionPassword.Create("password");
        var second = DocumentProtectionPassword.Create("password");
        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.True(first.Verify("password"));
        Assert.False(first.Verify("Password"));
        var vector = new DocumentProtectionPassword
        {
            Algorithm = "PBKDF2-SHA256", Iterations = 1,
            Salt = Convert.ToBase64String(Encoding.UTF8.GetBytes("salt")),
            Hash = Convert.ToBase64String(Convert.FromHexString("120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b"))
        };
        Assert.True(vector.Verify("password"));
        Assert.False(vector.Verify("incorrect"));
    }

    [Theory]
    [InlineData(0, "ci4W7O6b2zMY+1TLPqwAIRXrWb3VHVDRNTPXlrOw/3Mn0Jse+yb/7qdrqwfJLMNYPpeZGJJ2Sr9SfUiQpN0jgQ==")]
    [InlineData(1, "mO673WLyyvkeXEF75NZnAUoAhHRdNmablQ8SLWNUziEM/hZ0ounbjMMCrwfDkVwHOwO58AM8A7R+y/EsGM2fww==")]
    [InlineData(2, "yKdTGMuADm+y1BxxA59Ldg/nO/qRZRg2N2jbZe1QFrTZ0mdtgZ/tBVz5Q3dRK04XBPr7H2v0wbUX9g1iL0AVVQ==")]
    [InlineData(1000, "pHUx0rlEIS9sDPkCEz38/fNvMfH7q/p6amETtfMZYonUUm+Sypdsuk4JbT842TZMrSNqZTdBxX/rLlD+c16/Tg==")]
    public void Office_sha512_verifies_independent_utf16_and_iteration_vectors(int iterations, string hash)
    {
        // Independently generated with Python hashlib following MS-OFFCRYPTO 2.4.2.4:
        // SHA512(salt + UTF16LE(password)), followed by SHA512(hash + LE32(counter)).
        // https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-offcrypto/1357ea58-646e-4483-92ef-95d718079d6f
        var verifier = new DocumentProtectionPassword
        {
            Algorithm = "Office-SHA512", Salt = "AAECAwQFBgcICQoLDA0ODw==", Iterations = iterations, Hash = hash
        };
        Assert.True(verifier.Verify("Pa\u00dfw\u00f6rd"));
        Assert.False(verifier.Verify("Password"));
    }

    [Fact]
    public void Trusted_load_can_replace_a_protected_document_and_resets_history()
    {
        var session = new EditorSession(FlowDocument.FromText("protected") with
        {
            Protection = new() { Mode = DocumentProtectionMode.ReadOnly, Password = DocumentProtectionPassword.Create("secret") }
        });
        var replacement = FlowDocument.FromText("loaded by host");
        session.Load(replacement);
        Assert.Same(replacement, session.Document);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
        session.InsertText("editable ");
        Assert.Equal("editable loaded by host", session.Document.Text);
    }
}
