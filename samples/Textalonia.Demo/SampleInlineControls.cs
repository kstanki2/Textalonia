using Avalonia.Controls;
using Avalonia.Interactivity;
using Textalonia.Controls;
using Textalonia.Model;

namespace Textalonia.Demo;

// State belongs to the descriptor, so viewport recycling and save/load preserve it.
public static class SampleInlineControls
{
    public static InlineDescriptor Counter() => new()
    {
        AltText = "Interactive counter", Width = 130, Height = 36,
        Payload = new ControlInlinePayload("demo.counter")
    };

    public static void Configure(TextaloniaEditor editor)
    {
        var factories = new InlineControlFactoryRegistry();
        factories.Register("demo.counter", new CounterFactory(editor));
        editor.InlineControlFactories = factories;
    }

    private sealed class CounterFactory(TextaloniaEditor editor) : IInlineControlFactory
    {
        private readonly Dictionary<Control, EventHandler<RoutedEventArgs>> _handlers = [];
        public Control Create(InlineDescriptor descriptor)
        {
            var button = new Button();
            EventHandler<RoutedEventArgs> clicked = (_, _) => editor.UpdateInline(descriptor.Id, current =>
            {
                var payload = (ControlInlinePayload)current.Payload;
                var count = payload.Properties.TryGetValue("count", out var text) && int.TryParse(text, out var value) ? value : 0;
                return current with { Payload = payload with { Properties = payload.Properties.SetItem("count", (count + 1).ToString()) } };
            });
            _handlers.Add(button, clicked); button.Click += clicked;
            Update(button, descriptor); return button;
        }
        public void Update(Control control, InlineDescriptor descriptor)
        {
            var payload = (ControlInlinePayload)descriptor.Payload;
            ((Button)control).Content = "Count: " + payload.Properties.GetValueOrDefault("count", "0");
        }
        public void Release(Control control)
        {
            if (_handlers.Remove(control, out var clicked)) ((Button)control).Click -= clicked;
        }
    }
}
