using System.Text;
using Textalonia.Baselines;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class ConversionDiagnosticsTests
{
    [Fact]
    public async Task Legacy_custom_formats_remain_usable_and_strict_mode_rejects_unknown_fidelity()
    {
        IDocumentFormat format = new LegacyFormat();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("legacy"));
        var result = await format.LoadWithReportAsync(input);
        Assert.Equal("legacy", result.Document.Text);
        Assert.Equal("conversion.diagnostics-unavailable", Assert.Single(result.Report.Diagnostics).Code);
        using var output = new MemoryStream([10, 20, 30]);
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() => format.SaveWithReportAsync(result.Document, output, new() { Mode = ConversionMode.Strict }));
        Assert.True(exception.Report.HasLoss);
        Assert.Equal(new byte[] { 10, 20, 30 }, output.ToArray());
        Assert.True(input.CanRead && output.CanWrite);
        using var legacy = new MemoryStream();
        await format.SaveAsync(result.Document, legacy);
        Assert.Equal("legacy", Encoding.UTF8.GetString(legacy.ToArray()));
    }

    [Fact]
    public async Task Reporting_custom_formats_contribute_actionable_diagnostics()
    {
        IDocumentFormat format = new ReportingFormat();
        using var stream = new MemoryStream();
        var result = await format.LoadWithReportAsync(stream);
        var diagnostic = Assert.Single(result.Report.Diagnostics);
        Assert.Equal("custom.field", diagnostic.Code);
        Assert.Equal("line 3", diagnostic.SourceLocation);
        Assert.NotEmpty(diagnostic.Fallback);
        await Assert.ThrowsAsync<DocumentConversionException>(() => format.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Native_strict_round_trip_keeps_every_model_property_and_merge_backup()
    {
        var original = BaselineDocuments.DocumentSemantics();
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Json.SaveWithReportAsync(original, stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(saved.Report.Diagnostics);
        stream.Position = 0;
        var loaded = await DocumentFormats.Json.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(loaded.Report.Diagnostics);
        Assert.Equal(DocumentFormats.Json.Serialize(original), DocumentFormats.Json.Serialize(loaded.Document));
    }

    [Fact]
    public async Task Plain_text_reports_loss_by_model_id_and_strict_export_never_touches_output()
    {
        var paragraph = new Paragraph("styled", new() { Bold = true });
        var document = new FlowDocument([new Section { Blocks = [paragraph] }]);
        using var output = new MemoryStream();
        var result = await DocumentFormats.PlainText.SaveWithReportAsync(document, output);
        Assert.Equal("styled", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "text.formatting" && d.ModelId == paragraph.Id);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "text.section");
        output.Position = 2;
        var bytes = output.ToArray();
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.PlainText.SaveWithReportAsync(document, output, new() { Mode = ConversionMode.Strict }));
        Assert.Equal(2, output.Position);
        Assert.Equal(bytes, output.ToArray());
    }

    [Fact]
    public async Task Odt_reports_new_office_metadata_losses_and_strict_export_is_atomic()
    {
        var document = FlowDocument.FromText("metadata") with
        {
            CoreProperties = new DocumentCoreProperties { Title = "Report" },
            CustomProperties = [new DocumentCustomProperty { Name = "Case", Value = "42" }],
            CustomXmlParts = [new DocumentCustomXmlPart { PartName = "customXml/item1.xml", Xml = "<root/>" }],
            CompatibilitySettings = new DocumentCompatibilitySettings
            { Xml = "<w:compat xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>" }
        };
        using var tolerant = new MemoryStream();
        var result = await DocumentFormats.Odt.SaveWithReportAsync(document, tolerant);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "conversion.core-properties");
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "conversion.custom-properties");
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "conversion.custom-xml");
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "conversion.compatibility-settings");
        using var destination = new MemoryStream([1, 2, 3]);
        destination.Position = 1;
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Odt.SaveWithReportAsync(
            document, destination, new() { Mode = ConversionMode.Strict }));
        Assert.Equal(1, destination.Position);
        Assert.Equal(new byte[] { 1, 2, 3 }, destination.ToArray());
    }

    [Fact]
    public async Task Explicit_plain_text_degradation_is_reported_and_does_not_mutate_source()
    {
        var document = new FlowDocument([new Section { Blocks = [new Paragraph("value", new() { Italic = true })] }]);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(DocumentFormats.Json.Serialize(document)));
        var result = await DocumentFormats.Json.LoadWithReportAsync(stream, new() { PlainTextOnly = true });
        Assert.IsType<Paragraph>(Assert.Single(result.Document.Blocks));
        Assert.Equal(TextStyle.Default, ((Paragraph)result.Document.Blocks[0]).Runs[0].Style);
        Assert.True(result.Report.HasLoss);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "conversion.plain-text-requested");
        Assert.IsType<Section>(document.Blocks[0]);
    }

    [Fact]
    public async Task Cancellation_and_malformed_data_are_never_successful_reports()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        foreach (var format in DocumentFormats.BuiltIn)
        {
            using var stream = new MemoryStream();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => format.LoadWithReportAsync(stream, cancellationToken: canceled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => format.SaveWithReportAsync(new(), stream, cancellationToken: canceled.Token));
            Assert.True(stream.CanRead);
        }
        using var rtf = new MemoryStream(Encoding.UTF8.GetBytes("{\\rtf1 unclosed"));
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Rtf.LoadWithReportAsync(rtf));
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        await Assert.ThrowsAsync<NotSupportedException>(() => DocumentFormats.Json.LoadWithReportAsync(json));
        using var docx = new MemoryStream([0, 1, 2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => DocumentFormats.Docx.LoadWithReportAsync(docx));
    }

    [Fact]
    public async Task Concurrent_reports_are_isolated_and_order_is_deterministic()
    {
        var document = new FlowDocument([new Section { Blocks = [new Paragraph("styled", new() { Bold = true })] }]);
        async Task<ConversionReport> Convert(IDocumentFormat format)
        {
            using var stream = new MemoryStream();
            return (await format.SaveWithReportAsync(document, stream)).Report;
        }
        var jobs = Enumerable.Range(0, 20).Select(i => Convert(i % 2 == 0 ? DocumentFormats.Json : DocumentFormats.PlainText)).ToArray();
        var reports = await Task.WhenAll(jobs);
        for (var i = 0; i < reports.Length; i++)
            if (i % 2 == 0) Assert.Empty(reports[i].Diagnostics);
            else Assert.Equal(reports[1].Diagnostics.ToArray(), reports[i].Diagnostics.ToArray());
    }

    [Fact]
    public async Task Cancellation_after_custom_parse_and_partial_destination_failures_propagate()
    {
        using var cancellation = new CancellationTokenSource();
        using var input = new MemoryStream();
        IDocumentFormat format = new CancelingFormat(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => format.LoadWithReportAsync(input, cancellationToken: cancellation.Token));
        using var output = new FailingStream();
        await Assert.ThrowsAsync<IOException>(() => DocumentFormats.PlainText.SaveWithReportAsync(FlowDocument.FromText("abcdef"), output));
        Assert.True(output.CanWrite);
        Assert.Equal("ab", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Invalid_utf8_is_an_error_instead_of_silent_replacement_text()
    {
        foreach (var format in new IDocumentFormat[] { DocumentFormats.Json, DocumentFormats.Html, DocumentFormats.PlainText })
        {
            using var stream = new MemoryStream([0xFF, 0xFF]);
            await Assert.ThrowsAsync<DecoderFallbackException>(() => format.LoadWithReportAsync(stream));
        }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Overflowing_native_cell_spans_and_merge_requests_are_rejected(bool rows)
    {
        var table = Table.Create(2, 2);
        var bad = table.SetCell(1, 1, table.Rows[1][1] with
        { RowSpan = rows ? int.MaxValue : 1, ColumnSpan = rows ? 1 : int.MaxValue });
        Assert.Throws<FormatException>(() => new FlowDocument([bad]).Validate());
        var json = DocumentFormats.Json.Serialize(new FlowDocument([table]));
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        root["document"]!["blocks"]![0]!["rows"]![1]![1]![rows ? "rowSpan" : "columnSpan"] = int.MaxValue;
        Assert.Throws<FormatException>(() => DocumentFormats.Json.Parse(root.ToJsonString()));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.MergeCells(1, 1, rows ? int.MaxValue : 1, rows ? 1 : int.MaxValue));
    }
    [Fact]
    public async Task Large_reports_are_bounded_with_explicit_truncation()
    {
        var document = new FlowDocument(Enumerable.Range(0, 1500).Select(i => new Paragraph("item", new() { Bold = true })));
        using var stream = new MemoryStream();
        var result = await DocumentFormats.PlainText.SaveWithReportAsync(document, stream);
        Assert.Equal(1025, result.Report.Diagnostics.Length);
        Assert.Equal("conversion.diagnostics-truncated", result.Report.Diagnostics[^1].Code);
        Assert.True(result.Report.HasLoss);
    }
    private class LegacyFormat : IDocumentFormat
    {
        public string Name => "Legacy";
        public IReadOnlyList<string> Extensions => [".legacy"];
        public virtual async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            return FlowDocument.FromText(await reader.ReadToEndAsync(cancellationToken));
        }
        public Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(document.PlainText), cancellationToken).AsTask();
    }

    private sealed class ReportingFormat : LegacyFormat, IReportingDocumentFormat
    {
        private static ConversionReport Report => new([new("custom.field", ConversionDiagnosticSeverity.Warning, "Computed field", "Retained its text.", SourceLocation: "line 3")]);
        public Task<DocumentLoadResult> LoadWithReportAsync(Stream stream, ConversionOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DocumentLoadResult(FlowDocument.FromText("field"), Report));
        public Task<DocumentSaveResult> SaveWithReportAsync(FlowDocument document, Stream stream, ConversionOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DocumentSaveResult(Report));
    }

    private sealed class CancelingFormat(CancellationTokenSource cancellation) : LegacyFormat
    {
        public override Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromResult(new FlowDocument());
        }
    }

    private sealed class FailingStream : Stream
    {
        private readonly MemoryStream _content = new();
        public byte[] ToArray() => _content.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _content.Length;
        public override long Position { get => _content.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            _content.Write(buffer, offset, Math.Min(2, count));
            throw new IOException("Simulated destination failure.");
        }
        protected override void Dispose(bool disposing) { if (disposing) _content.Dispose(); base.Dispose(disposing); }
    }
}
