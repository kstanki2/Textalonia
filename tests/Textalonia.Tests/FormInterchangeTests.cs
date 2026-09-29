using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class FormInterchangeTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    private static readonly XNamespace Tx = "urn:textalonia:fields:1";

    private static FlowDocument Form()
    {
        var check = new DocumentContentControl { Kind = ContentControlKind.CheckBox, IsChecked = true, Tag = "consent", Title = "Consent", LockControl = true };
        var drop = new DocumentContentControl { Kind = ContentControlKind.DropDown, Value = "b", Title = "Choice", Items = [new("Alpha", "a"), new("Beta", "b")], Placeholder = "Select" };
        var p = new Paragraph([new RichRun("Name: Ada "), new RichRun(new InlineDescriptor { Payload = new FormControlInlinePayload(check.Id), AltText = check.DisplayText }),
            new RichRun(" "), new RichRun(new InlineDescriptor { Payload = new FormControlInlinePayload(drop.Id), AltText = drop.DisplayText })]);
        var text = new DocumentContentControl { Kind = ContentControlKind.PlainText, Value = "Ada", Start = new() { ParagraphId = p.Id, Offset = 6, Affinity = AnchorAffinity.Before },
            End = new() { ParagraphId = p.Id, Offset = 9 }, Tag = "name", LockContents = true };
        check = check with { Start = new() { ParagraphId = p.Id, Offset = 10, Affinity = AnchorAffinity.Before }, End = new() { ParagraphId = p.Id, Offset = 11 } };
        drop = drop with { Start = new() { ParagraphId = p.Id, Offset = 12, Affinity = AnchorAffinity.Before }, End = new() { ParagraphId = p.Id, Offset = 13 } };
        return new FlowDocument([p]) { ContentControls = [text, check, drop], Protection = new() { Mode = DocumentProtectionMode.FormsOnly, Password = DocumentProtectionPassword.Create("forms password") },
            PermissionRanges = [new() { Start = text.Start, End = text.End, User = "editor@example.org" }] };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_formats_preserve_forms_protection_permissions_and_bindings(bool xaml)
    {
        var document = Form();
        document = document with { ContentControls = document.ContentControls.SetItem(0, document.ContentControls[0] with
        { Binding = new() { StoreItemId = Guid.NewGuid().ToString(), XPath = "/person/name", PrefixMappings = "" }, Data = ImmutableDictionary<string, string>.Empty.Add("required", "true") }) };
        TextDocumentFormat format = xaml ? DocumentFormats.Xaml : DocumentFormats.Json;
        var serialized = format.Serialize(document);
        var loaded = format.Parse(serialized);
        Assert.Equal(DocumentFormats.Json.Serialize(document), DocumentFormats.Json.Serialize(loaded));
        Assert.True(loaded.Protection.Password!.Verify("forms password"));
        Assert.Contains(xaml ? "Version=\"7\"" : "\"version\": 12", serialized);
    }

    [Fact]
    public async Task Docx_writes_standard_controls_locks_password_and_permission_markers_and_reopens()
    {
        var document = Form();
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Docx.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(saved.Report.Diagnostics);
        using (var archive = new ZipArchive(new MemoryStream(stream.ToArray()), ZipArchiveMode.Read))
        {
            var xml = Read(archive, "word/document.xml");
            Assert.Equal(3, xml.Descendants(W + "sdt").Count());
            Assert.Contains(xml.Descendants(W + "lock"), e => (string?)e.Attribute(W + "val") == "contentLocked");
            Assert.Equal("1", (string?)Assert.Single(xml.Descendants(W14 + "checked")).Attribute(W14 + "val"));
            Assert.Equal("editor@example.org", (string?)Assert.Single(xml.Descendants(W + "permStart")).Attribute(W + "ed"));
            var protection = Assert.Single(Read(archive, "word/settings.xml").Descendants(W + "documentProtection"));
            Assert.Equal("forms", (string?)protection.Attribute(W + "edit"));
            Assert.Equal("14", (string?)protection.Attribute(W + "cryptAlgorithmSid"));
            Assert.NotNull(archive.GetEntry("word/glossary/document.xml"));
        }
        stream.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(loaded.Report.Diagnostics);
        Assert.Equal(document.PlainText, loaded.Document.PlainText);
        Assert.Equal(3, loaded.Document.ContentControls.Length);
        Assert.True(loaded.Document.ContentControls.Single(c => c.Kind == ContentControlKind.CheckBox).IsChecked);
        Assert.Equal("b", loaded.Document.ContentControls.Single(c => c.Kind == ContentControlKind.DropDown).Value);
        Assert.True(loaded.Document.ContentControls.Single(c => c.Kind == ContentControlKind.PlainText).LockContents);
        Assert.Equal("editor@example.org", Assert.Single(loaded.Document.PermissionRanges).User);
        Assert.True(loaded.Document.Protection.Password!.Verify("forms password"));
    }

    [Fact]
    public async Task Imports_external_atomic_and_range_controls_with_native_interaction_values()
    {
        using var source = Package("""
            <w:p><w:sdt><w:sdtPr><w:tag w:val="name"/><w:text/><w:lock w:val="sdtLocked"/></w:sdtPr><w:sdtContent><w:r><w:t>Ada</w:t></w:r></w:sdtContent></w:sdt>
            <w:sdt><w:sdtPr><w14:checkbox><w14:checked w14:val="1"/></w14:checkbox></w:sdtPr><w:sdtContent><w:r><w:t>☒</w:t></w:r></w:sdtContent></w:sdt>
            <w:sdt><w:sdtPr><w:date w:fullDate="2026-09-28T00:00:00Z"><w:dateFormat w:val="yyyy-MM-dd"/></w:date></w:sdtPr><w:sdtContent><w:r><w:t>2026-09-28</w:t></w:r></w:sdtContent></w:sdt></w:p>
            """);
        var result = await DocumentFormats.Docx.LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict });
        Assert.Equal(3, result.Document.ContentControls.Length);
        Assert.True(result.Document.ContentControls[0].LockControl);
        Assert.Equal("Ada", result.Document.ContentControls[0].Value);
        Assert.True(result.Document.ContentControls[1].IsChecked);
        Assert.Equal("2026-09-28", result.Document.ContentControls[2].Value);
        Assert.Equal(2, new DocumentIndex(result.Document).Paragraphs[0].Paragraph.Runs.Count(r => r.Inline?.Payload is FormControlInlinePayload));
    }

    [Fact]
    public async Task Legacy_checkbox_imports_as_typed_interactive_field()
    {
        using var source = Package("""
            <w:p><w:r><w:fldChar w:fldCharType="begin"><w:ffData><w:name w:val="Agree"/><w:checkBox><w:checked w:val="1"/></w:checkBox></w:ffData></w:fldChar></w:r>
            <w:r><w:instrText> FORMCHECKBOX </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>☒</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p>
            """);
        var document = await DocumentFormats.Docx.LoadAsync(source);
        var control = Assert.Single(document.ContentControls);
        Assert.True(control.IsLegacyFormField);
        Assert.True(control.IsChecked);
        Assert.Equal(ContentControlKind.CheckBox, control.Kind);
        Assert.Equal("Agree", control.Tag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cross_paragraph_ranges_retain_exact_anchors_and_diagnose_unrepresentable_spans(bool partial)
    {
        var first = new Paragraph([new RichRun("First")]); var last = new Paragraph([new RichRun("Last")]);
        var control = new DocumentContentControl { Kind = ContentControlKind.RichText, Start = new() { ParagraphId = first.Id, Offset = partial ? 2 : 0, Affinity = AnchorAffinity.Before },
            End = new() { ParagraphId = last.Id, Offset = partial ? 2 : 4 }, Value = partial ? "rst\nLa" : "First\nLast" };
        var document = new FlowDocument([first, last]) { ContentControls = [control] };
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Docx.SaveWithReportAsync(document, stream);
        Assert.Equal(partial, saved.Report.Diagnostics.Any(d => d.Code == "docx.content-control-range"));
        stream.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        var reopened = Assert.Single(loaded.ContentControls);
        Assert.Equal(control.Value, reopened.Value);
        Assert.Equal(partial ? 2 : 0, reopened.Start.Offset);
        Assert.Equal(partial ? 2 : 4, reopened.End.Offset);
    }

    [Fact]
    public async Task Native_readonly_permissions_survive_docx_with_explicit_Word_limitation()
    {
        var document = Form();
        document = document with { PermissionRanges = [document.PermissionRanges[0] with { IsReadOnly = true, User = null }] };
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Docx.SaveWithReportAsync(document, stream);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == "docx.permission-range");
        stream.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        Assert.True(Assert.Single(loaded.PermissionRanges).IsReadOnly);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("rtf")]
    [InlineData("text")]
    public async Task Lossy_formats_report_form_and_protection_losses_before_strict_output(string name)
    {
        IDocumentFormat format = name switch { "html" => DocumentFormats.Html, "rtf" => DocumentFormats.Rtf, _ => DocumentFormats.PlainText };
        using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() => format.SaveWithReportAsync(Form(), output, new() { Mode = ConversionMode.Strict }));
        Assert.Contains(error.Report.Diagnostics, d => d.Code == "conversion.content-controls");
        Assert.Contains(error.Report.Diagnostics, d => d.Code == "conversion.edit-protection");
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task Protected_section_scope_and_header_controls_survive_docx()
    {
        var document = Form(); var last = new Paragraph([new RichRun("Unprotected")]);
        var headerParagraph = new Paragraph([new RichRun("Header form")]);
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [headerParagraph] };
        var headerControl = new DocumentContentControl { Kind = ContentControlKind.PlainText, Value = "Header form",
            Start = new() { StoryId = header.Id, ParagraphId = headerParagraph.Id, Affinity = AnchorAffinity.Before },
            End = new() { StoryId = header.Id, ParagraphId = headerParagraph.Id, Offset = 11 } };
        var firstSection = new DocumentSection { HeaderFooter = new() { PrimaryHeader = new() { StoryId = header.Id, LinkToPrevious = false } } };
        document = document with { Blocks = document.Blocks.Add(last), Stories = document.Stories.Add(header.Id, header),
            ContentControls = document.ContentControls.Add(headerControl), Sections = [firstSection, new() { StartParagraphId = last.Id }],
            Protection = document.Protection with { ProtectedSectionIds = [firstSection.Id] } };
        using var stream = new MemoryStream();
        await DocumentFormats.Docx.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        stream.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Equal(loaded.Document.Sections[0].Id, Assert.Single(loaded.Document.Protection.ProtectedSectionIds));
        Assert.Equal("Header form", Assert.Single(loaded.Document.ContentControls.Where(c => c.Start.StoryId != Guid.Empty)).Value);
    }

    [Fact]
    public async Task Imports_permissions_between_block_elements_and_rejects_unclosed_ranges()
    {
        using var source = Package("<w:permStart w:id='0' w:edGrp='everyone'/><w:p><w:r><w:t>Editable</w:t></w:r></w:p><w:permEnd w:id='0'/>");
        var document = await DocumentFormats.Docx.LoadAsync(source);
        var permission = Assert.Single(document.PermissionRanges);
        Assert.Equal(0, permission.Start.Offset); Assert.Equal(8, permission.End.Offset); Assert.Equal("everyone", permission.Group);
        using var malformed = Package("<w:p><w:permStart w:id='0'/><w:r><w:t>Do not unlock</w:t></w:r></w:p>");
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Docx.LoadAsync(malformed));
    }

    [Fact]
    public async Task Legacy_password_verifier_is_retained_without_allowing_passwordless_unlock()
    {
        using var source = Package("<w:p><w:r><w:t>Protected</w:t></w:r></w:p>", "<w:documentProtection w:edit='readOnly' w:enforcement='1' w:password='ABCD'/>");
        var result = await DocumentFormats.Docx.LoadWithReportAsync(source);
        Assert.Contains(result.Report.Diagnostics, diagnostic => diagnostic.Code == "docx.protection-password");
        Assert.NotNull(result.Document.Protection.Password);
        Assert.False(result.Document.Protection.Password.Verify(""));
        Assert.False(new Textalonia.Editing.EditorSession(result.Document).TryUnprotect());
        using var saved = new MemoryStream(); await DocumentFormats.Docx.SaveAsync(result.Document, saved);
        using var archive = new ZipArchive(new MemoryStream(saved.ToArray()), ZipArchiveMode.Read);
        Assert.Equal("ABCD", (string?)Read(archive, "word/settings.xml").Descendants(W + "documentProtection").Single().Attribute(W + "password"));
    }

    [Fact]
    public async Task Missing_enforcement_and_iso_password_attributes_do_not_unlock_imports()
    {
        var verifier = DocumentProtectionPassword.Create("iso password");
        var settings = $"<w:documentProtection w:edit='readOnly' w:algorithmName='SHA-512' w:hashValue='{verifier.Hash}' w:saltValue='{verifier.Salt}' w:spinCount='{verifier.Iterations}'/>";
        using var source = Package("<w:p><w:r><w:t>Protected</w:t></w:r></w:p>", settings);
        var document = await DocumentFormats.Docx.LoadAsync(source);
        Assert.True(document.Protection.Enforce);
        var session = new Textalonia.Editing.EditorSession(document);
        session.InsertText("forbidden"); Assert.Equal("Protected", session.Document.Text);
        Assert.False(session.TryUnprotect()); Assert.True(session.TryUnprotect("iso password"));
    }

    private static XDocument Read(ZipArchive archive, string name)
    { using var stream = archive.GetEntry(name)!.Open(); return XDocument.Load(stream); }

    private static MemoryStream Package(string content, string? settings = null)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false)))
                writer.Write($"<w:document xmlns:w='{W}' xmlns:w14='{W14}'><w:body>{content}</w:body></w:document>");
            if (settings is not null)
            {
                using var writer = new StreamWriter(archive.CreateEntry("word/settings.xml").Open(), new UTF8Encoding(false));
                writer.Write($"<w:settings xmlns:w='{W}'>{settings}</w:settings>");
            }
        }
        output.Position = 0; return output;
    }
}
