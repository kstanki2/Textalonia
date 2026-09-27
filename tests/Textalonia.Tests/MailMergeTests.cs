using System.Collections.Immutable;
using System.Globalization;
using Textalonia.MailMerge;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class MailMergeTests
{
    private static RichRun Field(string name, string? format = null, string? fallback = null, TextStyle? style = null) =>
        new(new InlineDescriptor
        {
            AltText = $"«{name}»",
            Payload = new MergeFieldInlinePayload(name) { Format = format, FallbackText = fallback }
        }, style);

    private static FlowDocument Template(params RichRun[] runs) => new([new Paragraph(runs)]);
    private static Dictionary<string, object?> Record(string name, object? value) => new() { [name] = value };

    [Fact]
    public void Merge_replaces_repeated_typed_fields_preserving_surroundings_styles_resources_and_template()
    {
        var bold = TextStyle.Default with { FontWeight = 700 };
        var italic = TextStyle.Default with { Italic = true };
        var control = new InlineDescriptor { AltText = "Button", Payload = new ControlInlinePayload("button") };
        var paragraph = new Paragraph
        {
            Runs = [new("Dear ", bold), Field("Name", style: italic), new("! ", bold), Field("Name"),
                new("Literal {{Na"), new("me}}"), new(control)],
            Style = ParagraphStyle.Default with { HeadingLevel = 2 }, DefaultStyle = bold
        };
        var untouched = new Paragraph("Unchanged");
        var template = new FlowDocument([paragraph, untouched])
        {
            Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("photo", new()
            { Kind = DocumentResourceKind.Host, Location = "photo" })
        };
        var serialized = DocumentFormats.Json.Serialize(template);
        var data = Record("Name", "Ada {{Name}}");

        var merged = MailMergeProcessor.Merge(template, data);

        merged.Validate();
        Assert.Equal("Dear Ada {{Name}}! Ada {{Name}}Literal {{Name}}Button\nUnchanged", merged.PlainText);
        var result = Assert.IsType<Paragraph>(merged.Blocks[0]);
        Assert.Equal(paragraph.Id, result.Id);
        Assert.Same(paragraph.Style, result.Style);
        Assert.Same(paragraph.DefaultStyle, result.DefaultStyle);
        Assert.Same(italic, result.Runs[1].Style);
        Assert.Same(control, result.Runs[^1].Inline);
        Assert.Same(untouched, merged.Blocks[1]);
        Assert.Same(template.Resources, merged.Resources);
        Assert.Empty(MailMergeProcessor.GetFieldNames(merged));
        Assert.Equal(serialized, DocumentFormats.Json.Serialize(template));
        Assert.Equal("Ada {{Name}}", data["Name"]);
    }

    [Fact]
    public void Preview_preserves_field_identity_definition_and_positions_and_can_be_reused()
    {
        var field = Field("Total", "N2", style: TextStyle.Default with { FontWeight = 700 });
        var template = Template(new("Total: "), field, new("."));
        var preview = MailMergeProcessor.Preview(template, Record("Total", 1234.5m));
        var next = MailMergeProcessor.Preview(preview, Record("Total", 19m));
        var run = Assert.IsType<Paragraph>(preview.Blocks[0]).Runs[1];

        Assert.Equal("Total: 1,234.50.", preview.PlainText);
        Assert.Equal("Total: 19.00.", next.PlainText);
        Assert.Equal(template.Text, preview.Text);
        Assert.Equal(field.Inline!.Id, run.Inline!.Id);
        Assert.Same(field.Inline.Payload, run.Inline.Payload);
        Assert.Same(field.Style, run.Style);
        Assert.Equal("Total: «Total».", template.PlainText);
        Assert.Equal("Total: 3.50.", MailMergeProcessor.Merge(preview, Record("Total", 3.5m)).PlainText);
        preview.Validate();
    }

    [Fact]
    public void Numeric_and_date_formats_use_requested_culture_and_invariant_default()
    {
        var template = Template(Field("Amount", "N2"), new(" / "), Field("Date", "dd MMMM yyyy"));
        var record = new Dictionary<string, object?> { ["Amount"] = 1234.5m, ["Date"] = new DateTime(2026, 9, 27) };
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("1,234.50 / 27 September 2026", MailMergeProcessor.Merge(template, record).PlainText);
            Assert.Equal("1.234,50 / 27 September 2026", MailMergeProcessor.Merge(template, record,
                new() { Culture = CultureInfo.GetCultureInfo("de-DE") }).PlainText);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Field_names_are_ordinal_case_sensitive_even_with_case_insensitive_record_dictionary()
    {
        var template = Template(Field("Name"), Field("name"), Field("Name"));
        Assert.Equal(["Name", "name"], MailMergeProcessor.GetFieldNames(template).ToArray());
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "Ada" };
        Assert.Throws<KeyNotFoundException>(() => MailMergeProcessor.Merge(template, data));
        var retained = MailMergeProcessor.Merge(template, data, new() { MissingFieldBehavior = MissingFieldBehavior.KeepField });
        Assert.Equal("«Name»Ada«Name»", retained.PlainText);
        Assert.Equal(["Name"], MailMergeProcessor.GetFieldNames(retained).ToArray());
    }

    [Theory]
    [InlineData(MissingFieldBehavior.Throw)]
    [InlineData(MissingFieldBehavior.KeepField)]
    [InlineData(MissingFieldBehavior.Empty)]
    public void Fallback_wins_over_missing_policy_and_null_uses_fallback_or_empty(MissingFieldBehavior behavior)
    {
        var template = Template(Field("Missing", fallback: "Guest"), new(" / "), Field("Null", fallback: "Unknown"),
            new(" / "), Field("Blank"));
        var data = new Dictionary<string, object?> { ["Null"] = null, ["Blank"] = null };
        var options = new MailMergeOptions { MissingFieldBehavior = behavior };
        Assert.Equal("Guest / Unknown / ", MailMergeProcessor.Merge(template, data, options).PlainText);
        var preview = MailMergeProcessor.Preview(template, data, options);
        Assert.Equal("Guest / Unknown / ", preview.PlainText);
        Assert.Equal(["Missing", "Null", "Blank"], MailMergeProcessor.GetFieldNames(preview).ToArray());
    }

    [Fact]
    public void Missing_policy_throws_keeps_or_removes_field_and_distinguishes_empty_fallback()
    {
        var template = Template(new("a"), Field("Absent"), new("b"));
        var data = new Dictionary<string, object?>();
        Assert.Contains("Absent", Assert.Throws<KeyNotFoundException>(() => MailMergeProcessor.Merge(template, data)).Message);
        Assert.Throws<KeyNotFoundException>(() => MailMergeProcessor.Preview(template, data));
        var keep = new MailMergeOptions { MissingFieldBehavior = MissingFieldBehavior.KeepField };
        Assert.Same(template, MailMergeProcessor.Merge(template, data, keep));
        Assert.Same(template, MailMergeProcessor.Preview(template, data, keep));
        var empty = new MailMergeOptions { MissingFieldBehavior = MissingFieldBehavior.Empty };
        var merged = MailMergeProcessor.Merge(template, data, empty);
        Assert.Equal("ab", merged.PlainText);
        Assert.Null(Assert.Single(Assert.IsType<Paragraph>(merged.Blocks[0]).Runs).Inline);
        Assert.Equal("ab", MailMergeProcessor.Preview(template, data, empty).PlainText);
        Assert.Equal("", MailMergeProcessor.Merge(Template(Field("Absent", fallback: "")), data).PlainText);
    }

    [Fact]
    public void Newlines_in_values_and_fallbacks_become_soft_breaks_without_changing_structure()
    {
        var template = Template(Field("Address"), new(" / "), Field("Missing", fallback: "x\r\ny\rz\n"));
        var data = Record("Address", "a\r\nb\rc\nd");
        foreach (var result in new[] { MailMergeProcessor.Merge(template, data), MailMergeProcessor.Preview(template, data) })
        {
            Assert.Single(result.Blocks);
            Assert.Equal("a\u2028b\u2028c\u2028d / x\u2028y\u2028z\u2028", result.PlainText);
            result.Validate();
        }
    }

    [Fact]
    public void Nested_sections_tables_hidden_cells_and_merge_backups_are_all_resolved()
    {
        var inner = Table.Create(1, 1);
        inner = inner.SetCell(0, 0, inner.Rows[0][0] with { Blocks = [new Paragraph([Field("Nested")])] });
        var table = Table.Create(1, 2) with { ColumnWidths = [2, 1], RowSizing = [new() { Mode = TableRowHeightMode.AtLeast, Height = 25 }] };
        table = table.SetCell(0, 0, table.Rows[0][0] with
        {
            Blocks = [new Paragraph([Field("Backup")])], Background = "#123456", Padding = new(1, 2, 3, 4)
        });
        table = table.SetCell(0, 1, table.Rows[0][1] with { Blocks = [new Paragraph([Field("Hidden")])] });
        table = table.MergeCells(0, 0, 1, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Section { Blocks = [new Paragraph([Field("Visible")]), inner] }] });
        var section = new Section { Blocks = [table], Padding = 12 };
        var template = new FlowDocument([section]);
        var record = new Dictionary<string, object?> { ["Visible"] = "V", ["Nested"] = "N", ["Backup"] = "B", ["Hidden"] = "H" };
        Assert.Equal(["Visible", "Nested", "Backup", "Hidden"], MailMergeProcessor.GetFieldNames(template).ToArray());

        var result = MailMergeProcessor.Merge(template, record);
        result.Validate();
        Assert.Empty(MailMergeProcessor.GetFieldNames(result));
        var resultSection = Assert.IsType<Section>(result.Blocks[0]);
        var resultTable = Assert.IsType<Table>(resultSection.Blocks[0]);
        Assert.Equal(section.Id, resultSection.Id);
        Assert.Equal(section.Padding, resultSection.Padding);
        Assert.Equal(table.Id, resultTable.Id);
        Assert.Equal(table.ColumnWidths, resultTable.ColumnWidths);
        Assert.Equal(table.RowSizing, resultTable.RowSizing);
        for (var column = 0; column < 2; column++) Assert.Equal(table.Rows[0][column].Id, resultTable.Rows[0][column].Id);
        Assert.Equal(table.Rows[0][0].Padding, resultTable.Rows[0][0].Padding);
        Assert.Equal(table.Rows[0][0].Background, resultTable.Rows[0][0].Background);
        Assert.Equal("B", Assert.IsType<Paragraph>(Assert.Single(resultTable.Rows[0][0].MergeOriginalBlocks)).PlainText);
        Assert.Equal("H", Assert.IsType<Paragraph>(Assert.Single(resultTable.Rows[0][1].Blocks)).PlainText);
        var split = new FlowDocument([resultTable.SplitCell(0, 0)]);
        split.Validate();
        Assert.Empty(MailMergeProcessor.GetFieldNames(split));
        Assert.Equal("V\nN\nH", split.PlainText);
        Assert.Equal(["Visible", "Nested", "Backup", "Hidden"], MailMergeProcessor.GetFieldNames(template).ToArray());
        var preview = MailMergeProcessor.Preview(template, record);
        preview.Validate();
        var previewTable = Assert.IsType<Table>(Assert.IsType<Section>(preview.Blocks[0]).Blocks[0]);
        Assert.Equal("B", Assert.IsType<Paragraph>(Assert.Single(previewTable.Rows[0][0].MergeOriginalBlocks)).PlainText);
        Assert.Equal("H", Assert.IsType<Paragraph>(Assert.Single(previewTable.Rows[0][1].Blocks)).PlainText);
    }

    [Fact]
    public void Splitting_merged_cells_after_mail_merge_restores_resolved_original_cells()
    {
        var table = Table.Create(1, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Paragraph([Field("First")])] });
        table = table.SetCell(0, 1, table.Rows[0][1] with { Blocks = [new Paragraph([Field("Second")])] });
        var template = new FlowDocument([table.MergeCells(0, 0, 1, 2)]);
        var output = MailMergeProcessor.Merge(template, new Dictionary<string, object?> { ["First"] = "A", ["Second"] = "B" });
        var split = Assert.IsType<Table>(output.Blocks[0]).SplitCell(0, 0);
        var document = new FlowDocument([split]);
        document.Validate();
        Assert.Equal("A\nB", document.PlainText);
        Assert.Single(split.Rows[0][0].Blocks);
        Assert.Empty(MailMergeProcessor.GetFieldNames(document));
    }

    [Fact]
    public void MergeMany_is_lazy_ordered_and_disposes_records_when_stopped_early()
    {
        var read = 0;
        var disposed = false;
        IEnumerable<IReadOnlyDictionary<string, object?>> Records()
        {
            try
            {
                read++;
                yield return Record("Name", "Ada");
                read++;
                yield return Record("Name", "Grace");
                read++;
                yield return Record("Name", "Linus");
            }
            finally { disposed = true; }
        }
        var template = Template(Field("Name"));
        var outputs = MailMergeProcessor.MergeMany(template, Records());
        Assert.Equal(0, read);
        using (var enumerator = outputs.GetEnumerator())
        {
            Assert.Equal(0, read);
            Assert.True(enumerator.MoveNext());
            var first = enumerator.Current;
            Assert.Equal("Ada", first.PlainText);
            Assert.Equal(1, read);
            Assert.True(enumerator.MoveNext());
            Assert.Equal("Grace", enumerator.Current.PlainText);
            Assert.Equal("Ada", first.PlainText);
            Assert.Equal(2, read);
        }
        Assert.True(disposed);
        Assert.Equal(2, read);
        Assert.Equal("«Name»", template.PlainText);
    }

    [Fact]
    public void MergeMany_captures_culture_options_and_reads_records_only_when_requested()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = "#";
        var record = Record("Value", 1.5m);
        var outputs = MailMergeProcessor.MergeMany(Template(Field("Value", "F1")), [record], new() { Culture = culture });
        culture.NumberFormat.NumberDecimalSeparator = "%";
        record["Value"] = 2.5m;
        Assert.Equal("2#5", Assert.Single(outputs).PlainText);
        Assert.Empty(MailMergeProcessor.MergeMany(Template(Field("Value")), []));
    }

    [Fact]
    public void Cancellation_prevents_next_record_from_being_read_and_disposes_source()
    {
        using var cancellation = new CancellationTokenSource();
        var read = 0;
        var disposed = false;
        IEnumerable<IReadOnlyDictionary<string, object?>> Records()
        {
            try
            {
                while (true) { read++; yield return Record("Name", read); }
            }
            finally { disposed = true; }
        }
        var template = Template(Field("Name"));
        using var outputs = MailMergeProcessor.MergeMany(template, Records(), cancellationToken: cancellation.Token).GetEnumerator();
        Assert.True(outputs.MoveNext());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => outputs.MoveNext());
        Assert.Equal(1, read);
        Assert.True(disposed);
        Assert.Throws<OperationCanceledException>(() => MailMergeProcessor.Merge(template, Record("Name", "Ada"), cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => MailMergeProcessor.Preview(template, Record("Name", "Ada"), cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => MailMergeProcessor.MergeMany(template, Records(), cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Cancellation_during_value_formatting_aborts_the_atomic_merge()
    {
        using var cancellation = new CancellationTokenSource();
        var template = Template(Field("Name"));
        var value = new TestValue(() => { cancellation.Cancel(); return "Ada"; });
        Assert.Throws<OperationCanceledException>(() => MailMergeProcessor.Merge(template, Record("Name", value), cancellationToken: cancellation.Token));
        Assert.Equal("«Name»", template.PlainText);
    }

    [Fact]
    public void Arguments_options_templates_and_null_batch_records_are_validated()
    {
        var template = Template(Field("Name"));
        var data = Record("Name", "Ada");
        Assert.Throws<ArgumentNullException>(() => MailMergeProcessor.GetFieldNames(null!));
        Assert.Throws<ArgumentNullException>(() => MailMergeProcessor.Merge(null!, data));
        Assert.Throws<ArgumentNullException>(() => MailMergeProcessor.Merge(template, null!));
        Assert.Throws<ArgumentNullException>(() => MailMergeProcessor.Preview(template, null!));
        Assert.Throws<ArgumentNullException>(() => MailMergeProcessor.MergeMany(template, null!));
        Assert.Throws<ArgumentNullException>(() => MailMergeProcessor.Merge(template, data, new() { Culture = null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MailMergeProcessor.Merge(template, data, new() { MissingFieldBehavior = (MissingFieldBehavior)100 }));
        var invalid = Template(Field(" "));
        Assert.Throws<FormatException>(() => MailMergeProcessor.GetFieldNames(invalid));
        Assert.Throws<FormatException>(() => MailMergeProcessor.MergeMany(invalid, []));
        Assert.Throws<ArgumentNullException>(() => MailMergeProcessor.MergeMany(template, [null!]).ToArray());
    }

    [Fact]
    public void Unsupported_values_and_invalid_formats_fail_without_mutating_template()
    {
        var formatted = Template(Field("Value", "N2"));
        Assert.Throws<FormatException>(() => MailMergeProcessor.Merge(formatted, Record("Value", "123")));
        Assert.Throws<FormatException>(() => MailMergeProcessor.Merge(Template(Field("Value", "Q")), Record("Value", 12m)));
        Assert.Throws<ArgumentException>(() => MailMergeProcessor.Merge(Template(Field("Value")), Record("Value", new object())));
        Assert.Equal("«Value»", formatted.PlainText);
        Assert.Equal("Truex", MailMergeProcessor.Merge(Template(Field("Bool"), Field("Char")),
            new Dictionary<string, object?> { ["Bool"] = true, ["Char"] = 'x' }).PlainText);
    }

    [Theory]
    [InlineData("D999999999")]
    [InlineData("F999999999")]
    [InlineData("G1025")]
    [InlineData("N00000000000001025")]
    public void Excessive_precision_is_rejected_before_invoking_formatter(string format)
    {
        var called = false;
        var value = new TestValue(() => { called = true; return "value"; });
        var template = Template(Field("Value", format));
        Assert.Throws<FormatException>(() => MailMergeProcessor.Merge(template, Record("Value", value)));
        Assert.False(called);
    }

    [Fact]
    public void Values_containing_nul_are_rejected_for_merge_and_preview()
    {
        var template = Template(Field("Value"));
        foreach (var value in new object[] { "bad\0value", '\0', new TestValue(() => "bad\0value") })
        {
            Assert.Throws<FormatException>(() => MailMergeProcessor.Merge(template, Record("Value", value)));
            Assert.Throws<FormatException>(() => MailMergeProcessor.Preview(template, Record("Value", value)));
        }
        Assert.Equal("«Value»", template.PlainText);
    }

    [Fact]
    public void Value_length_is_bounded_consistently_for_preview_and_merge()
    {
        var template = Template(Field("Value"));
        var allowed = new string('a', 16_384);
        Assert.Equal(allowed, MailMergeProcessor.Merge(template, Record("Value", allowed)).PlainText);
        var preview = MailMergeProcessor.Preview(template, Record("Value", allowed));
        preview.Validate();
        Assert.Equal(allowed, preview.PlainText);
        var oversized = Record("Value", new string('a', 16_385));
        Assert.Throws<FormatException>(() => MailMergeProcessor.Merge(template, oversized));
        Assert.Throws<FormatException>(() => MailMergeProcessor.Preview(template, oversized));
        Assert.Equal(1024, MailMergeProcessor.Merge(Template(Field("Value", "D1024")), Record("Value", 1)).PlainText.Length);
    }

    private sealed class TestValue(Func<string> format) : IFormattable
    {
        public string ToString(string? formatString, IFormatProvider? formatProvider) => format();
    }
}
