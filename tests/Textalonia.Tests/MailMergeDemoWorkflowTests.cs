using Textalonia.Demo;
using Textalonia.MailMerge;
using Xunit;

namespace Textalonia.Tests;

public sealed class MailMergeDemoWorkflowTests
{
    [Fact]
    public void Invoice_sample_expands_nested_rows_and_combines_recipients()
    {
        var template = MailMergeWindow.CreateInvoiceTemplate();
        template.Validate();
        var records = MailMergeWindow.ParseRecords(MailMergeWindow.InvoiceSampleRecords);
        var items = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(records[0]["Items"]);
        var taxes = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(items[0]["Taxes"]);
        Assert.Equal(2, taxes.Count);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(records[2]["Items"]));

        var separate = MailMergeProcessor.MergeMany(template, records).ToArray();
        Assert.Equal(3, separate.Length);
        Assert.Contains("Consulting", separate[0].PlainText);
        Assert.Contains("Sales tax", separate[0].PlainText);
        Assert.DoesNotContain("TableStart:", separate[0].PlainText);
        Assert.Contains("Support", separate[1].PlainText);
        Assert.DoesNotContain("Sales tax", separate[1].PlainText);
        Assert.DoesNotContain("Support", separate[2].PlainText);

        var combined = MailMergeCombinedProcessor.Merge(template, records);
        Assert.Equal(3, combined.Sections.Length);
        Assert.Contains("Taylor Morgan", combined.PlainText);
    }
}
