using System.Globalization;
using Avalonia.Media;
using Textalonia.Editing;
using Textalonia.Layout;
using Textalonia.MailMerge;
using Textalonia.Model;
using Textalonia.Model.Fields;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class FieldLifecycleTests
{
    [Fact]
    public async Task Conversion_preserves_cache_by_default_and_updates_only_with_explicit_policy()
    {
        var session = new EditorSession();
        session.InsertField("DATE \\@ yyyy", FlowDocument.FromText("cached"));
        using var stored = new MemoryStream();
        await DocumentFormats.Json.SaveWithReportAsync(session.Document, stored);
        stored.Position = 0;
        var cached = await DocumentFormats.Json.LoadWithReportAsync(stored);
        Assert.Equal("cached", cached.Document.Text);
        stored.Position = 0;
        var updated = await DocumentFormats.Json.LoadWithReportAsync(stored, new()
        { FieldOptions = new() { Clock = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero) } });
        Assert.Equal("2026", updated.Document.Text); Assert.Equal("cached", session.Document.Text);
        Assert.Empty(updated.Report.Diagnostics);
    }

    [Fact]
    public async Task Strict_failed_update_does_not_write_destination_and_lock_prevents_resolver_calls()
    {
        var session = new EditorSession();
        var field = session.InsertField("DOCVARIABLE external", FlowDocument.FromText("cache"))!;
        session.SetFieldLocked(field.Id, true);
        var options = new FieldEvaluationOptions { DocumentVariableResolver = _ => throw new Exception("Must not resolve locked field") };
        Assert.Equal("cache", FieldEvaluator.Update(session.Document, options).Document.Text);
        session.SetFieldLocked(field.Id, false);
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Json.SaveWithReportAsync(session.Document, stream,
            new() { Mode = ConversionMode.Strict, FieldOptions = new() }));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void Merge_evaluates_nested_general_fields_when_requested_without_mutating_template()
    {
        var session = new EditorSession();
        session.InsertField("IF { MERGEFIELD amount } > 10 \"large\" \"small\"", FlowDocument.FromText("cached"));
        var values = new Dictionary<string, object?> { ["amount"] = 20 };
        Assert.Equal("cached", MailMergeProcessor.Merge(session.Document, values).Text);
        var merged = MailMergeProcessor.Merge(session.Document, values, new() { FieldOptions = new() });
        Assert.Equal("large", merged.Text); Assert.Equal("cached", session.Document.Text);
        merged.Validate();
    }
}

public class FieldPaginationTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Contents_length_repaginates_to_stable_page_numbers() => fixture.Session.Dispatch(() =>
    {
        var document = new FlowDocument(Enumerable.Range(0, 24).Select(i => (Block)new Paragraph("Heading " + i)
        { Style = ParagraphStyle.Default with { HeadingLevel = 1, PageBreakBefore = i > 0 } }));
        document = document with { Blocks = document.Blocks.Insert(0, new Paragraph("contents")) };
        document = FieldOperations.Insert(document, Guid.Empty, 0, "contents".Length, "TOC \\o \"1-3\" \\h", FlowDocument.FromText("contents"));
        using var engine = new PaginationEngine();
        var result = engine.UpdateFields(document, font: FontFamily.Default);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "field.pagination-not-converged");
        Assert.Contains("Heading 23\t", result.Document.Text);
        Assert.DoesNotContain("\t?", result.Document.Text);
        Assert.Equal(0, engine.UpdateFields(result.Document).UpdatedCount);
        result.Document.Validate();
    }, CancellationToken.None);
}
