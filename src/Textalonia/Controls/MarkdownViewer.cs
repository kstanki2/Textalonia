using System.Text;
using Avalonia;
using Avalonia.Threading;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.Controls;

/// <summary>A read-only Markdown surface with asynchronous, revision-checked parsing and optional code highlighting.</summary>
public class MarkdownViewer : TextaloniaViewer
{
    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownViewer, string>(nameof(Markdown), "");
    public static readonly StyledProperty<ConversionOptions?> ConversionOptionsProperty =
        AvaloniaProperty.Register<MarkdownViewer, ConversionOptions?>(nameof(ConversionOptions));
    public static readonly StyledProperty<ICodeHighlighter?> CodeHighlighterProperty =
        AvaloniaProperty.Register<MarkdownViewer, ICodeHighlighter?>(nameof(CodeHighlighter));
    public static readonly DirectProperty<MarkdownViewer, bool> IsParsingProperty =
        AvaloniaProperty.RegisterDirect<MarkdownViewer, bool>(nameof(IsParsing), viewer => viewer.IsParsing);
    public static readonly DirectProperty<MarkdownViewer, Exception?> ParseErrorProperty =
        AvaloniaProperty.RegisterDirect<MarkdownViewer, Exception?>(nameof(ParseError), viewer => viewer.ParseError);
    public static readonly DirectProperty<MarkdownViewer, ConversionReport> ParseReportProperty =
        AvaloniaProperty.RegisterDirect<MarkdownViewer, ConversionReport>(nameof(ParseReport), viewer => viewer.ParseReport);
    public static readonly DirectProperty<MarkdownViewer, bool> IsHighlightingProperty =
        AvaloniaProperty.RegisterDirect<MarkdownViewer, bool>(nameof(IsHighlighting), viewer => viewer.IsHighlighting);
    public static readonly DirectProperty<MarkdownViewer, Exception?> HighlightErrorProperty =
        AvaloniaProperty.RegisterDirect<MarkdownViewer, Exception?>(nameof(HighlightError), viewer => viewer.HighlightError);

    private bool _isParsing, _isHighlighting, _detached, _initialized, _applyingSource;
    private Exception? _parseError, _highlightError;
    private ConversionReport _parseReport = ConversionReport.Empty;
    private long _parseRevision, _highlightRevision;
    private CancellationTokenSource? _parseCancellation, _highlightCancellation;
    private Task _parseTask = Task.CompletedTask, _highlightTask = Task.CompletedTask;
    private FlowDocument? _presentation, _presentationSource;
    private readonly CodeHighlightCache _highlightCache = new();

    public MarkdownViewer() => _initialized = true;

    public string Markdown { get => GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public ConversionOptions? ConversionOptions { get => GetValue(ConversionOptionsProperty); set => SetValue(ConversionOptionsProperty, value); }
    public ICodeHighlighter? CodeHighlighter { get => GetValue(CodeHighlighterProperty); set => SetValue(CodeHighlighterProperty, value); }
    public bool IsParsing => _isParsing;
    public Exception? ParseError => _parseError;
    public ConversionReport ParseReport => _parseReport;
    public bool IsHighlighting => _isHighlighting;
    public Exception? HighlightError => _highlightError;
    internal override FlowDocument PresentationDocument => ReferenceEquals(_presentationSource, Document) ? _presentation ?? Document : Document;
    internal CodeHighlightCache HighlightCache => _highlightCache;

    /// <summary>Replace source and await this parse attempt. Failures are exposed through ParseError and ParseReport.</summary>
    public Task UpdateMarkdownAsync(string markdown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        cancellationToken.ThrowIfCancellationRequested();
        Markdown = markdown;
        return WaitForParsingAsync(cancellationToken);
    }

    /// <summary>Append source. Bursts coalesce; selection and the reader's viewport are preserved without following the tail.</summary>
    public void AppendMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        Markdown += markdown;
    }

    /// <summary>Wait for the currently scheduled parse. Canceling the wait does not cancel the bound source update.</summary>
    public Task WaitForParsingAsync(CancellationToken cancellationToken = default) => _parseTask.WaitAsync(cancellationToken);
    /// <summary>Wait for the currently scheduled highlighting pass; call after waiting for parsing.</summary>
    public Task WaitForHighlightingAsync(CancellationToken cancellationToken = default) => _highlightTask.WaitAsync(cancellationToken);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == ConversionOptionsProperty) QueueParse();
        else if (change.Property == CodeHighlighterProperty)
        { _highlightCache.Clear(); QueueHighlight(); }
        else if (change.Property == DocumentProperty && _initialized && !_applyingSource) QueueHighlight();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _detached = true; _parseRevision++; _highlightRevision++;
        _parseCancellation?.Cancel(); _highlightCancellation?.Cancel();
        _presentation = null; _presentationSource = null; _highlightCache.Clear();
        SetAndRaise(IsParsingProperty, ref _isParsing, false);
        SetAndRaise(IsHighlightingProperty, ref _isHighlighting, false);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_detached) { _detached = false; QueueParse(); }
    }

    private void QueueParse()
    {
        var revision = ++_parseRevision;
        _parseCancellation?.Cancel(); _parseCancellation?.Dispose();
        _parseCancellation = new();
        _highlightRevision++; _highlightCancellation?.Cancel();
        SetAndRaise(IsHighlightingProperty, ref _isHighlighting, false);
        SetAndRaise(ParseErrorProperty, ref _parseError, null);
        SetAndRaise(ParseReportProperty, ref _parseReport, ConversionReport.Empty);
        SetAndRaise(IsParsingProperty, ref _isParsing, true);
        if (_detached)
        { SetAndRaise(IsParsingProperty, ref _isParsing, false); _parseTask = Task.CompletedTask; return; }
        _parseTask = ParseLatestAsync(Markdown ?? "", ConversionOptions, Document, Session.Revision, revision, _parseCancellation.Token);
    }

    /// <summary>Runs on a worker thread. Overrides must not access Avalonia controls and should honor cancellation.</summary>
    protected virtual async Task<DocumentLoadResult> ParseMarkdownAsync(string markdown, ConversionOptions? options, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown), writable: false);
        return await new MarkdownDocumentFormat().LoadWithReportAsync(stream, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task ParseLatestAsync(string source, ConversionOptions? options, FlowDocument previous, int documentRevision,
        long revision, CancellationToken token)
    {
        try
        {
            // A short quiet period keeps a burst of streamed chunks from starting one parser per chunk.
            await Task.Delay(15, token).ConfigureAwait(false);
            var result = await Task.Run(async () =>
            {
                var parsed = await ParseMarkdownAsync(source, options, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return parsed with { Document = MarkdownDocumentReconciler.Reuse(previous, parsed.Document, token) };
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision != _parseRevision || token.IsCancellationRequested) return;
                if (Session.Revision != documentRevision)
                {
                    SetAndRaise(ParseErrorProperty, ref _parseError, new InvalidOperationException("The document changed while Markdown was parsing."));
                    SetAndRaise(IsParsingProperty, ref _isParsing, false);
                    return;
                }
                var before = Session.Index.Text;
                var after = result.Document.Text;
                var anchor = MarkdownDocumentReconciler.MapPosition(before, after, Session.Selection.Anchor);
                var active = MarkdownDocumentReconciler.MapPosition(before, after, Session.Selection.Active);
                _presentation = null; _presentationSource = null;
                _applyingSource = true;
                try { ReplaceDocumentPreservingView(result.Document, anchor, active); }
                finally { _applyingSource = false; }
                SetAndRaise(ParseReportProperty, ref _parseReport, result.Report);
                SetAndRaise(IsParsingProperty, ref _isParsing, false);
                QueueHighlight();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision != _parseRevision || token.IsCancellationRequested) return;
                SetAndRaise(ParseErrorProperty, ref _parseError, error);
                SetAndRaise(ParseReportProperty, ref _parseReport, error is DocumentConversionException conversion ? conversion.Report : ConversionReport.Empty);
                SetAndRaise(IsParsingProperty, ref _isParsing, false);
            });
        }
    }

    private void QueueHighlight()
    {
        var revision = ++_highlightRevision;
        _highlightCancellation?.Cancel(); _highlightCancellation?.Dispose();
        _highlightCancellation = new();
        // Returning to an older canonical index would reset its mutable geometry cache.
        // Keep a fresh unstyled presentation snapshot sharing the current index branches.
        _presentation = _presentation is not null && ReferenceEquals(_presentationSource, Document)
            ? DocumentTree.ReuseSnapshot(_presentation, Document, CancellationToken.None) : null;
        _presentationSource = _presentation is null ? null : Document;
        RefreshPresentation();
        SetAndRaise(HighlightErrorProperty, ref _highlightError, null);
        var adapter = _detached ? null : CodeHighlighter;
        SetAndRaise(IsHighlightingProperty, ref _isHighlighting, adapter is not null);
        _highlightTask = adapter is null ? Task.CompletedTask : HighlightLatestAsync(Document, adapter, revision, _highlightCancellation.Token);
    }

    private async Task HighlightLatestAsync(FlowDocument document, ICodeHighlighter adapter, long revision, CancellationToken token)
    {
        try
        {
            var presentation = await Task.Run(() => _highlightCache.HighlightAsync(document, adapter, token), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision != _highlightRevision || token.IsCancellationRequested || !ReferenceEquals(document, Document)) return;
                _presentation = presentation; _presentationSource = document;
                SetAndRaise(IsHighlightingProperty, ref _isHighlighting, false);
                RefreshPresentation();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision != _highlightRevision || token.IsCancellationRequested || !ReferenceEquals(document, Document)) return;
                SetAndRaise(HighlightErrorProperty, ref _highlightError, error);
                SetAndRaise(IsHighlightingProperty, ref _isHighlighting, false);
            });
        }
    }
}
