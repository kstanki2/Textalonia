using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Demo;

/// <summary>A protected form authored with the public content-control API.</summary>
public sealed class FormWindow : Window
{
    public FormWindow()
    {
        Title = "Protected form"; Width = 1000; Height = 760;
        var editor = new TextaloniaEditor { Document = CreateDocument(), PdfExporter = new Textalonia.Pdf.Skia.PdfExporter() };
        var instructions = new TextBlock { Text = "Fill the fields with Tab and Shift+Tab. Use Space for consent, Enter for a list or date, and the Forms menu to edit any value. Document labels are protected.", TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { Text = "Form filling is enabled. Export PDF from Output to print the completed values.", TextWrapping = TextWrapping.Wrap };
        editor.OperationFailed += (_, args) => status.Text = args.Exception.Message;
        var layout = new Grid { Margin = new Thickness(16), RowDefinitions = new("Auto,*,Auto"), RowSpacing = 10 };
        layout.Children.Add(instructions); Grid.SetRow(editor, 1); layout.Children.Add(editor); Grid.SetRow(status, 2); layout.Children.Add(status);
        Content = layout;
        Opened += (_, _) => { editor.SelectNextContentControl(); editor.FocusDocument(); };
    }

    internal static FlowDocument CreateDocument()
    {
        var session = new EditorSession(FlowDocument.FromText("Registration form\nName: <name>\nNotes: <notes>\nPlan: <plan>\nContact preference: <contact>\nStart date: <date>\nI agree: <consent>"));
        Add("<name>", new() { Kind = ContentControlKind.PlainText, Title = "Name", Value = "Enter your name", LockControl = true });
        Add("<notes>", new() { Kind = ContentControlKind.RichText, Title = "Notes", Value = "Add a note", LockControl = true });
        Add("<plan>", new() { Kind = ContentControlKind.DropDown, Title = "Plan", Placeholder = "Choose a plan", LockControl = true,
            Items = [new("Standard", "standard"), new("Extended", "extended")] });
        Add("<contact>", new() { Kind = ContentControlKind.ComboBox, Title = "Contact preference", Placeholder = "Choose or enter a preference", LockControl = true,
            Items = [new("Email", "email"), new("Telephone", "telephone")] });
        Add("<date>", new() { Kind = ContentControlKind.Date, Title = "Start date", Placeholder = "yyyy-MM-dd", LockControl = true });
        Add("<consent>", new() { Kind = ContentControlKind.CheckBox, Title = "Consent", LockControl = true });
        return session.Document with { Protection = new() { Mode = DocumentProtectionMode.FormsOnly } };

        void Add(string marker, DocumentContentControl control)
        {
            var start = session.Index.Text.IndexOf(marker, StringComparison.Ordinal);
            session.Select(start, start + marker.Length); session.InsertContentControl(control);
        }
    }
}
