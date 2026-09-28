using System.Globalization;
using Textalonia.Editing;
using Textalonia.MailMerge;
using Textalonia.Model;
using Textalonia.Model.Fields;
using Xunit;

namespace Textalonia.Tests;

public sealed class Dx11FieldEvaluationTests
{
    private static FlowDocument Field(string instruction) =>
        FieldOperations.Insert(FlowDocument.FromText("cached"), Guid.Empty, 0, 6, instruction, FlowDocument.FromText("cached"));

    [Fact]
    public void Scoped_values_reach_nested_fields_and_formula_uses_selected_culture()
    {
        var document = FlowDocument.FromText("first second");
        const string instruction = "IF { MERGEFIELD Quantity } > 1 \"{ = { MERGEFIELD UnitPrice } * { MERGEFIELD Quantity } \\# 0.00 }\" \"single\"";
        document = FieldOperations.Insert(document, Guid.Empty, 0, 5, instruction, FlowDocument.FromText("first"));
        document = FieldOperations.Insert(document, Guid.Empty, 6, 6, instruction, FlowDocument.FromText("second"));
        var firstId = document.Fields[0].Id;
        var first = new Dictionary<string, object?> { ["Quantity"] = 2, ["UnitPrice"] = "12,5" };
        var second = new Dictionary<string, object?> { ["Quantity"] = 1, ["UnitPrice"] = "99,5" };

        var result = FieldEvaluator.Update(document, new()
        {
            Culture = CultureInfo.GetCultureInfo("fr-FR"),
            MergeValues = new Dictionary<string, object?> { ["Quantity"] = 100 },
            MergeValuesResolver = field => field.Id == firstId ? first : second
        });

        Assert.Empty(result.Diagnostics);
        Assert.Equal("25,00 single", result.Document.Text);
    }

    [Fact]
    public void Picture_resource_callback_inserts_typed_image_and_stabilizes()
    {
        var resource = new DocumentResource { Kind = DocumentResourceKind.Host, MediaType = "image/png", Location = "receipt-signature" };
        var options = new FieldEvaluationOptions
        {
            MergeValuesResolver = _ => new Dictionary<string, object?> { ["Asset"] = "receipt-signature" },
            PictureResourceResolver = context =>
            {
                Assert.Equal("receipt-signature", context.Argument);
                Assert.Equal("receipt-signature", context.MergeValues["Asset"]);
                return new(resource) { AltText = "Signature", Width = 120, Height = 40 };
            }
        };
        var first = FieldEvaluator.Update(Field("INCLUDEPICTURE \"receipt-signature\""), options);

        Assert.Empty(first.Diagnostics);
        var run = Assert.Single(Assert.IsType<Paragraph>(Assert.Single(first.Document.Blocks)).Runs);
        var image = Assert.IsType<ImageInlinePayload>(run.Inline!.Payload);
        Assert.Equal(120, run.Inline.Width);
        Assert.Equal(40, run.Inline.Height);
        Assert.Equal("Signature", run.Inline.AltText);
        Assert.Equal(resource, first.Document.Resources[image.ResourceId]);
        first.Document.Validate();

        var repeated = FieldEvaluator.Update(first.Document, options);
        Assert.Empty(repeated.Diagnostics);
        Assert.Equal(0, repeated.UpdatedCount);

        var changedResource = resource with { Location = "revised-signature" };
        var changed = FieldEvaluator.Update(repeated.Document, options with
        {
            PictureResourceResolver = _ => new(changedResource) { AltText = "Signature", Width = 120, Height = 40 }
        });
        Assert.Empty(changed.Diagnostics);
        Assert.Equal(1, changed.UpdatedCount);
        var changedRun = Assert.Single(Assert.IsType<Paragraph>(Assert.Single(changed.Document.Blocks)).Runs);
        var changedImage = Assert.IsType<ImageInlinePayload>(changedRun.Inline!.Payload);
        Assert.Equal(changedResource, changed.Document.Resources[changedImage.ResourceId]);
        Assert.DoesNotContain(image.ResourceId, changed.Document.Resources.Keys);
        var stable = FieldEvaluator.Update(changed.Document, options with
        {
            PictureResourceResolver = _ => new(changedResource) { AltText = "Signature", Width = 120, Height = 40 }
        });
        Assert.Empty(stable.Diagnostics);
        Assert.Equal(0, stable.UpdatedCount);
    }

    [Fact]
    public void Rich_document_variable_in_repeated_rows_receives_the_child_scope()
    {
        static Paragraph Boundary(string name) => new([new RichRun(MergeFields.Create(name))]);
        var value = new Paragraph("?");
        var template = new FlowDocument([Boundary("TableStart:Items"), value, Boundary("TableEnd:Items")]);
        template = template with { Fields = [new DocumentField
        {
            Instruction = "DOCVARIABLE Description",
            Start = new DocumentAnchor { ParagraphId = value.Id, Offset = 0, Affinity = AnchorAffinity.Before },
            End = new DocumentAnchor { ParagraphId = value.Id, Offset = 1 }
        }] };
        var rows = new[]
        {
            new Dictionary<string, object?> { ["Description"] = "Pen" },
            new Dictionary<string, object?> { ["Description"] = "Book" }
        };

        var merged = MailMergeProcessor.Merge(template,
            new Dictionary<string, object?> { ["Items"] = rows },
            new MailMergeOptions { FieldOptions = new FieldEvaluationOptions
            {
                DocumentVariableResolver = context =>
                    FlowDocument.FromText((string)context.MergeValues[context.Argument]!, TextStyle.Default with { Bold = true })
            } });

        merged.Validate();
        Assert.Equal("Pen\nBook", merged.PlainText);
        Assert.All(merged.Blocks, block => Assert.True(Assert.IsType<Paragraph>(block).Runs[0].Style.Bold));
    }
}
