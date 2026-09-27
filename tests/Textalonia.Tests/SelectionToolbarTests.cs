using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class SelectionToolbarTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Toolbar_indicates_mixed_bold_font_size_and_heading_then_uniform_choice() =>
        fixture.Session.Dispatch(() =>
        {
            var editor = new TextaloniaEditor { Document = new FlowDocument([
                new Paragraph("a", new() { Bold = true, FontSize = 20 }) { Style = new() { HeadingLevel = 1 } },
                new Paragraph("b")
            ]) };
            var toolbar = new TextaloniaToolbar { Editor = editor };
            editor.Session.SelectAll();
            var bold = toolbar.Children.OfType<ToggleButton>().Single(b => AutomationProperties.GetName(b) == "Bold");
            var size = toolbar.Children.OfType<ComboBox>().Single(b => AutomationProperties.GetName(b) == "Font size");
            var heading = toolbar.Children.OfType<ComboBox>().Single(b => AutomationProperties.GetName(b) == "Paragraph style");
            Assert.Null(bold.IsChecked); Assert.Null(size.SelectedItem); Assert.Equal(-1, heading.SelectedIndex);
            editor.Session.ToggleBold();
            Assert.True(bold.IsChecked);
            editor.Session.ApplyStyle(s => s with { FontSize = 24 });
            Assert.Equal("24", size.SelectedItem);
            editor.Session.SetHeading(2); Assert.Equal(2, heading.SelectedIndex);
            editor.Session.Select(0, 0); editor.Session.ToggleBold(); Assert.False(bold.IsChecked);
        }, CancellationToken.None);
}
