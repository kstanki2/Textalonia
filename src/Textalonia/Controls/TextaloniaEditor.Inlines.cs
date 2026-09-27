using Avalonia;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    public static readonly StyledProperty<IInlineResourceResolver?> InlineResourceResolverProperty =
        AvaloniaProperty.Register<TextaloniaEditor, IInlineResourceResolver?>(nameof(InlineResourceResolver));
    public static readonly StyledProperty<InlineImageOptions?> InlineImageOptionsProperty =
        AvaloniaProperty.Register<TextaloniaEditor, InlineImageOptions?>(nameof(InlineImageOptions));
    public static readonly StyledProperty<InlineControlFactoryRegistry?> InlineControlFactoriesProperty =
        AvaloniaProperty.Register<TextaloniaEditor, InlineControlFactoryRegistry?>(nameof(InlineControlFactories));

    /// <summary>Host resource access. The default serves embedded bytes only.</summary>
    public IInlineResourceResolver? InlineResourceResolver
    { get => GetValue(InlineResourceResolverProperty); set => SetValue(InlineResourceResolverProperty, value); }
    public InlineImageOptions? InlineImageOptions
    { get => GetValue(InlineImageOptionsProperty); set => SetValue(InlineImageOptionsProperty, value); }
    /// <summary>Explicit factories for trusted descriptor types. Unknown types display alt text.</summary>
    public InlineControlFactoryRegistry? InlineControlFactories
    { get => GetValue(InlineControlFactoriesProperty); set => SetValue(InlineControlFactoriesProperty, value); }

    public void InsertInline(InlineDescriptor descriptor, DocumentResource? resource = null) => Session.InsertInline(descriptor, resource);
    public void UpdateInline(Guid id, Func<InlineDescriptor, InlineDescriptor> change) => Session.UpdateInline(id, change);
}
