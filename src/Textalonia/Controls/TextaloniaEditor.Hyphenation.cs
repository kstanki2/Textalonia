using Avalonia.Threading;
using Textalonia.Proofing;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    private IHyphenationService? _hyphenationService;

    /// <summary>Optional host dictionary service for discretionary line breaks. The editor does not own it.</summary>
    public IHyphenationService? HyphenationService
    {
        get => _hyphenationService;
        set
        {
            if (ReferenceEquals(value, _hyphenationService)) return;
            if (_hyphenationService is not null) _hyphenationService.Changed -= OnHyphenationChanged;
            _hyphenationService = value;
            if (value is not null) value.Changed += OnHyphenationChanged;
            _surface?.Refresh();
        }
    }

    private void OnHyphenationChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _hyphenationService)) return;
        if (Dispatcher.UIThread.CheckAccess()) _surface?.Refresh();
        else Dispatcher.UIThread.Post(() => { if (ReferenceEquals(sender, _hyphenationService)) _surface?.Refresh(); });
    }
}
