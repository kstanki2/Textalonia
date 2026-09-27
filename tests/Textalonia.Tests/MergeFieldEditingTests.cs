using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Textalonia.Controls;
using Textalonia.Demo;
using Textalonia.Editing;
using Textalonia.MailMerge;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class MergeFieldEditingTests
{
    [Fact]
    public void Insert_and_update_preserve_atomic_position_style_identity_and_undo()
    {
        var session = new EditorSession(FlowDocument.FromText("Hello !"));
        session.Select(6, 6);
        session.ApplyStyle(style => style with { Bold = true, Foreground = "#123456" });
        session.InsertMergeField("Name");
        var inserted = session.CurrentMergeField!;
        Assert.Equal("Hello \uFFFC!", session.Document.Text);
        Assert.Equal("Hello «Name»!", session.Document.PlainText);
        Assert.Equal(7, session.Selection.Active);
        session.UpdateMergeField(inserted.Id, "CustomerName", "N2", "Customer");
        var updated = session.CurrentMergeField!;
        Assert.Equal(inserted.Id, updated.Id);
        Assert.Equal(new MergeFieldInlinePayload("CustomerName") { Format = "N2", FallbackText = "Customer" }, updated.Payload);
        Assert.Equal("«CustomerName»", updated.AltText);
        var run = session.Index.At(6).Paragraph.Runs.Single(item => item.Inline?.Id == updated.Id);
        Assert.True(run.Style.Bold); Assert.Equal("#123456", run.Style.Foreground);
        session.Undo(); Assert.Equal(inserted, session.CurrentMergeField);
        session.Redo(); Assert.Equal(updated, session.CurrentMergeField);
        session.Undo(); session.Undo(); Assert.Equal("Hello !", session.Document.Text);
        session.Redo(); Assert.Equal(inserted, session.CurrentMergeField);
    }

    [Fact]
    public void Copy_paste_remaps_field_identity_preserves_definition_and_deletes_one_token()
    {
        var session = new EditorSession();
        session.InsertMergeField("Total", "C2", "Unavailable");
        var original = session.CurrentMergeField!;
        session.SelectAll();
        Assert.Equal("«Total»", session.SelectedText);
        var fragment = session.CopyFragment();
        session.Select(1, 1); session.InsertFragment(fragment);
        Assert.Equal(2, session.Index.Length);
        var pasted = session.CurrentMergeField!;
        Assert.NotEqual(original.Id, pasted.Id); Assert.Equal(original.Payload, pasted.Payload);
        session.DeleteBackward(); Assert.Equal(1, session.Index.Length);
        Assert.Equal(original.Id, session.CurrentMergeField!.Id);
        session.Undo(); Assert.Equal(2, session.Index.Length);
        session.Select(0, 0); session.DeleteForward();
        Assert.Equal(pasted.Id, session.CurrentMergeField!.Id);
    }

    [Fact]
    public void Read_only_field_commands_do_not_modify_the_document_or_history()
    {
        var session = new EditorSession(); session.InsertMergeField("Name");
        var before = session.Document; var field = session.CurrentMergeField!;
        session.IsReadOnly = true;
        session.InsertMergeField("Other"); session.UpdateMergeField(field.Id, "Changed");
        session.DeleteBackward();
        Assert.Same(before, session.Document); Assert.False(session.CanUndo);
        session.IsReadOnly = false; session.Undo(); Assert.Equal("", session.Document.Text);
    }

    [Fact]
    public void Selection_lookup_does_not_treat_surrounding_text_or_multiple_tokens_as_one_field()
    {
        var field = MergeFields.Create("Name");
        var session = new EditorSession(new FlowDocument([new Paragraph([new RichRun("ab"), new RichRun(field), new RichRun("cd")])]));
        session.Select(0, 0); Assert.Null(session.CurrentMergeField);
        session.Select(2, 2); Assert.Equal(field, session.CurrentMergeField);
        session.Select(3, 3); Assert.Equal(field, session.CurrentMergeField);
        session.Select(2, 3); Assert.Equal(field, session.CurrentMergeField);
        session.Select(1, 3); Assert.Null(session.CurrentMergeField);
        session.Select(4, 4); Assert.Null(session.CurrentMergeField);
    }

    [Fact]
    public void Fields_inside_table_cells_are_editable_and_undoable()
    {
        var session = new EditorSession();
        session.InsertTable(1, 1);
        session.InsertMergeField("Name");
        var field = session.CurrentMergeField!;
        var table = session.CurrentCell()!.Value.Table;
        session.UpdateMergeField(field.Id, "Company", fallbackText: "Individual");
        Assert.Equal(table.Id, session.CurrentCell()!.Value.Table.Id);
        Assert.Equal("Company", ((MergeFieldInlinePayload)session.CurrentMergeField!.Payload).Name);
        session.Undo(); Assert.Equal(field, session.CurrentMergeField);
        session.Document.Validate();
    }

    [Fact]
    public void Update_rejects_missing_or_nonfield_inline_identity_without_mutation()
    {
        var inline = new InlineDescriptor { Payload = new ControlInlinePayload("sample") };
        var session = new EditorSession(new FlowDocument([new Paragraph([new RichRun(inline)])]));
        var before = session.Document;
        Assert.Throws<ArgumentException>(() => session.UpdateMergeField(inline.Id, "Name"));
        Assert.Throws<ArgumentException>(() => session.UpdateMergeField(Guid.NewGuid(), "Name"));
        Assert.Same(before, session.Document); Assert.False(session.CanUndo);
    }

    [Fact]
    public void Demo_records_are_typed_and_can_preview_and_generate_without_mutating_template()
    {
        var template = MailMergeWindow.CreateTemplate();
        var records = MailMergeWindow.ParseRecords("""
            [{"CustomerName":"Alex","Balance":1250.5,"DueDate":"2026-10-15"},
             {"CustomerName":"Sam","Company":"Contoso","Balance":87.25,"DueDate":"2026-10-20"}]
            """);
        Assert.IsType<DateTime>(records[0]["DueDate"]); Assert.IsType<decimal>(records[0]["Balance"]);
        var options = new MailMergeOptions { Culture = CultureInfo.GetCultureInfo("en-US") };
        var preview = MailMergeProcessor.Preview(template, records[0], options);
        Assert.Contains("Alex", preview.PlainText);
        Assert.Contains("$1,250.50", preview.PlainText);
        Assert.Contains("October 15, 2026", preview.PlainText);
        Assert.Contains("Individual customer", preview.PlainText);
        Assert.Equal(4, MailMergeProcessor.GetFieldNames(preview).Length);
        var outputs = MailMergeProcessor.MergeMany(template, records, options).ToArray();
        Assert.Equal(2, outputs.Length); Assert.Contains("Sam", outputs[1].PlainText);
        Assert.All(outputs, output => Assert.Empty(MailMergeProcessor.GetFieldNames(output)));
        Assert.Contains("«CustomerName»", template.PlainText);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[{\"Name\":{},\"Balance\":2}]")]
    [InlineData("[{\"Name\":\"one\",\"Name\":\"two\"}]")]
    public void Demo_rejects_empty_nested_or_duplicate_recipient_data(string json) =>
        Assert.Throws<FormatException>(() => MailMergeWindow.ParseRecords(json));
}

public class MergeFieldToolbarTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Demo_previews_navigates_and_generates_separate_template_snapshots() => fixture.Session.Dispatch(() =>
    {
        var original = MailMergeWindow.CreateTemplate();
        var window = new MailMergeWindow(original);
        try
        {
            window.Show(); window.UpdateLayout();
            var layout = Assert.IsType<Grid>(window.Content);
            var buttons = Assert.IsType<WrapPanel>(layout.Children[1]);
            var saveAll = buttons.Children.OfType<Button>().Single(button => Equals(button.Content, "Save all as DOCX ZIP..."));
            Assert.False(saveAll.IsEnabled);
            var pair = Assert.IsType<Grid>(layout.Children[2]);
            var template = Assert.IsType<TextaloniaEditor>(Assert.IsType<DockPanel>(pair.Children[0]).Children[1]);
            var tabs = Assert.IsType<TabControl>(pair.Children[1]);
            var preview = Assert.IsType<TextaloniaViewer>(Assert.IsType<DockPanel>(Assert.IsType<TabItem>(tabs.Items[0]).Content).Children[1]);
            void Click(string name) => buttons.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == name)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(original, template.Document); Assert.True(preview.IsReadOnly);
            Assert.Contains("Alex Chen", preview.Document.PlainText);
            Click("Next recipient"); Assert.Contains("Sam Rivera", preview.Document.PlainText);
            Click("Generate all"); Assert.Equal(2, tabs.SelectedIndex);
            Assert.True(saveAll.IsEnabled);
            var outputPanel = Assert.IsType<DockPanel>(Assert.IsType<TabItem>(tabs.Items[2]).Content);
            var selection = Assert.IsType<ComboBox>(outputPanel.Children[1]);
            var output = Assert.IsType<TextaloniaViewer>(outputPanel.Children[2]);
            Assert.Equal(3, selection.ItemCount); Assert.Empty(MailMergeProcessor.GetFieldNames(output.Document));
            selection.SelectedIndex = 2; Assert.Contains("Taylor Morgan", output.Document.PlainText);
            Assert.Same(original, template.Document);
            var data = Assert.IsType<TextBox>(Assert.IsType<DockPanel>(Assert.IsType<TabItem>(tabs.Items[1]).Content).Children[1]);
            var validData = data.Text;
            tabs.SelectedIndex = 1; data.Text = "[]";
            Assert.False(saveAll.IsEnabled);
            Click("Generate all"); Assert.Equal(1, tabs.SelectedIndex); Assert.False(saveAll.IsEnabled);
            data.Text = validData; Click("Generate all"); Assert.True(saveAll.IsEnabled);
            template.Session.InsertText("Updated template. "); Assert.False(saveAll.IsEnabled);
            template.Document = original; Click("Generate all"); Assert.True(saveAll.IsEnabled);
            tabs.SelectedIndex = 0; window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("TEXTALONIA_MERGE_SCREENSHOT") is { Length: > 0 } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Field_flyout_inserts_updates_and_respects_read_only() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor();
        var toolbar = new TextaloniaToolbar { Editor = editor };
        var window = new Window { Content = toolbar, Width = 700, Height = 400 };
        try
        {
            window.Show(); window.UpdateLayout();
            var fieldButton = toolbar.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Insert or edit a merge field");
            var flyout = Assert.IsType<Flyout>(fieldButton.Flyout);
            var panel = Assert.IsType<StackPanel>(flyout.Content);
            TextBox Input(string name) => panel.Children.OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == name);
            Button Action(string name) => panel.Children.OfType<Button>().Single(control => AutomationProperties.GetName(control) == name);
            flyout.ShowAt(fieldButton);
            Assert.False(Action("Update selected merge field").IsEnabled);
            editor.Session.Select(0, 0);
            Assert.False(Action("Update selected merge field").IsEnabled);
            Input("Merge field name").Text = "Name";
            Action("Insert merge field").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var id = editor.CurrentMergeField!.Id;
            flyout.ShowAt(fieldButton);
            Assert.Equal("Name", Input("Merge field name").Text);
            Assert.True(Action("Update selected merge field").IsEnabled);
            Input("Merge field name").Text = "CustomerName";
            Input("Merge field value format").Text = "N2";
            Action("Update selected merge field").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(id, editor.CurrentMergeField!.Id);
            Assert.Equal("CustomerName", ((MergeFieldInlinePayload)editor.CurrentMergeField.Payload).Name);
            editor.IsReadOnly = true;
            Assert.False(fieldButton.IsEnabled);
            Assert.False(Action("Insert merge field").IsEnabled);
        }
        finally { window.Close(); }
    }, CancellationToken.None);
}
