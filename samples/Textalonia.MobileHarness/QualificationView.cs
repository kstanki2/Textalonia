using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Model;

namespace Textalonia.MobileHarness;

/// <summary>Shared, workload-free mobile qualification screen; native launchers are separate projects.</summary>
public sealed class QualificationView : UserControl
{
    private readonly TextaloniaEditor _editor = new() { ShowToolbar = false, MinHeight = 80 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    public QualificationView()
    {
        var factories = new InlineControlFactoryRegistry();
        factories.Register("qualification.focus", new FocusFactory());
        _editor.InlineControlFactories = factories;
        _editor.Document = Fixture();
        AutomationProperties.SetName(_editor, "Mobile qualification document");
        var actions = new WrapPanel();
        foreach (var (label, command) in new[]
        {
            ("Select all", _editor.SelectAllCommand), ("Copy", _editor.CopyCommand),
            ("Cut", _editor.CutCommand), ("Paste", _editor.PasteCommand), ("Undo", _editor.UndoCommand)
        }) actions.Children.Add(new Button { Content = label, Command = command, MinHeight = 44, Margin = new Thickness(2) });
        var selectWord = new Button { Content = "Select word", MinHeight = 44, Margin = new Thickness(2) };
        selectWord.Click += (_, _) =>
        {
            var caret = _editor.Session.Selection.Active;
            _editor.Session.Select(_editor.Session.PreviousWord(caret), _editor.Session.NextWord(caret));
        };
        actions.Children.Add(selectWord);
        var readOnly = new CheckBox { Content = "Read only", MinHeight = 44 };
        readOnly.IsCheckedChanged += (_, _) => _editor.IsReadOnly = readOnly.IsChecked == true;
        var reset = new Button { Content = "Reset fixture", MinHeight = 44 };
        reset.Click += (_, _) => _editor.Document = Fixture();
        actions.Children.Add(readOnly); actions.Children.Add(reset);
        var otherFocus = new TextBox { PlaceholderText = "Other focus / keyboard target", MinHeight = 44 };
        AutomationProperties.SetName(otherFocus, "Other keyboard target");
        var header = new StackPanel { Spacing = 4, Children =
        {
            new TextBlock { Text = "Experimental mobile qualification", FontSize = 18 },
            actions, otherFocus, _status
        }};
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(8) };
        grid.Children.Add(header); grid.Children.Add(_editor); Grid.SetRow(_editor, 1);
        Content = grid;
        _editor.SelectionChanged += (_, _) => UpdateStatus();
        _editor.DocumentChanged += (_, _) => UpdateStatus();
        _editor.SizeChanged += (_, _) => UpdateStatus();
        AttachedToVisualTree += (_, _) => UpdateStatus();
    }

    private void UpdateStatus()
    {
        var selection = _editor.Session.Selection;
        _status.Text = $"Selection UTF-16 {selection.Anchor} → {selection.Active}; revision {_editor.Session.Revision}; " +
            $"editor {_editor.Bounds.Width:F0} × {_editor.Bounds.Height:F0} DIP; scale {TopLevel.GetTopLevel(this)?.RenderScaling:F2}";
    }

    private static FlowDocument Fixture()
    {
        var blocks = new List<Block>
        {
            new Paragraph("Long press a word, then cross either handle. Tap for a caret handle."),
            new Paragraph("Latin العربية עברית 123 punctuation ! e\u0301 👩‍💻"),
            new Paragraph([new RichRun("Embedded keyboard target: "), new RichRun(new InlineDescriptor
            {
                Width = 190, Height = 44, AltText = "Embedded keyboard target",
                Payload = new ControlInlinePayload("qualification.focus")
            })])
        };
        var table = Table.Create(2, 2);
        table = table.SetCell(0, 0, new TableCell { Blocks = [new Paragraph("Touch selection inside a table cell")] });
        table = table.SetCell(0, 1, new TableCell { Blocks = [new Paragraph("Latin العربية עברית 123")] });
        var nested = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("Nested cell selection")] });
        table = table.SetCell(1, 0, new TableCell { Blocks = [nested] });
        blocks.Add(table);
        blocks.AddRange(Enumerable.Range(1, 80).Select(n => new Paragraph($"Paragraph {n}: Scroll here, open the keyboard, select text, and compose 中文 日本 한글.")));
        return new FlowDocument(blocks);
    }

    private sealed class FocusFactory : IInlineControlFactory
    {
        public Control Create(InlineDescriptor descriptor)
        {
            var control = new TextBox { PlaceholderText = "Embedded focus" };
            AutomationProperties.SetName(control, descriptor.AltText);
            return control;
        }
        public void Update(Control control, InlineDescriptor descriptor) { }
        public void Release(Control control) { }
    }
}
