using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class MarkdownCodeFontTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Code_font_uses_platform_fallbacks_for_inline_and_fenced_text() => fixture.Session.Dispatch(() =>
    {
        var document = DocumentFormats.Markdown.Parse("`inline`\n\n```\ncode\n```");
        var inline = Assert.IsType<Paragraph>(document.Blocks[0]).Runs[0].Style;
        var block = Assert.IsType<Paragraph>(Assert.IsType<Section>(document.Blocks[1]).Blocks[0]).DefaultStyle;
        Assert.Equal(inline.FontFamily, block.FontFamily);
        var family = new FontFamily(block.FontFamily!);
        Assert.True(family.FamilyNames.HasFallbacks);
        Assert.Contains("Consolas", family.FamilyNames);
        Assert.Contains("Menlo", family.FamilyNames);
        Assert.Contains("DejaVu Sans Mono", family.FamilyNames);
        // Font availability belongs to the host. On systems with a listed family,
        // verify shaping rather than merely inspecting the serialized font string.
        if (FontManager.Current.SystemFonts.Any(f => family.FamilyNames.Contains(f.Name)))
        {
            using var narrow = DocumentLayout.CreateTextLayout(new Paragraph("iiii", block), 1000, FontFamily.Default, Brushes.Black);
            using var wide = DocumentLayout.CreateTextLayout(new Paragraph("WWWW", block), 1000, FontFamily.Default, Brushes.Black);
            Assert.Equal(narrow.Width, wide.Width, 4);
        }
    }, CancellationToken.None);
}
