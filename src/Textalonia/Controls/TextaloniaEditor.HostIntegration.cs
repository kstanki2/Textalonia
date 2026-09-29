using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.Controls;

/// <summary>
/// Presents editor-created modal windows. The host owns the service and parent window; the editor
/// owns each dialog window. An implementation must finish only after its dialog closes, and must
/// cancel without applying edits when the token is canceled. It must not dispose either window.
/// </summary>
public interface IEditorDialogService
{
    Task<TResult> ShowAsync<TResult>(Window dialog, Window owner, CancellationToken cancellationToken);
}

/// <summary>The successful result of a document load or save operation.</summary>
public sealed class DocumentIoCompletedEventArgs(IDocumentFormat format, FlowDocument document, ConversionReport? report) : EventArgs
{
    public IDocumentFormat Format { get; } = format;
    /// <summary>The document loaded or captured when saving began, even if the editor has since changed.</summary>
    public FlowDocument Document { get; } = document;
    /// <summary>Null for the basic load/save methods, or the detailed interchange report.</summary>
    public ConversionReport? Report { get; } = report;
}

/// <summary>Details of an editor view layout that finished successfully.</summary>
public sealed class EditorLayoutCompletedEventArgs(int revision, DocumentViewMode viewMode, int pageCount) : EventArgs
{
    public int Revision { get; } = revision;
    public DocumentViewMode ViewMode { get; } = viewMode;
    public int PageCount { get; } = pageCount;
}

/// <summary>The host and document restrictions that govern editor operations.</summary>
public sealed class EditingModeChangedEventArgs(bool isReadOnly, DocumentProtection protection, EditPolicy policy, EditIdentity identity) : EventArgs
{
    public bool IsReadOnly { get; } = isReadOnly;
    public DocumentProtection Protection { get; } = protection;
    public EditPolicy Policy { get; } = policy;
    public EditIdentity Identity { get; } = identity;
}

public partial class TextaloniaEditor
{
    private IEditorDialogService _dialogService = new AvaloniaEditorDialogService();
    private readonly Dictionary<Window, CancellationTokenSource> _activeDialogs = [];
    private EditingModeChangedEventArgs? _lastEditingMode;
    private EventHandler<EditingModeChangedEventArgs>? _editingModeChanged;
    private (FlowDocument Document, int Revision, DocumentViewMode ViewMode, double Zoom, double Width,
        double PageGap, int PagesPerRow, int PageCount, FontFamily? Font, IBrush? Foreground,
        IBrush? Border, Avalonia.Thickness Padding, long HyphenationRevision)? _lastCompletedLayout;

    /// <summary>Host-owned modal presentation service. The editor never disposes it.</summary>
    public IEditorDialogService DialogService
    {
        get => _dialogService;
        set => _dialogService = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Requests cancellation of active editor dialogs. Call on the UI thread; pending edits are discarded.</summary>
    public void CancelActiveDialogs()
    {
        Dispatcher.UIThread.VerifyAccess();
        foreach (var cancellation in _activeDialogs.Values.ToArray()) cancellation.Cancel();
    }

    /// <summary>Raised after a successful load has replaced the document.</summary>
    public event EventHandler<DocumentIoCompletedEventArgs>? LoadCompleted;
    /// <summary>Raised after a successful save of the captured document.</summary>
    public event EventHandler<DocumentIoCompletedEventArgs>? SaveCompleted;
    /// <summary>Raised when a new document/view geometry has completed layout.</summary>
    public event EventHandler<EditorLayoutCompletedEventArgs>? LayoutCompleted;
    /// <summary>Raised when read-only, protection, host policy or identity changes.</summary>
    public event EventHandler<EditingModeChangedEventArgs> EditingModeChanged
    {
        add { _lastEditingMode ??= CaptureEditingMode(); _editingModeChanged += value; }
        remove => _editingModeChanged -= value;
    }

    private async Task<TResult> ShowEditorDialogAsync<TResult>(Window dialog, Window owner,
        CancellationToken cancellationToken = default, bool cancellationReturnsDefault = true)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeDialogs.Add(dialog, lifetime);
        try { return await DialogService.ShowAsync<TResult>(dialog, owner, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && cancellationReturnsDefault) { return default!; }
        finally { _activeDialogs.Remove(dialog); }
    }

    private bool IsDialogCancellationRequested(Window dialog) =>
        _activeDialogs.TryGetValue(dialog, out var cancellation) && cancellation.IsCancellationRequested;

    private void PublishLoadCompleted(IDocumentFormat format, FlowDocument document, ConversionReport? report) =>
        LoadCompleted?.Invoke(this, new(format, document, report));

    private void PublishSaveCompleted(IDocumentFormat format, FlowDocument document, ConversionReport? report) =>
        SaveCompleted?.Invoke(this, new(format, document, report));

    private EditingModeChangedEventArgs CaptureEditingMode() =>
        new(Session.IsReadOnly, Session.Document.Protection, Session.EditPolicy, Session.Identity);

    private void PublishEditingModeChanged()
    {
        var current = CaptureEditingMode();
        if (_lastEditingMode is null) { _lastEditingMode = current; return; }
        if (_lastEditingMode.IsReadOnly == current.IsReadOnly &&
            Equals(_lastEditingMode.Protection, current.Protection) &&
            Equals(_lastEditingMode.Policy, current.Policy) &&
            Equals(_lastEditingMode.Identity, current.Identity)) return;
        _lastEditingMode = current;
        _editingModeChanged?.Invoke(this, current);
    }

    internal void PublishLayoutCompleted()
    {
        var current = (Session.Document, Session.Revision, ViewMode, Zoom, _surface?.Bounds.Width ?? 0,
            PageGap, PagesPerRow, PageCount, FontFamily, Foreground, BorderBrush, DocumentPadding,
            HyphenationService?.Revision ?? 0);
        if (_lastCompletedLayout == current) return;
        _lastCompletedLayout = current;
        var completed = new EditorLayoutCompletedEventArgs(Session.Revision, ViewMode, PageCount);
        var surface = _surface;
        // EnsureLayout can run inside Render. Notify hosts after that pass so listeners may safely update UI.
        Dispatcher.UIThread.Post(() =>
        {
            if (_lastCompletedLayout != current) return;
            if (!ReferenceEquals(_surface, surface) || TopLevel.GetTopLevel(this) is null)
            { _lastCompletedLayout = null; return; }
            LayoutCompleted?.Invoke(this, completed);
        }, DispatcherPriority.Background);
    }
}

internal sealed class AvaloniaEditorDialogService : IEditorDialogService
{
    public async Task<TResult> ShowAsync<TResult>(Window dialog, Window owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(owner);
        Dispatcher.UIThread.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = cancellationToken.Register(() =>
        {
            void Close() { if (dialog.IsVisible) dialog.Close(); }
            if (Dispatcher.UIThread.CheckAccess()) Close();
            else Dispatcher.UIThread.Post(Close);
        });
        var result = dialog.ShowDialog<TResult>(owner);
        if (cancellationToken.IsCancellationRequested && dialog.IsVisible) dialog.Close();
        var value = await result;
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }
}
