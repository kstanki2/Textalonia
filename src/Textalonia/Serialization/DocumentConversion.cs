using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Serialization;

public enum ConversionDiagnosticSeverity { Information, Warning, Error }
public enum ConversionMode { Tolerant, Strict }

/// <summary>A deterministic explanation of content omitted or approximated by a codec.</summary>
public sealed record ConversionDiagnostic(
    string Code,
    ConversionDiagnosticSeverity Severity,
    string UnsupportedFeature,
    string Fallback,
    Guid? ModelId = null,
    string? SourceLocation = null);

public sealed class ConversionReport
{
    public static ConversionReport Empty { get; } = new([]);
    public ImmutableArray<ConversionDiagnostic> Diagnostics { get; }
    public bool HasLoss => Diagnostics.Any(d => d.Severity != ConversionDiagnosticSeverity.Information);
    public ConversionReport(IEnumerable<ConversionDiagnostic> diagnostics) => Diagnostics = diagnostics.ToImmutableArray();
}

public sealed record ConversionOptions
{
    /// <summary>Strict conversion throws when any loss or unverified custom codec is reported.</summary>
    public ConversionMode Mode { get; init; }
    /// <summary>Explicitly flatten the successfully parsed document or export snapshot to plain text.</summary>
    public bool PlainTextOnly { get; init; }
}

public sealed record DocumentLoadResult(FlowDocument Document, ConversionReport Report);
public sealed record DocumentSaveResult(ConversionReport Report);

public sealed class DocumentConversionException : InvalidOperationException
{
    public ConversionReport Report { get; }
    public DocumentConversionException(ConversionReport report)
        : base("Strict conversion rejected content loss: " + string.Join(", ", report.Diagnostics.Select(d => d.Code).Distinct())) => Report = report;
}

/// <summary>Optional reporting contract for custom codecs. Legacy IDocumentFormat implementations remain valid.</summary>
public interface IReportingDocumentFormat : IDocumentFormat
{
    Task<DocumentLoadResult> LoadWithReportAsync(Stream stream, ConversionOptions? options = null, CancellationToken cancellationToken = default);
    Task<DocumentSaveResult> SaveWithReportAsync(FlowDocument document, Stream stream, ConversionOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Additive conversion API. Streams remain caller-owned. Export is staged before writing, so strict
/// rejection leaves the destination untouched. Cancellation or an I/O failure during the final copy
/// can leave a partial write; use a temporary file and atomic replacement for transactional saves.
/// </summary>
public static class DocumentFormatExtensions
{
    public static async Task<DocumentLoadResult> LoadWithReportAsync(this IDocumentFormat format, Stream stream,
        ConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(stream);
        options = ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var diagnostics = ConversionDiagnostics.Begin();
        FlowDocument document;
        if (format is IReportingDocumentFormat reporting)
        {
            var result = await reporting.LoadWithReportAsync(stream, options with { Mode = ConversionMode.Tolerant, PlainTextOnly = false }, cancellationToken);
            document = result.Document;
            diagnostics.AddRange(result.Report.Diagnostics);
        }
        else
        {
            ReportLegacyFormat(format);
            document = await format.LoadAsync(stream, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        document.Validate();
        if (options.PlainTextOnly) document = Flatten(document);
        cancellationToken.ThrowIfCancellationRequested();
        var report = diagnostics.ToReport();
        RejectLoss(options, report);
        return new(document, report);
    }

    public static async Task<DocumentSaveResult> SaveWithReportAsync(this IDocumentFormat format, FlowDocument document,
        Stream stream, ConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        options = ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        document.Validate();
        using var diagnostics = ConversionDiagnostics.Begin();
        if (options.PlainTextOnly) document = Flatten(document);
        ReportExportLosses(format, document);
        using var buffer = new MemoryStream();
        if (format is IReportingDocumentFormat reporting)
        {
            var result = await reporting.SaveWithReportAsync(document, buffer, options with { Mode = ConversionMode.Tolerant, PlainTextOnly = false }, cancellationToken);
            diagnostics.AddRange(result.Report.Diagnostics);
        }
        else
        {
            ReportLegacyFormat(format);
            await format.SaveAsync(document, buffer, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var report = diagnostics.ToReport();
        RejectLoss(options, report);
        buffer.Position = 0;
        await buffer.CopyToAsync(stream, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(report);
    }

    internal static void ReportExportLosses(IDocumentFormat format, FlowDocument document)
    {
        if (format is HtmlDocumentFormat or RtfDocumentFormat or DocxDocumentFormat or MarkdownDocumentFormat)
        {
            ReportMergeHistory(document.Blocks);
            ReportUnusedResources(document, format is DocxDocumentFormat);
        }
        if (format is PlainTextDocumentFormat) ReportPlainTextLoss(document);
        if (format is HtmlDocumentFormat or MarkdownDocumentFormat)
            foreach (var inline in new DocumentIndex(document).Paragraphs.SelectMany(p => p.Paragraph.Runs)
                .Select(r => r.Inline).OfType<InlineDescriptor>().Where(inline => inline.Payload is MergeFieldInlinePayload))
                ReportMergeFieldLoss(inline, format is HtmlDocumentFormat ? "html" : "markdown");
        if (format is HtmlDocumentFormat or RtfDocumentFormat or DocxDocumentFormat)
            ReportIntegrationSemantics(document.Blocks);
    }

    private static void ReportIntegrationSemantics(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
            switch (block)
            {
                case Paragraph paragraph:
                    if (paragraph.DefaultStyle.IsCode || paragraph.Runs.Any(run => run.Style.IsCode))
                        ConversionDiagnostics.Report("conversion.inline-code", "Inline code semantics",
                            "Supported visual formatting is retained, but the code annotation is omitted.", paragraph.Id);
                    break;
                case Section section:
                    if (section.Semantic != SectionSemantic.None)
                        ConversionDiagnostics.Report("conversion.section-semantic", "Quote or fenced code semantics and language",
                            "Supported section appearance and text are retained; semantic annotations are omitted.", section.Id);
                    ReportIntegrationSemantics(section.Blocks);
                    break;
                case Table table:
                    for (var row = 0; row < table.Rows.Length; row++)
                        for (var column = 0; column < table.ColumnCount; column++)
                            if (!table.IsCovered(row, column)) ReportIntegrationSemantics(table.Rows[row][column].Blocks);
                    break;
            }
    }
    private static ConversionOptions ValidateOptions(ConversionOptions? options)
    {
        options ??= new();
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options));
        return options;
    }

    private static void RejectLoss(ConversionOptions options, ConversionReport report)
    {
        if (options.Mode == ConversionMode.Strict && report.HasLoss) throw new DocumentConversionException(report);
    }

    private static void ReportLegacyFormat(IDocumentFormat format)
    {
        if (format is not (JsonDocumentFormat or PlainTextDocumentFormat or HtmlDocumentFormat or RtfDocumentFormat or DocxDocumentFormat or XamlDocumentFormat or MarkdownDocumentFormat))
            ConversionDiagnostics.Report("conversion.diagnostics-unavailable", "Custom codec fidelity is unknown",
                "The legacy codec was used; implement IReportingDocumentFormat to provide loss diagnostics.");
    }

    private static FlowDocument Flatten(FlowDocument document)
    {
        ReportPlainTextLoss(document);
        ConversionDiagnostics.Report("conversion.plain-text-requested", "Explicit plain-text conversion",
            "Only paragraph text and inline alternative text are retained.", severity: ConversionDiagnosticSeverity.Information);
        return FlowDocument.FromText(document.PlainText);
    }

    private static void ReportPlainTextLoss(FlowDocument document)
    {
        StyleConversion.ReportLosses(DocumentFormats.PlainText, document);
        void Visit(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
                switch (block)
                {
                    case Paragraph p:
                        if (p.Style != ParagraphStyle.Default || p.DefaultStyle != TextStyle.Default || p.Runs.Any(r => r.Style != TextStyle.Default))
                            ConversionDiagnostics.Report("text.formatting", "Paragraph or character formatting", "Formatting is discarded; text is retained.", p.Id);
                        foreach (var inline in p.Runs.Select(r => r.Inline).OfType<InlineDescriptor>())
                            if (inline.Payload is MergeFieldInlinePayload) ReportMergeFieldLoss(inline, "text");
                            else ConversionDiagnostics.Report("text.inline", "Inline content", "The inline object is replaced by its alternative text.", inline.Id);
                        break;
                    case Section section:
                        ConversionDiagnostics.Report("text.section", "Section structure and decoration", "Visible paragraphs are flattened.", section.Id);
                        Visit(section.Blocks);
                        break;
                    case Table table:
                        ConversionDiagnostics.Report("text.table", "Table structure and geometry", "Visible cell paragraphs are flattened in row order.", table.Id);
                        for (var r = 0; r < table.Rows.Length; r++)
                            for (var c = 0; c < table.ColumnCount; c++)
                                if (!table.IsCovered(r, c)) Visit(table.Rows[r][c].Blocks);
                        break;
                }
        }
        Visit(document.Blocks);
        if (document.Resources.Count != 0)
            ConversionDiagnostics.Report("text.resources", "Document resources", "Resource descriptors and embedded data are discarded.");
    }

    private static void ReportMergeFieldLoss(InlineDescriptor inline, string format) =>
        ConversionDiagnostics.Report(format + ".merge-field", "Live mail-merge field definition",
            "The field is replaced by its display text and can no longer be merged. Use native JSON, Textalonia XAML, DOCX, or RTF to retain merge fields, or merge the document before exporting.", inline.Id);

    private static void ReportUnusedResources(FlowDocument document, bool includeFonts)
    {
        var visible = new HashSet<string>(new DocumentIndex(document).Paragraphs.SelectMany(p => p.Paragraph.Runs)
            .Select(r => r.Inline?.Payload).OfType<ImageInlinePayload>().Select(p => p.ResourceId), StringComparer.Ordinal);
        if (includeFonts) visible.UnionWith(document.Fonts.Select(font => font.ResourceId));
        foreach (var resource in document.Resources.Keys.Order(StringComparer.Ordinal))
            if (!visible.Contains(resource))
                ConversionDiagnostics.Report("conversion.unused-resource", "Resource without a visible image reference",
                    "Only resources referenced by visible images can be transferred by this format.", sourceLocation: "resource:" + resource);
    }
    private static void ReportMergeHistory(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
            if (block is Section section) ReportMergeHistory(section.Blocks);
            else if (block is Table table)
            {
                if (table.Rows.SelectMany((row, r) => row.Select((cell, c) => (cell, r, c))).Any(item =>
                    !item.cell.MergeOriginalBlocks.IsEmpty || table.IsCovered(item.r, item.c) &&
                    (item.cell.Background is not null || item.cell.Padding is not null || item.cell.Borders is not null ||
                    item.cell.Blocks.Length != 1 || item.cell.Blocks[0] is not Paragraph { Length: 0, Style: var style, DefaultStyle: var textStyle } ||
                    style != ParagraphStyle.Default || textStyle != TextStyle.Default)))
                    ConversionDiagnostics.Report("conversion.merge-history", "Hidden cells and merge restoration history",
                        "Only visible cells and supported merge geometry are exported; use native storage to preserve split restoration.", table.Id);
                for (var r = 0; r < table.Rows.Length; r++)
                    for (var c = 0; c < table.ColumnCount; c++)
                        if (!table.IsCovered(r, c)) ReportMergeHistory(table.Rows[r][c].Blocks);
            }
    }
}

// A scope follows async execution (including Task.Run) without sharing reports across concurrent conversions.
// Legacy Parse/Serialize remain source compatible and do not allocate a report.
internal static class ConversionDiagnostics
{
    private static readonly AsyncLocal<Scope?> Current = new();
    internal static Scope Begin() => new();
    internal static void Report(string code, string feature, string fallback, Guid? modelId = null,
        string? sourceLocation = null, ConversionDiagnosticSeverity severity = ConversionDiagnosticSeverity.Warning) =>
        Current.Value?.Add(new(code, severity, feature, fallback, modelId, sourceLocation));

    internal sealed class Scope : IDisposable
    {
        private const int MaximumDiagnostics = 1024;
        private readonly Scope? _previous = Current.Value;
        private readonly List<ConversionDiagnostic> _items = [];
        private readonly HashSet<ConversionDiagnostic> _seen = [];
        internal Scope() => Current.Value = this;
        internal void Add(ConversionDiagnostic diagnostic)
        {
            lock (_items)
            {
                if (_items.Count > MaximumDiagnostics || !_seen.Add(diagnostic)) return;
                if (_items.Count == MaximumDiagnostics)
                    _items.Add(new("conversion.diagnostics-truncated", ConversionDiagnosticSeverity.Warning,
                        "Additional conversion losses", "The report is limited to 1024 distinct diagnostics."));
                else _items.Add(diagnostic);
            }
        }
        internal void AddRange(IEnumerable<ConversionDiagnostic> diagnostics)
        {
            foreach (var diagnostic in diagnostics) Add(diagnostic);
        }
        internal ConversionReport ToReport() { lock (_items) return new(_items); }
        public void Dispose() => Current.Value = _previous;
    }
}
