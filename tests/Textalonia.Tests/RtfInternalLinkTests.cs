using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class RtfInternalLinkTests
{
    [Fact]
    public void Internal_links_keep_standard_destinations_tooltips_and_activation_through_two_roundtrips()
    {
        var session = new EditorSession(FlowDocument.FromText("Target\nJump"));
        session.Select(0, 6); session.AddBookmark("Target name");
        session.Select(7, 11);
        var destination = new InternalLinkDestination { BookmarkName = "Target name", Tooltip = "See \"target\" \\ details", Activation = InternalLinkActivation.Click };
        session.ApplyStyle(s => s with { InternalLink = destination });
        var document = session.Document;
        for (var pass = 0; pass < 2; pass++)
        {
            var rtf = DocumentFormats.Rtf.Serialize(document);
            Assert.Contains("HYPERLINK", rtf); Assert.Contains("\\\\l", rtf);
            document = DocumentFormats.Rtf.Parse(rtf);
            Assert.Equal("Target\nJump", document.Text);
            var run = document.GetStoryIndex(Guid.Empty).Paragraphs[1].Paragraph.Runs[0];
            Assert.Equal(destination, run.Style.InternalLink); Assert.Null(run.Style.Hyperlink);
            Assert.Equal("Target name", Assert.Single(document.Bookmarks).Name);
        }
    }

    [Fact]
    public void Standard_local_hyperlink_is_distinct_from_an_external_uri_and_plain_keeps_destination()
    {
        var document = DocumentFormats.Rtf.Parse(@"{\rtf1 {\*\bkmkstart target}Target{\*\bkmkend target}\par {\field{\*\fldinst HYPERLINK \\l ""target"" \\o ""Go to target""}{\fldrslt {\plain Jump}}}}");
        var link = document.GetStoryIndex(Guid.Empty).Paragraphs[1].Paragraph.Runs[0].Style;
        Assert.Null(link.Hyperlink); Assert.Equal("target", link.InternalLink?.BookmarkName);
        Assert.Equal("Go to target", link.InternalLink?.Tooltip);
        Assert.Equal(InternalLinkActivation.ModifierClick, link.InternalLink?.Activation);
    }

    [Fact]
    public void Unsafe_external_link_with_local_switch_does_not_become_an_internal_link()
    {
        var document = DocumentFormats.Rtf.Parse(@"{\rtf1 {\field{\*\fldinst HYPERLINK ""javascript:alert(1)"" \\l ""target""}{\fldrslt Jump}}}");
        var style = document.GetStoryIndex(Guid.Empty).Paragraphs[0].Paragraph.Runs[0].Style;
        Assert.Equal("Jump", document.Text); Assert.Null(style.Hyperlink); Assert.Null(style.InternalLink);
    }
}
