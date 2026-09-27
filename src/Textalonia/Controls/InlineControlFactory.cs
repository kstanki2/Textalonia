using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>
/// Creates view-owned controls for an explicitly registered descriptor type. Calls occur on the
/// UI thread. A control must have no parent and must not be shared between surfaces. Descriptor
/// dimensions govern arrangement; update document data to persist a size change.
/// </summary>
public interface IInlineControlFactory
{
    Control Create(InlineDescriptor descriptor);

    /// <summary>Updates an existing control after immutable descriptor data changes.</summary>
    void Update(Control control, InlineDescriptor descriptor) { }

    /// <summary>
    /// Releases subscriptions and other resources after the control leaves the viewport or its
    /// surface detaches. Each successful Create is paired with one Release.
    /// </summary>
    void Release(Control control)
    {
        if (control is IDisposable disposable) disposable.Dispose();
    }
}

/// <summary>
/// Explicit host registrations only: descriptor strings are never interpreted as CLR type names.
/// Focusable child controls participate in normal tab traversal and receive keyboard input first;
/// surface components leave child-originated keys, text and pointer events to the child.
/// </summary>
public sealed class InlineControlFactoryRegistry
{
    private ImmutableDictionary<string, IInlineControlFactory> _factories =
        ImmutableDictionary.Create<string, IInlineControlFactory>(StringComparer.Ordinal);

    /// <summary>Raised on the registering thread after a registration changes.</summary>
    public event EventHandler? Changed;

    public void Register(string type, IInlineControlFactory factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(factory);
        ImmutableInterlocked.AddOrUpdate(ref _factories, type, factory, (_, _) => factory);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!ImmutableInterlocked.TryRemove(ref _factories, type, out _)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryGet(string type, [NotNullWhen(true)] out IInlineControlFactory? factory)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _factories.TryGetValue(type, out factory);
    }
}
