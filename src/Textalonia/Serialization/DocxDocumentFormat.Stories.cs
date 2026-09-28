using System.Collections.Immutable;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class DocxDocumentFormat
{
    private static string NumberFormatName(PageNumberFormat value) => value switch
    {
        PageNumberFormat.UpperRoman => "upperRoman", PageNumberFormat.LowerRoman => "lowerRoman",
        PageNumberFormat.UpperLetter => "upperLetter", PageNumberFormat.LowerLetter => "lowerLetter", _ => "decimal"
    };
    private static PageNumberFormat ReadNumberFormat(string? value, XElement? source) => value switch
    {
        null or "decimal" => PageNumberFormat.Decimal, "upperRoman" => PageNumberFormat.UpperRoman,
        "lowerRoman" => PageNumberFormat.LowerRoman, "upperLetter" => PageNumberFormat.UpperLetter,
        "lowerLetter" => PageNumberFormat.LowerLetter, _ => UnsupportedNumberFormat(source)
    };
    private static PageNumberFormat UnsupportedNumberFormat(XElement? source)
    {
        Loss("note-number-format", "Unsupported numbering sequence", "Decimal numbering applied.", source);
        return PageNumberFormat.Decimal;
    }
    private static NoteSettings ReadNoteSettings(XElement? element, bool endnote)
    {
        var result = new NoteSettings { Placement = endnote ? NotePlacement.DocumentEnd : NotePlacement.PageBottom };
        if (element is null) return result;
        result = result with
        {
            NumberFormat = ReadNumberFormat(Value(element.Element(W + "numFmt")), element.Element(W + "numFmt")),
            Start = Number(element.Element(W + "numStart"), 1),
            Restart = Value(element.Element(W + "numRestart")) switch { "eachSect" => NoteRestartPolicy.EachSection, "eachPage" when !endnote => NoteRestartPolicy.EachPage, _ => NoteRestartPolicy.Continuous },
            Placement = Value(element.Element(W + "pos")) switch { "beneathText" when !endnote => NotePlacement.BelowText, "sectEnd" when endnote => NotePlacement.SectionEnd, _ => endnote ? NotePlacement.DocumentEnd : NotePlacement.PageBottom }
        };
        if (Value(element.Element(W + "numRestart")) is { } restart && restart is not ("continuous" or "eachSect") && (endnote || restart != "eachPage"))
            Loss("note-restart", "Unsupported note restart rule", "Continuous numbering applied.", element);
        if (Value(element.Element(W + "pos")) is { } pos && !(endnote ? pos is "docEnd" or "sectEnd" : pos is "pageBottom" or "beneathText"))
            Loss("note-placement", "Unsupported note placement", "Default note placement applied.", element);
        foreach (var child in element.Elements().Where(e => e.Name.LocalName is not ("numFmt" or "numStart" or "numRestart" or "pos" or "footnote" or "endnote")))
            Loss("note-property", child.Name.LocalName, "Supported note settings retained.", child);
        return result;
    }
    private static XElement WriteNoteSettings(NoteSettings settings, bool endnote, bool hasNotes = true) => new(W + (endnote ? "endnotePr" : "footnotePr"),
        Val("pos", settings.Placement switch { NotePlacement.BelowText => "beneathText", NotePlacement.SectionEnd => "sectEnd", NotePlacement.DocumentEnd => "docEnd", _ => "pageBottom" }),
        Val("numFmt", NumberFormatName(settings.NumberFormat)), Val("numStart", settings.Start),
        Val("numRestart", settings.Restart switch { NoteRestartPolicy.EachSection => "eachSect", NoteRestartPolicy.EachPage => "eachPage", _ => "continuous" }),
        hasNotes ? new XElement(W + (endnote ? "endnote" : "footnote"), new XAttribute(W + "id", -1)) : null,
        hasNotes ? new XElement(W + (endnote ? "endnote" : "footnote"), new XAttribute(W + "id", 0)) : null);

    private static DocumentSection ReadPhysicalSection(XElement source, Guid start, bool oddEven,
        Func<XElement, bool, StoryReference> readReference)
    {
        var size = source.Element(W + "pgSz"); var margins = source.Element(W + "pgMar");
        var landscape = (string?)size?.Attribute(W + "orient") == "landscape";
        var width = Dimension(size, W + "w", 12240) / 15; var height = Dimension(size, W + "h", 15840) / 15;
        var columns = source.Element(W + "cols"); var number = (int?)columns?.Attribute(W + "num") ?? 1;
        if (number is < 1 or > 32) throw new FormatException("Invalid DOCX column count.");
        var columnItems = columns?.Elements(W + "col").ToArray() ?? [];
        var page = new PageSettings
        {
            Width = landscape ? height : width, Height = landscape ? width : height,
            Orientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait,
            Margins = new(Dimension(margins, W + "left", 1440) / 15, Dimension(margins, W + "top", 1440) / 15,
                Dimension(margins, W + "right", 1440) / 15, Dimension(margins, W + "bottom", 1440) / 15),
            Gutter = Dimension(margins, W + "gutter", 0) / 15,
            Columns = columnItems.Length > 0 ? columnItems.Select(e => new PageColumn(Dimension(e, W + "w", 1))).ToImmutableArray() :
                number > 1 ? Enumerable.Range(0, number).Select(_ => new PageColumn()).ToImmutableArray() : [],
            ColumnSpacing = Dimension(columns, W + "space", 360) / 15
        };
        if (columnItems.Any(e => e.Attribute(W + "space") is not null && Dimension(e, W + "space") != Dimension(columns, W + "space", 360)))
            Loss("column-spacing", "Different inter-column distances", "One section-wide column distance retained.", columns);
        var header = new HeaderFooterSettings
        {
            DifferentFirstPage = source.Element(W + "titlePg") is { } first && On(first), DifferentOddEvenPages = oddEven,
            HeaderDistance = Dimension(margins, W + "header", 540) / 15,
            FooterDistance = Dimension(margins, W + "footer", 540) / 15
        };
        foreach (var footer in new[] { false, true })
            foreach (var reference in source.Elements(W + (footer ? "footerReference" : "headerReference")))
            {
                var variant = (string?)reference.Attribute(W + "type") switch { "first" => HeaderFooterVariant.First, "even" => HeaderFooterVariant.Even, _ => HeaderFooterVariant.Primary };
                header = header.WithReference(footer, variant, readReference(reference, footer));
            }
        foreach (var child in source.Elements().Where(e => e.Name != Tx + "watermark" && e.Name.LocalName is not ("pgSz" or "pgMar" or "cols" or "type" or "pgNumType" or "titlePg" or "headerReference" or "footerReference")))
            Loss("section-property", child.Name.LocalName, "Supported section and header/footer settings retained.", child);
        return new DocumentSection
        {
            StartParagraphId = start, PageSettings = page, HeaderFooter = header,
            BreakKind = Value(source.Element(W + "type")) switch { "continuous" => SectionBreakKind.Continuous, "oddPage" => SectionBreakKind.OddPage, "evenPage" => SectionBreakKind.EvenPage, "nextColumn" => SectionBreakKind.NextColumn, _ => SectionBreakKind.NextPage },
            PageNumberStart = (int?)source.Element(W + "pgNumType")?.Attribute(W + "start"),
            PageNumberFormat = ReadNumberFormat((string?)source.Element(W + "pgNumType")?.Attribute(W + "fmt"), source.Element(W + "pgNumType"))
        };
    }
    private static XElement WritePhysicalSection(DocumentSection section, Func<StoryReference, bool, string> relationship)
    {
        var page = section.PageSettings; var settings = section.HeaderFooter;
        var result = new XElement(W + "sectPr");
        foreach (var footer in new[] { false, true }) foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
        {
            var reference = settings.GetReference(footer, variant);
            if (reference.LinkToPrevious) continue;
            result.Add(new XElement(W + (footer ? "footerReference" : "headerReference"),
                new XAttribute(W + "type", variant switch { HeaderFooterVariant.First => "first", HeaderFooterVariant.Even => "even", _ => "default" }),
                new XAttribute(R + "id", relationship(reference, footer))));
        }
        result.Add(Val("type", section.BreakKind switch { SectionBreakKind.Continuous => "continuous", SectionBreakKind.OddPage => "oddPage", SectionBreakKind.EvenPage => "evenPage", SectionBreakKind.NextColumn => "nextColumn", _ => "nextPage" }),
            new XElement(W + "pgSz", new XAttribute(W + "w", Twips(page.EffectiveWidth)), new XAttribute(W + "h", Twips(page.EffectiveHeight)), page.Orientation == PageOrientation.Landscape ? new XAttribute(W + "orient", "landscape") : null),
            new XElement(W + "pgMar", new XAttribute(W + "left", Twips(page.Margins.Left)), new XAttribute(W + "top", Twips(page.Margins.Top)), new XAttribute(W + "right", Twips(page.Margins.Right)), new XAttribute(W + "bottom", Twips(page.Margins.Bottom)),
                new XAttribute(W + "gutter", Twips(page.Gutter)), new XAttribute(W + "header", Twips(settings.HeaderDistance)), new XAttribute(W + "footer", Twips(settings.FooterDistance))),
            new XElement(W + "pgNumType", new XAttribute(W + "fmt", NumberFormatName(section.PageNumberFormat)), section.PageNumberStart is { } start ? new XAttribute(W + "start", start) : null));
        if (!page.Columns.IsEmpty)
        {
            var usable = page.EffectiveWidth - page.Margins.Left - page.Margins.Right - page.Gutter - (page.Columns.Length - 1) * page.ColumnSpacing;
            var total = page.Columns.Sum(c => c.Width);
            result.Add(new XElement(W + "cols", new XAttribute(W + "num", page.Columns.Length), new XAttribute(W + "space", Twips(page.ColumnSpacing)), new XAttribute(W + "equalWidth", 0),
                page.Columns.Select(c => new XElement(W + "col", new XAttribute(W + "w", Twips(usable * c.Width / total))))));
        }
        if (settings.DifferentFirstPage) result.Add(new XElement(W + "titlePg"));
        if (page.Borders is not null || page.LineNumbering is not null || page.Grid is not null || !page.BalanceColumns || page.Background is not (null or "#FFFFFF"))
            Loss("page-decoration", "Page decoration, line numbering, grid or unbalanced columns", "Supported physical section geometry retained.", id: section.Id);
        return result;
    }
    private static ImmutableArray<Block> RemoveNotePrefix(ImmutableArray<Block> blocks, string mark)
    {
        if (blocks.IsEmpty) return blocks;
        var first = blocks[0];
        if (first is Paragraph paragraph && paragraph.Runs.FirstOrDefault() is { Inline: null } run && run.Text.StartsWith(mark, StringComparison.Ordinal))
            first = paragraph with { Runs = paragraph.Runs.SetItem(0, run with { Text = run.Text[mark.Length..] }).Where(r => r.Text.Length > 0).ToImmutableArray() };
        else if (first is Section section) first = section with { Blocks = RemoveNotePrefix(section.Blocks, mark) };
        else if (first is Table table) first = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = RemoveNotePrefix(table.Rows[0][0].Blocks, mark) });
        return blocks.SetItem(0, first);
    }
    private static ImmutableArray<Block> InsertTableSectionBoundary(ImmutableArray<Block> blocks, Guid paragraphId, Paragraph boundary)
    {
        var result = ImmutableArray.CreateBuilder<Block>();
        foreach (var block in blocks)
        {
            if (block is Table table && new DocumentIndex(new FlowDocument([table])).Paragraphs.Any(p => p.Paragraph.Id == paragraphId)) result.Add(boundary);
            result.Add(block is Section section ? section with { Blocks = InsertTableSectionBoundary(section.Blocks, paragraphId, boundary) } : block);
        }
        return result.ToImmutable();
    }

}
